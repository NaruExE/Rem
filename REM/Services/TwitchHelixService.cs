using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using REM.Models;

namespace REM.Services;

public sealed class TwitchHelixService : IDisposable
{
    private const string RedirectUri = "http://localhost:8080/callback/";
    private const string RedirectUriExact = "http://localhost:8080/callback";

    private readonly DatabaseService _database;
    private readonly ActionLogService _actionLog;
    private readonly HttpClient _http;

    private string? _appAccessToken;
    private DateTimeOffset _appTokenExpiresAt;
    private bool _disposed;

    public TwitchHelixService(DatabaseService database, ActionLogService actionLog)
    {
        _database = database;
        _actionLog = actionLog;
        _http = new HttpClient(new BandwidthCountingHandler())
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public bool HasClientCredentials =>
        !string.IsNullOrWhiteSpace(_database.GetSetting(DatabaseService.KeyTwitchClientId)) &&
        !string.IsNullOrWhiteSpace(_database.GetSetting(DatabaseService.KeyTwitchClientSecret));

    public bool HasUserAuth =>
        !string.IsNullOrWhiteSpace(_database.GetSetting(DatabaseService.KeyTwitchUserAccessToken)) &&
        !string.IsNullOrWhiteSpace(_database.GetSetting(DatabaseService.KeyTwitchUserId));

    public string? ConnectedUserLogin => _database.GetSetting(DatabaseService.KeyTwitchUserLogin);

    public bool HasChatScopes =>
        HasUserAuth &&
        string.Equals(_database.GetSetting(DatabaseService.KeyTwitchHasChatScopes), "1", StringComparison.Ordinal);

    public (string? AccessToken, string? IrcLogin, string? DisplayName, bool HasChatScopes) GetChatCredentials() =>
        (
            _database.GetSetting(DatabaseService.KeyTwitchUserAccessToken),
            _database.GetSetting(DatabaseService.KeyTwitchUserIrcLogin),
            _database.GetSetting(DatabaseService.KeyTwitchUserLogin),
            HasChatScopes
        );

    public async Task<string?> ResolveUserIdByLoginAsync(string login, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(login) || !HasClientCredentials)
        {
            return null;
        }

        try
        {
            var url = $"https://api.twitch.tv/helix/users?login={Uri.EscapeDataString(login.Trim().ToLowerInvariant())}";
            using var doc = await SendHelixGetAsync(url, useUserToken: false, cancellationToken).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
            {
                return data[0].TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            }
        }
        catch
        {
            // Optional lookup for 7TV emotes on direct URLs.
        }

        return null;
    }

    public async Task<Dictionary<string, string>> ResolveGameIdsAsync(
        IEnumerable<string> categoryNames,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        foreach (var rawName in categoryNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = rawName.Trim();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var cached = _database.GetCachedTwitchGameId(name);
            if (!string.IsNullOrWhiteSpace(cached))
            {
                result[name] = cached;
            }
            else
            {
                missing.Add(name);
            }
        }

        if (missing.Count == 0)
        {
            return result;
        }

        var query = string.Join("&", missing.Select(n => $"name={Uri.EscapeDataString(n)}"));
        using var doc = await SendHelixGetAsync($"https://api.twitch.tv/helix/games?{query}", useUserToken: false, cancellationToken).ConfigureAwait(false);

        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            foreach (var item in data.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                var name = item.GetProperty("name").GetString();
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                {
                    result[name] = id;
                    _database.CacheTwitchGameId(name, id);
                }
            }
        }

