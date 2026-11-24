using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using REM.Models;

namespace REM.Services;

public sealed class GoLibrespotService : IDisposable
{
    private const int ApiPort = 3678;
    private static readonly Uri BaseUri = new($"http://127.0.0.1:{ApiPort}/");
    private static readonly Uri EventsWsUri = new($"ws://127.0.0.1:{ApiPort}/events");

    private readonly DatabaseService _database;
    private readonly ActionLogService _actionLog;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private Process? _process;
    private CancellationTokenSource? _wsCts;
    private GoLibrespotStatus? _lastStatus;
    private bool _isConnected;
    private bool _disposed;

    public GoLibrespotService(DatabaseService database, ActionLogService actionLog)
    {
        _database = database;
        _actionLog = actionLog;
        _http = new HttpClient
        {
            BaseAddress = BaseUri,
            Timeout = TimeSpan.FromSeconds(10)
        };

        AppDomain.CurrentDomain.ProcessExit += OnHostProcessExit;
    }

    public GoLibrespotStatus? CurrentStatus => _lastStatus;
    public bool IsDaemonRunning => _process is { HasExited: false };
    public bool IsWebSocketConnected => _isConnected;

    public event EventHandler<GoLibrespotStatus?>? StatusChanged;
    public event EventHandler<GoLibrespotDeviceAuth?>? PairingCodeChanged;
    public event EventHandler? QueueChanged;
    public event EventHandler<string>? PlaybackError;
    public event EventHandler<bool>? ConnectionStateChanged;

