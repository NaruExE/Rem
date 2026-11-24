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

        foreach (var seed in seeds.Take(4))
        {
            if (recommendations.Count >= maxResults || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var artist = GetPrimaryArtist(seed.Artists);
            var similar = await GetSimilarTracksAsync(artist, seed.Name, apiKey, 6, cancellationToken).ConfigureAwait(false);
            if (similar.Count == 0)
            {
                similar = await GetSimilarArtistTopTracksAsync(artist, apiKey, 4, cancellationToken).ConfigureAwait(false);
            }

            foreach (var rec in similar)
            {
                if (seen.Add(BuildKey(rec.Artist, rec.Title)))
                {
                    recommendations.Add(rec);
                    if (recommendations.Count >= maxResults)
                    {
                        break;
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