        return result;
    }

    public async Task<List<TwitchCategoryItem>> SearchCategoriesAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var list = new List<TwitchCategoryItem>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return list;
        }

        var url = $"https://api.twitch.tv/helix/search/categories?query={Uri.EscapeDataString(query.Trim())}&first=10";
        using var doc = await SendHelixGetAsync(url, useUserToken: false, cancellationToken).ConfigureAwait(false);

        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            foreach (var item in data.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                var name = item.GetProperty("name").GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        _database.CacheTwitchGameId(name, id);
                    }

                    list.Add(new TwitchCategoryItem(name, id));
                }
            }
        }

        return list;
    }

    public async Task<TwitchStreamsPage> GetLiveStreamsByGameIdAsync(
        string gameId,
        string? language,
        CancellationToken cancellationToken,
        string? afterCursor = null)
    {
        var sb = new StringBuilder($"https://api.twitch.tv/helix/streams?game_id={Uri.EscapeDataString(gameId)}&first=20");
        if (!string.IsNullOrWhiteSpace(language) &&
            !string.Equals(language, "All", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("&language=").Append(Uri.EscapeDataString(language));
        }

        if (!string.IsNullOrWhiteSpace(afterCursor))
        {
            sb.Append("&after=").Append(Uri.EscapeDataString(afterCursor));
        }

        using var doc = await SendHelixGetAsync(sb.ToString(), useUserToken: false, cancellationToken).ConfigureAwait(false);
        return ParseStreamsPage(doc);
    }

    public async Task<TwitchStreamsPage> GetFollowedLiveStreamsAsync(
        CancellationToken cancellationToken,
        string? afterCursor = null)
    {
        var userId = _database.GetSetting(DatabaseService.KeyTwitchUserId);
        if (string.IsNullOrWhiteSpace(userId) || !HasUserAuth)
        {
            throw new InvalidOperationException("Connect your Twitch account in Audio Streamer Settings to view followed live streams.");
        }

        var sb = new StringBuilder($"https://api.twitch.tv/helix/streams/followed?user_id={Uri.EscapeDataString(userId)}&first=20");
        if (!string.IsNullOrWhiteSpace(afterCursor))
        {
            sb.Append("&after=").Append(Uri.EscapeDataString(afterCursor));
        }

        using var doc = await SendHelixGetAsync(sb.ToString(), useUserToken: true, cancellationToken).ConfigureAwait(false);
        return ParseStreamsPage(doc);
    }

    public async Task<(bool Success, string? Login, string? Error)> AuthenticateUserAsync(
        CancellationToken cancellationToken)
    {
        var clientId = _database.GetSetting(DatabaseService.KeyTwitchClientId)?.Trim();
        var clientSecret = _database.GetSetting(DatabaseService.KeyTwitchClientSecret)?.Trim();
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return (false, null, "Save your Twitch Client ID and Client Secret first.");
        }

        var state = Guid.NewGuid().ToString("N");
        var authUrl =
            $"https://id.twitch.tv/oauth2/authorize?response_type=code&client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUriExact)}&scope=user%3Aread%3Afollows%20chat%3Aread%20chat%3Aedit&state={state}";

        using var listener = new HttpListener();
        listener.Prefixes.Add(RedirectUri);

        try
        {
            listener.Start();
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));

            var contextTask = listener.GetContextAsync();
            using var reg = timeoutCts.Token.Register(() =>
            {
                try
                {
                    listener.Stop();
                }
                catch
                {
                    // Ignore listener stop race.
                }
            });

            var context = await contextTask.ConfigureAwait(false);
            var req = context.Request;
            var code = req.QueryString["code"];
            var returnedState = req.QueryString["state"];
            var error = req.QueryString["error_description"] ?? req.QueryString["error"];

            var html = string.IsNullOrWhiteSpace(error) && !string.IsNullOrWhiteSpace(code)
                ? "<html><body style='font-family:sans-serif;background:#0e0f12;color:#e6e8ee;padding:32px'><h2>REM connected to Twitch</h2><p>You can close this tab and return to REM.</p></body></html>"
                : $"<html><body style='font-family:sans-serif;background:#0e0f12;color:#f07d8d;padding:32px'><h2>Twitch Authorization Failed</h2><p>{WebUtility.HtmlEncode(error ?? "Missing code")}</p></body></html>";

            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            context.Response.Close();

            if (!string.IsNullOrWhiteSpace(error))
            {
                return (false, null, error);
            }

            if (!string.Equals(state, returnedState, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(code))
            {
                return (false, null, "OAuth state verification failed.");
            }

            using var tokenContent = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code"] = code,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = RedirectUriExact
            });

            using var tokenResp = await _http.PostAsync("https://id.twitch.tv/oauth2/token", tokenContent, cancellationToken).ConfigureAwait(false);
            var tokenBody = await tokenResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!tokenResp.IsSuccessStatusCode)
            {
                return (false, null, $"Token exchange failed ({(int)tokenResp.StatusCode}): {tokenBody}");
            }

            using var tokenDoc = JsonDocument.Parse(tokenBody);
            var userToken = tokenDoc.RootElement.GetProperty("access_token").GetString();
            if (string.IsNullOrWhiteSpace(userToken))
            {
                return (false, null, "Twitch returned an empty user access token.");
            }

            using var userReq = new HttpRequestMessage(HttpMethod.Get, "https://api.twitch.tv/helix/users");
            userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
            userReq.Headers.Add("Client-Id", clientId);

            using var userResp = await _http.SendAsync(userReq, cancellationToken).ConfigureAwait(false);
            var userBody = await userResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!userResp.IsSuccessStatusCode)
            {
                return (false, null, $"Failed to fetch Twitch user profile ({(int)userResp.StatusCode}).");
            }

            using var userDoc = JsonDocument.Parse(userBody);
            var firstUser = userDoc.RootElement.GetProperty("data")[0];
            var userId = firstUser.GetProperty("id").GetString() ?? string.Empty;
            var ircLogin = firstUser.TryGetProperty("login", out var loginEl)
                ? loginEl.GetString()?.Trim().ToLowerInvariant() ?? string.Empty
                : string.Empty;
            var userLogin = firstUser.GetProperty("display_name").GetString()
                ?? (string.IsNullOrWhiteSpace(ircLogin) ? "TwitchUser" : ircLogin);

            _database.SaveSetting(DatabaseService.KeyTwitchUserAccessToken, userToken);
            _database.SaveSetting(DatabaseService.KeyTwitchUserId, userId);
            _database.SaveSetting(DatabaseService.KeyTwitchUserLogin, userLogin);
            _database.SaveSetting(DatabaseService.KeyTwitchUserIrcLogin, ircLogin);
            _database.SaveSetting(DatabaseService.KeyTwitchHasChatScopes, "1");
            _actionLog.Log("Twitch", $"Connected Twitch account '{userLogin}' (with chat scopes).");
            return (true, userLogin, null);
        }
        catch (OperationCanceledException)
        {
            return (false, null, "Twitch login timed out or was cancelled.");
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public void DisconnectUser()
    {
        _database.SaveSetting(DatabaseService.KeyTwitchUserAccessToken, string.Empty);
        _database.SaveSetting(DatabaseService.KeyTwitchUserId, string.Empty);
        _database.SaveSetting(DatabaseService.KeyTwitchUserLogin, string.Empty);
        _database.SaveSetting(DatabaseService.KeyTwitchUserIrcLogin, string.Empty);
        _database.SaveSetting(DatabaseService.KeyTwitchHasChatScopes, string.Empty);
        _actionLog.Log("Twitch", "Disconnected Twitch user account.");
    }

    public void InvalidateAppToken()
    {
        _appAccessToken = null;
        _appTokenExpiresAt = DateTimeOffset.MinValue;
    }

    public async Task<Bitmap?> LoadSmallThumbnailAsync(string? templateUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(templateUrl))
        {
            return null;
        }

        try
        {
            var url = templateUrl
                .Replace("{width}", "320", StringComparison.OrdinalIgnoreCase)
                .Replace("{height}", "180", StringComparison.OrdinalIgnoreCase);

            var bytes = await _http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
            using var ms = new MemoryStream(bytes, writable: false);
            return Bitmap.DecodeToWidth(ms, 160);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
    }

    private async Task<JsonDocument> SendHelixGetAsync(
        string url,
        bool useUserToken,
        CancellationToken cancellationToken)
    {
        var clientId = _database.GetSetting(DatabaseService.KeyTwitchClientId)?.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("Twitch Client ID is not configured. Open Audio Streamer Settings → Twitch API.");
        }

        var token = useUserToken
            ? _database.GetSetting(DatabaseService.KeyTwitchUserAccessToken)
            : await GetAppAccessTokenAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(useUserToken
                ? "Twitch account is not connected."
                : "Could not obtain Twitch App Access Token.");
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("Client-Id", clientId);

        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized && !useUserToken)
        {
            InvalidateAppToken();
            token = await GetAppAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            using var retryReq = new HttpRequestMessage(HttpMethod.Get, url);
            retryReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            retryReq.Headers.Add("Client-Id", clientId);
            using var retryResp = await _http.SendAsync(retryReq, cancellationToken).ConfigureAwait(false);
            return await ParseSuccessJsonAsync(retryResp, cancellationToken).ConfigureAwait(false);
        }

        return await ParseSuccessJsonAsync(resp, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ParseSuccessJsonAsync(
        HttpResponseMessage resp,
        CancellationToken cancellationToken)
    {
        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Twitch API returned {(int)resp.StatusCode}: {body}");
        }

        return JsonDocument.Parse(body);
    }

    private async Task<string> GetAppAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_appAccessToken) &&
            _appTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            return _appAccessToken;
        }

        var clientId = _database.GetSetting(DatabaseService.KeyTwitchClientId)?.Trim();
        var clientSecret = _database.GetSetting(DatabaseService.KeyTwitchClientSecret)?.Trim();
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException("Set your Twitch Client ID and Client Secret in Audio Streamer Settings → Twitch API.");
        }

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "client_credentials"
        });

        using var resp = await _http.PostAsync("https://id.twitch.tv/oauth2/token", content, cancellationToken).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Twitch OAuth failed ({(int)resp.StatusCode}): {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.GetProperty("access_token").GetString();
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var expEl) ? expEl.GetInt32() : 3600;

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Twitch returned an empty App Access Token.");
        }

        _appAccessToken = token;
        _appTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        return token;
    }

    private static TwitchStreamsPage ParseStreamsPage(JsonDocument doc)
    {
        var list = new List<TwitchLiveStream>();
        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            foreach (var item in data.EnumerateArray())
            {
                var userId = item.TryGetProperty("user_id", out var uid) ? uid.GetString() ?? "" : "";
                var userLogin = item.TryGetProperty("user_login", out var ulog) ? ulog.GetString() ?? "" : "";
                var userName = item.TryGetProperty("user_name", out var uname) ? uname.GetString() ?? userLogin : userLogin;
                var gameName = item.TryGetProperty("game_name", out var gname) ? gname.GetString() ?? "" : "";
                var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var viewers = item.TryGetProperty("viewer_count", out var vc) ? vc.GetInt32() : 0;
                var language = item.TryGetProperty("language", out var lang) ? lang.GetString() ?? "" : "";
                var thumb = item.TryGetProperty("thumbnail_url", out var th) ? th.GetString() : null;

                if (!string.IsNullOrWhiteSpace(userLogin))
                {
                    list.Add(new TwitchLiveStream(userId, userLogin, userName, gameName, title, viewers, language, thumb));
                }
            }
        }

        string? nextCursor = null;
        if (doc.RootElement.TryGetProperty("pagination", out var pagination) &&
            pagination.TryGetProperty("cursor", out var cursorProp) &&
            cursorProp.ValueKind == JsonValueKind.String)
        {
            var raw = cursorProp.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                nextCursor = raw;
            }
        }

        return new TwitchStreamsPage(list, nextCursor);
    }
}

