using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using REM.Models;

namespace REM.Services;

public sealed record PlaylistItemsResult(
    List<SpotifyTrackItem> Tracks,
    bool AccessRestricted);

public sealed class SpotifyWebApiService : IDisposable
{
    private const string RedirectUri = "http://127.0.0.1:8888/callback";
    private const string ListenerPrefix = "http://127.0.0.1:8888/callback/";
    private const string Scopes = "user-read-private user-library-read user-library-modify playlist-read-private playlist-read-collaborative playlist-modify-public playlist-modify-private user-read-recently-played user-top-read user-follow-read user-follow-modify";
    private const long MaxArtCacheBytes = 50L * 1024L * 1024L;

    private readonly DatabaseService _database;
    private readonly ActionLogService _actionLog;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private readonly string _artCacheDir;
    private DateTimeOffset _rateLimitedUntilUtc = DateTimeOffset.MinValue;
    private bool _disposed;

    public SpotifyWebApiService(DatabaseService database, ActionLogService actionLog)
    {
        _database = database;
        _actionLog = actionLog;
        _artCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "REM",
            "spotify-art-cache");
        _http = new HttpClient(new BandwidthCountingHandler())
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public bool HasClientId => !string.IsNullOrWhiteSpace(_database.GetSetting(DatabaseService.KeySpotifyClientId));

    public bool IsConnected =>
        !string.IsNullOrWhiteSpace(_database.GetSetting(DatabaseService.KeySpotifyRefreshToken)) ||
        !string.IsNullOrWhiteSpace(_database.GetSetting(DatabaseService.KeySpotifyAccessToken));

    public string? UserDisplayName => _database.GetSetting(DatabaseService.KeySpotifyUserDisplayName);
    public string? LastApiError { get; private set; }
    public bool IsRateLimited => DateTimeOffset.UtcNow < _rateLimitedUntilUtc;
    public int RateLimitRemainingSeconds => Math.Max(0, (int)Math.Ceiling((_rateLimitedUntilUtc - DateTimeOffset.UtcNow).TotalSeconds));

    public async Task<(bool Success, string? DisplayName, string? Error)> AuthenticatePkceAsync(
        CancellationToken cancellationToken)
    {
        var clientId = _database.GetSetting(DatabaseService.KeySpotifyClientId)?.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return (false, null, "Enter and save your Spotify Client ID first.");
        }

        var verifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Guid.NewGuid().ToString("N");

        var authUrl =
            "https://accounts.spotify.com/authorize" +
            $"?client_id={Uri.EscapeDataString(clientId)}" +
            "&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&code_challenge_method=S256" +
            $"&code_challenge={Uri.EscapeDataString(challenge)}" +
            $"&scope={Uri.EscapeDataString(Scopes)}" +
            $"&state={Uri.EscapeDataString(state)}";

        using var listener = new HttpListener();
        listener.Prefixes.Add(ListenerPrefix);

