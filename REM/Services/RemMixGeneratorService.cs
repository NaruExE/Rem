using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using REM.Models;

namespace REM.Services;

/// <summary>
/// Generates personal REM mixes (Discover Weekly, Daylist, Daily Mix 1-3, Release Radar)
/// and Song/Artist Radio using Last.fm, ListenBrainz fallback, local play history, and Spotify Web API.
/// </summary>
public sealed partial class RemMixGeneratorService : IDisposable
{
    private readonly DatabaseService _database;
    private readonly SpotifyWebApiService _webApi;
    private readonly LastFmService _lastFm;
    private readonly ActionLogService _actionLog;
    private readonly HttpClient _http;
    private bool _disposed;

    [GeneratedRegex(@"\s*[\(\[](?:feat\.?|ft\.?|with|remaster(?:ed)?|live|version|edit|mono|stereo)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CleanParenRegex();

    public RemMixGeneratorService(
        DatabaseService database,
        SpotifyWebApiService webApi,
        LastFmService lastFm,
        ActionLogService actionLog)
    {
        _database = database;
        _webApi = webApi;
        _lastFm = lastFm;
        _actionLog = actionLog;
        _http = new HttpClient(new BandwidthCountingHandler())
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("REM-Spotify/1.0");
    }

    public async Task<RemMixDescriptor> BuildSongRadioAsync(
        SpotifyTrackItem seed,
        bool preferDeepCuts,
        CancellationToken cancellationToken)
    {
        var recs = await _lastFm.GetTrackRadioSeedsAsync(seed.Artists, seed.Name, 26, preferDeepCuts, cancellationToken)
            .ConfigureAwait(false);
        if (recs.Count < 6)
        {
            var lb = await FetchListenBrainzTagTracksAsync(GetPrimaryArtist(seed.Artists), cancellationToken)
                .ConfigureAwait(false);
            recs.AddRange(lb);
        }

        var tracks = await ResolveRecommendationsToSpotifyAsync(recs, targetCount: 16, skipUris: [seed.Uri], cancellationToken)
            .ConfigureAwait(false);
        if (tracks.Count == 0 && !_webApi.IsRateLimited)
        {
            var fallbackArtist = GetPrimaryArtist(seed.Artists);
            tracks = await _webApi.SearchTracksAsync(fallbackArtist, 10, cancellationToken)
                .ConfigureAwait(false);
        }

        return new RemMixDescriptor(
            Key: $"song-radio:{seed.Id}",
            Title: $"{seed.Name} Radio",
            Subtitle: $"Based on {seed.Name} · {GetPrimaryArtist(seed.Artists)}{(preferDeepCuts ? " · Deep Cuts" : "")}",
            Badge: "SONG RADIO",
            AccentHex: "#1DB954",
            GeneratedAtUtc: DateTime.UtcNow,
            Tracks: tracks);
    }

    public async Task<RemMixDescriptor> BuildArtistRadioAsync(
        SpotifyArtistItem artist,
        bool preferDeepCuts,
        CancellationToken cancellationToken)
    {
        var recs = await _lastFm.GetArtistRadioSeedsAsync(artist.Name, 24, preferDeepCuts, cancellationToken)
            .ConfigureAwait(false);
        if (recs.Count < 6)
        {
            var lb = await FetchListenBrainzTagTracksAsync(artist.Name, cancellationToken).ConfigureAwait(false);
            recs.AddRange(lb);
        }

        var tracks = await ResolveRecommendationsToSpotifyAsync(recs, targetCount: 16, skipUris: [], cancellationToken)
            .ConfigureAwait(false);
        if (tracks.Count == 0 && !_webApi.IsRateLimited)
        {
            tracks = await _webApi.GetArtistTopTracksAsync(artist.Id, artist.Name, cancellationToken).ConfigureAwait(false);
        }

        return new RemMixDescriptor(
            Key: $"artist-radio:{artist.Id}",
            Title: $"{artist.Name} Radio",
            Subtitle: $"Similar artists & top tracks{(preferDeepCuts ? " · Deep Cuts" : "")}",
            Badge: "ARTIST RADIO",
            AccentHex: "#8B5CF6",
            GeneratedAtUtc: DateTime.UtcNow,
            Tracks: tracks);
    }

    public async Task<RemMixDescriptor> BuildDiscoverWeeklyAsync(
        bool forceRefresh,
        bool preferDeepCuts,
        CancellationToken cancellationToken)
    {
        const string cacheKey = "rem-discover-weekly";
        if (!forceRefresh)
        {
            var cached = await _database.GetSpotifyCachedMixAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            if (cached is not null && cached.Tracks.Count > 0 && (DateTime.UtcNow - cached.GeneratedAtUtc).TotalDays < 7)
            {
                return cached;
            }
        }

        var topTracks = await _webApi.GetTopTracksAsync(12, cancellationToken).ConfigureAwait(false);
        var recent = await _webApi.GetRecentlyPlayedAsync(15, cancellationToken).ConfigureAwait(false);
        var liked = await _webApi.GetLikedSongsAsync(cancellationToken).ConfigureAwait(false);
        var localHistory = await _database.GetSpotifyRecentPlayHistoryAsync(200, cancellationToken).ConfigureAwait(false);

        var knownUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in topTracks.Concat(recent).Concat(liked))
        {
            knownUris.Add(t.Uri);
        }
        foreach (var h in localHistory)
        {
            knownUris.Add(h.TrackUri);
        }

        var seeds = topTracks.Concat(recent).Take(8).ToList();
        var recs = new List<LastFmRecommendation>();
        if (_lastFm.HasApiKey && seeds.Count > 0)
        {
            var fypRecs = await _lastFm.GetFypRecommendationsAsync(seeds, 28, cancellationToken).ConfigureAwait(false);
            recs.AddRange(fypRecs);
            if (preferDeepCuts)
            {
                recs = recs.Skip(Math.Min(6, recs.Count / 4)).ToList();
            }
        }

        var tracks = await ResolveRecommendationsToSpotifyAsync(recs, targetCount: 16, skipUris: knownUris, cancellationToken)
            .ConfigureAwait(false);
        if (tracks.Count < 6 && seeds.Count > 0 && !_webApi.IsRateLimited)
        {
            foreach (var s in seeds.Take(3))
            {
                var alt = await _webApi.SearchTracksAsync(GetPrimaryArtist(s.Artists), 6, cancellationToken).ConfigureAwait(false);
                foreach (var t in alt)
                {
                    if (knownUris.Add(t.Uri)) tracks.Add(t);
                }
            }
        }

        var mix = new RemMixDescriptor(
            Key: cacheKey,
            Title: "REM Discover Weekly",
            Subtitle: $"Fresh finds filtered against your library & play history{(preferDeepCuts ? " · Deep Cuts" : "")}",
            Badge: "WEEKLY",
            AccentHex: "#1DB954",
            GeneratedAtUtc: DateTime.UtcNow,
            Tracks: tracks);

        if (tracks.Count > 0)
        {
            await _database.SaveSpotifyCachedMixAsync(mix, cancellationToken).ConfigureAwait(false);
        }

        return mix;
    }

    public async Task<RemMixDescriptor> BuildDaylistAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var nowLocal = DateTime.Now;
        var slot = GetTimeSlot(nowLocal.Hour);
        var cacheKey = $"rem-daylist-{slot.Key}";

        if (!forceRefresh)
        {
            var cached = await _database.GetSpotifyCachedMixAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            if (cached is not null && cached.Tracks.Count > 0 && (DateTime.UtcNow - cached.GeneratedAtUtc).TotalHours < 4)
            {
                return cached;
            }
        }

        var localHistory = await _database.GetSpotifyRecentPlayHistoryAsync(250, cancellationToken).ConfigureAwait(false);
        var slotArtistPool = localHistory
            .Where(h => GetTimeSlot(h.HourOfDay).Key == slot.Key)
            .Select(h => h.ArtistName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        var recent = await _webApi.GetRecentlyPlayedAsync(20, cancellationToken).ConfigureAwait(false);
        var topTracks = await _webApi.GetTopTracksAsync(12, cancellationToken).ConfigureAwait(false);
        if (slotArtistPool.Count == 0)
        {
            slotArtistPool = recent.Concat(topTracks)
                .Select(t => GetPrimaryArtist(t.Artists))
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();
        }

        var tags = new List<string>();
        foreach (var artist in slotArtistPool.Take(3))
        {
            var artistTags = await _lastFm.GetArtistTopTagsAsync(artist, cancellationToken).ConfigureAwait(false);
            foreach (var tag in artistTags)
            {
                if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
            }
        }

        var titleTags = tags.Take(2).ToList();
        var dynamicTitle = titleTags.Count >= 2
            ? $"{slot.Label} {ToTitle(titleTags[0])} & {ToTitle(titleTags[1])}"
            : titleTags.Count == 1
                ? $"{slot.Label} {ToTitle(titleTags[0])}"
                : $"{slot.Label} Rotation";

        var recs = new List<LastFmRecommendation>();
        foreach (var tag in titleTags)
        {
            var tagTracks = await _lastFm.GetTagTopTracksAsync(tag, 12, cancellationToken).ConfigureAwait(false);
            recs.AddRange(tagTracks);
        }

        var resolved = await ResolveRecommendationsToSpotifyAsync(recs, targetCount: 16, skipUris: [], cancellationToken)
            .ConfigureAwait(false);
        var combined = new List<SpotifyTrackItem>();
        var seenUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var familiar in recent.Concat(topTracks).Take(8))
        {
            if (seenUris.Add(familiar.Uri)) combined.Add(familiar);
        }
        foreach (var r in resolved)
        {
            if (seenUris.Add(r.Uri)) combined.Add(r);
        }

        var mix = new RemMixDescriptor(
            Key: cacheKey,
            Title: dynamicTitle,
            Subtitle: $"Daylist for your {slot.Label.ToLowerInvariant()} listening window",
            Badge: "DAYLIST",
            AccentHex: slot.AccentHex,
            GeneratedAtUtc: DateTime.UtcNow,
            Tracks: combined);

        if (combined.Count > 0)
        {
            await _database.SaveSpotifyCachedMixAsync(mix, cancellationToken).ConfigureAwait(false);
        }

        return mix;
    }