    public string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "REM",
        "go-librespot");

    public string? ResolveExecutablePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Tools", "go-librespot.exe"),
            Path.Combine(AppContext.BaseDirectory, "go-librespot.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "REM",
                "Tools",
                "go-librespot.exe")
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
                    var full = Path.Combine(dir.Trim(), "go-librespot.exe");
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
                return (false, "go-librespot.exe was not found in Tools\\go-librespot.exe, next to REM.exe, or in PATH.");
            }

            WriteConfigFile();

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
            psi.ArgumentList.Add("--config_dir");
            psi.ArgumentList.Add(ConfigDirectory);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!proc.Start())
            {
                proc.Dispose();
                return (false, "Could not start go-librespot.exe.");
            }

            _process = proc;
            _ = DrainStreamAsync(proc.StandardOutput, isError: false);
            _ = DrainStreamAsync(proc.StandardError, isError: true);

            _actionLog.Log("Spotify", $"Started go-librespot daemon (PID {proc.Id}).");
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
        _lastStatus = null;

        var proc = Interlocked.Exchange(ref _process, null);
        if (proc is not null)
        {
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

            _actionLog.Log("Spotify", "Stopped go-librespot daemon.");
        }

        ConnectionStateChanged?.Invoke(this, false);
        StatusChanged?.Invoke(this, null);
        PairingCodeChanged?.Invoke(this, null);
    }

    public async Task<GoLibrespotStatus?> GetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var resp = await _http.GetAsync("status", cancellationToken).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NoContent || !resp.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var status = ParseStatus(doc.RootElement);
            _lastStatus = status;
            return status;
        }
        catch
        {
            return null;
        }
    }

    public async Task<GoLibrespotDeviceAuth?> GetAuthCodeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var resp = await _http.GetAsync("auth/code", cancellationToken).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NoContent || !resp.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var url = GetString(doc.RootElement, "url") ?? "https://spotify.com/pair";
            var code = GetString(doc.RootElement, "code") ?? string.Empty;
            var expires = doc.RootElement.TryGetProperty("expires_at", out var exProp) &&
                          DateTimeOffset.TryParse(exProp.GetString(), out var dt)
                ? dt
                : DateTimeOffset.UtcNow.AddMinutes(5);

            return string.IsNullOrWhiteSpace(code) ? null : new GoLibrespotDeviceAuth(url, code, expires);
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<SpotifyQueueEntry>> GetQueueAsync(CancellationToken cancellationToken)
    {
        var entries = new List<SpotifyQueueEntry>();
        try
        {
            using var resp = await _http.GetAsync("player/queue", cancellationToken).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NoContent || !resp.IsSuccessStatusCode)
            {
                return entries;
            }

            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("next_tracks", out var nextTracks) &&
                nextTracks.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in nextTracks.EnumerateArray())
                {
                    var uri = GetString(entry, "uri") ?? string.Empty;
                    var provider = GetString(entry, "provider") ?? "context";
                    SpotifyTrackItem? trackItem = null;
                    if (entry.TryGetProperty("track", out var tr) && tr.ValueKind == JsonValueKind.Object)
                    {
                        var parsed = ParseTrack(tr);
                        if (parsed is not null)
                        {
                            var id = parsed.Uri.Split(':').LastOrDefault() ?? string.Empty;
                            trackItem = new SpotifyTrackItem(
                                id,
                                parsed.Uri,
                                parsed.Name,
                                parsed.ArtistsDisplay,
                                parsed.AlbumName,
                                parsed.AlbumCoverUrl,
                                parsed.DurationMs);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(uri))
                    {
                        entries.Add(new SpotifyQueueEntry(uri, provider, trackItem));
                    }
                }
            }
        }
        catch
        {
            // Return empty queue on 204/error.
        }

        return entries;
    }

    public async Task<List<SpotifyPlaylistItem>> GetNativePlaylistsAsync(CancellationToken cancellationToken)
    {
        var list = new List<SpotifyPlaylistItem>();
        try
        {
            using var resp = await _http.GetAsync("library/playlists?offset=0&limit=50", cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.NoContent)
            {
                return list;
            }

            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in items.EnumerateArray())
                {
                    var uri = GetString(p, "uri") ?? string.Empty;
                    var id = uri.Split(':').LastOrDefault() ?? string.Empty;
                    var name = GetString(p, "name") ?? "Playlist";
                    var desc = GetString(p, "description") ?? string.Empty;
                    var owner = GetString(p, "owner_username") ?? "Spotify";
                    var len = p.TryGetProperty("length", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
                    var imageUrl = GetString(p, "image_url");

                    if (!string.IsNullOrWhiteSpace(uri))
                    {
                        list.Add(new SpotifyPlaylistItem(id, uri, name, desc, owner, len, imageUrl));
                    }
                }
            }
        }
        catch
        {
        }

        return list;
    }

    public async Task<List<SpotifyTrackItem>> GetContextTracksAsync(
        string contextUri,
        CancellationToken cancellationToken)
    {
        var list = new List<SpotifyTrackItem>();
        if (string.IsNullOrWhiteSpace(contextUri))
        {
            return list;
        }

        try
        {
            var url = $"context/tracks?uri={Uri.EscapeDataString(contextUri)}";
            for (var attempt = 0; attempt < 10; attempt++)
            {
                using var resp = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return list;
                }

                var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var ready = doc.RootElement.TryGetProperty("ready", out var r) && r.GetBoolean();
                var length = doc.RootElement.TryGetProperty("length", out var len) ? len.GetInt32() : 0;
                var cached = doc.RootElement.TryGetProperty("cached", out var c) ? c.GetInt32() : 0;

                if (ready && (cached >= Math.Min(length, 20) || attempt >= 5))
                {
                    if (doc.RootElement.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in tracks.EnumerateArray())
                        {
                            var uri = GetString(entry, "uri") ?? string.Empty;
                            if (entry.TryGetProperty("track", out var tr) && tr.ValueKind == JsonValueKind.Object)
                            {
                                var parsed = ParseTrack(tr);
                                if (parsed is not null)
                                {
                                    var id = parsed.Uri.Split(':').LastOrDefault() ?? string.Empty;
                                    list.Add(new SpotifyTrackItem(
                                        id,
                                        parsed.Uri,
                                        parsed.Name,
                                        parsed.ArtistsDisplay,
                                        parsed.AlbumName,
                                        parsed.AlbumCoverUrl,
                                        parsed.DurationMs,
                                        contextUri));
                                }
                            }
                            else if (!string.IsNullOrWhiteSpace(uri))
                            {
                                var id = uri.Split(':').LastOrDefault() ?? uri;
                                list.Add(new SpotifyTrackItem(id, uri, id, "Loading metadata...", string.Empty, null, 0, contextUri));
                            }
                        }
                    }

                    return list;
                }

                await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        return list;
    }

    public async Task<bool?> GetNativeLikedStateAsync(string trackUri, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"library/liked?uris={Uri.EscapeDataString(trackUri)}";
            using var resp = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.NoContent)
            {
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array &&
                items.GetArrayLength() > 0)
            {
                return items[0].TryGetProperty("liked", out var liked) && liked.GetBoolean();
            }
        }
        catch
        {
        }

        return null;
    }

    public async Task<bool> SetNativeLikedStateAsync(string trackUri, bool liked, CancellationToken cancellationToken)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync(
                "library/liked",
                new { uris = new[] { trackUri }, liked },
                cancellationToken).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> PlayAsync(string uri, string? skipToUri = null, CancellationToken cancellationToken = default)
    {
        try
        {
            object payload = string.IsNullOrWhiteSpace(skipToUri)
                ? new { uri }
                : new { uri, skip_to_uri = skipToUri };
            using var resp = await _http.PostAsJsonAsync("player/play", payload, cancellationToken).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public Task<bool> PauseAsync(CancellationToken cancellationToken = default) => PostEmptyAsync("player/pause", cancellationToken);
    public Task<bool> ResumeAsync(CancellationToken cancellationToken = default) => PostEmptyAsync("player/resume", cancellationToken);
    public Task<bool> PlayPauseAsync(CancellationToken cancellationToken = default) => PostEmptyAsync("player/playpause", cancellationToken);
    public Task<bool> NextAsync(CancellationToken cancellationToken = default) => PostJsonAsync("player/next", new { }, cancellationToken);
    public Task<bool> PrevAsync(CancellationToken cancellationToken = default) => PostJsonAsync("player/prev", new { allow_seeking = true }, cancellationToken);
    public Task<bool> SeekAsync(long positionMs, CancellationToken cancellationToken = default) => PostJsonAsync("player/seek", new { position = Math.Max(0, positionMs), relative = false }, cancellationToken);
    public Task<bool> SetVolumeAsync(int volume, CancellationToken cancellationToken = default) => PostJsonAsync("player/volume", new { volume = Math.Clamp(volume, 0, 100), relative = false }, cancellationToken);
    public Task<bool> SetShuffleAsync(bool enabled, CancellationToken cancellationToken = default) => PostJsonAsync("player/shuffle_context", new { shuffle_context = enabled }, cancellationToken);
    public Task<bool> SetRepeatContextAsync(bool enabled, CancellationToken cancellationToken = default) => PostJsonAsync("player/repeat_context", new { repeat_context = enabled }, cancellationToken);
    public Task<bool> SetRepeatTrackAsync(bool enabled, CancellationToken cancellationToken = default) => PostJsonAsync("player/repeat_track", new { repeat_track = enabled }, cancellationToken);
    public Task<bool> AddToQueueAsync(string trackUri, CancellationToken cancellationToken = default) => PostJsonAsync("player/add_to_queue", new { uri = trackUri }, cancellationToken);

    private async Task<bool> PostEmptyAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(string.Empty);
            using var resp = await _http.PostAsync(path, content, cancellationToken).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> PostJsonAsync(string path, object payload, CancellationToken cancellationToken)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync(path, payload, cancellationToken).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private void WriteConfigFile()
    {
        Directory.CreateDirectory(ConfigDirectory);

        var bitrate = int.TryParse(_database.GetSetting(DatabaseService.KeySpotifyBitrate), out var br) && br == 320
            ? 320
            : 160;
        var volume = int.TryParse(_database.GetSetting(DatabaseService.KeySpotifyVolume), out var vol)
            ? Math.Clamp(vol, 0, 100)
            : 80;

        var yaml = $"""
            device_name: "REM"
            device_type: "computer"
            audio_backend: "wasapi"
            bitrate: {bitrate}
            volume_steps: 100
            initial_volume: {volume}
            zeroconf_enabled: false
            credentials:
              type: device_auth
            server:
              enabled: true
              address: "127.0.0.1"
              port: {ApiPort}
            metadata:
              enabled: true
              context_sweep: false
              max_tracks: 200
            """;

        File.WriteAllText(Path.Combine(ConfigDirectory, "config.yml"), yaml, Encoding.UTF8);
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
        while (!ct.IsCancellationRequested && !_disposed && IsDaemonRunning)
        {
            try
            {
                var ready = await WaitForHttpReachableAsync(ct).ConfigureAwait(false);
                if (!ready)
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    continue;
                }

                await PollInitialStateAsync(ct).ConfigureAwait(false);
                await RunWebSocketSessionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                _isConnected = false;
                ConnectionStateChanged?.Invoke(this, false);
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> WaitForHttpReachableAsync(CancellationToken ct)
    {
        for (var i = 0; i < 20 && !ct.IsCancellationRequested && IsDaemonRunning; i++)
        {
            try
            {
                using var resp = await _http.GetAsync(string.Empty, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch
            {
                // Daemon still booting.
            }

            await Task.Delay(300, ct).ConfigureAwait(false);
        }

        return false;
    }

    private async Task PollInitialStateAsync(CancellationToken ct)
    {
        var status = await GetStatusAsync(ct).ConfigureAwait(false);
        if (status is not null)
        {
            PairingCodeChanged?.Invoke(this, null);
            StatusChanged?.Invoke(this, status);
            return;
        }

        var auth = await GetAuthCodeAsync(ct).ConfigureAwait(false);
        if (auth is not null)
        {
            PairingCodeChanged?.Invoke(this, auth);
        }
    }

    private async Task RunWebSocketSessionAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(EventsWsUri, ct).ConfigureAwait(false);

        _isConnected = true;
        ConnectionStateChanged?.Invoke(this, true);

        _ = Task.Run(() => PollPairingWhileUnauthenticatedAsync(ct), ct);

        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _isConnected = false;
                    ConnectionStateChanged?.Invoke(this, false);
                    return;
                }

                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var payload = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            await HandleWebSocketEventAsync(payload, ct).ConfigureAwait(false);
        }
    }

    private async Task PollPairingWhileUnauthenticatedAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _isConnected && _lastStatus is null && IsDaemonRunning)
        {
            var status = await GetStatusAsync(ct).ConfigureAwait(false);
            if (status is not null)
            {
                PairingCodeChanged?.Invoke(this, null);
                StatusChanged?.Invoke(this, status);
                return;
            }

            var auth = await GetAuthCodeAsync(ct).ConfigureAwait(false);
            PairingCodeChanged?.Invoke(this, auth);
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleWebSocketEventAsync(string json, CancellationToken ct)
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

            root.TryGetProperty("data", out var data);

            switch (type)
            {
                case "active":
                case "metadata":
                case "will_play":
                {
                    PairingCodeChanged?.Invoke(this, null);
                    var fresh = await GetStatusAsync(ct).ConfigureAwait(false);
                    if (fresh is not null)
                    {
                        StatusChanged?.Invoke(this, fresh);
                    }
                    break;
                }
                case "inactive":
                case "stopped":
                {
                    if (_lastStatus is not null)
                    {
                        _lastStatus = _lastStatus with { Stopped = true, Paused = false };
                        StatusChanged?.Invoke(this, _lastStatus);
                    }
                    break;
                }
                case "playing":
                {
                    if (_lastStatus is not null)
                    {
                        _lastStatus = _lastStatus with { Stopped = false, Paused = false, Buffering = false };
                        StatusChanged?.Invoke(this, _lastStatus);
                    }
                    else
                    {
                        var fresh = await GetStatusAsync(ct).ConfigureAwait(false);
                        if (fresh is not null)
                        {
                            StatusChanged?.Invoke(this, fresh);
                        }
                    }
                    break;
                }
                case "paused":
                case "not_playing":
                {
                    if (_lastStatus is not null)
                    {
                        _lastStatus = _lastStatus with { Paused = true, Buffering = false };
                        StatusChanged?.Invoke(this, _lastStatus);
                    }
                    break;
                }
                case "seek":
                {
                    if (_lastStatus?.Track is not null &&
                        data.ValueKind == JsonValueKind.Object &&
                        data.TryGetProperty("position", out var posProp) &&
                        posProp.ValueKind == JsonValueKind.Number)
                    {
                        var pos = posProp.GetInt64();
                        var dur = data.TryGetProperty("duration", out var durProp) && durProp.ValueKind == JsonValueKind.Number
                            ? durProp.GetInt64()
                            : _lastStatus.Track.DurationMs;
                        _lastStatus = _lastStatus with
                        {
                            Track = _lastStatus.Track with { PositionMs = pos, DurationMs = dur }
                        };
                        StatusChanged?.Invoke(this, _lastStatus);
                    }
                    break;
                }
                case "volume":
                {
                    if (_lastStatus is not null &&
                        data.ValueKind == JsonValueKind.Object &&
                        data.TryGetProperty("value", out var vProp) &&
                        vProp.ValueKind == JsonValueKind.Number)
                    {
                        var max = data.TryGetProperty("max", out var mProp) && mProp.ValueKind == JsonValueKind.Number
                            ? Math.Max(1, mProp.GetInt32())
                            : _lastStatus.VolumeSteps;
                        _lastStatus = _lastStatus with { Volume = vProp.GetInt32(), VolumeSteps = max };
                        StatusChanged?.Invoke(this, _lastStatus);
                    }
                    break;
                }
                case "shuffle_context":
                {
                    if (_lastStatus is not null && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("value", out var sh))
                    {
                        _lastStatus = _lastStatus with { ShuffleContext = sh.GetBoolean() };
                        StatusChanged?.Invoke(this, _lastStatus);
                    }
                    break;
                }
                case "repeat_context":
                {
                    if (_lastStatus is not null && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("value", out var rc))
                    {
                        _lastStatus = _lastStatus with { RepeatContext = rc.GetBoolean() };
                        StatusChanged?.Invoke(this, _lastStatus);
                    }
                    break;
                }
                case "repeat_track":
                {
                    if (_lastStatus is not null && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("value", out var rt))
                    {
                        _lastStatus = _lastStatus with { RepeatTrack = rt.GetBoolean() };
                        StatusChanged?.Invoke(this, _lastStatus);
                    }
                    break;
                }
                case "queue":
                {
                    QueueChanged?.Invoke(this, EventArgs.Empty);
                    break;
                }
                case "playback_error":
                {
                    var msg = data.ValueKind == JsonValueKind.Object
                        ? GetString(data, "message") ?? "Playback failed in go-librespot."
                        : "Playback failed in go-librespot.";
                    _actionLog.Log("Error", $"go-librespot playback error: {msg}");
                    PlaybackError?.Invoke(this, msg);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Ignore malformed individual event.
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

                if (isError && line.Contains("error", StringComparison.OrdinalIgnoreCase))
                {
                    _actionLog.Log("Warning", $"go-librespot: {line}");
                }
            }
        }
        catch
        {
            // Stream closed cleanly.
        }
    }

    private static GoLibrespotStatus ParseStatus(JsonElement el)
    {
        GoLibrespotTrack? track = null;
        if (el.TryGetProperty("track", out var tr) && tr.ValueKind == JsonValueKind.Object)
        {
            track = ParseTrack(tr);
        }

        return new GoLibrespotStatus(
            Username: GetString(el, "username") ?? string.Empty,
            DeviceId: GetString(el, "device_id") ?? string.Empty,
            DeviceName: GetString(el, "device_name") ?? "REM",
            ContextUri: GetString(el, "context_uri"),
            ContextName: GetString(el, "context_name"),
            Stopped: el.TryGetProperty("stopped", out var st) && st.GetBoolean(),
            Paused: el.TryGetProperty("paused", out var pa) && pa.GetBoolean(),
            Buffering: el.TryGetProperty("buffering", out var bu) && bu.GetBoolean(),
            Volume: el.TryGetProperty("volume", out var vo) && vo.ValueKind == JsonValueKind.Number ? vo.GetInt32() : 80,
            VolumeSteps: el.TryGetProperty("volume_steps", out var vs) && vs.ValueKind == JsonValueKind.Number ? Math.Max(1, vs.GetInt32()) : 100,
            RepeatContext: el.TryGetProperty("repeat_context", out var rc) && rc.GetBoolean(),
            RepeatTrack: el.TryGetProperty("repeat_track", out var rt) && rt.GetBoolean(),
            ShuffleContext: el.TryGetProperty("shuffle_context", out var sc) && sc.GetBoolean(),
            Track: track);
    }

    private static GoLibrespotTrack? ParseTrack(JsonElement el)
    {
        var uri = GetString(el, "uri");
        var name = GetString(el, "name");
        if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var artists = new List<string>();
        if (el.TryGetProperty("artist_names", out var an) && an.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in an.EnumerateArray())
            {
                var v = item.GetString();
                if (!string.IsNullOrWhiteSpace(v))
                {
                    artists.Add(v.Trim());
                }
            }
        }

        return new GoLibrespotTrack(
            Uri: uri,
            Name: name,
            ArtistNames: artists,
            AlbumName: GetString(el, "album_name") ?? string.Empty,
            AlbumCoverUrl: GetString(el, "album_cover_url"),
            PositionMs: el.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Number ? pos.GetInt64() : 0,
            DurationMs: el.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number ? dur.GetInt64() : 0,
            Format: GetString(el, "format"),
            Bitrate: el.TryGetProperty("bitrate", out var br) && br.ValueKind == JsonValueKind.Number ? br.GetInt32() : null);
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= OnHostProcessExit;
        StopDaemon();
        _lifecycleGate.Dispose();
        _http.Dispose();
    }
}