        try
        {
            listener.Start();
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));

            var ctxTask = listener.GetContextAsync();
            using var reg = timeoutCts.Token.Register(() =>
            {
                try { listener.Stop(); } catch { }
            });

            var context = await ctxTask.ConfigureAwait(false);
            var req = context.Request;
            var code = req.QueryString["code"];
            var returnedState = req.QueryString["state"];
            var error = req.QueryString["error"];

            var html = string.IsNullOrWhiteSpace(error) && !string.IsNullOrWhiteSpace(code)
                ? "<html><body style='font-family:sans-serif;background:#0e0f12;color:#e6e8ee;padding:32px'><h2>REM connected to Spotify</h2><p>You can close this tab and return to REM.</p></body></html>"
                : $"<html><body style='font-family:sans-serif;background:#0e0f12;color:#f07d8d;padding:32px'><h2>Spotify Authorization Failed</h2><p>{WebUtility.HtmlEncode(error ?? "Missing code")}</p></body></html>";

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
                return (false, null, "Spotify OAuth state verification failed.");
            }

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["code_verifier"] = verifier
            });

            using var tokenResp = await _http.PostAsync("https://accounts.spotify.com/api/token", form, cancellationToken).ConfigureAwait(false);
            var tokenBody = await tokenResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!tokenResp.IsSuccessStatusCode)
            {
                return (false, null, $"Token exchange failed ({(int)tokenResp.StatusCode}): {tokenBody}");
            }

            SaveTokenPayload(tokenBody);

            var displayName = await FetchCurrentUserDisplayNameAsync(cancellationToken).ConfigureAwait(false) ?? "Spotify User";
            _database.SaveSetting(DatabaseService.KeySpotifyUserDisplayName, displayName);
            _actionLog.Log("Spotify", $"Connected Spotify Web API via PKCE as '{displayName}'.");
            return (true, displayName, null);
        }
        catch (OperationCanceledException)
        {
            return (false, null, "Spotify login timed out or was cancelled.");
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public void Disconnect()
    {
        _database.SaveSetting(DatabaseService.KeySpotifyAccessToken, string.Empty);
        _database.SaveSetting(DatabaseService.KeySpotifyRefreshToken, string.Empty);
        _database.SaveSetting(DatabaseService.KeySpotifyTokenExpiresAt, string.Empty);
        _database.SaveSetting(DatabaseService.KeySpotifyUserDisplayName, string.Empty);
        _actionLog.Log("Spotify", "Disconnected Spotify Web API PKCE session.");
    }

    public async Task<List<SpotifyPlaylistItem>> GetUserPlaylistsAsync(CancellationToken cancellationToken)
    {
        var list = new List<SpotifyPlaylistItem>();
        using var doc = await SendApiGetAsync("https://api.spotify.com/v1/me/playlists?limit=50", cancellationToken).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var p in items.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = GetString(p, "id") ?? string.Empty;
            var uri = GetString(p, "uri") ?? (string.IsNullOrEmpty(id) ? string.Empty : $"spotify:playlist:{id}");
            var name = GetString(p, "name") ?? "Untitled Playlist";
            var desc = GetString(p, "description") ?? string.Empty;
            var owner = p.TryGetProperty("owner", out var ow) ? (GetString(ow, "display_name") ?? GetString(ow, "id") ?? "Spotify") : "Spotify";

            var count = 0;
            if (p.TryGetProperty("items", out var plItems) && plItems.TryGetProperty("total", out var itTotal) && itTotal.ValueKind == JsonValueKind.Number)
            {
                count = itTotal.GetInt32();
            }
            else if (p.TryGetProperty("tracks", out var plTracks) && plTracks.TryGetProperty("total", out var trTotal) && trTotal.ValueKind == JsonValueKind.Number)
            {
                count = trTotal.GetInt32();
            }

            string? imageUrl = null;
            if (p.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
            {
                foreach (var img in imgs.EnumerateArray())
                {
                    imageUrl = GetString(img, "url");
                    if (!string.IsNullOrWhiteSpace(imageUrl))
                    {
                        break;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(uri))
            {
                list.Add(new SpotifyPlaylistItem(id, uri, name, desc, owner, count, imageUrl));
            }
        }

        return list;
    }

    // Feb 2026 Web API spec: GET /v1/playlists/{id}/items (NOT /tracks) with public embed fallback
    public async Task<PlaylistItemsResult> GetPlaylistItemsAsync(
        string playlistId,
        string? playlistUri,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return new PlaylistItemsResult([], AccessRestricted: true);
        }

        var token = await EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var apiRestricted = string.IsNullOrWhiteSpace(token);
        var tracks = new List<SpotifyTrackItem>();

        if (!string.IsNullOrWhiteSpace(token))
        {
            var url = $"https://api.spotify.com/v1/playlists/{Uri.EscapeDataString(playlistId)}/items?limit=50";
            using var resp = await SendAuthorizedAsync(HttpMethod.Get, url, token, cancellationToken).ConfigureAwait(false);

            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                apiRestricted = true;
            }
            else if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);

                if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in items.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        JsonElement trackObj;
                        if (!entry.TryGetProperty("item", out trackObj) || trackObj.ValueKind != JsonValueKind.Object)
                        {
                            if (!entry.TryGetProperty("track", out trackObj) || trackObj.ValueKind != JsonValueKind.Object)
                            {
                                continue;
                            }
                        }

                        var parsed = ParseTrackObject(trackObj, playlistUri);
                        if (parsed is not null)
                        {
                            tracks.Add(parsed);
                        }
                    }
                }
            }
        }

        if (tracks.Count > 0)
        {
            return new PlaylistItemsResult(tracks, AccessRestricted: false);
        }

        // Spotify's Dev Mode Web API blocks reading playlists owned by other users or Spotify itself;
        // fall back cleanly to Spotify's public embed metadata so all public playlists still show & play.
        var embedTracks = await FetchPlaylistEmbedTracksAsync(playlistId, playlistUri, cancellationToken).ConfigureAwait(false);
        if (embedTracks.Count > 0)
        {
            _actionLog.Log("Spotify", $"Loaded {embedTracks.Count} tracks for playlist {playlistId} via public embed fallback.");
            return new PlaylistItemsResult(embedTracks, AccessRestricted: false);
        }

        return new PlaylistItemsResult([], AccessRestricted: apiRestricted);
    }

    private async Task<List<SpotifyTrackItem>> FetchPlaylistEmbedTracksAsync(
        string playlistId,
        string? playlistUri,
        CancellationToken cancellationToken)
    {
        var list = new List<SpotifyTrackItem>();
        try
        {
            var embedUrl = $"https://open.spotify.com/embed/playlist/{Uri.EscapeDataString(playlistId)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, embedUrl);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

            using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return list;
            }

            var html = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            const string marker = "id=\"__NEXT_DATA__\"";
            var markerIdx = html.IndexOf(marker, StringComparison.Ordinal);
            if (markerIdx < 0)
            {
                return list;
            }

            var jsonStart = html.IndexOf('>', markerIdx);
            if (jsonStart < 0)
            {
                return list;
            }
            jsonStart++;

            var jsonEnd = html.IndexOf("</script>", jsonStart, StringComparison.OrdinalIgnoreCase);
            if (jsonEnd <= jsonStart)
            {
                return list;
            }

            var json = html[jsonStart..jsonEnd];
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("props", out var props) ||
                !props.TryGetProperty("pageProps", out var pageProps) ||
                !pageProps.TryGetProperty("state", out var state) ||
                !state.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("entity", out var entity))
            {
                return list;
            }

            var playlistName = GetString(entity, "name") ?? GetString(entity, "title") ?? string.Empty;
            string? fallbackCoverUrl = null;
            if (entity.TryGetProperty("coverArt", out var coverArt) &&
                coverArt.TryGetProperty("sources", out var sources) &&
                sources.ValueKind == JsonValueKind.Array)
            {
                foreach (var src in sources.EnumerateArray())
                {
                    var u = GetString(src, "url");
                    if (!string.IsNullOrWhiteSpace(u))
                    {
                        fallbackCoverUrl ??= u;
                        if (src.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number && w.GetInt32() is >= 150 and <= 400)
                        {
                            fallbackCoverUrl = u;
                            break;
                        }
                    }
                }
            }

            if (!entity.TryGetProperty("trackList", out var trackList) || trackList.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var t in trackList.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var uri = GetString(t, "uri");
                var title = GetString(t, "title") ?? GetString(t, "name");
                if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var id = uri.Split(':').LastOrDefault() ?? string.Empty;
                var subtitle = (GetString(t, "subtitle") ?? "Spotify Artist").Replace('\u00A0', ' ');
                var durationMs = t.TryGetProperty("duration", out var durProp) && durProp.ValueKind == JsonValueKind.Number
                    ? durProp.GetInt64()
                    : 0;

                string? itemCover = fallbackCoverUrl;
                if (t.TryGetProperty("imageUrl", out var imgProp) && imgProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(imgProp.GetString()))
                {
                    itemCover = imgProp.GetString();
                }

                list.Add(new SpotifyTrackItem(
                    id,
                    uri,
                    title,
                    subtitle,
                    playlistName,
                    itemCover,
                    durationMs,
                    playlistUri));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _actionLog.Log("Warning", $"Spotify embed fallback failed for {playlistId}: {ex.Message}");
        }

        return list;
    }

    public async Task<List<SpotifyTrackItem>> GetLikedSongsAsync(CancellationToken cancellationToken)
    {
        var list = new List<SpotifyTrackItem>();
        using var doc = await SendApiGetAsync("https://api.spotify.com/v1/me/tracks?limit=50", cancellationToken).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (entry.TryGetProperty("track", out var tr) && tr.ValueKind == JsonValueKind.Object)
            {
                var parsed = ParseTrackObject(tr, "spotify:collection:tracks");
                if (parsed is not null)
                {
                    list.Add(parsed);
                }
            }
            else if (entry.TryGetProperty("item", out var it) && it.ValueKind == JsonValueKind.Object)
            {
                var parsed = ParseTrackObject(it, "spotify:collection:tracks");
                if (parsed is not null)
                {
                    list.Add(parsed);
                }
            }
        }

        return list;
    }

    // Feb 2026 Web API spec: GET /v1/me/library/contains?uris=...
    public async Task<bool?> IsTrackLikedAsync(string trackUri, CancellationToken cancellationToken)
    {
        var token = await EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(trackUri))
        {
            return null;
        }

        var url = $"https://api.spotify.com/v1/me/library/contains?uris={Uri.EscapeDataString(trackUri)}";
        using var resp = await SendAuthorizedAsync(HttpMethod.Get, url, token, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
        {
            return doc.RootElement[0].GetBoolean();
        }

        return null;
    }

    // Feb 2026 Web API spec: PUT/DELETE /v1/me/library?uris=...
    public async Task<bool> SetTrackLikedAsync(string trackUri, bool liked, CancellationToken cancellationToken)
    {
        var token = await EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(trackUri))
        {
            return false;
        }

        var url = $"https://api.spotify.com/v1/me/library?uris={Uri.EscapeDataString(trackUri)}";
        var method = liked ? HttpMethod.Put : HttpMethod.Delete;
        using var resp = await SendAuthorizedAsync(method, url, token, cancellationToken).ConfigureAwait(false);
        return resp.IsSuccessStatusCode;
    }

    // Feb 2026 Web API spec: batch GET /v1/me/library/contains?uris=... (chunks of 40)
    public async Task<Dictionary<string, bool>> CheckTracksLikedBatchAsync(
        IEnumerable<string> trackUris,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        var distinct = trackUris
            .Where(u => !string.IsNullOrWhiteSpace(u) && u.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (distinct.Count == 0)
        {
            return result;
        }

        var token = await EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return result;
        }

        for (var i = 0; i < distinct.Count; i += 40)
        {
            var chunk = distinct.Skip(i).Take(40).ToList();
            var url = $"https://api.spotify.com/v1/me/library/contains?uris={Uri.EscapeDataString(string.Join(",", chunk))}";
            using var resp = await SendAuthorizedAsync(HttpMethod.Get, url, token, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                continue;
            }

            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var idx = 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (idx >= chunk.Count)
                {
                    break;
                }

                if (el.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    result[chunk[idx]] = el.GetBoolean();
                }

                idx++;
            }
        }

        return result;
    }

    // Feb 2026 Web API spec: POST /v1/playlists/{id}/items
    public async Task<(bool Success, bool NeedsReauth, string? Error)> AddTrackToPlaylistAsync(
        string playlistId,
        string trackUri,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistId) || string.IsNullOrWhiteSpace(trackUri))
        {
            return (false, false, "Missing playlist ID or track URI.");
        }

        var token = await EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return (false, true, "Connect Spotify (PKCE) in Settings → Spotify & APIs first.");
        }

        var url = $"https://api.spotify.com/v1/playlists/{Uri.EscapeDataString(playlistId)}/items";
        var payload = JsonSerializer.Serialize(new { uris = new[] { trackUri } });

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            resp.Dispose();
            var refreshed = await ForceRefreshAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(refreshed))
            {
                return (false, true, "Spotify session expired. Reconnect Spotify (PKCE) in Settings.");
            }

            using var retryReq = new HttpRequestMessage(HttpMethod.Post, url);
            retryReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed);
            retryReq.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            resp = await _http.SendAsync(retryReq, cancellationToken).ConfigureAwait(false);
        }

        using (resp)
        {
            if (resp.IsSuccessStatusCode)
            {
                return (true, false, null);
            }

            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                return (false, true, "Reconnect Spotify (PKCE) in Settings to grant playlist editing permission.");
            }

            var errBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (false, false, $"Spotify returned {(int)resp.StatusCode}: {errBody}");
        }
    }

    // Feb 2026 Web API spec: GET /v1/search strict limit=10 cap
    public async Task<List<SpotifyTrackItem>> SearchTracksAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = new List<SpotifyTrackItem>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return list;
        }

        var cappedLimit = Math.Clamp(limit, 1, 10);
        var url = $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query.Trim())}&type=track&limit={cappedLimit}";
        using var doc = await SendApiGetAsync(url, cancellationToken).ConfigureAwait(false);
        if (doc is null ||
            !doc.RootElement.TryGetProperty("tracks", out var tracksObj) ||
            !tracksObj.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in items.EnumerateArray())
        {
            var parsed = ParseTrackObject(item, contextUri: null);
            if (parsed is not null)
            {
                list.Add(parsed);
            }
        }

        return list;
    }

    public async Task<List<SpotifyTrackItem>> GetRecentlyPlayedAsync(int limit, CancellationToken cancellationToken)
    {
        var list = new List<SpotifyTrackItem>();
        var url = $"https://api.spotify.com/v1/me/player/recently-played?limit={Math.Clamp(limit, 1, 20)}";
        using var doc = await SendApiGetAsync(url, cancellationToken).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var entry in items.EnumerateArray())
        {
            if (entry.TryGetProperty("track", out var tr) && tr.ValueKind == JsonValueKind.Object)
            {
                var parsed = ParseTrackObject(tr, contextUri: null);
                if (parsed is not null)
                {
                    list.Add(parsed);
                }
            }
        }

        return list;
    }

    public async Task<List<SpotifyTrackItem>> GetRecentlyPlayedWithTimestampsAsync(int limit, CancellationToken cancellationToken)
    {
        var list = new List<SpotifyTrackItem>();
        var url = $"https://api.spotify.com/v1/me/player/recently-played?limit={Math.Clamp(limit, 1, 50)}";
        using var doc = await SendApiGetAsync(url, cancellationToken).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var entry in items.EnumerateArray())
        {
            if (!entry.TryGetProperty("track", out var tr) || tr.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            DateTimeOffset? playedAt = null;
            var playedRaw = GetString(entry, "played_at");
            if (!string.IsNullOrWhiteSpace(playedRaw) &&
                DateTimeOffset.TryParse(playedRaw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dto))
            {
                playedAt = dto;
            }

            var parsed = ParseTrackObject(tr, contextUri: null);
            if (parsed is not null)
            {
                list.Add(playedAt.HasValue ? parsed with { PlayedAt = playedAt } : parsed);
            }
        }

        return list;
    }

    public async Task<List<SpotifyTrackItem>> GetTopTracksAsync(int limit, CancellationToken cancellationToken)
    {
        var list = new List<SpotifyTrackItem>();
        var url = $"https://api.spotify.com/v1/me/top/tracks?limit={Math.Clamp(limit, 1, 20)}&time_range=short_term";
        using var doc = await SendApiGetAsync(url, cancellationToken).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var tr in items.EnumerateArray())
        {
            var parsed = ParseTrackObject(tr, contextUri: null);
            if (parsed is not null)
            {
                list.Add(parsed);
            }
        }

        return list;
    }

    public async Task<SpotifyTrackItem?> GetTrackByIdAsync(string trackId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return null;
        }

        using var doc = await SendApiGetAsync(
            $"https://api.spotify.com/v1/tracks/{Uri.EscapeDataString(trackId.Trim())}",
            cancellationToken).ConfigureAwait(false);
        return doc is null ? null : ParseTrackObject(doc.RootElement, contextUri: null);
    }

    public async Task<SpotifySearchBundle> SearchAllAsync(
        string query,
        int limitPerType,
        CancellationToken cancellationToken)
    {
        var tracks = new List<SpotifyTrackItem>();
        var artists = new List<SpotifyArtistItem>();
        var albums = new List<SpotifyAlbumItem>();
        var playlists = new List<SpotifyPlaylistItem>();

        if (string.IsNullOrWhiteSpace(query))
        {
            return new SpotifySearchBundle(tracks, artists, albums, playlists);
        }

        var trimmedQuery = Uri.EscapeDataString(query.Trim());
        var cappedLimit = Math.Clamp(limitPerType, 1, 10);
        var doc = await SendApiGetAsync(
            $"https://api.spotify.com/v1/search?q={trimmedQuery}&type=track,artist,album,playlist&limit={cappedLimit}",
            cancellationToken).ConfigureAwait(false);

        if (doc is null && !IsRateLimited)
        {
            doc = await SendApiGetAsync(
                $"https://api.spotify.com/v1/search?q={trimmedQuery}&type=track,artist,album&limit=5",
                cancellationToken).ConfigureAwait(false);
        }

        if (doc is null && !IsRateLimited)
        {
            doc = await SendApiGetAsync(
                $"https://api.spotify.com/v1/search?q={trimmedQuery}&type=track&limit={cappedLimit}",
                cancellationToken).ConfigureAwait(false);
        }

        if (doc is null)
        {
            return new SpotifySearchBundle(tracks, artists, albums, playlists);
        }

        using (doc)
        {
            if (doc.RootElement.TryGetProperty("tracks", out var tracksObj) &&
                tracksObj.TryGetProperty("items", out var tItems) &&
                tItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in tItems.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var parsed = ParseTrackObject(item, contextUri: null);
                    if (parsed is not null) tracks.Add(parsed);
                }
            }

            if (doc.RootElement.TryGetProperty("artists", out var artistsObj) &&
                artistsObj.TryGetProperty("items", out var aItems) &&
                aItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in aItems.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var parsed = ParseArtistObject(item);
                    if (parsed is not null) artists.Add(parsed);
                }
            }

            if (doc.RootElement.TryGetProperty("albums", out var albumsObj) &&
                albumsObj.TryGetProperty("items", out var alItems) &&
                alItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in alItems.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var parsed = ParseAlbumObject(item);
                    if (parsed is not null) albums.Add(parsed);
                }
            }

            if (doc.RootElement.TryGetProperty("playlists", out var plsObj) &&
                plsObj.TryGetProperty("items", out var pItems) &&
                pItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in pItems.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var parsed = ParsePlaylistObject(item);
                    if (parsed is not null) playlists.Add(parsed);
                }
            }
        }

        return new SpotifySearchBundle(tracks, artists, albums, playlists);
    }

    public async Task<SpotifyArtistItem?> SearchArtistByNameAsync(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var url = $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(name.Trim())}&type=artist&limit=3";
        using var doc = await SendApiGetAsync(url, cancellationToken).ConfigureAwait(false);
        if (doc is null ||
            !doc.RootElement.TryGetProperty("artists", out var artistsObj) ||
            !artistsObj.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        SpotifyArtistItem? first = null;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var parsed = ParseArtistObject(item);
            if (parsed is null) continue;
            first ??= parsed;
            if (string.Equals(parsed.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return parsed;
            }
        }

        return first;
    }

    public async Task<SpotifyArtistItem?> GetArtistAsync(string artistId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistId))
        {
            return null;
        }

        using var doc = await SendApiGetAsync(
            $"https://api.spotify.com/v1/artists/{Uri.EscapeDataString(artistId.Trim())}",
            cancellationToken).ConfigureAwait(false);
        return doc is null ? null : ParseArtistObject(doc.RootElement);
    }

    // Feb 2026 Web API spec: GET /artists/{id}/albums strict limit <= 10
    public async Task<List<SpotifyAlbumItem>> GetArtistAlbumsAsync(
        string artistId,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = new List<SpotifyAlbumItem>();
        if (string.IsNullOrWhiteSpace(artistId))
        {
            return list;
        }

        var url = $"https://api.spotify.com/v1/artists/{Uri.EscapeDataString(artistId.Trim())}/albums?include_groups=album,single&limit={Math.Clamp(limit, 1, 10)}";
        using var doc = await SendApiGetAsync(url, cancellationToken).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var parsed = ParseAlbumObject(item);
            if (parsed is not null && seenNames.Add($"{parsed.Name}::{parsed.ReleaseDate}"))
            {
                list.Add(parsed);
            }
        }

        return list;
    }

    // Feb 2026 Web API removed GET /artists/{id}/top-tracks for Dev Mode; build playable top tracks from the artist's albums ranked by Last.fm.
    public async Task<List<SpotifyTrackItem>> GetArtistTopTracksAsync(
        string artistId,
        string artistName,
        CancellationToken cancellationToken,
        IReadOnlyList<SpotifyAlbumItem>? prefetchedAlbums = null,
        IReadOnlyList<string>? preferredTrackNames = null)
    {
        var albums = prefetchedAlbums is { Count: > 0 }
            ? prefetchedAlbums.ToList()
            : (!string.IsNullOrWhiteSpace(artistId)
                ? await GetArtistAlbumsAsync(artistId, 10, cancellationToken).ConfigureAwait(false)
                : []);

        var collected = new List<SpotifyTrackItem>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (albums.Count > 0)
        {
            var pickAlbums = albums
                .OrderByDescending(a => string.Equals(a.AlbumType, "album", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(a => a.ReleaseDate, StringComparer.Ordinal)
                .Take(3)
                .ToList();

            foreach (var al in pickAlbums)
            {
                if (cancellationToken.IsCancellationRequested || IsRateLimited) break;
                var (_, albumTracks) = await GetAlbumAsync(al.Id, cancellationToken).ConfigureAwait(false);
                foreach (var tr in albumTracks)
                {
                    if (seenNames.Add(tr.Name))
                    {
                        collected.Add(tr);
                    }
                }
            }
        }

        if (collected.Count > 0 && preferredTrackNames is { Count: > 0 })
        {
            var rankLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < preferredTrackNames.Count; i++)
            {
                rankLookup.TryAdd(preferredTrackNames[i].Trim(), i);
            }

            collected = collected
                .OrderBy(t => rankLookup.TryGetValue(t.Name.Trim(), out var rank) ? rank : int.MaxValue)
                .ToList();
        }

        if (collected.Count == 0 && !IsRateLimited && !string.IsNullOrWhiteSpace(artistName))
        {
            collected = await SearchTracksAsync(artistName.Trim(), 10, cancellationToken).ConfigureAwait(false);
        }

        return collected.Take(15).ToList();
    }

    public async Task<(SpotifyAlbumItem? Album, List<SpotifyTrackItem> Tracks)> GetAlbumAsync(
        string albumId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(albumId))
        {
            return (null, []);
        }

        using var doc = await SendApiGetAsync(
            $"https://api.spotify.com/v1/albums/{Uri.EscapeDataString(albumId.Trim())}",
            cancellationToken).ConfigureAwait(false);
        if (doc is null)
        {
            return (null, []);
        }

        var album = ParseAlbumObject(doc.RootElement);
        var tracks = new List<SpotifyTrackItem>();
        if (doc.RootElement.TryGetProperty("tracks", out var trWrap) &&
            trWrap.TryGetProperty("items", out var items) &&
            items.ValueKind == JsonValueKind.Array)
        {
            foreach (var tr in items.EnumerateArray())
            {
                if (tr.ValueKind != JsonValueKind.Object) continue;
                var uri = GetString(tr, "uri");
                var name = GetString(tr, "name");
                if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(name)) continue;

                var id = GetString(tr, "id") ?? uri.Split(':').LastOrDefault() ?? string.Empty;
                var dur = tr.TryGetProperty("duration_ms", out var dEl) && dEl.ValueKind == JsonValueKind.Number
                    ? dEl.GetInt64()
                    : 0;
                var (artistsText, primaryArtistId) = ParseArtistsFromObject(tr);
                tracks.Add(new SpotifyTrackItem(
                    id,
                    uri,
                    name,
                    string.IsNullOrWhiteSpace(artistsText) ? (album?.Artists ?? "Unknown Artist") : artistsText,
                    album?.Name ?? string.Empty,
                    album?.ImageUrl,
                    dur,
                    ContextUri: album?.Uri,
                    PrimaryArtistId: primaryArtistId ?? album?.PrimaryArtistId,
                    AlbumId: album?.Id));
            }
        }

        return (album, tracks);
    }

    public async Task<List<SpotifyAlbumItem>> GetSavedAlbumsAsync(int limit, CancellationToken cancellationToken)
    {
        var list = new List<SpotifyAlbumItem>();
        var url = $"https://api.spotify.com/v1/me/albums?limit={Math.Clamp(limit, 1, 20)}";
        using var doc = await SendApiGetAsync(url, cancellationToken).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var entry in items.EnumerateArray())
        {
            if (entry.TryGetProperty("album", out var al) && al.ValueKind == JsonValueKind.Object)
            {
                var parsed = ParseAlbumObject(al);
                if (parsed is not null) list.Add(parsed);
            }
        }

        return list;
    }

    public async Task<bool?> IsAlbumSavedAsync(string albumId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(albumId)) return null;
        using var doc = await SendApiGetAsync(
            $"https://api.spotify.com/v1/me/albums/contains?ids={Uri.EscapeDataString(albumId.Trim())}",
            cancellationToken).ConfigureAwait(false);
        if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
        {
            var el = doc.RootElement[0];
            if (el.ValueKind is JsonValueKind.True or JsonValueKind.False) return el.GetBoolean();
        }

        return null;
    }

    public async Task<bool> SetAlbumSavedAsync(string albumId, bool saved, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(albumId)) return false;
        var method = saved ? HttpMethod.Put : HttpMethod.Delete;
        var ok = await SendSimpleWriteAsync(
            method,
            $"https://api.spotify.com/v1/me/albums?ids={Uri.EscapeDataString(albumId.Trim())}",
            null,
            cancellationToken).ConfigureAwait(false);
        if (ok) return true;

        return await SendSimpleWriteAsync(
            method,
            $"https://api.spotify.com/v1/me/library?uris={Uri.EscapeDataString($"spotify:album:{albumId.Trim()}")}",
            null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<SpotifyArtistItem>> GetTopArtistsAsync(
        int limit,
        string timeRange,
        CancellationToken cancellationToken)
    {
        var list = new List<SpotifyArtistItem>();
        var range = string.IsNullOrWhiteSpace(timeRange) ? "short_term" : timeRange;
        var url = $"https://api.spotify.com/v1/me/top/artists?limit={Math.Clamp(limit, 1, 20)}&time_range={Uri.EscapeDataString(range)}";
        using var doc = await SendApiGetAsync(url, cancellationToken).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var parsed = ParseArtistObject(item);
            if (parsed is not null) list.Add(parsed);
        }

        return list;
    }

    public async Task<List<SpotifyArtistItem>> GetFollowedArtistsAsync(int limit, CancellationToken cancellationToken)
    {
        var list = new List<SpotifyArtistItem>();
        var url = $"https://api.spotify.com/v1/me/following?type=artist&limit={Math.Clamp(limit, 1, 20)}";
        using var doc = await SendApiGetAsync(url, cancellationToken).ConfigureAwait(false);
        if (doc is null ||
            !doc.RootElement.TryGetProperty("artists", out var artistsWrap) ||
            !artistsWrap.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var parsed = ParseArtistObject(item);
            if (parsed is not null) list.Add(parsed);
        }

        return list;
    }

    public async Task<bool?> IsArtistFollowedAsync(string artistId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistId)) return null;
        using var doc = await SendApiGetAsync(
            $"https://api.spotify.com/v1/me/following/contains?type=artist&ids={Uri.EscapeDataString(artistId.Trim())}",
            cancellationToken).ConfigureAwait(false);
        if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
        {
            var el = doc.RootElement[0];
            if (el.ValueKind is JsonValueKind.True or JsonValueKind.False) return el.GetBoolean();
        }

        return null;
    }

    public async Task<bool> SetArtistFollowedAsync(string artistId, bool follow, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistId)) return false;
        var method = follow ? HttpMethod.Put : HttpMethod.Delete;
        return await SendSimpleWriteAsync(
            method,
            $"https://api.spotify.com/v1/me/following?type=artist&ids={Uri.EscapeDataString(artistId.Trim())}",
            null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<(SpotifyPlaylistItem? Playlist, bool NeedsReauth, string? Error)> CreatePlaylistAsync(
        string name,
        string description,
        bool isPublic,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, false, "Playlist name cannot be empty.");
        }

        var token = await EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return (null, true, "Connect Spotify (PKCE) in Settings first.");
        }

        var payload = JsonSerializer.Serialize(new
        {
            name = name.Trim(),
            description = description?.Trim() ?? string.Empty,
            @public = isPublic
        });

        using var resp = await SendJsonAsync(HttpMethod.Post, "https://api.spotify.com/v1/me/playlists", payload, token, cancellationToken)
            .ConfigureAwait(false);
        HttpResponseMessage? finalResp = resp;
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.BadRequest)
        {
            using var meDoc = await SendApiGetAsync("https://api.spotify.com/v1/me", cancellationToken).ConfigureAwait(false);
            var userId = meDoc is null ? null : GetString(meDoc.RootElement, "id");
            if (!string.IsNullOrWhiteSpace(userId))
            {
                finalResp = await SendJsonAsync(
                    HttpMethod.Post,
                    $"https://api.spotify.com/v1/users/{Uri.EscapeDataString(userId)}/playlists",
                    payload,
                    token,
                    cancellationToken).ConfigureAwait(false);
                resp.Dispose();
            }
        }

        using (finalResp)
        {
            if (finalResp.IsSuccessStatusCode)
            {
                var body = await finalResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                return (ParsePlaylistObject(doc.RootElement), false, null);
            }

            if (finalResp.StatusCode == HttpStatusCode.Forbidden)
            {
                return (null, true, "Reconnect Spotify (PKCE) in Settings to grant playlist editing permission.");
            }

            var err = await finalResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (null, false, $"Spotify returned {(int)finalResp.StatusCode}: {err}");
        }
    }

    public async Task<(bool Success, string? Error)> UpdatePlaylistDetailsAsync(
        string playlistId,
        string name,
        string description,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistId) || string.IsNullOrWhiteSpace(name))
        {
            return (false, "Missing playlist ID or name.");
        }

        var payload = JsonSerializer.Serialize(new
        {
            name = name.Trim(),
            description = description?.Trim() ?? string.Empty
        });

        var ok = await SendSimpleWriteAsync(
            HttpMethod.Put,
            $"https://api.spotify.com/v1/playlists/{Uri.EscapeDataString(playlistId)}",
            payload,
            cancellationToken).ConfigureAwait(false);
        return ok ? (true, null) : (false, "Could not update playlist details.");
    }

    public async Task<(bool Success, string? Error)> AddTracksToPlaylistAsync(
        string playlistId,
        IReadOnlyList<string> trackUris,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistId) || trackUris.Count == 0)
        {
            return (false, "No tracks to add.");
        }

        var token = await EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return (false, "Connect Spotify (PKCE) in Settings first.");
        }

        foreach (var chunk in trackUris.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().Chunk(50))
        {
            var payload = JsonSerializer.Serialize(new { uris = chunk });
            using var resp = await SendJsonAsync(
                HttpMethod.Post,
                $"https://api.spotify.com/v1/playlists/{Uri.EscapeDataString(playlistId)}/items",
                payload,
                token,
                cancellationToken).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                using var fallback = await SendJsonAsync(
                    HttpMethod.Post,
                    $"https://api.spotify.com/v1/playlists/{Uri.EscapeDataString(playlistId)}/tracks",
                    payload,
                    token,
                    cancellationToken).ConfigureAwait(false);
                if (!fallback.IsSuccessStatusCode)
                {
                    var err = await fallback.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    return (false, $"Spotify returned {(int)fallback.StatusCode}: {err}");
                }
            }
        }

        return (true, null);
    }

    public async Task<bool> RemoveTrackFromPlaylistAsync(
        string playlistId,
        string trackUri,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistId) || string.IsNullOrWhiteSpace(trackUri))
        {
            return false;
        }

        var payload = JsonSerializer.Serialize(new
        {
            items = new[] { new { uri = trackUri } },
            tracks = new[] { new { uri = trackUri } }
        });

        var ok = await SendSimpleWriteAsync(
            HttpMethod.Delete,
            $"https://api.spotify.com/v1/playlists/{Uri.EscapeDataString(playlistId)}/items",
            payload,
            cancellationToken).ConfigureAwait(false);
        if (ok) return true;

        var fallbackPayload = JsonSerializer.Serialize(new { tracks = new[] { new { uri = trackUri } } });
        return await SendSimpleWriteAsync(
            HttpMethod.Delete,
            $"https://api.spotify.com/v1/playlists/{Uri.EscapeDataString(playlistId)}/tracks",
            fallbackPayload,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> FollowPlaylistAsync(string playlistId, CancellationToken cancellationToken) =>
        SendSimpleWriteAsync(
            HttpMethod.Put,
            $"https://api.spotify.com/v1/playlists/{Uri.EscapeDataString(playlistId)}/followers",
            "{}",
            cancellationToken);

    public Task<bool> UnfollowPlaylistAsync(string playlistId, CancellationToken cancellationToken) =>
        SendSimpleWriteAsync(
            HttpMethod.Delete,
            $"https://api.spotify.com/v1/playlists/{Uri.EscapeDataString(playlistId)}/followers",
            null,
            cancellationToken);

    private async Task<bool> SendSimpleWriteAsync(
        HttpMethod method,
        string url,
        string? jsonBody,
        CancellationToken cancellationToken)
    {
        var token = await EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        using var resp = await SendJsonAsync(method, url, jsonBody, token, cancellationToken).ConfigureAwait(false);
        return resp.IsSuccessStatusCode;
    }

    private async Task<HttpResponseMessage> SendJsonAsync(
        HttpMethod method,
        string url,
        string? jsonBody,
        string token,
        CancellationToken cancellationToken)
    {
        HttpRequestMessage BuildRequest(string bearer)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (jsonBody is not null)
            {
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }

            return req;
        }

        using var req = BuildRequest(token);
        var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            resp.Dispose();
            var refreshed = await ForceRefreshAccessTokenAsync(cancellationToken, token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(refreshed))
            {
                using var retryReq = BuildRequest(refreshed);
                return await _http.SendAsync(retryReq, cancellationToken).ConfigureAwait(false);
            }
        }

        return resp;
    }

    public async Task<byte[]?> DownloadImageBytesAsync(string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url) || _disposed)
        {
            return null;
        }

        var cachePath = GetArtCacheFilePath(url);
        try
        {
            if (File.Exists(cachePath))
            {
                try { File.SetLastAccessTimeUtc(cachePath, DateTime.UtcNow); } catch { }
                return await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Fall through to HTTP fetch if disk read fails.
        }

        try
        {
            var bytes = await _http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
            if (bytes.Length > 0)
            {
                _ = Task.Run(() => TryWriteArtCacheFile(cachePath, bytes));
            }

            return bytes;
        }
        catch
        {
            return null;
        }
    }

    public long GetArtCacheSizeBytes()
    {
        try
        {
            if (!Directory.Exists(_artCacheDir))
            {
                return 0;
            }

            long total = 0;
            foreach (var file in Directory.EnumerateFiles(_artCacheDir, "*.img"))
            {
                try { total += new FileInfo(file).Length; } catch { }
            }

            return total;
        }
        catch
        {
            return 0;
        }
    }

    public void ClearArtCache()
    {
        try
        {
            if (Directory.Exists(_artCacheDir))
            {
                Directory.Delete(_artCacheDir, recursive: true);
            }
        }
        catch
        {
            // Ignore transient locks.
        }
    }

    private string GetArtCacheFilePath(string url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return Path.Combine(_artCacheDir, $"{hex}.img");
    }

    private void TryWriteArtCacheFile(string targetPath, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(_artCacheDir);
            var tempPath = targetPath + ".tmp";
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, targetPath, overwrite: true);
            PruneArtCacheIfNeeded();
        }
        catch
        {
            // Ignore cache write errors.
        }
    }

    private void PruneArtCacheIfNeeded()
    {
        try
        {
            var files = new DirectoryInfo(_artCacheDir)
                .EnumerateFiles("*.img")
                .OrderBy(f => f.LastAccessTimeUtc)
                .ToList();
            long total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (total <= MaxArtCacheBytes)
                {
                    break;
                }

                var len = f.Length;
                try
                {
                    f.Delete();
                    total -= len;
                }
                catch
                {
                    // Skip locked file.
                }
            }
        }
        catch
        {
            // Ignore pruning errors.
        }
    }

    public async Task<Bitmap?> LoadSmallBitmapAsync(string? url, int decodeWidth, CancellationToken cancellationToken)
    {
        var bytes = await DownloadImageBytesAsync(url, cancellationToken).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0 || _disposed)
        {
            return null;
        }

        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            return Bitmap.DecodeToWidth(ms, decodeWidth);
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
        _tokenGate.Dispose();
        _http.Dispose();
    }

    private async Task<string?> FetchCurrentUserDisplayNameAsync(CancellationToken cancellationToken)
    {
        using var doc = await SendApiGetAsync("https://api.spotify.com/v1/me", cancellationToken).ConfigureAwait(false);
        if (doc is null)
        {
            return null;
        }

        return GetString(doc.RootElement, "display_name") ?? GetString(doc.RootElement, "id");
    }

    private async Task<JsonDocument?> SendApiGetAsync(string url, CancellationToken cancellationToken)
    {
        if (IsRateLimited)
        {
            LastApiError = $"Spotify rate limit active — try again in {RateLimitRemainingSeconds}s.";
            return null;
        }

        var token = await EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            LastApiError = "Spotify token missing or expired — reconnect Spotify in Settings → Spotify & APIs.";
            return null;
        }

        using var resp = await SendAuthorizedAsync(HttpMethod.Get, url, token, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var shortPath = Uri.TryCreate(url, UriKind.Absolute, out var parsedUri) ? parsedUri.AbsolutePath : url;
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryDelta = resp.Headers.RetryAfter?.Delta;
                if (retryDelta is null && resp.Headers.RetryAfter?.Date is { } retryDate)
                {
                    retryDelta = retryDate - DateTimeOffset.UtcNow;
                }

                var waitSeconds = Math.Clamp((int)Math.Ceiling(retryDelta?.TotalSeconds ?? 30), 5, 300);
                _rateLimitedUntilUtc = DateTimeOffset.UtcNow.AddSeconds(waitSeconds);
                LastApiError = $"Spotify rate-limited (429) — retry in {waitSeconds}s.";
                _actionLog.Log("Warning", $"Spotify 429 Too Many Requests on {shortPath}; cooling down {waitSeconds}s.");
            }
            else
            {
                var errBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var snippet = errBody.Length > 160 ? errBody[..160] + "..." : errBody;
                LastApiError = resp.StatusCode == HttpStatusCode.Unauthorized
                    ? "Spotify authorization rejected (401) — reconnect Spotify in Settings."
                    : $"Spotify API {(int)resp.StatusCode} on {shortPath}";
                _actionLog.Log("Warning", $"Spotify API {(int)resp.StatusCode} on {shortPath}: {snippet}");
            }

            return null;
        }

        LastApiError = null;
        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(body) ? null : JsonDocument.Parse(body);
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpMethod method,
        string url,
        string token,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            resp.Dispose();
            var refreshed = await ForceRefreshAccessTokenAsync(cancellationToken, token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(refreshed))
            {
                using var retryReq = new HttpRequestMessage(method, url);
                retryReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed);
                return await _http.SendAsync(retryReq, cancellationToken).ConfigureAwait(false);
            }
        }

        return resp;
    }

    private async Task<string?> EnsureAccessTokenAsync(CancellationToken cancellationToken)
    {
        var accessToken = _database.GetSetting(DatabaseService.KeySpotifyAccessToken);
        var refreshToken = _database.GetSetting(DatabaseService.KeySpotifyRefreshToken);
        var expiresRaw = _database.GetSetting(DatabaseService.KeySpotifyTokenExpiresAt);

        if (string.IsNullOrWhiteSpace(accessToken) && string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        var isExpiring = !DateTimeOffset.TryParse(expiresRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAt)
            || expiresAt <= DateTimeOffset.UtcNow.AddSeconds(90);

        if (!isExpiring && !string.IsNullOrWhiteSpace(accessToken))
        {
            return accessToken;
        }

        return await ForceRefreshAccessTokenAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ForceRefreshAccessTokenAsync(
        CancellationToken cancellationToken,
        string? failedAccessToken = null)
    {
        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentAccess = _database.GetSetting(DatabaseService.KeySpotifyAccessToken);
            var expiresRaw = _database.GetSetting(DatabaseService.KeySpotifyTokenExpiresAt);
            var isFresh = DateTimeOffset.TryParse(expiresRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAt)
                && expiresAt > DateTimeOffset.UtcNow.AddSeconds(90);

            if (!string.IsNullOrWhiteSpace(currentAccess) &&
                ((failedAccessToken is null && isFresh) ||
                 (failedAccessToken is not null && !string.Equals(currentAccess, failedAccessToken, StringComparison.Ordinal))))
            {
                return currentAccess;
            }

            var clientId = _database.GetSetting(DatabaseService.KeySpotifyClientId)?.Trim();
            var refreshToken = _database.GetSetting(DatabaseService.KeySpotifyRefreshToken)?.Trim();
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(refreshToken))
            {
                return null;
            }

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            });

            using var resp = await _http.PostAsync("https://accounts.spotify.com/api/token", form, cancellationToken).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _actionLog.Log("Warning", $"Spotify PKCE token refresh failed ({(int)resp.StatusCode}).");
                return null;
            }

            SaveTokenPayload(body);
            return _database.GetSetting(DatabaseService.KeySpotifyAccessToken);
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private void SaveTokenPayload(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var access = GetString(root, "access_token") ?? string.Empty;
        var refresh = GetString(root, "refresh_token");
        var expiresIn = root.TryGetProperty("expires_in", out var exp) && exp.ValueKind == JsonValueKind.Number
            ? exp.GetInt32()
            : 3600;

        _database.SaveSetting(DatabaseService.KeySpotifyAccessToken, access);
        if (!string.IsNullOrWhiteSpace(refresh))
        {
            _database.SaveSetting(DatabaseService.KeySpotifyRefreshToken, refresh);
        }

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        _database.SaveSetting(DatabaseService.KeySpotifyTokenExpiresAt, expiresAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private static SpotifyTrackItem? ParseTrackObject(JsonElement tr, string? contextUri)
    {
        var uri = GetString(tr, "uri");
        var name = GetString(tr, "name");
        if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var id = GetString(tr, "id") ?? uri.Split(':').LastOrDefault() ?? string.Empty;
        var durationMs = tr.TryGetProperty("duration_ms", out var dur) && dur.ValueKind == JsonValueKind.Number
            ? dur.GetInt64()
            : 0;

        var (artistsText, primaryArtistId) = ParseArtistsFromObject(tr);

        var albumName = string.Empty;
        string? albumId = null;
        string? coverUrl = null;
        if (tr.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object)
        {
            albumName = GetString(album, "name") ?? string.Empty;
            albumId = GetString(album, "id");
            coverUrl = PickPreferredImageUrl(album);
        }

        return new SpotifyTrackItem(
            id,
            uri,
            name,
            artistsText,
            albumName,
            coverUrl,
            durationMs,
            contextUri,
            PrimaryArtistId: primaryArtistId,
            AlbumId: albumId);
    }

    private static (string ArtistsText, string? PrimaryArtistId) ParseArtistsFromObject(JsonElement obj)
    {
        var artistNames = new List<string>();
        string? primaryArtistId = null;
        if (obj.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in artists.EnumerateArray())
            {
                var an = GetString(a, "name");
                if (!string.IsNullOrWhiteSpace(an))
                {
                    artistNames.Add(an);
                }

                primaryArtistId ??= GetString(a, "id");
            }
        }

        var text = artistNames.Count > 0 ? string.Join(", ", artistNames) : "Unknown Artist";
        return (text, primaryArtistId);
    }

    private static SpotifyArtistItem? ParseArtistObject(JsonElement a)
    {
        if (a.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetString(a, "id");
        var name = GetString(a, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var uri = GetString(a, "uri") ?? $"spotify:artist:{id}";
        var imageUrl = PickPreferredImageUrl(a);
        long followers = 0;
        if (a.TryGetProperty("followers", out var f) &&
            f.ValueKind == JsonValueKind.Object &&
            f.TryGetProperty("total", out var ft) &&
            ft.ValueKind == JsonValueKind.Number)
        {
            followers = ft.GetInt64();
        }

        var genres = new List<string>();
        if (a.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in g.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        genres.Add(s);
                    }
                }
            }
        }

        var popularity = a.TryGetProperty("popularity", out var pop) && pop.ValueKind == JsonValueKind.Number
            ? pop.GetInt32()
            : 0;

        var subtitle = followers > 0
            ? $"{followers:N0} followers"
            : genres.FirstOrDefault() ?? "Artist";

        return new SpotifyArtistItem(id, uri, name, imageUrl, subtitle, followers, genres, popularity);
    }

    private static SpotifyAlbumItem? ParseAlbumObject(JsonElement al)
    {
        if (al.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetString(al, "id");
        var name = GetString(al, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var uri = GetString(al, "uri") ?? $"spotify:album:{id}";
        var (artistsText, primaryArtistId) = ParseArtistsFromObject(al);
        var releaseDate = GetString(al, "release_date") ?? string.Empty;
        var totalTracks = al.TryGetProperty("total_tracks", out var tt) && tt.ValueKind == JsonValueKind.Number
            ? tt.GetInt32()
            : 0;
        var albumType = GetString(al, "album_type") ?? "album";
        var imageUrl = PickPreferredImageUrl(al);

        return new SpotifyAlbumItem(
            id,
            uri,
            name,
            artistsText,
            releaseDate,
            totalTracks,
            imageUrl,
            albumType,
            primaryArtistId);
    }

    private static SpotifyPlaylistItem? ParsePlaylistObject(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetString(p, "id") ?? string.Empty;
        var uri = GetString(p, "uri") ?? (string.IsNullOrEmpty(id) ? string.Empty : $"spotify:playlist:{id}");
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        var name = GetString(p, "name") ?? "Untitled Playlist";
        var desc = GetString(p, "description") ?? string.Empty;
        var owner = p.TryGetProperty("owner", out var ow) && ow.ValueKind == JsonValueKind.Object
            ? (GetString(ow, "display_name") ?? GetString(ow, "id") ?? "Spotify")
            : "Spotify";

        var count = 0;
        if (p.TryGetProperty("tracks", out var trObj) &&
            trObj.ValueKind == JsonValueKind.Object &&
            trObj.TryGetProperty("total", out var tot) &&
            tot.ValueKind == JsonValueKind.Number)
        {
            count = tot.GetInt32();
        }
        else if (p.TryGetProperty("items", out var itemsObj) &&
                 itemsObj.ValueKind == JsonValueKind.Object &&
                 itemsObj.TryGetProperty("total", out var itemsTot) &&
                 itemsTot.ValueKind == JsonValueKind.Number)
        {
            count = itemsTot.GetInt32();
        }

        var imageUrl = PickPreferredImageUrl(p);
        return new SpotifyPlaylistItem(id, uri, name, desc, owner, count, imageUrl);
    }

    private static string? PickPreferredImageUrl(JsonElement obj)
    {
        string? imageUrl = null;
        if (!obj.TryGetProperty("images", out var imgs) || imgs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var img in imgs.EnumerateArray())
        {
            var u = GetString(img, "url");
            if (!string.IsNullOrWhiteSpace(u))
            {
                imageUrl = u;
                if (img.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number && w.GetInt32() is >= 250 and <= 400)
                {
                    break;
                }
            }
        }

        return imageUrl;
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

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