    public async Task<RemMixDescriptor> BuildDailyMixAsync(
        int mixIndex,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var idx = Math.Clamp(mixIndex, 1, 3);
        var cacheKey = $"rem-daily-mix-{idx}";
        if (!forceRefresh)
        {
            var cached = await _database.GetSpotifyCachedMixAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            if (cached is not null && cached.Tracks.Count > 0 && (DateTime.UtcNow - cached.GeneratedAtUtc).TotalHours < 20)
            {
                return cached;
            }
        }

        var topArtists = await _webApi.GetTopArtistsAsync(18, "medium_term", cancellationToken).ConfigureAwait(false);
        if (topArtists.Count == 0)
        {
            var topTracks = await _webApi.GetTopTracksAsync(18, cancellationToken).ConfigureAwait(false);
            var artistNames = topTracks.Select(t => GetPrimaryArtist(t.Artists))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var name in artistNames)
            {
                topArtists.Add(new SpotifyArtistItem(string.Empty, string.Empty, name, null));
            }
        }

        // Partition top artists cleanly into 3 mix slices.
        var slice = topArtists
            .Where((_, i) => (i % 3) == (idx - 1))
            .Take(5)
            .ToList();
        if (slice.Count == 0)
        {
            slice = topArtists.Take(4).ToList();
        }

