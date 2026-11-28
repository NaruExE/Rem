using System.Text.Json;
using REM.Models;

namespace REM.Services;

public sealed class LastFmService : IDisposable
{
    private readonly DatabaseService _database;
    private readonly ActionLogService _actionLog;
    private readonly HttpClient _http;
    private bool _disposed;

    public LastFmService(DatabaseService database, ActionLogService actionLog)
    {
        _database = database;
        _actionLog = actionLog;
        _http = new HttpClient(new BandwidthCountingHandler())
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
    }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_database.GetSetting(DatabaseService.KeySpotifyLastFmApiKey));

    public async Task<List<LastFmRecommendation>> GetFypRecommendationsAsync(
        IReadOnlyList<SpotifyTrackItem> seeds,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var apiKey = _database.GetSetting(DatabaseService.KeySpotifyLastFmApiKey)?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey) || seeds.Count == 0)
        {
            return [];
        }

        var recommendations = new List<LastFmRecommendation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var seed in seeds)
        {
            seen.Add(BuildKey(GetPrimaryArtist(seed.Artists), seed.Name));
        }

        var seedList = seeds.Take(4).ToList();
        using var throttle = new SemaphoreSlim(3, 3);
        var perSeedLists = await Task.WhenAll(seedList.Select(async seed =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new List<LastFmRecommendation>();
                }

                var artist = GetPrimaryArtist(seed.Artists);
                var similar = await GetSimilarTracksAsync(artist, seed.Name, apiKey, 6, cancellationToken).ConfigureAwait(false);
                if (similar.Count == 0)
                {
                    similar = await GetSimilarArtistTopTracksAsync(artist, apiKey, 4, cancellationToken).ConfigureAwait(false);
                }

                return similar;
            }
            finally
            {
                throttle.Release();
            }
        })).ConfigureAwait(false);

        foreach (var similar in perSeedLists)
        {
            foreach (var rec in similar)
            {
                if (seen.Add(BuildKey(rec.Artist, rec.Title)))
                {
                    recommendations.Add(rec);
                    if (recommendations.Count >= maxResults)
                    {
                        return recommendations;
                    }
                }
            }
        }

        return recommendations;
    }

    private async Task<List<LastFmRecommendation>> GetSimilarTracksAsync(
        string artist,
        string track,
        string apiKey,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = new List<LastFmRecommendation>();
        try
        {
            var url = $"https://ws.audioscrobbler.com/2.0/?method=track.getsimilar&artist={Uri.EscapeDataString(artist)}&track={Uri.EscapeDataString(track)}&api_key={Uri.EscapeDataString(apiKey)}&autocorrect=1&limit={limit}&format=json";
            using var resp = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return list;
            }

            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("similartracks", out var root) &&
                root.TryGetProperty("track", out var tracks) &&
                tracks.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in tracks.EnumerateArray())
                {
                    var title = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? recArtist = null;
                    if (item.TryGetProperty("artist", out var a) && a.TryGetProperty("name", out var an))
                    {
                        recArtist = an.GetString();
                    }

                    if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(recArtist))
                    {
                        list.Add(new LastFmRecommendation(recArtist.Trim(), title.Trim()));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _actionLog.Log("Warning", $"Last.fm track.getSimilar failed: {ex.Message}");
        }

        return list;
    }

    private async Task<List<LastFmRecommendation>> GetSimilarArtistTopTracksAsync(
        string artist,
        string apiKey,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = new List<LastFmRecommendation>();
        try
        {
            var simUrl = $"https://ws.audioscrobbler.com/2.0/?method=artist.getsimilar&artist={Uri.EscapeDataString(artist)}&api_key={Uri.EscapeDataString(apiKey)}&autocorrect=1&limit=3&format=json";
            using var simResp = await _http.GetAsync(simUrl, cancellationToken).ConfigureAwait(false);
            if (!simResp.IsSuccessStatusCode)
            {
                return list;
            }

            var simBody = await simResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var simDoc = JsonDocument.Parse(simBody);
            string? similarArtist = null;
            if (simDoc.RootElement.TryGetProperty("similarartists", out var sa) &&
                sa.TryGetProperty("artist", out var artists) &&
                artists.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in artists.EnumerateArray())
                {
                    similarArtist = a.TryGetProperty("name", out var an) ? an.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(similarArtist))
                    {
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(similarArtist))
            {
                return list;
            }

            var topUrl = $"https://ws.audioscrobbler.com/2.0/?method=artist.gettoptracks&artist={Uri.EscapeDataString(similarArtist)}&api_key={Uri.EscapeDataString(apiKey)}&autocorrect=1&limit={limit}&format=json";
            using var topResp = await _http.GetAsync(topUrl, cancellationToken).ConfigureAwait(false);
            if (!topResp.IsSuccessStatusCode)
            {
                return list;
            }

            var topBody = await topResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var topDoc = JsonDocument.Parse(topBody);
            if (topDoc.RootElement.TryGetProperty("toptracks", out var tt) &&
                tt.TryGetProperty("track", out var tracks) &&
                tracks.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in tracks.EnumerateArray())
                {
                    var title = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        list.Add(new LastFmRecommendation(similarArtist.Trim(), title.Trim()));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _actionLog.Log("Warning", $"Last.fm artist.getSimilar failed: {ex.Message}");
        }

        return list;
    }

    private static string GetPrimaryArtist(string artists)
    {
        var idx = artists.IndexOf(',');
        return (idx >= 0 ? artists[..idx] : artists).Trim();
    }

    private static string BuildKey(string artist, string title) =>
        $"{artist.Trim().ToLowerInvariant()}::{title.Trim().ToLowerInvariant()}";

    public async Task<LastFmArtistProfile?> GetArtistProfileAsync(
        string artistName,
        CancellationToken cancellationToken)
    {
        var apiKey = _database.GetSetting(DatabaseService.KeySpotifyLastFmApiKey)?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(artistName))
        {
            return null;
        }

        try
        {
            var url = $"https://ws.audioscrobbler.com/2.0/?method=artist.getinfo&artist={Uri.EscapeDataString(artistName.Trim())}&api_key={Uri.EscapeDataString(apiKey)}&autocorrect=1&format=json";
            using var resp = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("artist", out var art) || art.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var resolvedName = art.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? artistName : artistName;
            long listeners = 0;
            long playCount = 0;
            if (art.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
            {
                if (stats.TryGetProperty("listeners", out var lEl)) long.TryParse(lEl.GetString(), out listeners);
                if (stats.TryGetProperty("playcount", out var pEl)) long.TryParse(pEl.GetString(), out playCount);
            }

            string? bio = null;
            if (art.TryGetProperty("bio", out var bioEl) &&
                bioEl.TryGetProperty("summary", out var sumEl) &&
                sumEl.ValueKind == JsonValueKind.String)
            {
                var rawBio = sumEl.GetString();
                if (!string.IsNullOrWhiteSpace(rawBio))
                {
                    var anchorIdx = rawBio.IndexOf("<a ", StringComparison.OrdinalIgnoreCase);
                    bio = (anchorIdx >= 0 ? rawBio[..anchorIdx] : rawBio).Trim();
                }
            }

            var tags = new List<string>();
            if (art.TryGetProperty("tags", out var tagsWrap) &&
                tagsWrap.TryGetProperty("tag", out var tagArr) &&
                tagArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tagArr.EnumerateArray())
                {
                    var tn = t.TryGetProperty("name", out var tnEl) ? tnEl.GetString()?.Trim() : null;
                    if (!string.IsNullOrWhiteSpace(tn) && tags.Count < 6)
                    {
                        tags.Add(tn);
                    }
                }
            }

            var similar = new List<string>();
            if (art.TryGetProperty("similar", out var simWrap) &&
                simWrap.TryGetProperty("artist", out var simArr) &&
                simArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in simArr.EnumerateArray())
                {
                    var sn = s.TryGetProperty("name", out var snEl) ? snEl.GetString()?.Trim() : null;
                    if (!string.IsNullOrWhiteSpace(sn))
                    {
                        similar.Add(sn);
                    }
                }
            }

            return new LastFmArtistProfile(resolvedName, bio, listeners, playCount, tags, similar);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<string>> GetArtistTopTagsAsync(string artistName, CancellationToken cancellationToken)
    {
        var apiKey = _database.GetSetting(DatabaseService.KeySpotifyLastFmApiKey)?.Trim();
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(artistName))
        {
            return list;
        }

        try
        {
            var url = $"https://ws.audioscrobbler.com/2.0/?method=artist.gettoptags&artist={Uri.EscapeDataString(artistName.Trim())}&api_key={Uri.EscapeDataString(apiKey)}&autocorrect=1&format=json";
            using var resp = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return list;

            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("toptags", out var wrap) &&
                wrap.TryGetProperty("tag", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in arr.EnumerateArray())
                {
                    var name = t.TryGetProperty("name", out var nEl) ? nEl.GetString()?.Trim() : null;
                    if (!string.IsNullOrWhiteSpace(name) &&
                        !string.Equals(name, "seen live", StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(name);
                        if (list.Count >= 5) break;
                    }
                }
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

    public async Task<List<LastFmRecommendation>> GetTrackRadioSeedsAsync(
        string artist,
        string track,
        int limit,
        bool preferDeepCuts,
        CancellationToken cancellationToken)
    {
        var apiKey = _database.GetSetting(DatabaseService.KeySpotifyLastFmApiKey)?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        var fetchCount = preferDeepCuts ? Math.Clamp(limit * 2, 20, 60) : Math.Clamp(limit, 10, 40);
        var similar = await GetSimilarTracksAsync(GetPrimaryArtist(artist), track, apiKey, fetchCount, cancellationToken)
            .ConfigureAwait(false);

        if (preferDeepCuts && similar.Count > 12)
        {
            // Skip top ultra-mainstream picks when Deep Cuts mode is active.
            similar = similar.Skip(Math.Min(6, similar.Count / 4)).Take(limit).ToList();
        }

        if (similar.Count < 8)
        {
            var artistSeeds = await GetArtistRadioSeedsAsync(GetPrimaryArtist(artist), limit, preferDeepCuts, cancellationToken)
                .ConfigureAwait(false);
            var seen = new HashSet<string>(similar.Select(s => BuildKey(s.Artist, s.Title)), StringComparer.OrdinalIgnoreCase);
            foreach (var rec in artistSeeds)
            {
                if (seen.Add(BuildKey(rec.Artist, rec.Title)))
                {
                    similar.Add(rec);
                }
            }
        }

        return similar.Take(limit).ToList();
    }

    public async Task<List<LastFmRecommendation>> GetArtistRadioSeedsAsync(
        string artist,
        int limit,
        bool preferDeepCuts,
        CancellationToken cancellationToken)
    {
        var apiKey = _database.GetSetting(DatabaseService.KeySpotifyLastFmApiKey)?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(artist))
        {
            return [];
        }

        var list = new List<LastFmRecommendation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var simUrl = $"https://ws.audioscrobbler.com/2.0/?method=artist.getsimilar&artist={Uri.EscapeDataString(artist.Trim())}&api_key={Uri.EscapeDataString(apiKey)}&autocorrect=1&limit=10&format=json";
            using var simResp = await _http.GetAsync(simUrl, cancellationToken).ConfigureAwait(false);
            var artistPool = new List<string>();
            if (simResp.IsSuccessStatusCode)
            {
                var body = await simResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("similarartists", out var sa) &&
                    sa.TryGetProperty("artist", out var arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in arr.EnumerateArray())
                    {
                        var sn = a.TryGetProperty("name", out var nEl) ? nEl.GetString()?.Trim() : null;
                        if (!string.IsNullOrWhiteSpace(sn)) artistPool.Add(sn);
                    }
                }
            }

            if (preferDeepCuts && artistPool.Count > 4)
            {
                artistPool = artistPool.Skip(3).ToList();
            }

            artistPool.Insert(0, artist.Trim());
            foreach (var candidateArtist in artistPool.Take(7))
            {
                if (list.Count >= limit || cancellationToken.IsCancellationRequested) break;
                var topUrl = $"https://ws.audioscrobbler.com/2.0/?method=artist.gettoptracks&artist={Uri.EscapeDataString(candidateArtist)}&api_key={Uri.EscapeDataString(apiKey)}&autocorrect=1&limit=6&format=json";
                using var topResp = await _http.GetAsync(topUrl, cancellationToken).ConfigureAwait(false);
                if (!topResp.IsSuccessStatusCode) continue;

                var topBody = await topResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var topDoc = JsonDocument.Parse(topBody);
                if (topDoc.RootElement.TryGetProperty("toptracks", out var tt) &&
                    tt.TryGetProperty("track", out var tracks) &&
                    tracks.ValueKind == JsonValueKind.Array)
                {
                    var items = tracks.EnumerateArray().Skip(preferDeepCuts ? 2 : 0).Take(4);
                    foreach (var tr in items)
                    {
                        var tn = tr.TryGetProperty("name", out var nEl) ? nEl.GetString()?.Trim() : null;
                        if (!string.IsNullOrWhiteSpace(tn) && seen.Add(BuildKey(candidateArtist, tn)))
                        {
                            list.Add(new LastFmRecommendation(candidateArtist, tn));
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        return list.Take(limit).ToList();
    }

    public async Task<List<LastFmRecommendation>> GetTagTopTracksAsync(
        string tag,
        int limit,
        CancellationToken cancellationToken)
    {
        var apiKey = _database.GetSetting(DatabaseService.KeySpotifyLastFmApiKey)?.Trim();
        var list = new List<LastFmRecommendation>();
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(tag))
        {
            return list;
        }

        try
        {
            var url = $"https://ws.audioscrobbler.com/2.0/?method=tag.gettoptracks&tag={Uri.EscapeDataString(tag.Trim())}&api_key={Uri.EscapeDataString(apiKey)}&limit={Math.Clamp(limit, 5, 30)}&format=json";
            using var resp = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return list;

            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("tracks", out var wrap) &&
                wrap.TryGetProperty("track", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var tr in arr.EnumerateArray())
                {
                    var title = tr.TryGetProperty("name", out var nEl) ? nEl.GetString()?.Trim() : null;
                    var artist = tr.TryGetProperty("artist", out var aEl) && aEl.TryGetProperty("name", out var anEl)
                        ? anEl.GetString()?.Trim()
                        : null;
                    if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(artist))
                    {
                        list.Add(new LastFmRecommendation(artist, title));
                    }
                }
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

    public async Task<List<string>> GetArtistTopTrackNamesAsync(
        string artistName,
        int limit,
        CancellationToken cancellationToken)
    {
        var apiKey = _database.GetSetting(DatabaseService.KeySpotifyLastFmApiKey)?.Trim();
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(artistName))
        {
            return list;
        }

        try
        {
            var url = $"https://ws.audioscrobbler.com/2.0/?method=artist.gettoptracks&artist={Uri.EscapeDataString(artistName.Trim())}&api_key={Uri.EscapeDataString(apiKey)}&autocorrect=1&limit={Math.Clamp(limit, 5, 30)}&format=json";
            using var resp = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return list;
            }

            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("toptracks", out var tt) &&
                tt.TryGetProperty("track", out var tracks) &&
                tracks.ValueKind == JsonValueKind.Array)
            {
                foreach (var tr in tracks.EnumerateArray())
                {
                    var name = tr.TryGetProperty("name", out var nEl) ? nEl.GetString()?.Trim() : null;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        list.Add(name);
                    }
                }
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
    }
}

