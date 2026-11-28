using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
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
    private const string LatestSoloistWindowsZipUrl =
        "https://soloist-builds.spotifycdn.com/soloist_release_windows_x86_64.zip";
    private static readonly Uri WsUri = new($"ws://{WsAddress}");
    private static readonly SemaphoreSlim SoloistDownloadGate = new(1, 1);

    public static string ManagedToolsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "REM",
        "Tools");

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
    private readonly object _queueLock = new();
    private readonly Dictionary<string, string> _knownCoverUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _knownDurationsMs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SpotifyTrackItem> _manualUserQueue = [];
    private List<SpotifyTrackItem> _activeQueueTracks = [];
    private List<int> _activeShuffleOrder = [];
    private int _activeQueueIndex = -1;
    private string? _currentManualTrackUri;
    private string? _pendingTargetUri;
    private DateTimeOffset _pendingTargetUntilUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastUserPauseUtc = DateTimeOffset.MinValue;
    private CancellationTokenSource? _autoAdvanceCts;
    private int _autoAdvanceInFlight;
    private AudioDeviceChangeWatcher? _deviceWatcher;
    private int _deviceRestartInFlight;

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

        StartDeviceWatcher();
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

    private volatile string _lastStderrLine = string.Empty;

    public async Task<(string? Path, string? Error)> EnsureExecutableAsync(
        bool forceRedownload = false,
        Action<string>? onStatus = null,
        CancellationToken cancellationToken = default)
    {
        var existing = forceRedownload ? null : ResolveExecutablePath();
        if (!string.IsNullOrWhiteSpace(existing))
        {
            // Refresh managed builds older than 85 days (Spotify expires builds after 90 days).
            var managedPath = Path.Combine(ManagedToolsDirectory, "soloist.exe");
            if (!string.Equals(existing, managedPath, StringComparison.OrdinalIgnoreCase) ||
                (DateTime.UtcNow - File.GetLastWriteTimeUtc(existing)).TotalDays < 85)
            {
                return (existing, null);
            }

            _actionLog.Log("Spotify", "Managed soloist.exe is >85 days old; refreshing build...");
        }

        await SoloistDownloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRedownload)
            {
                var recheck = ResolveExecutablePath();
                var managedPath = Path.Combine(ManagedToolsDirectory, "soloist.exe");
                if (!string.IsNullOrWhiteSpace(recheck) &&
                    (!string.Equals(recheck, managedPath, StringComparison.OrdinalIgnoreCase) ||
                     (DateTime.UtcNow - File.GetLastWriteTimeUtc(recheck)).TotalDays < 85))
                {
                    return (recheck, null);
                }
            }

            if (IsDaemonRunning)
            {
                StopDaemon();
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            onStatus?.Invoke("Downloading Spotify Soloist (soloist.exe)...");
            Directory.CreateDirectory(ManagedToolsDirectory);
            var targetPath = Path.Combine(ManagedToolsDirectory, "soloist.exe");
            var tempZip = Path.Combine(ManagedToolsDirectory, "soloist.zip.download");

            try
            {
                if (File.Exists(tempZip))
                {
                    File.Delete(tempZip);
                }

                using var http = new HttpClient(new BandwidthCountingHandler())
                {
                    Timeout = TimeSpan.FromMinutes(4)
                };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("REM/1.0");

                using var resp = await http.GetAsync(
                    LatestSoloistWindowsZipUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                await using (var remoteStream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var localStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await remoteStream.CopyToAsync(localStream, cancellationToken).ConfigureAwait(false);
                }

                var extractedSha = string.Empty;
                var foundBinary = await Task.Run(() =>
                {
                    using var archive = ZipFile.OpenRead(tempZip);
                    foreach (var entry in archive.Entries)
                    {
                        if (!string.Equals(Path.GetFileName(entry.FullName), "soloist.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var stagePath = targetPath + ".tmp";
                        entry.ExtractToFile(stagePath, overwrite: true);
                        var (valid, sha, err) = ToolIntegrityHelper.ValidateExecutableAndHash(stagePath, minBytes: 500_000);
                        if (!valid)
                        {
                            try { File.Delete(stagePath); } catch { }
                            throw new InvalidOperationException($"Extracted soloist.exe failed integrity check: {err}");
                        }

                        extractedSha = sha;
                        File.Move(stagePath, targetPath, overwrite: true);
                        return true;
                    }

                    return false;
                }, cancellationToken).ConfigureAwait(false);

                if (!foundBinary || !File.Exists(targetPath))
                {
                    return (null, "soloist.exe was not found inside Spotify's downloaded archive.");
                }

                try { File.SetLastWriteTimeUtc(targetPath, DateTime.UtcNow); } catch { }
                _actionLog.Log("Spotify", $"Downloaded soloist.exe to '{targetPath}' (SHA-256: {extractedSha[..16]}...).");
                return (targetPath, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _actionLog.Log("Warning", $"Automatic Soloist download failed: {ex.Message}");
                return (null, ex.Message);
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }
        }
        finally
        {
            SoloistDownloadGate.Release();
        }
    }

    public async Task<(bool Started, string? Error)> EnsureDaemonRunningAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var allowExpiredRetry = true;

        startAttempt:
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? exePath;
        Process? launchedProc = null;
        try
        {
            if (_process is { HasExited: false })
            {
                EnsureWebSocketLoopStarted();
                return (true, null);
            }

            exePath = ResolveExecutablePath();
            if (exePath is null)
            {
                var (downloaded, dlErr) = await EnsureExecutableAsync(
                    forceRedownload: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(downloaded))
                {
                    return (false, $"Could not find or auto-download soloist.exe: {dlErr ?? "unknown error"}");
                }

                exePath = downloaded;
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

            _lastStderrLine = string.Empty;
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Exited += (_, _) => OnDaemonExited(proc);

            if (!proc.Start())
            {
                proc.Dispose();
                return (false, "Could not start soloist.exe.");
            }

            _process = proc;
            launchedProc = proc;
            _ = DrainStreamAsync(proc.StandardOutput, isError: false);
            _ = DrainStreamAsync(proc.StandardError, isError: true);

            _actionLog.Log("Spotify", $"Started Spotify Soloist daemon (PID {proc.Id}).");
            EnsureWebSocketLoopStarted();
        }
        finally
        {
            _lifecycleGate.Release();
        }

        await Task.Delay(350, cancellationToken).ConfigureAwait(false);
        if (launchedProc is { HasExited: true })
        {
            var exitCode = -1;
            try { exitCode = launchedProc.ExitCode; } catch { }
            if (allowExpiredRetry &&
                (exitCode == 10 || _lastStderrLine.Contains("expired", StringComparison.OrdinalIgnoreCase)))
            {
                allowExpiredRetry = false;
                _actionLog.Log("Spotify", "Detected expired soloist.exe build; auto-downloading latest release...");
                var (refreshed, _) = await EnsureExecutableAsync(forceRedownload: true, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(refreshed))
                {
                    goto startAttempt;
                }
            }

            return (false, string.IsNullOrWhiteSpace(_lastStderrLine)
                ? $"soloist.exe exited immediately (code {exitCode})."
                : $"soloist.exe exited (code {exitCode}): {_lastStderrLine}");
        }

        StartDeviceWatcher();
        return (true, null);
    }

    public async Task<(bool Restarted, string? Error)> RestartDaemonAsync(CancellationToken cancellationToken)
    {
        StopDaemon();
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        return await EnsureDaemonRunningAsync(cancellationToken).ConfigureAwait(false);
    }

    public void StopDaemon()
    {
        StopDeviceWatcher();
        CancelAndDisposeWsCts();
        CancelAutoAdvanceTimer();
        lock (_queueLock)
        {
            _activeQueueTracks.Clear();
            _activeShuffleOrder.Clear();
            _activeQueueIndex = -1;
            _manualUserQueue.Clear();
            _currentManualTrackUri = null;
            _pendingTargetUri = null;
            _pendingTargetUntilUtc = DateTimeOffset.MinValue;
            _knownDurationsMs.Clear();
        }
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

    private void StartDeviceWatcher()
    {
        if (_disposed || _deviceWatcher is not null)
        {
            return;
        }

        try
        {
            var watcher = new AudioDeviceChangeWatcher();
            watcher.DefaultRenderDeviceChanged += OnDefaultRenderDeviceChanged;
            _deviceWatcher = watcher;
        }
        catch
        {
        }
    }

    private void StopDeviceWatcher()
    {
        var watcher = Interlocked.Exchange(ref _deviceWatcher, null);
        if (watcher is not null)
        {
            watcher.DefaultRenderDeviceChanged -= OnDefaultRenderDeviceChanged;
            watcher.Dispose();
        }
    }

    private void OnDefaultRenderDeviceChanged(object? sender, string deviceId)
    {
        if (_disposed || !IsDaemonRunning || Interlocked.CompareExchange(ref _deviceRestartInFlight, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var st = _lastStatus;
                var wasPlaying = st is { Stopped: false, Paused: false, Track: not null };
                var trackUri = st?.Track?.Uri;
                var posMs = GetEstimatedPositionMs();

                _actionLog.Log("Spotify", "Windows default audio output changed; restarting Soloist on new device...");
                var (restarted, _) = await RestartDaemonAsync(CancellationToken.None).ConfigureAwait(false);
                if (!restarted || !wasPlaying || string.IsNullOrWhiteSpace(trackUri))
                {
                    return;
                }

                // Wait for WebSocket + Spotify Connect auth before resuming track.
                for (var i = 0; i < 45 && !_disposed; i++)
                {
                    if (_isConnected && _isAuthenticated)
                    {
                        break;
                    }

                    await Task.Delay(100).ConfigureAwait(false);
                }

                if (_isConnected && !_disposed)
                {
                    if (await PlayAsync(trackUri, CancellationToken.None).ConfigureAwait(false) && posMs > 1500)
                    {
                        await Task.Delay(350).ConfigureAwait(false);
                        await SeekAsync(posMs, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
            }
            finally
            {
                Interlocked.Exchange(ref _deviceRestartInFlight, 0);
            }
        });
    }

    public void RememberTrackMetadata(IEnumerable<SpotifyTrackItem> tracks)
    {
        lock (_queueLock)
        {
            foreach (var tr in tracks)
            {
                if (string.IsNullOrWhiteSpace(tr.Uri))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(tr.AlbumCoverUrl))
                {
                    if (_knownCoverUrls.Count >= 300)
                    {
                        _knownCoverUrls.Clear();
                    }

                    _knownCoverUrls[tr.Uri] = tr.AlbumCoverUrl!;
                }

                if (tr.DurationMs > 0)
                {
                    if (_knownDurationsMs.Count >= 300)
                    {
                        _knownDurationsMs.Clear();
                    }

                    _knownDurationsMs[tr.Uri] = tr.DurationMs;
                }
            }
        }
    }

    public async Task<bool> PlayTrackListAsync(
        IReadOnlyList<SpotifyTrackItem> tracks,
        int startIndex,
        CancellationToken cancellationToken = default)
    {
        if (tracks.Count == 0)
        {
            return false;
        }

        RememberTrackMetadata(tracks);
        var clampedStart = Math.Clamp(startIndex, 0, tracks.Count - 1);
        var selectedTrack = tracks[clampedStart];

        if (!IsDaemonRunning && HasApiKey)
        {
            var (started, _) = await EnsureDaemonRunningAsync(cancellationToken).ConfigureAwait(false);
            if (!started)
            {
                return false;
            }

            await Task.Delay(450, cancellationToken).ConfigureAwait(false);
        }

        CancelAutoAdvanceTimer();
        var isShuffled = _lastStatus?.ShuffleContext == true;
        SetupActiveListLocked(tracks, clampedStart, isShuffled, selectedTrack.Uri);

        var ok = await PlayAsync(selectedTrack.Uri, cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            lock (_queueLock)
            {
                _activeQueueTracks.Clear();
                _activeShuffleOrder.Clear();
                _activeQueueIndex = -1;
                _manualUserQueue.Clear();
                _currentManualTrackUri = null;
                _pendingTargetUri = null;
            }

            return false;
        }

        PublishLocalQueueSnapshot();
        ScheduleAutoAdvanceTimer();
        return true;
    }

    private bool HasLocalActiveListLocked =>
        _activeShuffleOrder.Count > 0 && _activeQueueTracks.Count > 0 && _activeQueueIndex >= 0;

    private void SetupActiveListLocked(
        IReadOnlyList<SpotifyTrackItem> tracks,
        int startIndex,
        bool shuffled,
        string selectedUri)
    {
        lock (_queueLock)
        {
            _activeQueueTracks = tracks.Where(t => !string.IsNullOrWhiteSpace(t.Uri)).ToList();
            _manualUserQueue.Clear();
            _currentManualTrackUri = null;
            if (_activeQueueTracks.Count == 0)
            {
                _activeShuffleOrder.Clear();
                _activeQueueIndex = -1;
                _pendingTargetUri = null;
                return;
            }

            var targetUri = tracks[Math.Clamp(startIndex, 0, tracks.Count - 1)].Uri;
            var actualStart = _activeQueueTracks.FindIndex(t => string.Equals(t.Uri, targetUri, StringComparison.Ordinal));
            if (actualStart < 0) actualStart = 0;

            _activeShuffleOrder = Enumerable.Range(0, _activeQueueTracks.Count).ToList();
            if (shuffled && _activeQueueTracks.Count > 2)
            {
                // Keep chosen startIndex first, shuffle remaining indices with Fisher-Yates.
                _activeShuffleOrder[0] = actualStart;
                var rest = Enumerable.Range(0, _activeQueueTracks.Count).Where(i => i != actualStart).ToList();
                for (var i = rest.Count - 1; i > 0; i--)
                {
                    var j = Random.Shared.Next(i + 1);
                    (rest[i], rest[j]) = (rest[j], rest[i]);
                }

                for (var i = 0; i < rest.Count; i++)
                {
                    _activeShuffleOrder[i + 1] = rest[i];
                }

                _activeQueueIndex = 0;
            }
            else
            {
                _activeQueueIndex = actualStart;
            }

            _pendingTargetUri = selectedUri;
            _pendingTargetUntilUtc = DateTimeOffset.UtcNow.AddSeconds(3);
        }
    }

    private int NextExpectedOrderIndexLocked()
    {
        if (_activeShuffleOrder.Count == 0 || _activeQueueIndex < 0)
        {
            return -1;
        }

        var nextPos = _activeQueueIndex + 1;
        if (nextPos >= _activeShuffleOrder.Count)
        {
            return _lastStatus?.RepeatContext == true ? 0 : -1;
        }

        return nextPos;
    }

    private bool HasNextInActiveListLocked() =>
        _manualUserQueue.Count > 0 || NextExpectedOrderIndexLocked() >= 0;

    private bool HasNextInActiveList()
    {
        lock (_queueLock)
        {
            return HasNextInActiveListLocked();
        }
    }

    private string? PopNextTargetUriLocked()
    {
        if (_manualUserQueue.Count > 0)
        {
            var nextManual = _manualUserQueue[0];
            _manualUserQueue.RemoveAt(0);
            _currentManualTrackUri = nextManual.Uri;
            return nextManual.Uri;
        }

        var nextPos = NextExpectedOrderIndexLocked();
        if (nextPos >= 0)
        {
            _activeQueueIndex = nextPos;
            _currentManualTrackUri = null;
            return _activeQueueTracks[_activeShuffleOrder[nextPos]].Uri;
        }

        return null;
    }

    private List<SpotifyQueueEntry> BuildLocalQueueEntriesLocked(int limit = 25)
    {
        var entries = new List<SpotifyQueueEntry>();
        if (!HasLocalActiveListLocked || limit <= 0)
        {
            return entries;
        }

        foreach (var manual in _manualUserQueue)
        {
            if (entries.Count >= limit)
            {
                return entries;
            }

            entries.Add(new SpotifyQueueEntry(manual.Uri, "queue", manual));
        }

        var count = _activeShuffleOrder.Count;
        for (var i = _activeQueueIndex + 1; i < count && entries.Count < limit; i++)
        {
            var t = _activeQueueTracks[_activeShuffleOrder[i]];
            entries.Add(new SpotifyQueueEntry(t.Uri, "context", t));
        }

        if (_lastStatus?.RepeatContext == true)
        {
            for (var i = 0; i <= _activeQueueIndex && i < count && entries.Count < limit; i++)
            {
                var t = _activeQueueTracks[_activeShuffleOrder[i]];
                entries.Add(new SpotifyQueueEntry(t.Uri, "context", t));
            }
        }

        return entries;
    }

    private void PublishLocalQueueSnapshot(int limit = 25)
    {
        List<SpotifyQueueEntry>? entries = null;
        lock (_queueLock)
        {
            if (HasLocalActiveListLocked)
            {
                entries = BuildLocalQueueEntriesLocked(limit);
            }
        }

        if (entries is not null)
        {
            QueueUpdated?.Invoke(this, entries);
        }
    }

    private long LookupKnownDurationMsLocked(string? uri) =>
        !string.IsNullOrWhiteSpace(uri) && _knownDurationsMs.TryGetValue(uri, out var d) ? d : 0;

    private void CancelAutoAdvanceTimer()
    {
        var current = Interlocked.Exchange(ref _autoAdvanceCts, null);
        if (current is not null)
        {
            try { current.Cancel(); } catch (ObjectDisposedException) { }
            finally { current.Dispose(); }
        }
    }

    private void ScheduleAutoAdvanceTimer()
    {
        CancelAutoAdvanceTimer();
        var st = _lastStatus;
        if (_disposed || !IsDaemonRunning || !_isClockRunning ||
            st is not { Stopped: false, Paused: false, RepeatTrack: false, Track: not null })
        {
            return;
        }

        long durationMs;
        string expectedUri = st.Track.Uri;
        lock (_queueLock)
        {
            if (!HasLocalActiveListLocked || !HasNextInActiveListLocked())
            {
                return;
            }

            durationMs = st.Track.DurationMs > 0
                ? st.Track.DurationMs
                : LookupKnownDurationMsLocked(expectedUri);
        }

        if (durationMs <= 2000 || string.IsNullOrWhiteSpace(expectedUri))
        {
            return;
        }

        var remainingMs = durationMs - GetEstimatedPositionMs();
        var delayMs = (int)Math.Clamp(remainingMs - 100, 150, 3_600_000);
        var cts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _autoAdvanceCts, cts);
        if (prev is not null)
        {
            try { prev.Cancel(); } catch { }
            prev.Dispose();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested || _disposed || !_isClockRunning)
                {
                    return;
                }

                var cur = _lastStatus;
                if (cur is not { Stopped: false, Paused: false, RepeatTrack: false, Track: not null } ||
                    !string.Equals(cur.Track.Uri, expectedUri, StringComparison.Ordinal))
                {
                    return;
                }

                if (GetEstimatedPositionMs() < durationMs - 1500)
                {
                    return;
                }

                await AdvanceToNextInActiveListAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        });
    }

    private async Task<bool> AdvanceToNextInActiveListAsync(CancellationToken cancellationToken)
    {
        if (_disposed || Interlocked.CompareExchange(ref _autoAdvanceInFlight, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            string? targetUri = null;
            lock (_queueLock)
            {
                if (HasLocalActiveListLocked)
                {
                    targetUri = PopNextTargetUriLocked();
                }

                if (!string.IsNullOrWhiteSpace(targetUri))
                {
                    _pendingTargetUri = targetUri;
                    _pendingTargetUntilUtc = DateTimeOffset.UtcNow.AddSeconds(4);
                }
            }

            if (string.IsNullOrWhiteSpace(targetUri))
            {
                return false;
            }

            CancelAutoAdvanceTimer();
            var ok = await PlayAsync(targetUri, cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                PublishLocalQueueSnapshot();
                ScheduleAutoAdvanceTimer();
            }

            return ok;
        }
        finally
        {
            Interlocked.Exchange(ref _autoAdvanceInFlight, 0);
        }
    }

    public async Task<bool> PlayAsync(string? uri = null, CancellationToken cancellationToken = default)
    {
        if (!_isDeviceActive)
        {
            await SendCommandAsync(new { type = "command", command = "activate" }, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < 15 && !_isDeviceActive && !cancellationToken.IsCancellationRequested; i++)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        var ok = string.IsNullOrWhiteSpace(uri)
            ? await SendCommandAsync(new { type = "command", command = "play" }, cancellationToken).ConfigureAwait(false)
            : await SendCommandAsync(new { type = "command", command = "play", uri }, cancellationToken).ConfigureAwait(false);

        if (!ok)
        {
            await SendCommandAsync(new { type = "command", command = "activate" }, cancellationToken).ConfigureAwait(false);
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            ok = string.IsNullOrWhiteSpace(uri)
                ? await SendCommandAsync(new { type = "command", command = "play" }, cancellationToken).ConfigureAwait(false)
                : await SendCommandAsync(new { type = "command", command = "play", uri }, cancellationToken).ConfigureAwait(false);
        }

        return ok;
    }

    public Task<bool> PauseAsync(CancellationToken cancellationToken = default)
    {
        _lastUserPauseUtc = DateTimeOffset.UtcNow;
        CancelAutoAdvanceTimer();
        return SendCommandAsync(new { type = "command", command = "pause" }, cancellationToken);
    }

    public Task<bool> PlayPauseAsync(CancellationToken cancellationToken = default)
    {
        var isPlaying = _lastStatus is { Stopped: false, Paused: false };
        return isPlaying ? PauseAsync(cancellationToken) : PlayAsync(uri: null, cancellationToken);
    }

    public async Task<bool> NextAsync(CancellationToken cancellationToken = default)
    {
        CancelAutoAdvanceTimer();
        string? targetUri = null;
        var hasLocal = false;

        lock (_queueLock)
        {
            hasLocal = HasLocalActiveListLocked;
            if (hasLocal)
            {
                targetUri = PopNextTargetUriLocked();
                if (!string.IsNullOrWhiteSpace(targetUri))
                {
                    _pendingTargetUri = targetUri;
                    _pendingTargetUntilUtc = DateTimeOffset.UtcNow.AddSeconds(4);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(targetUri))
        {
            var ok = await PlayAsync(targetUri, cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                PublishLocalQueueSnapshot();
                ScheduleAutoAdvanceTimer();
            }

            return ok;
        }

        if (hasLocal)
        {
            PublishLocalQueueSnapshot();
            return false;
        }

        return await SendCommandAsync(new { type = "command", command = "skip_next" }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> PrevAsync(CancellationToken cancellationToken = default)
    {
        CancelAutoAdvanceTimer();
        string? targetUri = null;
        var hasLocal = false;

        lock (_queueLock)
        {
            hasLocal = HasLocalActiveListLocked;
        }

        if (hasLocal)
        {
            if (GetEstimatedPositionMs() <= 4000)
            {
                lock (_queueLock)
                {
                    if (HasLocalActiveListLocked)
                    {
                        if (_currentManualTrackUri is not null)
                        {
                            // Return from the manually queued track back to the parked playlist track.
                            _currentManualTrackUri = null;
                            targetUri = _activeQueueTracks[_activeShuffleOrder[_activeQueueIndex]].Uri;
                        }
                        else if (_activeQueueIndex > 0)
                        {
                            _activeQueueIndex--;
                            targetUri = _activeQueueTracks[_activeShuffleOrder[_activeQueueIndex]].Uri;
                        }

                        if (!string.IsNullOrWhiteSpace(targetUri))
                        {
                            _pendingTargetUri = targetUri;
                            _pendingTargetUntilUtc = DateTimeOffset.UtcNow.AddSeconds(4);
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(targetUri))
            {
                var ok = await PlayAsync(targetUri, cancellationToken).ConfigureAwait(false);
                if (ok)
                {
                    PublishLocalQueueSnapshot();
                    ScheduleAutoAdvanceTimer();
                }

                return ok;
            }

            return await SeekAsync(0, cancellationToken).ConfigureAwait(false);
        }

        return await SendCommandAsync(new { type = "command", command = "skip_prev" }, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> SeekAsync(long positionMs, CancellationToken cancellationToken = default)
    {
        var clamped = Math.Max(0, positionMs);
        _lastSeekIssuedUtc = DateTimeOffset.UtcNow;
        SetPositionAnchor(clamped, _isClockRunning);
        if (_lastStatus is { Track: not null } st)
        {
            _lastStatus = st with { Track = st.Track with { PositionMs = clamped } };
        }

        ScheduleAutoAdvanceTimer();
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

    public Task<bool> AddToQueueAsync(string trackUri, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackUri))
        {
            return Task.FromResult(false);
        }

        var trackId = trackUri.Split(':').LastOrDefault() ?? string.Empty;
        return AddToQueueAsync(
            new SpotifyTrackItem(trackId, trackUri, trackId, string.Empty, string.Empty, null, 0),
            cancellationToken);
    }

    public async Task<bool> AddToQueueAsync(SpotifyTrackItem track, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(track.Uri))
        {
            return false;
        }

        RememberTrackMetadata([track]);

        var handledLocally = false;
        lock (_queueLock)
        {
            if (HasLocalActiveListLocked)
            {
                if (_manualUserQueue.Count < 50)
                {
                    _manualUserQueue.Add(track);
                }

                handledLocally = true;
            }
        }

        if (handledLocally)
        {
            PublishLocalQueueSnapshot();
            ScheduleAutoAdvanceTimer();
            return true;
        }

        return await SendCommandAsync(new { type = "command", command = "add_to_queue", uri = track.Uri }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> PlayFromQueueAsync(SpotifyQueueEntry entry, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entry.Uri))
        {
            return false;
        }

        string? matchedUri = null;
        var hasLocal = false;

        lock (_queueLock)
        {
            hasLocal = HasLocalActiveListLocked;
            if (hasLocal)
            {
                if (string.Equals(entry.Provider, "queue", StringComparison.OrdinalIgnoreCase))
                {
                    var qIdx = _manualUserQueue.FindIndex(t => string.Equals(t.Uri, entry.Uri, StringComparison.Ordinal));
                    if (qIdx >= 0)
                    {
                        var target = _manualUserQueue[qIdx];
                        _manualUserQueue.RemoveRange(0, qIdx + 1);
                        _currentManualTrackUri = target.Uri;
                        matchedUri = target.Uri;
                    }
                }
                else
                {
                    var count = _activeShuffleOrder.Count;
                    var foundOrderIdx = -1;
                    for (var i = _activeQueueIndex + 1; i < count; i++)
                    {
                        if (string.Equals(_activeQueueTracks[_activeShuffleOrder[i]].Uri, entry.Uri, StringComparison.Ordinal))
                        {
                            foundOrderIdx = i;
                            break;
                        }
                    }

                    if (foundOrderIdx < 0)
                    {
                        for (var i = 0; i <= _activeQueueIndex && i < count; i++)
                        {
                            if (string.Equals(_activeQueueTracks[_activeShuffleOrder[i]].Uri, entry.Uri, StringComparison.Ordinal))
                            {
                                foundOrderIdx = i;
                                break;
                            }
                        }
                    }

                    if (foundOrderIdx >= 0)
                    {
                        _activeQueueIndex = foundOrderIdx;
                        _currentManualTrackUri = null;
                        matchedUri = entry.Uri;
                    }
                }

                if (!string.IsNullOrWhiteSpace(matchedUri))
                {
                    _pendingTargetUri = matchedUri;
                    _pendingTargetUntilUtc = DateTimeOffset.UtcNow.AddSeconds(4);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(matchedUri))
        {
            CancelAutoAdvanceTimer();
            var ok = await PlayAsync(matchedUri, cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                PublishLocalQueueSnapshot();
                ScheduleAutoAdvanceTimer();
            }

            return ok;
        }

        return await PlayAsync(entry.Uri, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> ActivateAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync(new { type = "command", command = "activate" }, cancellationToken);

    public Task<bool> RequestStateAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync(new { type = "command", command = "get_state" }, cancellationToken);

    public Task<bool> RequestQueueAsync(int limit = 25, CancellationToken cancellationToken = default)
    {
        List<SpotifyQueueEntry>? local = null;
        lock (_queueLock)
        {
            if (HasLocalActiveListLocked)
            {
                local = BuildLocalQueueEntriesLocked(limit);
            }
        }

        if (local is not null)
        {
            QueueUpdated?.Invoke(this, local);
            return Task.FromResult(true);
        }

        return SendCommandAsync(new { type = "command", command = "get_queue", limit }, cancellationToken);
    }

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
                if (QueueUpdated is not null)
                {
                    await SendCommandAsync(new { type = "command", command = "get_queue", limit = 25 }, ct).ConfigureAwait(false);
                }

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

                    var payloadLen = (int)ms.Length;
                    HandleWebSocketMessage(ms.GetBuffer().AsMemory(0, payloadLen));
                    if (ms.Capacity > 128 * 1024)
                    {
                        ms.SetLength(0);
                        ms.Capacity = 0;
                    }
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

    private void HandleWebSocketMessage(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            using var doc = JsonDocument.Parse(utf8Json);
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
                    var previousTrack = _lastStatus!.Track;
                    var previousPosMs = GetEstimatedPositionMs();
                    var prevUri = previousTrack?.Uri;
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
                    SyncActiveListOnTrackChanged(track?.Uri, previousTrack, previousPosMs);
                    StatusChanged?.Invoke(this, _lastStatus);
                    if (QueueUpdated is not null)
                    {
                        _ = RequestQueueAsync(25);
                    }
                    break;
                }
                case "playback_changed":
                {
                    EnsureStatusInitialized();
                    var wasPlaying = !_lastStatus!.Paused && !_lastStatus.Stopped;
                    var prevTrack = _lastStatus.Track;
                    var prevPosMs = GetEstimatedPositionMs();
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

                    var curTrack = _lastStatus.Track;
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

                    long prevDur = 0;
                    if (prevTrack is not null)
                    {
                        if (prevTrack.DurationMs > 0)
                        {
                            prevDur = prevTrack.DurationMs;
                        }
                        else
                        {
                            lock (_queueLock)
                            {
                                prevDur = LookupKnownDurationMsLocked(prevTrack.Uri);
                            }
                        }
                    }

                    var endedNaturally =
                        (stopped || (paused && prevDur > 3000 && prevPosMs >= prevDur - 1800)) &&
                        wasPlaying &&
                        _lastStatus.RepeatTrack != true &&
                        (DateTimeOffset.UtcNow - _lastUserPauseUtc).TotalMilliseconds > 1500;

                    if (endedNaturally && HasNextInActiveList())
                    {
                        CancelAutoAdvanceTimer();
                        _ = AdvanceToNextInActiveListAsync(CancellationToken.None);
                    }
                    else if (!stopped && !paused && curTrack is not null)
                    {
                        ScheduleAutoAdvanceTimer();
                    }
                    else
                    {
                        CancelAutoAdvanceTimer();
                    }
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
                    if (nowPlaying)
                    {
                        ScheduleAutoAdvanceTimer();
                    }
                    else
                    {
                        CancelAutoAdvanceTimer();
                    }
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
                    var prevShuffle = _lastStatus!.ShuffleContext;
                    var prevRepeatCtx = _lastStatus.RepeatContext;
                    var prevRepeatTrk = _lastStatus.RepeatTrack;
                    var shuffle = prevShuffle;
                    var repeatCtx = prevRepeatCtx;
                    var repeatTrk = prevRepeatTrk;
                    ApplyOptionsElement(root, ref shuffle, ref repeatCtx, ref repeatTrk);

                    _lastStatus = _lastStatus with
                    {
                        ShuffleContext = shuffle,
                        RepeatContext = repeatCtx,
                        RepeatTrack = repeatTrk,
                        Track = StampCurrentTrackPosition(_lastStatus.Track),
                        HasAuthoritativePosition = false
                    };
                    if (prevShuffle != shuffle)
                    {
                        RebuildActiveListShuffle(shuffle, _lastStatus.Track?.Uri);
                    }
                    else if (prevRepeatCtx != repeatCtx || prevRepeatTrk != repeatTrk)
                    {
                        if (prevRepeatCtx != repeatCtx)
                        {
                            PublishLocalQueueSnapshot();
                        }

                        ScheduleAutoAdvanceTimer();
                    }
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
                    if (QueueUpdated is not null)
                    {
                        _ = RequestQueueAsync(25);
                    }
                    break;
                }
                case "queue_changed":
                {
                    var hasLocal = false;
                    lock (_queueLock)
                    {
                        hasLocal = HasLocalActiveListLocked;
                    }

                    if (hasLocal)
                    {
                        PublishLocalQueueSnapshot();
                    }
                    else
                    {
                        var entries = ParseQueuePayload(root);
                        QueueUpdated?.Invoke(this, entries);
                    }
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
        if (!stopped && !paused && track is not null)
        {
            ScheduleAutoAdvanceTimer();
        }
        else
        {
            CancelAutoAdvanceTimer();
        }

        if (QueueUpdated is not null)
        {
            _ = RequestQueueAsync(25);
        }
    }

    private void SyncActiveListOnTrackChanged(string? newUri, GoLibrespotTrack? previousTrack, long previousPosMs)
    {
        if (string.IsNullOrWhiteSpace(newUri))
        {
            CancelAutoAdvanceTimer();
            return;
        }

        var shouldSchedule = false;
        var shouldCancel = false;
        var shouldPublishQueue = false;
        var overrideWithNext = false;

        lock (_queueLock)
        {
            if (_activeShuffleOrder.Count == 0 || _activeQueueTracks.Count == 0)
            {
                return;
            }

            if (_pendingTargetUri is not null && DateTimeOffset.UtcNow <= _pendingTargetUntilUtc)
            {
                if (string.Equals(newUri, _pendingTargetUri, StringComparison.Ordinal))
                {
                    _pendingTargetUri = null;
                    shouldSchedule = true;
                }
                else
                {
                    // Ignore stale track_changed notifications from prior tracks during rapid skips.
                    return;
                }
            }
            else if (_currentManualTrackUri is not null &&
                     string.Equals(_currentManualTrackUri, newUri, StringComparison.Ordinal))
            {
                // Duplicate track_changed metadata refresh while our queued track is still playing.
                shouldSchedule = true;
            }
            else if (_manualUserQueue.Count > 0 &&
                     string.Equals(_manualUserQueue[0].Uri, newUri, StringComparison.Ordinal))
            {
                // User-queued track started naturally; mark it active without moving playlist index.
                _manualUserQueue.RemoveAt(0);
                _currentManualTrackUri = newUri;
                shouldSchedule = true;
                shouldPublishQueue = true;
            }
            else
            {
                var currentOrderTrack = (_activeQueueIndex >= 0 && _activeQueueIndex < _activeShuffleOrder.Count)
                    ? _activeQueueTracks[_activeShuffleOrder[_activeQueueIndex]].Uri
                    : null;
                if (_currentManualTrackUri is null && string.Equals(currentOrderTrack, newUri, StringComparison.Ordinal))
                {
                    shouldSchedule = true;
                }
                else
                {
                    var foundOrderIdx = -1;
                    for (var i = 0; i < _activeShuffleOrder.Count; i++)
                    {
                        if (string.Equals(_activeQueueTracks[_activeShuffleOrder[i]].Uri, newUri, StringComparison.Ordinal))
                        {
                            foundOrderIdx = i;
                            break;
                        }
                    }

                    var prevDur = previousTrack is not null
                        ? (previousTrack.DurationMs > 0 ? previousTrack.DurationMs : LookupKnownDurationMsLocked(previousTrack.Uri))
                        : 0;
                    var endedNaturally = prevDur > 3000 && previousPosMs >= prevDur - 6000 && _lastStatus?.RepeatTrack != true;
                    var hasNext = HasNextInActiveListLocked();

                    if (endedNaturally && hasNext)
                    {
                        // Spotify Connect auto-started an Autoplay/wrong track at song end; enforce our queue/list.
                        overrideWithNext = true;
                    }
                    else if (_currentManualTrackUri is null && foundOrderIdx >= 0)
                    {
                        _activeQueueIndex = foundOrderIdx;
                        shouldSchedule = true;
                        shouldPublishQueue = true;
                    }
                    else
                    {
                        // External playback switched away mid-track; yield cleanly.
                        _activeQueueTracks.Clear();
                        _activeShuffleOrder.Clear();
                        _activeQueueIndex = -1;
                        _manualUserQueue.Clear();
                        _currentManualTrackUri = null;
                        _pendingTargetUri = null;
                        shouldCancel = true;
                    }
                }
            }
        }

        if (shouldPublishQueue)
        {
            PublishLocalQueueSnapshot();
        }

        if (overrideWithNext)
        {
            CancelAutoAdvanceTimer();
            _ = AdvanceToNextInActiveListAsync(CancellationToken.None);
        }
        else if (shouldCancel)
        {
            CancelAutoAdvanceTimer();
        }
        else if (shouldSchedule)
        {
            ScheduleAutoAdvanceTimer();
        }
    }

    private void RebuildActiveListShuffle(bool shuffled, string? currentTrackUri)
    {
        lock (_queueLock)
        {
            if (_activeQueueTracks.Count == 0)
            {
                return;
            }

            var effectiveUri = _currentManualTrackUri is null ? currentTrackUri : null;
            var anchorIdx = !string.IsNullOrWhiteSpace(effectiveUri)
                ? _activeQueueTracks.FindIndex(t => string.Equals(t.Uri, effectiveUri, StringComparison.Ordinal))
                : (_activeQueueIndex >= 0 && _activeQueueIndex < _activeShuffleOrder.Count ? _activeShuffleOrder[_activeQueueIndex] : 0);
            if (anchorIdx < 0) anchorIdx = 0;

            _activeShuffleOrder = Enumerable.Range(0, _activeQueueTracks.Count).ToList();
            if (shuffled && _activeQueueTracks.Count > 2)
            {
                _activeShuffleOrder[0] = anchorIdx;
                var rest = Enumerable.Range(0, _activeQueueTracks.Count).Where(i => i != anchorIdx).ToList();
                for (var i = rest.Count - 1; i > 0; i--)
                {
                    var j = Random.Shared.Next(i + 1);
                    (rest[i], rest[j]) = (rest[j], rest[i]);
                }

                for (var i = 0; i < rest.Count; i++)
                {
                    _activeShuffleOrder[i + 1] = rest[i];
                }

                _activeQueueIndex = 0;
            }
            else
            {
                _activeQueueIndex = anchorIdx;
            }
        }

        PublishLocalQueueSnapshot();
        ScheduleAutoAdvanceTimer();
    }

    public long GetEstimatedPositionMs()
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

    private GoLibrespotTrack? MergeTrackMetadata(GoLibrespotTrack? previous, GoLibrespotTrack? next)
    {
        if (next is null)
        {
            return null;
        }

        string? knownCover = null;
        long knownDur = 0;
        if (!string.IsNullOrWhiteSpace(next.Uri) &&
            (string.IsNullOrWhiteSpace(next.AlbumCoverUrl) || next.DurationMs <= 0))
        {
            lock (_queueLock)
            {
                if (string.IsNullOrWhiteSpace(next.AlbumCoverUrl))
                {
                    _knownCoverUrls.TryGetValue(next.Uri, out knownCover);
                }

                if (next.DurationMs <= 0)
                {
                    _knownDurationsMs.TryGetValue(next.Uri, out knownDur);
                }
            }
        }

        if (previous is null || !string.Equals(previous.Uri, next.Uri, StringComparison.Ordinal))
        {
            return next with
            {
                AlbumCoverUrl = !string.IsNullOrWhiteSpace(next.AlbumCoverUrl) ? next.AlbumCoverUrl : knownCover,
                DurationMs = next.DurationMs > 0 ? next.DurationMs : knownDur
            };
        }

        return next with
        {
            Name = string.IsNullOrWhiteSpace(next.Name) || next.Name == "Unknown Track" ? previous.Name : next.Name,
            ArtistNames = next.ArtistNames.Count > 0 ? next.ArtistNames : previous.ArtistNames,
            AlbumName = string.IsNullOrWhiteSpace(next.AlbumName) ? previous.AlbumName : next.AlbumName,
            AlbumCoverUrl = !string.IsNullOrWhiteSpace(next.AlbumCoverUrl)
                ? next.AlbumCoverUrl
                : (!string.IsNullOrWhiteSpace(previous.AlbumCoverUrl) ? previous.AlbumCoverUrl : knownCover),
            DurationMs = next.DurationMs > 0 ? next.DurationMs : (previous.DurationMs > 0 ? previous.DurationMs : knownDur),
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
                    _lastStderrLine = line;
                    _actionLog.Log("Warning", $"soloist: {line}");
                }
                else if (isError && !string.IsNullOrWhiteSpace(line))
                {
                    _lastStderrLine = line;
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