        var recs = new List<LastFmRecommendation>();
        foreach (var artist in slice.Take(4))
        {
            var seeds = await _lastFm.GetArtistRadioSeedsAsync(artist.Name, 8, preferDeepCuts: false, cancellationToken)
                .ConfigureAwait(false);
            recs.AddRange(seeds);
        }

        var tracks = await ResolveRecommendationsToSpotifyAsync(recs, targetCount: 16, skipUris: [], cancellationToken)
            .ConfigureAwait(false);
        if (tracks.Count < 8 && !_webApi.IsRateLimited)
        {
            foreach (var artist in slice.Take(3))
            {
                if (_webApi.IsRateLimited || cancellationToken.IsCancellationRequested) break;
                var top = await _webApi.GetArtistTopTracksAsync(artist.Id, artist.Name, cancellationToken).ConfigureAwait(false);
                tracks.AddRange(top.Take(5));
            }
            tracks = tracks.DistinctBy(t => t.Uri).ToList();
        }

        var artistNamesSubtitle = slice.Count > 0
            ? string.Join(", ", slice.Take(3).Select(a => a.Name)) + (slice.Count > 3 ? " & more" : "")
            : "Mixed from your top rotation";

        var accents = new[] { "#1DB954", "#3B82F6", "#F59E0B" };
        var mix = new RemMixDescriptor(
            Key: cacheKey,
            Title: $"REM Daily Mix {idx}",
            Subtitle: artistNamesSubtitle,
            Badge: $"MIX {idx}",
            AccentHex: accents[idx - 1],
            GeneratedAtUtc: DateTime.UtcNow,
            Tracks: tracks);

