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
        var cal = CultureInfo.InvariantCulture.Calendar;
        var weekNum = cal.GetWeekOfYear(DateTime.Now, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
        var cacheKey = $"rem-discover-weekly-{DateTime.Now:yyyy}-W{weekNum:D2}";
        if (!forceRefresh)
        {
            var cached = await _database.GetSpotifyCachedMixAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            if (cached is not null && cached.Tracks.Count > 0 && cached.GeneratedAtUtc.Date >= DateTime.UtcNow.Date.AddDays(-7))
            {
                return cached;
            }
        }

        // Seed from medium-term & all-time taste (not last 20 songs) so one recent session doesn't hijack Discover Weekly.
        var medTracksTask = _webApi.GetTopTracksAsync(14, cancellationToken, "medium_term");
        var longTracksTask = _webApi.GetTopTracksAsync(14, cancellationToken, "long_term");
        var likedTask = _webApi.GetLikedSongsAsync(cancellationToken);
        await Task.WhenAll(medTracksTask, longTracksTask, likedTask).ConfigureAwait(false);

        var medTracks = await medTracksTask.ConfigureAwait(false);
        var longTracks = await longTracksTask.ConfigureAwait(false);
        var liked = await likedTask.ConfigureAwait(false);
        var localHistory = await _database.GetSpotifyRecentPlayHistoryAsync(250, cancellationToken).ConfigureAwait(false);

        var knownUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in medTracks.Concat(longTracks).Concat(liked))
        {
            knownUris.Add(t.Uri);
        }
        foreach (var h in localHistory)
        {
            knownUris.Add(h.TrackUri);
        }

        var seeds = medTracks.Concat(longTracks).DistinctBy(t => t.Uri).Take(8).ToList();
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
            Subtitle: $"Weekly finds tuned to your long-term taste{(preferDeepCuts ? " · Deep Cuts" : "")}",
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
        var cacheKey = $"rem-daylist-{slot.Key}-{nowLocal:yyyy-MM-dd}";

        if (!forceRefresh)
        {
            var cached = await _database.GetSpotifyCachedMixAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            if (cached is not null && cached.Tracks.Count > 0)
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

        var medTracksTask = _webApi.GetTopTracksAsync(14, cancellationToken, "medium_term");
        var recentTask = _webApi.GetRecentlyPlayedAsync(12, cancellationToken);
        await Task.WhenAll(medTracksTask, recentTask).ConfigureAwait(false);

        var topTracks = await medTracksTask.ConfigureAwait(false);
        var recent = await recentTask.ConfigureAwait(false);
        if (slotArtistPool.Count == 0)
        {
            slotArtistPool = topTracks.Concat(recent)
                .Select(t => GetPrimaryArtist(t.Artists))
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();
        }

        var tags = new List<string>();
        using var dayTagGate = new SemaphoreSlim(3, 3);
        var dayTagLists = await Task.WhenAll(slotArtistPool.Take(3).Select(async artist =>
        {
            await dayTagGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await _lastFm.GetArtistTopTagsAsync(artist, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                dayTagGate.Release();
            }
        })).ConfigureAwait(false);
        foreach (var artistTags in dayTagLists)
        {
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

        foreach (var familiar in topTracks.Concat(recent).Take(8))
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
        var todayKey = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var cacheKey = $"rem-daily-mix-{idx}-{todayKey}";
        if (!forceRefresh)
        {
            var cached = await _database.GetSpotifyCachedMixAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            if (cached is not null && cached.Tracks.Count > 0)
            {
                return cached;
            }
        }

        // Pull medium_term + long_term top artists so daily mixes reflect genuine distinct tastes, not the last 20 songs.
        var medArtistsTask = _webApi.GetTopArtistsAsync(20, "medium_term", cancellationToken);
        var longArtistsTask = _webApi.GetTopArtistsAsync(20, "long_term", cancellationToken);
        await Task.WhenAll(medArtistsTask, longArtistsTask).ConfigureAwait(false);

        var allArtists = medArtistsTask.Result
            .Concat(longArtistsTask.Result)
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .GroupBy(a => string.IsNullOrWhiteSpace(a.Id) ? a.Name : a.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (allArtists.Count == 0)
        {
            var topTracks = await _webApi.GetTopTracksAsync(18, cancellationToken, "medium_term").ConfigureAwait(false);
            foreach (var name in topTracks.Select(t => GetPrimaryArtist(t.Artists)).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                allArtists.Add(new SpotifyArtistItem(string.Empty, string.Empty, name, null));
            }
        }

        // Enrich missing genre info via Last.fm tags when Spotify omitted genres, bounded to top 12 artists.
        var enrichedArtists = new List<(SpotifyArtistItem Artist, string BucketLabel)>(allArtists.Count);
        using var tagGate = new SemaphoreSlim(3, 3);
        var enrichTasks = allArtists.Take(15).Select(async a =>
        {
            string? bucket = ClassifyGenreBucket(a.Genres);
            if (bucket is null && _lastFm.HasApiKey)
            {
                await tagGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var lfmTags = await _lastFm.GetArtistTopTagsAsync(a.Name, cancellationToken).ConfigureAwait(false);
                    bucket = ClassifyGenreBucket(lfmTags) ?? (lfmTags.Count > 0 ? ToTitle(lfmTags[0]) : null);
                }
                finally
                {
                    tagGate.Release();
                }
            }

            return (Artist: a, BucketLabel: bucket ?? "Mix Rotation");
        });
        enrichedArtists.AddRange(await Task.WhenAll(enrichTasks).ConfigureAwait(false));

        // Group into distinct vibe clusters sorted by artist affinity count, picking cluster idx-1.
        var clusters = enrichedArtists
            .GroupBy(x => x.BucketLabel, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => (Bucket: g.Key, Artists: g.Select(x => x.Artist).Take(5).ToList()))
            .ToList();

        List<SpotifyArtistItem> slice;
        string clusterLabel;
        if (clusters.Count >= idx)
        {
            clusterLabel = clusters[idx - 1].Bucket;
            slice = clusters[idx - 1].Artists;
        }
        else
        {
            clusterLabel = clusters.Count > 0 ? clusters[(idx - 1) % clusters.Count].Bucket : "Personal Rotation";
            slice = allArtists
                .Where((_, i) => (i % 3) == (idx - 1))
                .Take(5)
                .ToList();
            if (slice.Count == 0)
            {
                slice = allArtists.Take(4).ToList();
            }
        }

        var recs = new List<LastFmRecommendation>();
        using var seedGate = new SemaphoreSlim(3, 3);
        var seedTasks = slice.Take(4).Select(async artist =>
        {
            await seedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await _lastFm.GetArtistRadioSeedsAsync(artist.Name, 8, preferDeepCuts: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                seedGate.Release();
            }
        });
        foreach (var seedBatch in await Task.WhenAll(seedTasks).ConfigureAwait(false))
        {
            recs.AddRange(seedBatch);
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
            ? $"{clusterLabel} · " + string.Join(", ", slice.Take(3).Select(a => a.Name)) + (slice.Count > 3 ? " & more" : "")
            : "Mixed from your top rotation";

        var accents = new[] { "#38BDF8", "#A78BFA", "#F472B6" };
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
        var cacheKey = $"rem-release-radar-{DateTime.Now:yyyy-MM-dd}";
        if (!forceRefresh)
        {
            var cached = await _database.GetSpotifyCachedMixAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            if (cached is not null && cached.Tracks.Count > 0)
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

    public static string GetCurrentMixCacheKey(string kind)
    {
        var nowLocal = DateTime.Now;
        var cal = CultureInfo.InvariantCulture.Calendar;
        var weekNum = cal.GetWeekOfYear(nowLocal, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
        var todayKey = nowLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return kind switch
        {
            "discover" => $"rem-discover-weekly-{nowLocal:yyyy}-W{weekNum:D2}",
            "daylist" => $"rem-daylist-{GetTimeSlot(nowLocal.Hour).Key}-{todayKey}",
            "daily1" => $"rem-daily-mix-1-{todayKey}",
            "daily2" => $"rem-daily-mix-2-{todayKey}",
            "daily3" => $"rem-daily-mix-3-{todayKey}",
            _ => $"rem-release-radar-{todayKey}"
        };
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

    private static string? ClassifyGenreBucket(IReadOnlyList<string>? tagsOrGenres)
    {
        if (tagsOrGenres is null || tagsOrGenres.Count == 0)
        {
            return null;
        }

        foreach (var raw in tagsOrGenres)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var g = raw.ToLowerInvariant();
            if (g.Contains("phonk") || g.Contains("drift")) return "Phonk & Drift";
            if (g.Contains("hip hop") || g.Contains("hip-hop") || g.Contains("rap") || g.Contains("trap") || g.Contains("drill")) return "Hip-Hop & Rap";
            if (g.Contains("metal") || g.Contains("hardcore") || g.Contains("punk")) return "Rock & Metal";
            if (g.Contains("rock") || g.Contains("grunge") || g.Contains("alt-rock")) return "Rock & Alternative";
            if (g.Contains("r&b") || g.Contains("soul") || g.Contains("neo soul")) return "R&B & Soul";
            if (g.Contains("edm") || g.Contains("house") || g.Contains("techno") || g.Contains("electronic") || g.Contains("synth") || g.Contains("dubstep") || g.Contains("dnb")) return "Electronic & Synth";
            if (g.Contains("anime") || g.Contains("soundtrack") || g.Contains("ost") || g.Contains("video game") || g.Contains("j-pop") || g.Contains("j-rock")) return "Anime & Soundtracks";
            if (g.Contains("indie") || g.Contains("folk") || g.Contains("acoustic")) return "Indie & Chill";
            if (g.Contains("jazz") || g.Contains("lo-fi") || g.Contains("lofi") || g.Contains("chillhop")) return "Lo-Fi & Jazz";
            if (g.Contains("pop") || g.Contains("k-pop")) return "Pop Hits";
            if (g.Contains("arabic") || g.Contains("shaabi") || g.Contains("mahraganat") || g.Contains("egypt")) return "Arabic & Regional";
        }

        return ToTitle(tagsOrGenres[0]);
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
