using System.Net.WebSockets;
using System.Text;
using REM.Models;

namespace REM.Services;

/// <summary>
/// Lightweight IRC-over-WebSocket Twitch chat client using built-in ClientWebSocket.
/// Supports authenticated Read/Send via OAuth or anonymous read-only fallback.
/// </summary>
public sealed class TwitchChatService : IDisposable
{
    private static readonly Uri TwitchIrcEndpoint = new("wss://irc-ws.chat.twitch.tv:443");

    private readonly ActionLogService _actionLog;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _sessionCts;
    private Task? _receiveLoopTask;
    private string? _channel;
    private bool _canSend;
    private string? _authenticatedDisplayName;
    private string? _authenticatedIrcLogin;
    private bool _disposed;

    public TwitchChatService(ActionLogService actionLog)
    {
        _actionLog = actionLog;
    }

    public bool IsConnected { get; private set; }
    public bool CanSend => IsConnected && _canSend;
    public string? ActiveChannel => _channel;

    public event EventHandler<RawTwitchChatMessage>? MessageReceived;
    public event EventHandler<string>? SystemNoticeReceived;
    public event EventHandler<(bool IsConnected, bool CanSend, string StatusText)>? ConnectionStateChanged;

    public async Task ConnectAsync(
        string channelLogin,
        string? oauthToken,
        string? ircLogin,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var normalizedChannel = channelLogin.Trim().TrimStart('#').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedChannel))
        {
            return;
        }

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisconnectInternalLockedAsync().ConfigureAwait(false);

            var cleanToken = string.IsNullOrWhiteSpace(oauthToken) ? null : oauthToken.Trim();
            if (!string.IsNullOrWhiteSpace(cleanToken) &&
                cleanToken.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase))
            {
                cleanToken = cleanToken["oauth:".Length..];
            }

            var cleanLogin = string.IsNullOrWhiteSpace(ircLogin) ? null : ircLogin.Trim().ToLowerInvariant();
            var hasAuth = !string.IsNullOrWhiteSpace(cleanToken) && !string.IsNullOrWhiteSpace(cleanLogin);

            _channel = normalizedChannel;
            _canSend = hasAuth;
            _authenticatedIrcLogin = cleanLogin;
            _authenticatedDisplayName = string.IsNullOrWhiteSpace(displayName) ? cleanLogin : displayName.Trim();

            var cts = new CancellationTokenSource();
            _sessionCts = cts;

            var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(25);
            _socket = ws;

            ConnectionStateChanged?.Invoke(this, (false, false, $"Connecting to #{normalizedChannel}..."));

            await ws.ConnectAsync(TwitchIrcEndpoint, cancellationToken).ConfigureAwait(false);

            await SendRawLineLockedAsync(ws, "CAP REQ :twitch.tv/tags twitch.tv/commands", cancellationToken).ConfigureAwait(false);
            if (hasAuth)
            {
                await SendRawLineLockedAsync(ws, $"PASS oauth:{cleanToken}", cancellationToken).ConfigureAwait(false);
                await SendRawLineLockedAsync(ws, $"NICK {cleanLogin}", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SendRawLineLockedAsync(ws, $"NICK justinfan{Random.Shared.Next(10000, 99999)}", cancellationToken).ConfigureAwait(false);
            }

            await SendRawLineLockedAsync(ws, $"JOIN #{normalizedChannel}", cancellationToken).ConfigureAwait(false);

            IsConnected = true;
            var status = hasAuth
                ? $"#{normalizedChannel} (Connected)"
                : $"#{normalizedChannel} (Read-only)";
            ConnectionStateChanged?.Invoke(this, (true, _canSend, status));

            _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(ws, normalizedChannel, cts.Token), cts.Token);
            _actionLog.Log("TwitchChat", $"Joined #{normalizedChannel} chat ({(hasAuth ? "authenticated" : "anonymous")}).");
        }
        catch (OperationCanceledException)
        {
            IsConnected = false;
            _canSend = false;
            ConnectionStateChanged?.Invoke(this, (false, false, "Chat disconnected"));
        }
        catch (Exception ex)
        {
            IsConnected = false;
            _canSend = false;
            ConnectionStateChanged?.Invoke(this, (false, false, "Chat connection failed"));
            SystemNoticeReceived?.Invoke(this, $"Could not connect to #{normalizedChannel} chat: {ex.Message}");
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task<bool> SendMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_disposed || !IsConnected || !_canSend || _socket is not { State: WebSocketState.Open } ws || string.IsNullOrWhiteSpace(_channel))
        {
            return false;
        }

        var cleaned = text.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        if (string.IsNullOrEmpty(cleaned))
        {
            return false;
        }

        if (cleaned.Length > 500)
        {
            cleaned = cleaned[..500];
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendRawLineLockedAsync(ws, $"PRIVMSG #{_channel} :{cleaned}", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }

        // Twitch IRC does not echo our own PRIVMSG back over the socket, so emit locally.
        var selfName = _authenticatedDisplayName ?? _authenticatedIrcLogin ?? "You";
        MessageReceived?.Invoke(this, new RawTwitchChatMessage(
            Id: Guid.NewGuid().ToString("N"),
            UserName: selfName,
            ColorHex: "#A970FF",
            BadgeTag: null,
            Text: cleaned,
            TwitchEmotes: []));
        return true;
    }

    public async Task DisconnectAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectInternalLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectGate.Release();
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
            _sessionCts?.Cancel();
        }
        catch
        {
        }

        try
        {
            _socket?.Abort();
            _socket?.Dispose();
        }
        catch
        {
        }

        _sessionCts?.Dispose();
        _connectGate.Dispose();
        _sendGate.Dispose();
    }

    private async Task DisconnectInternalLockedAsync()
    {
        IsConnected = false;
        _canSend = false;
        _channel = null;

        var cts = _sessionCts;
        _sessionCts = null;
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
        }

        var ws = _socket;
        _socket = null;
        if (ws is not null)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", closeTimeout.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                try { ws.Abort(); } catch { }
            }
            finally
            {
                ws.Dispose();
            }
        }

        var loop = _receiveLoopTask;
        _receiveLoopTask = null;
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch { }
        }

        cts?.Dispose();
        ConnectionStateChanged?.Invoke(this, (false, false, "Chat disconnected"));
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, string expectedChannel, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var messageBuffer = new MemoryStream(8192);

        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                messageBuffer.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        IsConnected = false;
                        _canSend = false;
                        ConnectionStateChanged?.Invoke(this, (false, false, $"Disconnected from #{expectedChannel}"));
                        return;
                    }

                    if (result.Count > 0)
                    {
                        BandwidthMonitor.RecordBytes(result.Count);
                        messageBuffer.Write(buffer, 0, result.Count);
                    }
                }
                while (!result.EndOfMessage && !ct.IsCancellationRequested);

                if (ct.IsCancellationRequested || messageBuffer.Length == 0)
                {
                    continue;
                }

                var payload = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                var lines = payload.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    await HandleIrcLineAsync(ws, line, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                IsConnected = false;
                _canSend = false;
                ConnectionStateChanged?.Invoke(this, (false, false, "Chat disconnected"));
                SystemNoticeReceived?.Invoke(this, $"Chat connection lost: {ex.Message}");
            }
        }
    }

    private async Task HandleIrcLineAsync(ClientWebSocket ws, string line, CancellationToken ct)
    {
        if (line.StartsWith("PING", StringComparison.Ordinal))
        {
            var payload = line.Length > 5 ? line[5..] : ":tmi.twitch.tv";
            await _sendGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await SendRawLineLockedAsync(ws, $"PONG {payload}", ct).ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }

            return;
        }

        var cursor = 0;
        string? rawTags = null;
        if (line.StartsWith('@'))
        {
            var spaceIdx = line.IndexOf(' ');
            if (spaceIdx < 0)
            {
                return;
            }

            rawTags = line[1..spaceIdx];
            cursor = spaceIdx + 1;
        }

        if (cursor < line.Length && line[cursor] == ':')
        {
            var spaceIdx = line.IndexOf(' ', cursor);
            if (spaceIdx < 0)
            {
                return;
            }

            cursor = spaceIdx + 1;
        }

        if (cursor >= line.Length)
        {
            return;
        }

        var remainder = line[cursor..];
        if (remainder.StartsWith("PRIVMSG ", StringComparison.Ordinal))
        {
            var msgSep = remainder.IndexOf(" :", StringComparison.Ordinal);
            if (msgSep < 0)
            {
                return;
            }

            var messageText = remainder[(msgSep + 2)..];
            var rawMsg = BuildParsedMessage(rawTags, messageText);
            MessageReceived?.Invoke(this, rawMsg);
        }
        else if (remainder.StartsWith("NOTICE ", StringComparison.Ordinal))
        {
            var msgSep = remainder.IndexOf(" :", StringComparison.Ordinal);
            if (msgSep >= 0)
            {
                var notice = remainder[(msgSep + 2)..].Trim();
                if (!string.IsNullOrWhiteSpace(notice))
                {
                    if (notice.Contains("authentication failed", StringComparison.OrdinalIgnoreCase) ||
                        notice.Contains("Improperly formatted auth", StringComparison.OrdinalIgnoreCase))
                    {
                        _canSend = false;
                        ConnectionStateChanged?.Invoke(this, (true, false, $"#{_channel} (Auth expired — Reconnect Twitch)"));
                    }

                    SystemNoticeReceived?.Invoke(this, notice);
                }
            }
        }
    }

    private static RawTwitchChatMessage BuildParsedMessage(string? rawTags, string messageText)
    {
        var id = Guid.NewGuid().ToString("N");
        var displayName = "Viewer";
        var colorHex = "#9BA8C7";
        string? badgeTag = null;
        IReadOnlyList<TwitchEmoteRange> emotes = [];

        if (!string.IsNullOrWhiteSpace(rawTags))
        {
            foreach (var part in rawTags.Split(';'))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                var key = part[..eq];
                var val = part[(eq + 1)..];
                switch (key)
                {
                    case "id" when !string.IsNullOrWhiteSpace(val):
                        id = val;
                        break;
                    case "display-name" when !string.IsNullOrWhiteSpace(val):
                        displayName = UnescapeTagValue(val);
                        break;
                    case "color" when val.StartsWith('#') && val.Length == 7:
                        colorHex = val;
                        break;
                    case "badges" when !string.IsNullOrWhiteSpace(val):
                        badgeTag = ParsePrimaryBadge(val);
                        break;
                    case "emotes" when !string.IsNullOrWhiteSpace(val):
                        emotes = ParseEmotesTag(val);
                        break;
                }
            }
        }

        return new RawTwitchChatMessage(id, displayName, colorHex, badgeTag, messageText, emotes);
    }

    private static string? ParsePrimaryBadge(string badgesVal)
    {
        if (badgesVal.Contains("broadcaster/", StringComparison.Ordinal)) return "LIVE";
        if (badgesVal.Contains("moderator/", StringComparison.Ordinal)) return "MOD";
        if (badgesVal.Contains("vip/", StringComparison.Ordinal)) return "VIP";
        if (badgesVal.Contains("subscriber/", StringComparison.Ordinal)) return "SUB";
        return null;
    }

    private static IReadOnlyList<TwitchEmoteRange> ParseEmotesTag(string emotesVal)
    {
        var list = new List<TwitchEmoteRange>();
        foreach (var group in emotesVal.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = group.IndexOf(':');
            if (colon <= 0 || colon + 1 >= group.Length)
            {
                continue;
            }

            var emoteId = group[..colon];
            var positions = group[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var pos in positions)
            {
                var dash = pos.IndexOf('-');
                if (dash <= 0)
                {
                    continue;
                }

                if (int.TryParse(pos[..dash], out var start) &&
                    int.TryParse(pos[(dash + 1)..], out var end) &&
                    start >= 0 &&
                    end >= start)
                {
                    list.Add(new TwitchEmoteRange(emoteId, start, end));
                }
            }
        }

        if (list.Count > 1)
        {
            list.Sort((a, b) => a.StartIndex.CompareTo(b.StartIndex));
        }

        return list;
    }

    private static string UnescapeTagValue(string value) =>
        value.Replace("\\s", " ", StringComparison.Ordinal)
             .Replace("\\:", ";", StringComparison.Ordinal)
             .Replace("\\\\", "\\", StringComparison.Ordinal);

    private static async Task SendRawLineLockedAsync(ClientWebSocket ws, string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        BandwidthMonitor.RecordBytes(bytes.Length);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }
}