        if (tracks.Count > 0)
        {
            await _database.SaveSpotifyCachedMixAsync(mix, cancellationToken).ConfigureAwait(false);
        }

        return mix;
    }

    public async Task<RemMixDescriptor> BuildReleaseRadarAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        const string cacheKey = "rem-release-radar";
        if (!forceRefresh)
        {
            var cached = await _database.GetSpotifyCachedMixAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            if (cached is not null && cached.Tracks.Count > 0 && (DateTime.UtcNow - cached.GeneratedAtUtc).TotalHours < 20)
            {
                return cached;
            }
        }

        var followed = await _webApi.GetFollowedArtistsAsync(12, cancellationToken).ConfigureAwait(false);
        var topArtists = await _webApi.GetTopArtistsAsync(12, "short_term", cancellationToken).ConfigureAwait(false);

        var artists = followed.Concat(topArtists)
            .Where(a => !string.IsNullOrWhiteSpace(a.Id))
            .DistinctBy(a => a.Id)
            .Take(6)
            .ToList();

        var cutoff = DateTime.UtcNow.AddDays(-45);
        var recentReleases = new List<(SpotifyAlbumItem Album, DateTime ReleaseDt)>();

        for (var i = 0; i < artists.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested || _webApi.IsRateLimited) break;
            if (i > 0)
            {
                await Task.Delay(160, cancellationToken).ConfigureAwait(false);
            }

            var albums = await _webApi.GetArtistAlbumsAsync(artists[i].Id, 5, cancellationToken).ConfigureAwait(false);
            foreach (var al in albums)
            {
                if (TryParseReleaseDate(al.ReleaseDate, out var dt) && dt >= cutoff)
                {
                    recentReleases.Add((al, dt));
                }
            }
        }

        var tracks = new List<SpotifyTrackItem>();
        var seenUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (album, _) in recentReleases.OrderByDescending(x => x.ReleaseDt).Take(6))
        {
            if (cancellationToken.IsCancellationRequested || _webApi.IsRateLimited) break;
            var (_, albumTracks) = await _webApi.GetAlbumAsync(album.Id, cancellationToken).ConfigureAwait(false);
            foreach (var tr in albumTracks.Take(2))
            {
                if (seenUris.Add(tr.Uri)) tracks.Add(tr);
            }
        }

        var mix = new RemMixDescriptor(
            Key: cacheKey,
            Title: "REM Release Radar",
            Subtitle: "Recent albums & singles from artists you follow and listen to",
            Badge: "NEW DROPS",
            AccentHex: "#EC4899",
            GeneratedAtUtc: DateTime.UtcNow,
            Tracks: tracks);

        if (tracks.Count > 0)
        {
            await _database.SaveSpotifyCachedMixAsync(mix, cancellationToken).ConfigureAwait(false);
        }

        return mix;
    }

    private async Task<List<SpotifyTrackItem>> ResolveRecommendationsToSpotifyAsync(
        IReadOnlyList<LastFmRecommendation> recs,
        int targetCount,
        IReadOnlyCollection<string> skipUris,
        CancellationToken cancellationToken)
    {
        var results = new List<SpotifyTrackItem>();
        if (recs.Count == 0) return results;

        var cappedTarget = Math.Clamp(targetCount, 1, 16);
        var seenUris = new HashSet<string>(skipUris, StringComparer.OrdinalIgnoreCase);
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var networkLookups = 0;

        foreach (var rec in recs)
        {
            if (cancellationToken.IsCancellationRequested || results.Count >= cappedTarget)
            {
                break;
            }

            var cleanTitle = CleanParenRegex().Replace(rec.Title, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cleanTitle) || string.IsNullOrWhiteSpace(rec.Artist))
            {
                continue;
            }

            var lookupKey = $"{rec.Artist.Trim().ToLowerInvariant()}::{cleanTitle.ToLowerInvariant()}";
            SpotifyTrackItem? hit = await _database.GetResolvedSpotifyTrackAsync(lookupKey, cancellationToken)
                .ConfigureAwait(false);

            if (hit is null)
            {
                if (_webApi.IsRateLimited || networkLookups >= 16)
                {
                    if (_webApi.IsRateLimited) break;
                    continue;
                }

                if (networkLookups > 0)
                {
                    await Task.Delay(260, cancellationToken).ConfigureAwait(false);
                }

                networkLookups++;
                var hits = await _webApi.SearchTracksAsync($"{rec.Artist} {cleanTitle}", 1, cancellationToken)
                    .ConfigureAwait(false);
                hit = hits.FirstOrDefault();
                if (hit is not null)
                {
                    await _database.SaveResolvedSpotifyTrackAsync(lookupKey, hit, cancellationToken).ConfigureAwait(false);
                }
            }

            if (hit is null) continue;
            var titleKey = $"{hit.Artists.ToLowerInvariant()}::{hit.Name.ToLowerInvariant()}";
            if (seenUris.Add(hit.Uri) && seenTitles.Add(titleKey))
            {
                results.Add(hit);
            }
        }

        return results;
    }

    private async Task<List<LastFmRecommendation>> FetchListenBrainzTagTracksAsync(
        string artistName,
        CancellationToken cancellationToken)
    {
        var list = new List<LastFmRecommendation>();
        if (string.IsNullOrWhiteSpace(artistName)) return list;

        try
        {
            // Lightweight public MusicBrainz recording search fallback when Last.fm API key is absent.
            var url = $"https://musicbrainz.org/ws/2/recording/?query=artist:{Uri.EscapeDataString($"\"{artistName.Trim()}\"")}&limit=8&fmt=json";
            using var resp = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return list;

            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("recordings", out var recs) && recs.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in recs.EnumerateArray())
                {
                    var title = r.TryGetProperty("title", out var tEl) ? tEl.GetString()?.Trim() : null;
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        list.Add(new LastFmRecommendation(artistName.Trim(), title));
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

    private static (string Key, string Label, string AccentHex) GetTimeSlot(int hour) => hour switch
    {
        >= 6 and < 12 => ("morning", "Morning", "#F59E0B"),
        >= 12 and < 18 => ("afternoon", "Afternoon", "#3B82F6"),
        >= 18 and < 23 => ("evening", "Evening", "#8B5CF6"),
        _ => ("latenight", "Late Night", "#EC4899")
    };

    private static string GetPrimaryArtist(string artists)
    {
        var idx = artists.IndexOf(',');
        return (idx >= 0 ? artists[..idx] : artists).Trim();
    }

    private static string ToTitle(string raw) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw.Trim().ToLowerInvariant());

    private static bool TryParseReleaseDate(string raw, out DateTime dt)
    {
        if (DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dt))
        {
            return true;
        }

        if (DateTime.TryParseExact(raw, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dt))
        {
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
