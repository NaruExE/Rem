using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using REM.Models;
using REM.Modules;

namespace REM.Services;

/// <summary>
/// Lightweight peer room server/client using raw TcpListener RFC-6455 WebSocket upgrade (no admin needed),
/// optional Cloudflare Quick Tunnel (CGNAT bypass for Egypt), Direct LAN / Radmin IP mode, and VLC playback sync.
/// </summary>
public sealed class WatchPartyService : IDisposable
{
    private sealed class ConnectedPeer
    {
        public required Guid Id { get; init; }
        public required WebSocket Socket { get; init; }
        public required SemaphoreSlim SendLock { get; init; }
        public string Name { get; set; } = "Guest";
        public bool Authenticated { get; set; }
    }

    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const long DriftToleranceMs = 900;
    private const int MaxChatHistory = 150;
    private const int MaxPeers = 12;
    private const int MaxPinFailures = 5;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly VideoPlayerModule _videoPlayerModule;
    private readonly ActionLogService _actionLog;
    private readonly CloudflaredTunnelService _tunnel;
    private readonly object _peersLock = new();
    private readonly List<ConnectedPeer> _peers = [];
    private readonly List<WatchPartyChatMessage> _chatHistory = [];
    private readonly Dictionary<string, (int Count, DateTimeOffset LockedUntilUtc)> _pinFailuresByIp = new(StringComparer.Ordinal);

    private TcpListener? _listener;
    private ClientWebSocket? _clientSocket;
    private SemaphoreSlim? _clientSendLock;
    private CancellationTokenSource? _sessionCts;
    private bool _vlcHooked;

    private WatchPartyRole _role = WatchPartyRole.None;
    private string _nickname = "Host";
    private string _roomPin = string.Empty;
    private string _sessionToken = string.Empty;
    private string _roomUrl = string.Empty;
    private string _statusText = "Not connected";
    private bool _isConnected;

    private long _suppressOutboundUntilUtcTicks;
    private bool _lastPlayingState;
    private float _lastSpeed = 1.0f;
    private string? _lastSourceUrl;
    private long _lastPosMs;
    private DateTimeOffset _lastPosTimestamp = DateTimeOffset.UtcNow;
    private bool _disposed;

    public WatchPartyService(
        VideoPlayerModule videoPlayerModule,
        ActionLogService actionLog)
    {
        _videoPlayerModule = videoPlayerModule;
        _actionLog = actionLog;
        _tunnel = new CloudflaredTunnelService(actionLog);
    }

    public WatchPartyRole Role => _role;
    public bool IsConnected => _isConnected;
    public bool HostOnlyControls { get; set; }
    public string RoomUrl => _roomUrl;
    public string StatusText => _statusText;

    public event EventHandler? StateChanged;
    public event EventHandler<IReadOnlyList<string>>? PeersChanged;
    public event EventHandler<WatchPartyChatMessage>? ChatMessageReceived;
    public event EventHandler<string>? StatusTextChanged;

    public IReadOnlyList<string> GetPeerNames()
    {
        if (!_isConnected)
        {
            return [];
        }

        var list = new List<string>();
        if (_role == WatchPartyRole.Host)
        {
            list.Add($"{_nickname} (Host)");
            lock (_peersLock)
            {
                foreach (var peer in _peers.Where(p => p.Authenticated))
                {
                    list.Add(peer.Name);
                }
            }
        }
        else
        {
            lock (_peersLock)
            {
                list.AddRange(_peers.Select(p => p.Name));
            }
        }

        return list;
    }

    public IReadOnlyList<WatchPartyChatMessage> GetChatHistory()
    {
        lock (_peersLock)
        {
            return _chatHistory.ToList();
        }
    }

