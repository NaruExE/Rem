using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using REM.Models;

namespace REM.Services;

/// <summary>
/// Manages Spotify's official headless playback engine (soloist.exe) via its local WebSocket API.
/// </summary>
public sealed class SpotifySoloistService : IDisposable
{
    public const int DefaultCacheSizeMb = 1024;
    private const string WsAddress = "127.0.0.1:9090";
    private static readonly Uri WsUri = new($"ws://{WsAddress}");

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IoCounters ioCounters);

    private readonly DatabaseService _database;
    private readonly ActionLogService _actionLog;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private Process? _process;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _wsCts;
    private GoLibrespotStatus? _lastStatus;
    private long _archivedSoloistBytes;
    private long _currentProcessLastIoBytes;
    private long _positionAnchorMs;
    private DateTimeOffset _positionAnchorUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastSeekIssuedUtc = DateTimeOffset.MinValue;
    private bool _isClockRunning;
    private bool _isConnected;
    private bool _isAuthenticated;
    private bool _isDeviceActive;
    private string _authenticatedUser = string.Empty;
    private bool _disposed;

    public SpotifySoloistService(DatabaseService database, ActionLogService actionLog)
    {
        _database = database;
        _actionLog = actionLog;
        BandwidthMonitor.RegisterByteProvider("soloist", GetCumulativeSoloistBytes);
        AppDomain.CurrentDomain.ProcessExit += OnHostProcessExit;
    }

    public GoLibrespotStatus? CurrentStatus => _lastStatus;
    public bool IsDaemonRunning => _process is { HasExited: false };
    public bool IsWebSocketConnected => _isConnected;
    public bool IsAuthenticated => _isAuthenticated;
    public bool IsDeviceActive => _isDeviceActive;
    public string AuthenticatedUser => _authenticatedUser;
    public bool HasApiKey => !string.IsNullOrWhiteSpace(_database.GetSetting(DatabaseService.KeySpotifySoloistApiKey));

    public int ConfiguredCacheSizeMb
    {
        get
        {
            var raw = _database.GetSetting(DatabaseService.KeySpotifyCacheSizeMb);
            if (!int.TryParse(raw, out var mb))
            {
                return DefaultCacheSizeMb;
            }

            if (mb <= 0)
            {
                return 0;
            }

            return Math.Max(100, mb);
        }
    }

    public long GetDiskCacheSizeBytes()
    {
        try
        {
            if (!Directory.Exists(CacheDirectory))
            {
                return 0;
            }

            long total = 0;
            foreach (var file in Directory.EnumerateFiles(CacheDirectory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    // Skip transient or locked cache files.
                }
            }

            return total;
        }
        catch
        {
            return 0;
        }
    }

    public async Task<(bool Cleared, string? Error)> ClearDiskCacheAsync(CancellationToken cancellationToken)
    {
        var wasRunning = IsDaemonRunning;
        StopDaemon();
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);

        try
        {
            if (Directory.Exists(CacheDirectory))
            {
                Directory.Delete(CacheDirectory, recursive: true);
            }

            Directory.CreateDirectory(CacheDirectory);
            _actionLog.Log("Spotify", "Cleared Spotify Soloist disk audio cache.");
        }
        catch (Exception ex)
        {
            _actionLog.Log("Warning", $"Could not fully clear Soloist cache: {ex.Message}");
            if (wasRunning && HasApiKey)
            {
                await EnsureDaemonRunningAsync(cancellationToken).ConfigureAwait(false);
            }

            return (false, ex.Message);
        }

        if (wasRunning && HasApiKey)
        {
            await EnsureDaemonRunningAsync(cancellationToken).ConfigureAwait(false);
        }

        return (true, null);
    }

    public event EventHandler<GoLibrespotStatus?>? StatusChanged;
    public event EventHandler<(bool IsAuthenticated, string? Username)>? AuthStateChanged;
    public event EventHandler<List<SpotifyQueueEntry>>? QueueUpdated;
    public event EventHandler<string>? PlaybackError;
    public event EventHandler<bool>? ConnectionStateChanged;

    public string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "REM",
        "soloist",
        "data");

    public string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "REM",
        "soloist",
        "cache");

    public string? ResolveExecutablePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Tools", "soloist.exe"),
            Path.Combine(AppContext.BaseDirectory, "soloist.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "REM",
                "Tools",
                "soloist.exe")
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var full = Path.Combine(dir.Trim(), "soloist.exe");
                    if (File.Exists(full))
                    {
                        return full;
                    }
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        return null;
    }

    public async Task<(bool Started, string? Error)> EnsureDaemonRunningAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false })
            {
                EnsureWebSocketLoopStarted();
                return (true, null);
            }

            var exePath = ResolveExecutablePath();
            if (exePath is null)
            {
                return (false, "soloist.exe was not found in Tools\\soloist.exe, next to REM.exe, or in PATH.");
            }

            var apiKey = _database.GetSetting(DatabaseService.KeySpotifySoloistApiKey)?.Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return (false, "Enter your Spotify Soloist API Key in Spotify Settings → Spotify & APIs.");
            }

            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(CacheDirectory);

            var initialVol = int.TryParse(_database.GetSetting(DatabaseService.KeySpotifyVolume), out var vol)
                ? Math.Clamp(vol, 0, 100)
                : 80;

            var psi = new ProcessStartInfo(exePath)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory
            };

            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("REM");
            psi.ArgumentList.Add("-D");
            psi.ArgumentList.Add(DataDirectory);
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(CacheDirectory);
            psi.ArgumentList.Add("-z");
            psi.ArgumentList.Add(ConfiguredCacheSizeMb.ToString());
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(initialVol.ToString());
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add(WsAddress);
            psi.ArgumentList.Add("-k");
            psi.ArgumentList.Add(apiKey);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Exited += (_, _) => OnDaemonExited(proc);

            if (!proc.Start())
            {
                proc.Dispose();
                return (false, "Could not start soloist.exe.");
            }

            _process = proc;
            _ = DrainStreamAsync(proc.StandardOutput, isError: false);
            _ = DrainStreamAsync(proc.StandardError, isError: true);

            _actionLog.Log("Spotify", $"Started Spotify Soloist daemon (PID {proc.Id}).");
            EnsureWebSocketLoopStarted();
            return (true, null);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<(bool Restarted, string? Error)> RestartDaemonAsync(CancellationToken cancellationToken)
    {
        StopDaemon();
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        return await EnsureDaemonRunningAsync(cancellationToken).ConfigureAwait(false);
    }

    public void StopDaemon()
    {
        CancelAndDisposeWsCts();
        _isConnected = false;
        _isAuthenticated = false;
        _isDeviceActive = false;
        _lastStatus = null;

        var ws = Interlocked.Exchange(ref _ws, null);
        if (ws is not null)
        {
            try { ws.Dispose(); } catch { }
        }

        var proc = Interlocked.Exchange(ref _process, null);
        if (proc is not null)
        {
            SnapshotSoloistIoBytes(proc);
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(1500);
                }
            }
            catch
            {
                // Ignore race during shutdown.
            }
            finally
            {
                proc.Dispose();
            }

            _actionLog.Log("Spotify", "Stopped Spotify Soloist daemon.");
        }

        _positionAnchorMs = 0;
        _isClockRunning = false;

        ConnectionStateChanged?.Invoke(this, false);
        AuthStateChanged?.Invoke(this, (false, null));
        StatusChanged?.Invoke(this, null);
    }

    public async Task<bool> PlayAsync(string? uri = null, CancellationToken cancellationToken = default)
    {
        if (!_isDeviceActive)
        {
            await SendCommandAsync(new { type = "command", command = "activate" }, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(uri))
        {
            return await SendCommandAsync(new { type = "command", command = "play" }, cancellationToken).ConfigureAwait(false);
        }

        return await SendCommandAsync(new { type = "command", command = "play", uri }, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> PauseAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync(new { type = "command", command = "pause" }, cancellationToken);

    public Task<bool> PlayPauseAsync(CancellationToken cancellationToken = default)
    {
        var isPlaying = _lastStatus is { Stopped: false, Paused: false };
        return isPlaying ? PauseAsync(cancellationToken) : PlayAsync(uri: null, cancellationToken);
    }

    public Task<bool> NextAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync(new { type = "command", command = "skip_next" }, cancellationToken);

    public Task<bool> PrevAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync(new { type = "command", command = "skip_prev" }, cancellationToken);

    public Task<bool> SeekAsync(long positionMs, CancellationToken cancellationToken = default)
    {
        var clamped = Math.Max(0, positionMs);
        _lastSeekIssuedUtc = DateTimeOffset.UtcNow;
        SetPositionAnchor(clamped, _isClockRunning);
        if (_lastStatus is { Track: not null } st)
        {
            _lastStatus = st with { Track = st.Track with { PositionMs = clamped } };
        }

        return SendCommandAsync(new { type = "command", command = "seek", position_ms = clamped }, cancellationToken);
    }

    public Task<bool> SetVolumeAsync(int volume, CancellationToken cancellationToken = default) =>
        SendCommandAsync(new { type = "command", command = "set_volume", volume = Math.Clamp(volume, 0, 100) }, cancellationToken);

    public Task<bool> SetShuffleAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendCommandAsync(new { type = "command", command = "set_shuffle", enabled }, cancellationToken);

    public Task<bool> SetRepeatContextAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendCommandAsync(new { type = "command", command = "set_repeat_context", enabled }, cancellationToken);

    public Task<bool> SetRepeatTrackAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendCommandAsync(new { type = "command", command = "set_repeat_track", enabled }, cancellationToken);

    public Task<bool> AddToQueueAsync(string trackUri, CancellationToken cancellationToken = default) =>
        SendCommandAsync(new { type = "command", command = "add_to_queue", uri = trackUri }, cancellationToken);

    public Task<bool> ActivateAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync(new { type = "command", command = "activate" }, cancellationToken);

    public Task<bool> RequestStateAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync(new { type = "command", command = "get_state" }, cancellationToken);

    public Task<bool> RequestQueueAsync(int limit = 25, CancellationToken cancellationToken = default) =>
        SendCommandAsync(new { type = "command", command = "get_queue", limit }, cancellationToken);

    private async Task<bool> SendCommandAsync(object payload, CancellationToken cancellationToken)
    {
        var ws = Volatile.Read(ref _ws);
        if (ws is null || ws.State != WebSocketState.Open || _disposed)
        {
            return false;
        }

        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ws = Volatile.Read(ref _ws);
            if (ws is null || ws.State != WebSocketState.Open)
            {
                return false;
            }

            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void EnsureWebSocketLoopStarted()
    {
        if (_wsCts is { IsCancellationRequested: false })
        {
            return;
        }

        CancelAndDisposeWsCts();
        var cts = new CancellationTokenSource();
        _wsCts = cts;
        _ = Task.Run(() => WebSocketSupervisorLoopAsync(cts.Token));
    }

    private async Task WebSocketSupervisorLoopAsync(CancellationToken ct)
    {
        await Task.Delay(250, ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested && !_disposed && IsDaemonRunning)
        {
            using var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(WsUri, ct).ConfigureAwait(false);
                Volatile.Write(ref _ws, ws);
                _isConnected = true;
                ConnectionStateChanged?.Invoke(this, true);

                await SendCommandAsync(new { type = "command", command = "get_auth_state" }, ct).ConfigureAwait(false);
                await SendCommandAsync(new { type = "command", command = "get_state" }, ct).ConfigureAwait(false);
                await SendCommandAsync(new { type = "command", command = "get_queue", limit = 25 }, ct).ConfigureAwait(false);

                var buffer = new byte[32 * 1024];
                using var ms = new MemoryStream();

                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested && IsDaemonRunning)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }

                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    HandleWebSocketMessage(json);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Soloist may still be starting or restarting.
            }
            finally
            {
                if (ReferenceEquals(Volatile.Read(ref _ws), ws))
                {
                    Volatile.Write(ref _ws, null);
                }

                _isConnected = false;
                ConnectionStateChanged?.Invoke(this, false);
            }

            if (!ct.IsCancellationRequested && IsDaemonRunning)
            {
                await Task.Delay(750, ct).ConfigureAwait(false);
            }
        }
    }

    private void HandleWebSocketMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = GetString(root, "type");
            if (string.IsNullOrWhiteSpace(type))
            {
                return;
            }

            switch (type)
            {
                case "auth_state":
                {
                    var authed = GetBool(root, "authenticated")
                        ?? GetBool(root, "logged_in")
                        ?? GetBool(root, "is_authenticated")
                        ?? false;
                    var user = GetString(root, "username") ?? GetString(root, "user") ?? _authenticatedUser;
                    _isAuthenticated = authed;
                    if (!string.IsNullOrWhiteSpace(user))
                    {
                        _authenticatedUser = user;
                    }

                    AuthStateChanged?.Invoke(this, (_isAuthenticated, _authenticatedUser));
                    break;
                }
                case "playback_state":
                {
                    ApplyPlaybackStateSnapshot(root);
                    break;
                }
                case "track_changed":
                {
                    GoLibrespotTrack? track = null;
                    if (TryGetObject(root, "item", out var itemObj) || TryGetObject(root, "track", out itemObj))
                    {
                        track = ParseSoloistEntity(itemObj);
                    }

                    EnsureStatusInitialized();
                    var prevUri = _lastStatus!.Track?.Uri;
                    track = MergeTrackMetadata(_lastStatus.Track, track);
                    _isAuthenticated = true;

                    var sameTrack = track is not null && string.Equals(prevUri, track.Uri, StringComparison.Ordinal);
                    var isPlaying = !_lastStatus.Paused && !_lastStatus.Stopped && track is not null;
                    var hasAuthPos = false;
                    if (TryExtractPosition(root, out var extractedPos, out var pausedFromSpeed))
                    {
                        if (pausedFromSpeed.HasValue)
                        {
                            isPlaying = !pausedFromSpeed.Value && track is not null;
                        }

                        if (!WithinPostSeekZeroGrace(extractedPos))
                        {
                            SetPositionAnchor(extractedPos, isPlaying);
                            hasAuthPos = true;
                        }
                    }
                    else if (!sameTrack)
                    {
                        SetPositionAnchor(0, isPlaying);
                        hasAuthPos = true;
                    }
                    else
                    {
                        SetPositionAnchor(GetEstimatedPositionMs(), isPlaying);
                    }

                    if (track is not null)
                    {
                        track = track with { PositionMs = GetEstimatedPositionMs() };
                    }

                    _lastStatus = _lastStatus with
                    {
                        Track = track,
                        Stopped = track is null,
                        Paused = track is null ? _lastStatus.Paused : !isPlaying,
                        HasAuthoritativePosition = hasAuthPos
                    };
                    StatusChanged?.Invoke(this, _lastStatus);
                    _ = RequestQueueAsync(25);
                    break;
                }
                case "playback_changed":
                {
                    EnsureStatusInitialized();
                    _isAuthenticated = true;
                    var statusText = GetString(root, "status")?.ToLowerInvariant();
                    var stopped = statusText == "stopped" || statusText == "idle" || (GetBool(root, "stopped") ?? false);
                    var paused = statusText == "paused" || (GetBool(root, "paused") ?? false);
                    if (statusText == "playing")
                    {
                        stopped = false;
                        paused = false;
                    }
                    else if (GetBool(root, "is_playing") is { } playingFlag)
                    {
                        stopped = false;
                        paused = !playingFlag;
                    }

                    var curTrack = _lastStatus!.Track;
                    var prevUri = curTrack?.Uri;
                    if ((TryGetObject(root, "item", out var itemObj) || TryGetObject(root, "track", out itemObj)) &&
                        ParseSoloistEntity(itemObj) is { } nextTrack)
                    {
                        curTrack = MergeTrackMetadata(curTrack, nextTrack);
                    }

                    var hasAuthPos = false;
                    if (TryExtractPosition(root, out var extractedPos, out var pausedFromSpeed))
                    {
                        if (pausedFromSpeed.HasValue && statusText is null && GetBool(root, "paused") is null && GetBool(root, "is_playing") is null)
                        {
                            paused = pausedFromSpeed.Value;
                        }

                        var nowPlaying = !stopped && !paused && curTrack is not null;
                        if (!WithinPostSeekZeroGrace(extractedPos))
                        {
                            SetPositionAnchor(extractedPos, nowPlaying);
                            hasAuthPos = true;
                        }
                        else
                        {
                            SetPositionAnchor(GetEstimatedPositionMs(), nowPlaying);
                        }
                    }
                    else
                    {
                        var nowPlaying = !stopped && !paused && curTrack is not null;
                        var sameTrack = curTrack is not null && string.Equals(prevUri, curTrack.Uri, StringComparison.Ordinal);
                        SetPositionAnchor(sameTrack || prevUri is null ? GetEstimatedPositionMs() : 0, nowPlaying);
                    }

                    if (curTrack is not null)
                    {
                        curTrack = curTrack with { PositionMs = GetEstimatedPositionMs() };
                    }

                    _lastStatus = _lastStatus with
                    {
                        Stopped = stopped,
                        Paused = paused,
                        Track = curTrack,
                        HasAuthoritativePosition = hasAuthPos
                    };
                    StatusChanged?.Invoke(this, _lastStatus);
                    break;
                }
                case "position_sync":
                {
                    EnsureStatusInitialized();
                    var paused = GetBool(root, "paused") ?? _lastStatus!.Paused;
                    long pos;
                    if (TryExtractPosition(root, out var extractedPos, out var pausedFromSpeed))
                    {
                        if (pausedFromSpeed.HasValue && GetBool(root, "paused") is null)
                        {
                            paused = pausedFromSpeed.Value;
                        }

                        if (WithinPostSeekZeroGrace(extractedPos))
                        {
                            break;
                        }

                        pos = extractedPos;
                    }
                    else
                    {
                        pos = GetEstimatedPositionMs();
                    }

                    var nowPlaying = !paused && !_lastStatus!.Stopped && _lastStatus.Track is not null;
                    SetPositionAnchor(pos, nowPlaying);

                    var updatedTrack = _lastStatus!.Track is not null
                        ? _lastStatus.Track with { PositionMs = pos }
                        : null;

                    _lastStatus = _lastStatus with
                    {
                        Paused = paused,
                        Track = updatedTrack,
                        HasAuthoritativePosition = true
                    };
                    StatusChanged?.Invoke(this, _lastStatus);
                    break;
                }
                case "volume_changed":
                {
                    EnsureStatusInitialized();
                    var vol = (int)(GetLong(root, "volume") ?? _lastStatus!.Volume);
                    _lastStatus = _lastStatus! with
                    {
                        Volume = Math.Clamp(vol, 0, 100),
                        VolumeSteps = 100,
                        Track = StampCurrentTrackPosition(_lastStatus.Track),
                        HasAuthoritativePosition = false
                    };
                    StatusChanged?.Invoke(this, _lastStatus);
                    break;
                }
                case "options_changed":
                {
                    EnsureStatusInitialized();
                    var shuffle = _lastStatus!.ShuffleContext;
                    var repeatCtx = _lastStatus.RepeatContext;
                    var repeatTrk = _lastStatus.RepeatTrack;
                    ApplyOptionsElement(root, ref shuffle, ref repeatCtx, ref repeatTrk);

                    _lastStatus = _lastStatus with
                    {
                        ShuffleContext = shuffle,
                        RepeatContext = repeatCtx,
                        RepeatTrack = repeatTrk,
                        Track = StampCurrentTrackPosition(_lastStatus.Track),
                        HasAuthoritativePosition = false
                    };
                    StatusChanged?.Invoke(this, _lastStatus);
                    break;
                }
                case "device_changed":
                {
                    EnsureStatusInitialized();
                    _isDeviceActive = GetBool(root, "is_active") ?? GetBool(root, "active") ?? _isDeviceActive;
                    if (GetString(root, "username") is { } u)
                    {
                        _authenticatedUser = u;
                        _isAuthenticated = true;
                        AuthStateChanged?.Invoke(this, (true, u));
                    }

                    _lastStatus = _lastStatus! with
                    {
                        Track = StampCurrentTrackPosition(_lastStatus.Track),
                        HasAuthoritativePosition = false
                    };
                    StatusChanged?.Invoke(this, _lastStatus);
                    break;
                }
                case "context_changed":
                {
                    EnsureStatusInitialized();
                    string? ctxUri = GetString(root, "uri") ?? GetString(root, "context_uri");
                    string? ctxName = GetString(root, "name") ?? GetString(root, "context_name");
                    if (TryGetObject(root, "context", out var ctxObj))
                    {
                        ctxUri ??= GetString(ctxObj, "uri");
                        ctxName ??= ExtractEntityName(ctxObj);
                    }

                    _lastStatus = _lastStatus! with
                    {
                        ContextUri = ctxUri ?? _lastStatus.ContextUri,
                        ContextName = ctxName ?? _lastStatus.ContextName,
                        Track = StampCurrentTrackPosition(_lastStatus.Track),
                        HasAuthoritativePosition = false
                    };
                    StatusChanged?.Invoke(this, _lastStatus);
                    _ = RequestQueueAsync(25);
                    break;
                }
                case "queue_changed":
                {
                    var entries = ParseQueuePayload(root);
                    QueueUpdated?.Invoke(this, entries);
                    break;
                }
                case "command_result":
                {
                    var ok = GetBool(root, "success") ?? GetBool(root, "ok") ?? true;
                    var err = GetString(root, "error") ?? GetString(root, "message");
                    if (!ok && !string.IsNullOrWhiteSpace(err))
                    {
                        _actionLog.Log("Warning", $"Soloist command error: {err}");
                        if (err.Contains("play", StringComparison.OrdinalIgnoreCase) ||
                            err.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
                            err.Contains("expired", StringComparison.OrdinalIgnoreCase))
                        {
                            PlaybackError?.Invoke(this, err);
                        }
                    }
                    break;
                }
            }
        }
        catch
        {
            // Ignore unrecognized individual WS message.
        }
    }

    private void ApplyPlaybackStateSnapshot(JsonElement root)
    {
        EnsureStatusInitialized();

        if (GetBool(root, "authenticated") is { } authed)
        {
            _isAuthenticated = authed;
        }
        else
        {
            _isAuthenticated = true;
        }

        if (GetString(root, "username") is { } u)
        {
            _authenticatedUser = u;
        }

        if ((GetBool(root, "is_active") ?? GetBool(root, "active")) is { } act)
        {
            _isDeviceActive = act;
        }

        GoLibrespotTrack? track = _lastStatus!.Track;
        if (TryGetObject(root, "item", out var itemObj) || TryGetObject(root, "track", out itemObj))
        {
            track = MergeTrackMetadata(track, ParseSoloistEntity(itemObj));
        }

        var statusStr = GetString(root, "status")?.ToLowerInvariant();
        var stopped = statusStr is "stopped" or "idle" || (GetBool(root, "stopped") ?? (track is null));
        var paused = statusStr == "paused" || (GetBool(root, "paused") ?? false);
        if (statusStr == "playing" || GetBool(root, "is_playing") == true)
        {
            stopped = false;
            paused = false;
        }

        var hasAuthPos = false;
        if (TryExtractPosition(root, out var extractedPos, out var pausedFromSpeed))
        {
            if (pausedFromSpeed.HasValue && statusStr is null && GetBool(root, "paused") is null && GetBool(root, "is_playing") is null)
            {
                paused = pausedFromSpeed.Value;
            }

            if (!WithinPostSeekZeroGrace(extractedPos))
            {
                SetPositionAnchor(extractedPos, !stopped && !paused && track is not null);
                hasAuthPos = true;
            }
            else
            {
                SetPositionAnchor(GetEstimatedPositionMs(), !stopped && !paused && track is not null);
            }
        }
        else
        {
            SetPositionAnchor(GetEstimatedPositionMs(), !stopped && !paused && track is not null);
        }

        if (track is not null)
        {
            track = track with { PositionMs = GetEstimatedPositionMs() };
        }

        var vol = (int)(GetLong(root, "volume") ?? _lastStatus.Volume);
        var shuffle = _lastStatus.ShuffleContext;
        var repeatCtx = _lastStatus.RepeatContext;
        var repeatTrk = _lastStatus.RepeatTrack;
        ApplyOptionsElement(root, ref shuffle, ref repeatCtx, ref repeatTrk);

        _lastStatus = _lastStatus with
        {
            Username = _authenticatedUser,
            DeviceName = "REM",
            Stopped = stopped,
            Paused = paused,
            Volume = Math.Clamp(vol, 0, 100),
            VolumeSteps = 100,
            ShuffleContext = shuffle,
            RepeatContext = repeatCtx,
            RepeatTrack = repeatTrk,
            Track = track,
            HasAuthoritativePosition = hasAuthPos
        };

        AuthStateChanged?.Invoke(this, (_isAuthenticated, _authenticatedUser));
        StatusChanged?.Invoke(this, _lastStatus);
        _ = RequestQueueAsync(25);
    }

    private long GetEstimatedPositionMs()
    {
        if (!_isClockRunning)
        {
            return Math.Max(0, _positionAnchorMs);
        }

        var elapsed = (long)(DateTimeOffset.UtcNow - _positionAnchorUtc).TotalMilliseconds;
        return Math.Max(0, _positionAnchorMs + Math.Max(0, elapsed));
    }

    private void SetPositionAnchor(long posMs, bool isPlaying)
    {
        _positionAnchorMs = Math.Max(0, posMs);
        _positionAnchorUtc = DateTimeOffset.UtcNow;
        _isClockRunning = isPlaying;
    }

    private bool WithinPostSeekZeroGrace(long incomingPosMs) =>
        incomingPosMs == 0 &&
        _positionAnchorMs > 1500 &&
        (DateTimeOffset.UtcNow - _lastSeekIssuedUtc).TotalMilliseconds < 900;

    private GoLibrespotTrack? StampCurrentTrackPosition(GoLibrespotTrack? track) =>
        track is null ? null : track with { PositionMs = GetEstimatedPositionMs() };

    private static bool TryExtractPosition(JsonElement root, out long positionMs, out bool? isPausedFromSpeed)
    {
        positionMs = 0;
        isPausedFromSpeed = null;

        if (TryGetObject(root, "position", out var posObj))
        {
            var rawPos = GetLong(posObj, "position_ms") ?? GetLong(posObj, "ms");
            if (rawPos.HasValue)
            {
                var speed = posObj.TryGetProperty("speed", out var spProp) && spProp.ValueKind == JsonValueKind.Number
                    ? spProp.GetDouble()
                    : (double?)null;

                if (speed.HasValue)
                {
                    isPausedFromSpeed = speed.Value <= 0.01;
                }

                var tsMs = GetLong(posObj, "timestamp_ms") ?? 0;
                var adjusted = rawPos.Value;
                if (tsMs > 0 && speed is > 0.01)
                {
                    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var drift = Math.Clamp(nowMs - tsMs, 0, 5000);
                    adjusted += (long)Math.Round(drift * speed.Value);
                }

                positionMs = Math.Max(0, adjusted);
                return true;
            }
        }

        var flatPos = GetLong(root, "position_ms");
        if (!flatPos.HasValue &&
            root.TryGetProperty("position", out var numProp) &&
            numProp.ValueKind == JsonValueKind.Number &&
            numProp.TryGetInt64(out var numVal))
        {
            flatPos = numVal;
        }

        if (root.TryGetProperty("speed", out var topSp) && topSp.ValueKind == JsonValueKind.Number)
        {
            isPausedFromSpeed = topSp.GetDouble() <= 0.01;
        }

        if (flatPos.HasValue)
        {
            positionMs = Math.Max(0, flatPos.Value);
            return true;
        }

        return false;
    }

    private static void ApplyOptionsElement(JsonElement root, ref bool shuffle, ref bool repeatCtx, ref bool repeatTrk)
    {
        var source = TryGetObject(root, "options", out var optObj) ? optObj : root;

        if ((GetBool(source, "shuffle") ?? GetBool(source, "shuffle_mode")) is { } sh)
        {
            shuffle = sh;
        }

        if (GetBool(source, "repeat_context") is { } rc)
        {
            repeatCtx = rc;
        }

        if (GetBool(source, "repeat_track") is { } rt)
        {
            repeatTrk = rt;
        }

        if ((GetString(source, "repeat_mode") ?? GetString(source, "repeat")) is { } rm)
        {
            repeatCtx = string.Equals(rm, "context", StringComparison.OrdinalIgnoreCase);
            repeatTrk = string.Equals(rm, "track", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static GoLibrespotTrack? MergeTrackMetadata(GoLibrespotTrack? previous, GoLibrespotTrack? next)
    {
        if (next is null)
        {
            return null;
        }

        if (previous is null || !string.Equals(previous.Uri, next.Uri, StringComparison.Ordinal))
        {
            return next;
        }

        return next with
        {
            Name = string.IsNullOrWhiteSpace(next.Name) || next.Name == "Unknown Track" ? previous.Name : next.Name,
            ArtistNames = next.ArtistNames.Count > 0 ? next.ArtistNames : previous.ArtistNames,
            AlbumName = string.IsNullOrWhiteSpace(next.AlbumName) ? previous.AlbumName : next.AlbumName,
            AlbumCoverUrl = string.IsNullOrWhiteSpace(next.AlbumCoverUrl) ? previous.AlbumCoverUrl : next.AlbumCoverUrl,
            DurationMs = next.DurationMs > 0 ? next.DurationMs : previous.DurationMs,
            PositionMs = next.PositionMs > 0 ? next.PositionMs : previous.PositionMs
        };
    }

    private static GoLibrespotTrack? ParseSoloistEntity(JsonElement el)
    {
        var uri = GetString(el, "uri");
        var name = ExtractEntityName(el);
        if (string.IsNullOrWhiteSpace(uri) && string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var artists = new List<string>();
        var albumName = string.Empty;
        string? coverUrl = null;
        long durationMs = 0;

        if (TryGetObject(el, "decorations", out var dec))
        {
            if (dec.TryGetProperty("creators", out var creators) && creators.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in creators.EnumerateArray())
                {
                    var cname = TryGetObject(c, "entity", out var creatorEntity)
                        ? ExtractEntityName(creatorEntity)
                        : ExtractEntityName(c);
                    if (!string.IsNullOrWhiteSpace(cname) && !artists.Contains(cname, StringComparer.OrdinalIgnoreCase))
                    {
                        artists.Add(cname);
                    }
                }
            }

            if (artists.Count == 0 && dec.TryGetProperty("artists", out var decArtists) && decArtists.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in decArtists.EnumerateArray())
                {
                    var aname = a.ValueKind == JsonValueKind.String
                        ? a.GetString()
                        : TryGetObject(a, "entity", out var artistEntity)
                            ? ExtractEntityName(artistEntity)
                            : ExtractEntityName(a);
                    if (!string.IsNullOrWhiteSpace(aname) && !artists.Contains(aname, StringComparer.OrdinalIgnoreCase))
                    {
                        artists.Add(aname.Trim());
                    }
                }
            }

            if (TryGetObject(dec, "parent", out var parent))
            {
                var parentEntity = TryGetObject(parent, "entity", out var pe) ? pe : parent;
                albumName = ExtractEntityName(parentEntity) ?? string.Empty;
                coverUrl ??= ExtractBestCoverUrl(parentEntity);
            }

            if (TryGetObject(dec, "visual_identity", out var vis))
            {
                coverUrl = ExtractCoverArrayUrl(vis) ?? coverUrl;
            }

            if (TryGetObject(dec, "playback", out var pb))
            {
                durationMs = GetLong(pb, "duration_ms") ?? GetLong(pb, "duration") ?? 0;
            }
        }

        if (artists.Count == 0 && el.TryGetProperty("artists", out var flatArtistObjs) && flatArtistObjs.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in flatArtistObjs.EnumerateArray())
            {
                var aname = a.ValueKind == JsonValueKind.String ? a.GetString() : ExtractEntityName(a);
                if (!string.IsNullOrWhiteSpace(aname) && !artists.Contains(aname, StringComparer.OrdinalIgnoreCase))
                {
                    artists.Add(aname.Trim());
                }
            }
        }

        if (artists.Count == 0 && el.TryGetProperty("artist_names", out var flatArtists) && flatArtists.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in flatArtists.EnumerateArray())
            {
                var s = a.GetString();
                if (!string.IsNullOrWhiteSpace(s) && !artists.Contains(s.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    artists.Add(s.Trim());
                }
            }
        }

        if (artists.Count == 0 && (GetString(el, "artist") ?? GetString(el, "artist_name")) is { } singleArtist)
        {
            artists.Add(singleArtist);
        }

        if (durationMs <= 0)
        {
            durationMs = GetLong(el, "duration_ms") ?? GetLong(el, "length_ms") ?? GetLong(el, "duration") ?? 0;
        }

        coverUrl ??= GetString(el, "album_cover_url") ?? GetString(el, "cover_url");

        return new GoLibrespotTrack(
            Uri: uri ?? string.Empty,
            Name: name ?? "Unknown Track",
            ArtistNames: artists,
            AlbumName: string.IsNullOrWhiteSpace(albumName) ? (GetString(el, "album_name") ?? string.Empty) : albumName,
            AlbumCoverUrl: coverUrl,
            PositionMs: GetLong(el, "position_ms") ?? GetLong(el, "position") ?? 0,
            DurationMs: durationMs,
            Format: "Soloist Lossless/Ogg",
            Bitrate: null);
    }

    private static List<SpotifyQueueEntry> ParseQueuePayload(JsonElement root)
    {
        var list = new List<SpotifyQueueEntry>();
        JsonElement arrayProp;
        if (!TryGetArray(root, "upcoming", out arrayProp) &&
            !TryGetArray(root, "entries", out arrayProp) &&
            !TryGetArray(root, "tracks", out arrayProp) &&
            !TryGetArray(root, "next_tracks", out arrayProp) &&
            !TryGetArray(root, "items", out arrayProp) &&
            !TryGetArray(root, "queue", out arrayProp))
        {
            return list;
        }

        foreach (var entry in arrayProp.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var uri = GetString(entry, "uri") ?? string.Empty;
            var provider = GetString(entry, "source") ?? GetString(entry, "provider") ?? "context";
            GoLibrespotTrack? parsed = null;

            if (TryGetObject(entry, "item", out var itemObj) ||
                TryGetObject(entry, "entity", out itemObj) ||
                TryGetObject(entry, "track", out itemObj))
            {
                parsed = ParseSoloistEntity(itemObj);
            }
            else if (TryGetObject(entry, "decorations", out _) || !string.IsNullOrWhiteSpace(GetString(entry, "name")))
            {
                parsed = ParseSoloistEntity(entry);
            }

            if (string.IsNullOrWhiteSpace(uri) && parsed is not null)
            {
                uri = parsed.Uri;
            }

            if (!string.IsNullOrWhiteSpace(uri))
            {
                SpotifyTrackItem? trackItem = parsed is null
                    ? null
                    : new SpotifyTrackItem(
                        parsed.Uri.Split(':').LastOrDefault() ?? string.Empty,
                        parsed.Uri,
                        parsed.Name,
                        parsed.ArtistsDisplay,
                        parsed.AlbumName,
                        parsed.AlbumCoverUrl,
                        parsed.DurationMs);
                list.Add(new SpotifyQueueEntry(uri, provider, trackItem));
            }
        }

        return list;
    }

    private static string? ExtractEntityName(JsonElement entity)
    {
        if (TryGetObject(entity, "decorations", out var dec) &&
            TryGetObject(dec, "identity", out var id))
        {
            var n = GetString(id, "name");
            if (!string.IsNullOrWhiteSpace(n))
            {
                return n;
            }
        }

        return GetString(entity, "name") ?? GetString(entity, "title");
    }

    private static string? ExtractBestCoverUrl(JsonElement entity)
    {
        if (TryGetObject(entity, "decorations", out var dec) &&
            TryGetObject(dec, "visual_identity", out var vis))
        {
            return ExtractCoverArrayUrl(vis);
        }

        return null;
    }

    private static string? ExtractCoverArrayUrl(JsonElement visualIdentity)
    {
        if (!TryGetArray(visualIdentity, "cover", out var covers))
        {
            return null;
        }

        string? fallback = null;
        foreach (var c in covers.EnumerateArray())
        {
            var url = GetString(c, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            fallback ??= url;
            var size = GetString(c, "size")?.ToLowerInvariant();
            if (size is "large" or "default")
            {
                return url;
            }
        }

        return fallback;
    }

    private void EnsureStatusInitialized()
    {
        _lastStatus ??= new GoLibrespotStatus(
            Username: _authenticatedUser,
            DeviceId: string.Empty,
            DeviceName: "REM",
            ContextUri: null,
            ContextName: null,
            Stopped: true,
            Paused: false,
            Buffering: false,
            Volume: 80,
            VolumeSteps: 100,
            RepeatContext: false,
            RepeatTrack: false,
            ShuffleContext: false,
            Track: null);
    }

    private void OnDaemonExited(Process proc)
    {
        if (!ReferenceEquals(Volatile.Read(ref _process), proc))
        {
            return;
        }

        SnapshotSoloistIoBytes(proc);
        var exitCode = 0;
        try { exitCode = proc.ExitCode; } catch { }

        Volatile.Write(ref _process, null);
        _isConnected = false;
        _isClockRunning = false;
        ConnectionStateChanged?.Invoke(this, false);

        if (exitCode == 10 && !_disposed)
        {
            var msg = "Your soloist.exe build has reached Spotify's 90-day expiration window. Download an updated soloist.exe from developer.spotify.com and drop it into Tools\\.";
            _actionLog.Log("Error", msg);
            PlaybackError?.Invoke(this, msg);
        }
    }

    private async Task DrainStreamAsync(StreamReader reader, bool isError)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (isError &&
                    (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("expired", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("invalid api key", StringComparison.OrdinalIgnoreCase)))
                {
                    _actionLog.Log("Warning", $"soloist: {line}");
                }
            }
        }
        catch
        {
            // Stream closed cleanly.
        }
    }

    private static bool TryGetObject(JsonElement el, string propName, out JsonElement obj)
    {
        if (el.TryGetProperty(propName, out obj) && obj.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        obj = default;
        return false;
    }

    private static bool TryGetArray(JsonElement el, string propName, out JsonElement arr)
    {
        if (el.TryGetProperty(propName, out arr) && arr.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        arr = default;
        return false;
    }

    private static string? GetString(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var val = prop.GetString();
            return string.IsNullOrWhiteSpace(val) ? null : val.Trim();
        }

        return null;
    }

    private static bool? GetBool(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
        }

        return null;
    }

    private static long? GetLong(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var val))
        {
            return val;
        }

        return null;
    }

    private void CancelAndDisposeWsCts()
    {
        var current = Interlocked.Exchange(ref _wsCts, null);
        if (current is null)
        {
            return;
        }

        try { current.Cancel(); } catch (ObjectDisposedException) { }
        finally { current.Dispose(); }
    }

    private void OnHostProcessExit(object? sender, EventArgs e)
    {
        StopDaemon();
    }

    private long GetCumulativeSoloistBytes()
    {
        var archived = Interlocked.Read(ref _archivedSoloistBytes);
        var proc = Volatile.Read(ref _process);
        if (proc is null)
        {
            return archived;
        }

        try
        {
            if (!proc.HasExited && GetProcessIoCounters(proc.Handle, out var io))
            {
                var written = (long)Math.Min(io.WriteTransferCount, long.MaxValue);
                var current = Interlocked.Read(ref _currentProcessLastIoBytes);
                if (written > current)
                {
                    Interlocked.Exchange(ref _currentProcessLastIoBytes, written);
                    current = written;
                }

                return archived + current;
            }
        }
        catch
        {
            // Ignore transient handle error if process is exiting.
        }

        return archived + Interlocked.Read(ref _currentProcessLastIoBytes);
    }

    private void SnapshotSoloistIoBytes(Process proc)
    {
        var latest = Interlocked.Exchange(ref _currentProcessLastIoBytes, 0);
        try
        {
            if (GetProcessIoCounters(proc.Handle, out var io))
            {
                var written = (long)Math.Min(io.WriteTransferCount, long.MaxValue);
                if (written > latest)
                {
                    latest = written;
                }
            }
        }
        catch
        {
            // Process handle may already be closed.
        }

        if (latest > 0)
        {
            Interlocked.Add(ref _archivedSoloistBytes, latest);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        BandwidthMonitor.RegisterByteProvider("soloist", null);
        AppDomain.CurrentDomain.ProcessExit -= OnHostProcessExit;
        StopDaemon();
        _lifecycleGate.Dispose();
        _sendGate.Dispose();
    }
}

