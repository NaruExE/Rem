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
    private const string Scopes = "user-read-private user-library-read user-library-modify playlist-read-private playlist-read-collaborative playlist-modify-public playlist-modify-private user-read-recently-played user-top-read";
    private const long MaxArtCacheBytes = 50L * 1024L * 1024L;

    private readonly DatabaseService _database;
    private readonly ActionLogService _actionLog;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private readonly string _artCacheDir;
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
        var token = await EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        using var resp = await SendAuthorizedAsync(HttpMethod.Get, url, token, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

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
            var refreshed = await ForceRefreshAccessTokenAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task<string?> ForceRefreshAccessTokenAsync(CancellationToken cancellationToken)
    {
        var clientId = _database.GetSetting(DatabaseService.KeySpotifyClientId)?.Trim();
        var refreshToken = _database.GetSetting(DatabaseService.KeySpotifyRefreshToken)?.Trim();
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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

        var artistNames = new List<string>();
        if (tr.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in artists.EnumerateArray())
            {
                var an = GetString(a, "name");
                if (!string.IsNullOrWhiteSpace(an))
                {
                    artistNames.Add(an);
                }
            }
        }

        var albumName = string.Empty;
        string? coverUrl = null;
        if (tr.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object)
        {
            albumName = GetString(album, "name") ?? string.Empty;
            if (album.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
            {
                foreach (var img in imgs.EnumerateArray())
                {
                    var u = GetString(img, "url");
                    if (!string.IsNullOrWhiteSpace(u))
                    {
                        coverUrl = u;
                        if (img.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number && w.GetInt32() is >= 250 and <= 400)
                        {
                            break;
                        }
                    }
                }
            }
        }

        var artistsText = artistNames.Count > 0 ? string.Join(", ", artistNames) : "Unknown Artist";
        return new SpotifyTrackItem(id, uri, name, artistsText, albumName, coverUrl, durationMs, contextUri);
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