    public async Task StartHostAsync(
        string nickname,
        string? pin,
        WatchPartyConnectionMode mode,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await LeaveOrStopAsync().ConfigureAwait(false);

        _nickname = string.IsNullOrWhiteSpace(nickname) ? "Host" : nickname.Trim();
        _roomPin = pin?.Trim() ?? string.Empty;
        _sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        _role = WatchPartyRole.Host;
        lock (_peersLock)
        {
            _pinFailuresByIp.Clear();
        }

        var cts = new CancellationTokenSource();
        _sessionCts = cts;

        try
        {
            var listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            _listener = listener;

            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var lanIp = GetBestLanIpv4();

            SetStatus(mode == WatchPartyConnectionMode.CloudflareTunnel
                ? "Starting local room + Cloudflare tunnel..."
                : $"Hosting on Direct LAN ({lanIp}:{port})");

            _ = Task.Run(() => AcceptLoopAsync(listener, cts.Token), cts.Token);

            if (mode == WatchPartyConnectionMode.CloudflareTunnel)
            {
                var baseWss = await _tunnel.StartTunnelAsync(
                    port,
                    SetStatus,
                    cancellationToken).ConfigureAwait(false);
                _roomUrl = $"{baseWss.TrimEnd('/')}/{_sessionToken}";
                SetStatus($"Hosting via Cloudflare · LAN fallback: ws://{lanIp}:{port}/ws/{_sessionToken}");
            }
            else
            {
                _roomUrl = $"ws://{lanIp}:{port}/ws/{_sessionToken}";
                SetStatus($"Hosting Direct LAN / Radmin on {_roomUrl}");
            }

            _isConnected = true;
            HookVlcEvents();
            _ = Task.Run(() => HostHeartbeatLoopAsync(cts.Token), cts.Token);

            AddSystemChat($"Room started as {_nickname}. Share the link with friends.");
            StateChanged?.Invoke(this, EventArgs.Empty);
            PeersChanged?.Invoke(this, GetPeerNames());
        }
        catch
        {
            await LeaveOrStopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task JoinRoomAsync(
        string roomUrl,
        string nickname,
        string? pin,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var normalized = NormalizeRoomUri(roomUrl);
        await LeaveOrStopAsync().ConfigureAwait(false);

        _nickname = string.IsNullOrWhiteSpace(nickname) ? "Guest" : nickname.Trim();
        _roomPin = pin?.Trim() ?? string.Empty;
        _role = WatchPartyRole.Client;
        _roomUrl = normalized.ToString();

        var cts = new CancellationTokenSource();
        _sessionCts = cts;

        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _clientSocket = socket;
        _clientSendLock = new SemaphoreSlim(1, 1);

        try
        {
            SetStatus($"Connecting to {normalized.Host}...");
            await socket.ConnectAsync(normalized, cancellationToken).ConfigureAwait(false);

            _isConnected = true;
            HookVlcEvents();

            await SendClientPacketAsync(new WatchPartyPacket(
                Type: "join",
                Sender: _nickname,
                Pin: _roomPin,
                SentUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                cts.Token).ConfigureAwait(false);

            SetStatus($"Connected to {normalized.Host}");
            StateChanged?.Invoke(this, EventArgs.Empty);

            _ = Task.Run(() => ClientReceiveLoopAsync(socket, cts.Token), cts.Token);
        }
        catch
        {
            await LeaveOrStopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task SendChatAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!_isConnected || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var trimmed = text.Trim();
        var msg = new WatchPartyChatMessage(_nickname, trimmed, DateTime.Now.ToString("HH:mm"), IsSystem: false);
        var packet = new WatchPartyPacket(
            Type: "chat",
            Sender: _nickname,
            Text: trimmed,
            SentUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        if (_role == WatchPartyRole.Host)
        {
            AppendChat(msg);
            await BroadcastToPeersAsync(packet, excludePeerId: null, cancellationToken).ConfigureAwait(false);
        }
        else if (_role == WatchPartyRole.Client)
        {
            await SendClientPacketAsync(packet, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ShareUrlAsync(string urlOrInput, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(urlOrInput))
        {
            return;
        }

        var trimmed = urlOrInput.Trim();
        SuppressOutboundFor(2000);
        var resolved = await _videoPlayerModule.Engine.PlaySourceAsync(trimmed, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _lastSourceUrl = resolved.OriginalInput;
        _lastPlayingState = true;
        _lastPosMs = 0;
        _lastPosTimestamp = DateTimeOffset.UtcNow;

        if (_isConnected)
        {
            var packet = new WatchPartyPacket(
                Type: "load_url",
                Sender: _nickname,
                PositionMs: 0,
                IsPaused: false,
                Speed: _videoPlayerModule.Engine.PlaybackSpeed,
                MediaUrl: resolved.OriginalInput,
                MediaTitle: resolved.Title,
                SentUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            await DispatchOutboundAsync(packet, cancellationToken).ConfigureAwait(false);
            AddSystemChat($"Synced stream URL: {resolved.Title}");
        }
    }

    public async Task PlayLocalFileInVlcAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        SuppressOutboundFor(2000);
        await _videoPlayerModule.PlayFileInUiAsync(filePath, cancellationToken).ConfigureAwait(false);

        var fileName = Path.GetFileName(filePath);
        _lastSourceUrl = filePath;
        _lastPlayingState = true;
        _lastPosMs = 0;
        _lastPosTimestamp = DateTimeOffset.UtcNow;

        if (_isConnected)
        {
            var packet = new WatchPartyPacket(
                Type: "local_file",
                Sender: _nickname,
                MediaTitle: fileName,
                SentUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await DispatchOutboundAsync(packet, cancellationToken).ConfigureAwait(false);
            AddSystemChat($"Opened local file: {fileName} (friends must open their copy to watch together).");
        }
    }

    public async Task ForceSyncNowAsync(CancellationToken cancellationToken = default)
    {
        if (!_isConnected)
        {
            return;
        }

        var engine = _videoPlayerModule.Engine;
        var src = engine.CurrentSourceUrl;
        if (!string.IsNullOrWhiteSpace(src) && IsHttpUrl(src))
        {
            await DispatchOutboundAsync(new WatchPartyPacket(
                Type: "load_url",
                Sender: _nickname,
                PositionMs: engine.PositionMs,
                IsPaused: !engine.IsPlaying,
                Speed: engine.PlaybackSpeed,
                MediaUrl: src,
                MediaTitle: engine.CurrentTitle,
                SentUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await DispatchOutboundAsync(new WatchPartyPacket(
                Type: "sync",
                Sender: _nickname,
                Action: engine.IsPlaying ? "play" : "pause",
                PositionMs: engine.PositionMs,
                IsPaused: !engine.IsPlaying,
                Speed: engine.PlaybackSpeed,
                SentUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), cancellationToken).ConfigureAwait(false);
        }

        AddSystemChat("Sent manual playback sync to room.");
    }

    public async Task LeaveOrStopAsync()
    {
        var wasConnected = _isConnected || _role != WatchPartyRole.None;

        UnhookVlcEvents();

        var cts = Interlocked.Exchange(ref _sessionCts, null);
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        _tunnel.Stop();

        var listener = Interlocked.Exchange(ref _listener, null);
        if (listener is not null)
        {
            try { listener.Stop(); } catch { }
        }

        List<ConnectedPeer> peersToClose;
        lock (_peersLock)
        {
            peersToClose = _peers.ToList();
            _peers.Clear();
        }

        foreach (var peer in peersToClose)
        {
            try
            {
                if (peer.Socket.State == WebSocketState.Open)
                {
                    await peer.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Room closed", CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
            }

            peer.Socket.Dispose();
            peer.SendLock.Dispose();
        }

        var clientSocket = Interlocked.Exchange(ref _clientSocket, null);
        if (clientSocket is not null)
        {
            try
            {
                if (clientSocket.State == WebSocketState.Open)
                {
                    await clientSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Left room", CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
            }

            clientSocket.Dispose();
        }

        Interlocked.Exchange(ref _clientSendLock, null)?.Dispose();

        _role = WatchPartyRole.None;
        _isConnected = false;
        _roomUrl = string.Empty;

        if (wasConnected)
        {
            SetStatus("Disconnected");
            StateChanged?.Invoke(this, EventArgs.Empty);
            PeersChanged?.Invoke(this, []);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            LeaveOrStopAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        _tunnel.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? tcp = null;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleIncomingTcpAsync(tcp, ct), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                tcp?.Dispose();
                break;
            }
            catch (ObjectDisposedException)
            {
                tcp?.Dispose();
                break;
            }
            catch
            {
                tcp?.Dispose();
            }
        }
    }

    private async Task HandleIncomingTcpAsync(TcpClient tcp, CancellationToken ct)
    {
        NetworkStream? stream = null;
        WebSocket? ws = null;
        ConnectedPeer? peer = null;

        try
        {
            tcp.NoDelay = true;
            stream = tcp.GetStream();

            var headerText = await ReadHttpHeaderAsync(stream, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(headerText))
            {
                tcp.Dispose();
                return;
            }

            var wsKey = ExtractHeaderValue(headerText, "Sec-WebSocket-Key");
            if (string.IsNullOrWhiteSpace(wsKey))
            {
                var body = Encoding.UTF8.GetBytes("REM Watch-Party WebSocket endpoint active.");
                var okHeader = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(okHeader, ct).ConfigureAwait(false);
                await stream.WriteAsync(body, ct).ConfigureAwait(false);
                tcp.Dispose();
                return;
            }

            var reqPath = ExtractRequestPath(headerText);
            var expectedPath = $"/ws/{_sessionToken}";
            if (!string.Equals(reqPath.TrimEnd('/'), expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                var deny = Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(deny, ct).ConfigureAwait(false);
                tcp.Dispose();
                return;
            }

            bool roomFull;
            lock (_peersLock)
            {
                roomFull = _peers.Count >= MaxPeers;
            }

            if (roomFull)
            {
                var fullResp = Encoding.ASCII.GetBytes("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(fullResp, ct).ConfigureAwait(false);
                tcp.Dispose();
                return;
            }

            var remoteIp = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";

            var acceptHash = Convert.ToBase64String(
                SHA1.HashData(Encoding.ASCII.GetBytes(wsKey.Trim() + WebSocketGuid)));

            var handshakeResponse = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {acceptHash}\r\n\r\n");
            await stream.WriteAsync(handshakeResponse, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            ws = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(20));
            peer = new ConnectedPeer
            {
                Id = Guid.NewGuid(),
                Socket = ws,
                SendLock = new SemaphoreSlim(1, 1)
            };

            lock (_peersLock)
            {
                _peers.Add(peer);
            }

            var buffer = new byte[16 * 1024];
            var ms = new MemoryStream();

            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                    if (ms.Length > 256 * 1024)
                    {
                        return;
                    }
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                var packet = JsonSerializer.Deserialize<WatchPartyPacket>(json, JsonOptions);
                if (packet is not null)
                {
                    await HandleHostPacketAsync(peer, remoteIp, packet, ct).ConfigureAwait(false);
                }
            }
        }
        catch
        {
        }
        finally
        {
            if (peer is not null)
            {
                var wasAuth = peer.Authenticated;
                var name = peer.Name;
                lock (_peersLock)
                {
                    _peers.Remove(peer);
                }

                peer.SendLock.Dispose();
                if (wasAuth && _isConnected)
                {
                    AddSystemChat($"{name} left the party.");
                    var names = GetPeerNames().ToList();
                    PeersChanged?.Invoke(this, names);
                    _ = BroadcastToPeersAsync(new WatchPartyPacket(Type: "peer_list", Peers: names), null, CancellationToken.None);
                }
            }

            ws?.Dispose();
            stream?.Dispose();
            tcp.Dispose();
        }
    }

    private static string ExtractRequestPath(string headers)
    {
        var firstEnd = headers.IndexOf("\r\n", StringComparison.Ordinal);
        var line = firstEnd > 0 ? headers[..firstEnd] : headers;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return "/";
        }

        var path = parts[1];
        var qIdx = path.IndexOf('?');
        return qIdx >= 0 ? path[..qIdx] : path;
    }

    private async Task HandleHostPacketAsync(ConnectedPeer sender, string remoteIp, WatchPartyPacket packet, CancellationToken ct)
    {
        if (string.Equals(packet.Type, "join", StringComparison.Ordinal))
        {
            var nowUtc = DateTimeOffset.UtcNow;
            DateTimeOffset lockedUntil = DateTimeOffset.MinValue;
            lock (_peersLock)
            {
                if (_pinFailuresByIp.TryGetValue(remoteIp, out var entry))
                {
                    lockedUntil = entry.LockedUntilUtc;
                }
            }

            if (lockedUntil > nowUtc)
            {
                await SendToPeerAsync(sender, new WatchPartyPacket(Type: "error", Text: "Too many failed PIN attempts. Try again in 60s."), ct).ConfigureAwait(false);
                await sender.Socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Rate limited", ct).ConfigureAwait(false);
                return;
            }

            if (!string.IsNullOrEmpty(_roomPin) &&
                !string.Equals(_roomPin, packet.Pin?.Trim(), StringComparison.Ordinal))
            {
                lock (_peersLock)
                {
                    var prev = _pinFailuresByIp.TryGetValue(remoteIp, out var cur) ? cur.Count : 0;
                    var nextCount = prev + 1;
                    _pinFailuresByIp[remoteIp] = nextCount >= MaxPinFailures
                        ? (nextCount, nowUtc.AddSeconds(60))
                        : (nextCount, DateTimeOffset.MinValue);
                }

                await SendToPeerAsync(sender, new WatchPartyPacket(Type: "error", Text: "Incorrect room PIN."), ct).ConfigureAwait(false);
                await sender.Socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Bad PIN", ct).ConfigureAwait(false);
                return;
            }

            lock (_peersLock)
            {
                _pinFailuresByIp.Remove(remoteIp);
            }

            sender.Name = string.IsNullOrWhiteSpace(packet.Sender) ? "Guest" : packet.Sender.Trim();
            sender.Authenticated = true;

            var names = GetPeerNames().ToList();
            var engine = _videoPlayerModule.Engine;
            var currentSrc = engine.CurrentSourceUrl;
            var welcome = new WatchPartyPacket(
                Type: "welcome",
                Sender: _nickname,
                PositionMs: engine.PositionMs,
                IsPaused: !engine.IsPlaying,
                Speed: engine.PlaybackSpeed,
                MediaUrl: currentSrc is not null && IsHttpUrl(currentSrc) ? currentSrc : null,
                MediaTitle: engine.CurrentTitle,
                Peers: names,
                SentUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            await SendToPeerAsync(sender, welcome, ct).ConfigureAwait(false);
            AddSystemChat($"{sender.Name} joined the party.");
            PeersChanged?.Invoke(this, names);

            await BroadcastToPeersAsync(
                new WatchPartyPacket(Type: "peer_list", Peers: names),
                excludePeerId: sender.Id,
                ct).ConfigureAwait(false);
            return;
        }

        if (!sender.Authenticated)
        {
            return;
        }

        if (string.Equals(packet.Type, "chat", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(packet.Text))
        {
            var msg = new WatchPartyChatMessage(sender.Name, packet.Text.Trim(), DateTime.Now.ToString("HH:mm"), IsSystem: false);
            AppendChat(msg);
            await BroadcastToPeersAsync(packet with { Sender = sender.Name }, sender.Id, ct).ConfigureAwait(false);
            return;
        }

        if (HostOnlyControls)
        {
            await SendToPeerAsync(
                sender,
                new WatchPartyPacket(Type: "chat", Sender: "System", Text: "Host-only playback controls are enabled."),
                ct).ConfigureAwait(false);
            return;
        }

        // Sync or media load packets: apply locally and relay to other guests.
        await ApplyRemoteSyncPacketAsync(packet).ConfigureAwait(false);
        await BroadcastToPeersAsync(packet, sender.Id, ct).ConfigureAwait(false);
    }

    private async Task ClientReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var ms = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        AddSystemChat("Disconnected from room.");
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                    if (ms.Length > 256 * 1024)
                    {
                        return;
                    }
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                var packet = JsonSerializer.Deserialize<WatchPartyPacket>(json, JsonOptions);
                if (packet is not null)
                {
                    await HandleClientPacketAsync(packet).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (_isConnected)
            {
                AddSystemChat($"Connection lost: {ex.Message}");
            }
        }
        finally
        {
            if (_isConnected && !ct.IsCancellationRequested)
            {
                _ = LeaveOrStopAsync();
            }
        }
    }

    private async Task HandleClientPacketAsync(WatchPartyPacket packet)
    {
        switch (packet.Type)
        {
            case "error":
                AddSystemChat($"Room rejected connection: {packet.Text}");
                await LeaveOrStopAsync().ConfigureAwait(false);
                break;

            case "welcome":
                UpdateClientPeerList(packet.Peers);
                AddSystemChat($"Joined room hosted by {packet.Sender ?? "Host"}.");
                if (!string.IsNullOrWhiteSpace(packet.MediaUrl))
                {
                    await ApplyRemoteSyncPacketAsync(packet with { Type = "load_url" }).ConfigureAwait(false);
                }
                else if (!string.IsNullOrWhiteSpace(packet.MediaTitle))
                {
                    AddSystemChat($"Host is playing local file '{packet.MediaTitle}' — open your local copy in VLC to sync.");
                }
                break;

            case "peer_list":
                UpdateClientPeerList(packet.Peers);
                break;

            case "chat":
                if (!string.IsNullOrWhiteSpace(packet.Text))
                {
                    AppendChat(new WatchPartyChatMessage(
                        packet.Sender ?? "Peer",
                        packet.Text.Trim(),
                        DateTime.Now.ToString("HH:mm"),
                        IsSystem: false));
                }
                break;

            default:
                await ApplyRemoteSyncPacketAsync(packet).ConfigureAwait(false);
                break;
        }
    }

    private async Task ApplyRemoteSyncPacketAsync(WatchPartyPacket packet)
    {
        var engine = _videoPlayerModule.Engine;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var latencyMs = !packet.IsPaused && packet.SentUnixMs > 0
            ? Math.Clamp(nowMs - packet.SentUnixMs, 0, 1500)
            : 0;
        var targetPosMs = Math.Max(0, packet.PositionMs + latencyMs);

        if (string.Equals(packet.Type, "local_file", StringComparison.Ordinal))
        {
            AddSystemChat($"{packet.Sender ?? "Peer"} loaded local file '{packet.MediaTitle}' — open your copy in VLC.");
            return;
        }

        if (string.Equals(packet.Type, "load_url", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(packet.MediaUrl) &&
            IsHttpUrl(packet.MediaUrl))
        {
            SuppressOutboundFor(2200);
            _lastSourceUrl = packet.MediaUrl;
            await engine.PlaySourceAsync(
                packet.MediaUrl,
                titleOverride: packet.MediaTitle,
                startPositionMs: targetPosMs).ConfigureAwait(false);
            if (packet.IsPaused)
            {
                await engine.PauseAsync().ConfigureAwait(false);
            }

            if (Math.Abs(engine.PlaybackSpeed - packet.Speed) > 0.05f && packet.Speed >= 0.25f)
            {
                await engine.SetPlaybackSpeedAsync(packet.Speed).ConfigureAwait(false);
            }

            AddSystemChat($"{packet.Sender ?? "Peer"} synced media: {packet.MediaTitle ?? packet.MediaUrl}");
            return;
        }

        if (string.Equals(packet.Type, "sync", StringComparison.Ordinal) ||
            string.Equals(packet.Type, "heartbeat", StringComparison.Ordinal))
        {
            SuppressOutboundFor(1100);

            if (engine.IsInitialized)
            {
                if (packet.IsPaused && engine.IsPlaying)
                {
                    await engine.PauseAsync().ConfigureAwait(false);
                }
                else if (!packet.IsPaused && engine.IsPaused)
                {
                    await engine.ResumeAsync().ConfigureAwait(false);
                }

                if (Math.Abs(engine.PlaybackSpeed - packet.Speed) > 0.05f && packet.Speed >= 0.25f)
                {
                    await engine.SetPlaybackSpeedAsync(packet.Speed).ConfigureAwait(false);
                }

                var drift = Math.Abs(engine.PositionMs - targetPosMs);
                if (drift > DriftToleranceMs)
                {
                    await engine.SeekAsync(targetPosMs).ConfigureAwait(false);
                }
            }

            _lastPlayingState = !packet.IsPaused;
            _lastSpeed = packet.Speed;
            _lastPosMs = targetPosMs;
            _lastPosTimestamp = DateTimeOffset.UtcNow;
        }
    }

    private async Task HostHeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(3000, ct).ConfigureAwait(false);
                if (!_isConnected || _role != WatchPartyRole.Host)
                {
                    continue;
                }

                var engine = _videoPlayerModule.Engine;
                if (!engine.IsPlaying)
                {
                    continue;
                }

                bool hasPeers;
                lock (_peersLock)
                {
                    hasPeers = _peers.Any(p => p.Authenticated);
                }

                if (!hasPeers)
                {
                    continue;
                }

                await BroadcastToPeersAsync(new WatchPartyPacket(
                    Type: "heartbeat",
                    Sender: _nickname,
                    PositionMs: engine.PositionMs,
                    IsPaused: false,
                    Speed: engine.PlaybackSpeed,
                    SentUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    excludePeerId: null,
                    ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void HookVlcEvents()
    {
        if (_vlcHooked)
        {
            return;
        }

        _vlcHooked = true;
        var engine = _videoPlayerModule.Engine;
        _lastPlayingState = engine.IsPlaying;
        _lastSpeed = engine.PlaybackSpeed;
        _lastSourceUrl = engine.CurrentSourceUrl;
        _lastPosMs = engine.PositionMs;
        _lastPosTimestamp = DateTimeOffset.UtcNow;

        engine.PlaybackStateChanged += OnVlcPlaybackStateChanged;
    }

    private void UnhookVlcEvents()
    {
        if (!_vlcHooked)
        {
            return;
        }

        _vlcHooked = false;
        _videoPlayerModule.Engine.PlaybackStateChanged -= OnVlcPlaybackStateChanged;
    }

    private void OnVlcPlaybackStateChanged(object? sender, VideoPlaybackSnapshot snapshot)
    {
        if (!_isConnected || DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _suppressOutboundUntilUtcTicks))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var srcChanged = !string.IsNullOrWhiteSpace(snapshot.CurrentSourceUrl) &&
                         !string.Equals(snapshot.CurrentSourceUrl, _lastSourceUrl, StringComparison.Ordinal);

        if (srcChanged)
        {
            _lastSourceUrl = snapshot.CurrentSourceUrl;
            _lastPlayingState = snapshot.IsPlaying;
            _lastSpeed = snapshot.PlaybackSpeed;
            _lastPosMs = snapshot.PositionMs;
            _lastPosTimestamp = now;

            if (IsHttpUrl(snapshot.CurrentSourceUrl))
            {
                _ = DispatchOutboundAsync(new WatchPartyPacket(
                    Type: "load_url",
                    Sender: _nickname,
                    PositionMs: snapshot.PositionMs,
                    IsPaused: !snapshot.IsPlaying,
                    Speed: snapshot.PlaybackSpeed,
                    MediaUrl: snapshot.CurrentSourceUrl,
                    MediaTitle: snapshot.CurrentTitle,
                    SentUnixMs: now.ToUnixTimeMilliseconds()), CancellationToken.None);
            }
            else
            {
                var fileTitle = snapshot.CurrentTitle ?? Path.GetFileName(snapshot.CurrentSourceUrl) ?? "Local media";
                _ = DispatchOutboundAsync(new WatchPartyPacket(
                    Type: "local_file",
                    Sender: _nickname,
                    MediaTitle: fileTitle,
                    SentUnixMs: now.ToUnixTimeMilliseconds()), CancellationToken.None);
            }

            return;
        }

        var expectedPos = _lastPlayingState
            ? _lastPosMs + (long)((now - _lastPosTimestamp).TotalMilliseconds * _lastSpeed)
            : _lastPosMs;

        var playPauseChanged = snapshot.IsPlaying != _lastPlayingState && (snapshot.IsPlaying || snapshot.IsPaused);
        var speedChanged = Math.Abs(snapshot.PlaybackSpeed - _lastSpeed) > 0.05f;
        var seeked = Math.Abs(snapshot.PositionMs - expectedPos) > 1800;

        _lastPlayingState = snapshot.IsPlaying;
        _lastSpeed = snapshot.PlaybackSpeed;
        _lastPosMs = snapshot.PositionMs;
        _lastPosTimestamp = now;

        if (!playPauseChanged && !speedChanged && !seeked)
        {
            return;
        }

        var action = seeked ? "seek" : playPauseChanged ? (snapshot.IsPlaying ? "play" : "pause") : "speed";
        _ = DispatchOutboundAsync(new WatchPartyPacket(
            Type: "sync",
            Sender: _nickname,
            Action: action,
            PositionMs: snapshot.PositionMs,
            IsPaused: !snapshot.IsPlaying,
            Speed: snapshot.PlaybackSpeed,
            SentUnixMs: now.ToUnixTimeMilliseconds()), CancellationToken.None);
    }

    private Task DispatchOutboundAsync(WatchPartyPacket packet, CancellationToken ct) =>
        _role switch
        {
            WatchPartyRole.Host => BroadcastToPeersAsync(packet, excludePeerId: null, ct),
            WatchPartyRole.Client => SendClientPacketAsync(packet, ct),
            _ => Task.CompletedTask
        };

    private async Task BroadcastToPeersAsync(WatchPartyPacket packet, Guid? excludePeerId, CancellationToken ct)
    {
        List<ConnectedPeer> targets;
        lock (_peersLock)
        {
            targets = _peers
                .Where(p => p.Authenticated && (!excludePeerId.HasValue || p.Id != excludePeerId.Value))
                .ToList();
        }

        if (targets.Count == 0)
        {
            return;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(packet, JsonOptions);
        foreach (var peer in targets)
        {
            try
            {
                if (peer.Socket.State == WebSocketState.Open)
                {
                    await peer.SendLock.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await peer.Socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        peer.SendLock.Release();
                    }
                }
            }
            catch
            {
            }
        }
    }

    private static async Task SendToPeerAsync(ConnectedPeer peer, WatchPartyPacket packet, CancellationToken ct)
    {
        if (peer.Socket.State != WebSocketState.Open)
        {
            return;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(packet, JsonOptions);
        await peer.SendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await peer.Socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            peer.SendLock.Release();
        }
    }

    private async Task SendClientPacketAsync(WatchPartyPacket packet, CancellationToken ct)
    {
        var socket = _clientSocket;
        var gate = _clientSendLock;
        if (socket is null || gate is null || socket.State != WebSocketState.Open)
        {
            return;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(packet, JsonOptions);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private void UpdateClientPeerList(List<string>? names)
    {
        lock (_peersLock)
        {
            _peers.Clear();
            if (names is not null)
            {
                foreach (var n in names)
                {
                    _peers.Add(new ConnectedPeer
                    {
                        Id = Guid.NewGuid(),
                        Socket = null!,
                        SendLock = null!,
                        Name = n,
                        Authenticated = true
                    });
                }
            }
        }

        PeersChanged?.Invoke(this, GetPeerNames());
    }

    private void SuppressOutboundFor(int milliseconds)
    {
        var until = DateTimeOffset.UtcNow.AddMilliseconds(milliseconds).UtcTicks;
        Interlocked.Exchange(ref _suppressOutboundUntilUtcTicks, until);
    }

    private void AppendChat(WatchPartyChatMessage msg)
    {
        lock (_peersLock)
        {
            if (_chatHistory.Count >= MaxChatHistory)
            {
                _chatHistory.RemoveAt(0);
            }

            _chatHistory.Add(msg);
        }

        ChatMessageReceived?.Invoke(this, msg);
    }

    private void AddSystemChat(string text)
    {
        AppendChat(new WatchPartyChatMessage("System", text, DateTime.Now.ToString("HH:mm"), IsSystem: true));
    }

    private void SetStatus(string text)
    {
        _statusText = text;
        StatusTextChanged?.Invoke(this, text);
    }

    private static bool IsHttpUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    private static Uri NormalizeRoomUri(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "wss://" + trimmed["https://".Length..];
        }
        else if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "ws://" + trimmed["http://".Length..];
        }
        else if (!trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
                 !trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Contains("trycloudflare.com", StringComparison.OrdinalIgnoreCase)
                ? "wss://" + trimmed
                : "ws://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Invalid room URL.");
        }

        if (string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/")
        {
            var builder = new UriBuilder(uri) { Path = "/ws" };
            return builder.Uri;
        }

        return uri;
    }

    private static async Task<string?> ReadHttpHeaderAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                return null;
            }

            total += read;
            var str = Encoding.ASCII.GetString(buffer, 0, total);
            var endIdx = str.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (endIdx >= 0)
            {
                return str[..endIdx];
            }
        }

        return null;
    }

    private static string? ExtractHeaderValue(string headers, string headerName)
    {
        var lines = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            if (string.Equals(line[..colon].Trim(), headerName, StringComparison.OrdinalIgnoreCase))
            {
                return line[(colon + 1)..].Trim();
            }
        }

        return null;
    }

    private static string GetBestLanIpv4()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                    .Select(a => new
                    {
                        Ip = a.Address.ToString(),
                        Name = n.Name + " " + n.Description
                    }))
                .ToList();

            var vpn = candidates.FirstOrDefault(c =>
                c.Name.Contains("Radmin", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase));
            if (vpn is not null)
            {
                return vpn.Ip;
            }

            return candidates.FirstOrDefault()?.Ip ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }
}
