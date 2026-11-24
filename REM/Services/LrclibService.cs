using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using REM.Models;

namespace REM.Services;

public sealed partial class LrclibService : IDisposable
{
    private readonly ActionLogService? _actionLog;
    private readonly HttpClient _http;
    private bool _disposed;

    public LrclibService(ActionLogService? actionLog = null)
    {
        _actionLog = actionLog;
        _http = new HttpClient(new BandwidthCountingHandler())
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("REM/1.0");
    }

    [GeneratedRegex(@"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled)]
    private static partial Regex LrcTimestampRegex();

    [GeneratedRegex(@"\s*[\(\[]\s*(?:feat\.?|ft\.?|with|remaster(?:ed)?|official|explicit|clean|radio edit|single version|album version|deluxe|bonus|live|sped up|slowed|reverb|\d{4}\s*remaster)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BracketTagRegex();

    [GeneratedRegex(@"\s+-\s+(?:\d{4}\s+)?(?:remaster(?:ed)?|single version|radio edit|live|acoustic|sped up|slowed).*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DashSuffixRegex();

    [GeneratedRegex(@"\s+(?:feat\.?|ft\.?)\s+.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex InlineFeatRegex();

    public static string SanitizeTrackTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var cleaned = BracketTagRegex().Replace(raw, " ");
        cleaned = DashSuffixRegex().Replace(cleaned, string.Empty);
        cleaned = InlineFeatRegex().Replace(cleaned, string.Empty);
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    public static string SanitizePrimaryArtist(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) ||
            string.Equals(raw.Trim(), "Unknown Artist", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var first = raw.Split(',')[0];
        var ampIdx = first.IndexOf(" & ", StringComparison.Ordinal);
        if (ampIdx > 0)
        {
            first = first[..ampIdx];
        }

        first = InlineFeatRegex().Replace(first, string.Empty);
        return first.Trim();
    }

    public async Task<LyricsResult?> GetLyricsAsync(
        string artistName,
        string trackName,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(trackName))
        {
            return null;
        }

        var rawTrack = trackName.Trim();
        var cleanTrack = SanitizeTrackTitle(rawTrack);
        if (string.IsNullOrWhiteSpace(cleanTrack))
        {
            cleanTrack = rawTrack;
        }

        var primaryArtist = SanitizePrimaryArtist(artistName);

        // Stage 1: Exact /api/get when artist & duration are known
        if (!string.IsNullOrWhiteSpace(primaryArtist) && durationSeconds > 0)
        {
            var getUrl = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(primaryArtist)}&track_name={Uri.EscapeDataString(rawTrack)}&duration={durationSeconds}";
            var exact = await FetchSingleAsync(getUrl, cancellationToken).ConfigureAwait(false);
            if (exact is not null)
            {
                return exact;
            }
        }

        // Stage 2: Cleaned /api/get if title changed after stripping feat/remaster suffixes
        if (!string.IsNullOrWhiteSpace(primaryArtist) &&
            durationSeconds > 0 &&
            !string.Equals(cleanTrack, rawTrack, StringComparison.OrdinalIgnoreCase))
        {
            var cleanUrl = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(primaryArtist)}&track_name={Uri.EscapeDataString(cleanTrack)}&duration={durationSeconds}";
            var cleanedMatch = await FetchSingleAsync(cleanUrl, cancellationToken).ConfigureAwait(false);
            if (cleanedMatch is not null)
            {
                return cleanedMatch;
            }
        }

        // Stage 3: Structured /api/search?artist_name=...&track_name=...
        if (!string.IsNullOrWhiteSpace(primaryArtist))
        {
            var searchUrl = $"https://lrclib.net/api/search?artist_name={Uri.EscapeDataString(primaryArtist)}&track_name={Uri.EscapeDataString(cleanTrack)}";
            var structured = await FetchBestSearchMatchAsync(searchUrl, durationSeconds, cancellationToken).ConfigureAwait(false);
            if (structured is not null)
            {
                return structured;
            }
        }

        // Stage 4: Broad /api/search?q=...
        var broadQuery = string.IsNullOrWhiteSpace(primaryArtist)
            ? cleanTrack
            : $"{primaryArtist} {cleanTrack}";
        var broadUrl = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(broadQuery)}";
        return await FetchBestSearchMatchAsync(broadUrl, durationSeconds, cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<LrclibSearchCandidate>> SearchCandidatesAsync(
        string query,
        int targetDurationSeconds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var url = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(query.Trim())}";
        try
        {
            using var resp = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _actionLog?.Log("Warning", $"LRCLIB search HTTP {(int)resp.StatusCode} for '{query}'.");
                return [];
            }

            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var list = new List<LrclibSearchCandidate>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var parsed = ParseLyricsObject(item);
                if (parsed is null)
                {
                    continue;
                }

                var title = GetString(item, "trackName") ?? GetString(item, "name") ?? query;
                var artist = GetString(item, "artistName") ?? string.Empty;
                var album = GetString(item, "albumName") ?? string.Empty;
                var dur = item.TryGetProperty("duration", out var dProp) && dProp.ValueKind == JsonValueKind.Number
                    ? (int)Math.Round(dProp.GetDouble())
                    : 0;

                list.Add(new LrclibSearchCandidate(title, artist, album, dur, parsed.IsSynced, parsed));
            }

            return list
                .OrderByDescending(c => c.IsSynced)
                .ThenBy(c => targetDurationSeconds > 0 && c.DurationSeconds > 0
                    ? Math.Abs(c.DurationSeconds - targetDurationSeconds)
                    : 9999)
                .Take(12)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _actionLog?.Log("Warning", $"LRCLIB search error: {ex.Message}");
            return [];
        }
    }

    private async Task<LyricsResult?> FetchSingleAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var resp = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            return ParseLyricsObject(doc.RootElement);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _actionLog?.Log("Warning", $"LRCLIB get error: {ex.Message}");
            return null;
        }
    }

    private async Task<LyricsResult?> FetchBestSearchMatchAsync(
        string url,
        int targetDurationSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            using var resp = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var candidates = new List<(LyricsResult Result, int DurationDiff)>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var candidate = ParseLyricsObject(item);
                if (candidate is null)
                {
                    continue;
                }

                var itemDur = item.TryGetProperty("duration", out var dProp) && dProp.ValueKind == JsonValueKind.Number
                    ? (int)Math.Round(dProp.GetDouble())
                    : 0;
                var diff = targetDurationSeconds > 0 && itemDur > 0
                    ? Math.Abs(itemDur - targetDurationSeconds)
                    : 9999;
                candidates.Add((candidate, diff));
            }

            var bestSynced = candidates
                .Where(c => c.Result.IsSynced)
                .OrderBy(c => c.DurationDiff)
                .Select(c => c.Result)
                .FirstOrDefault();

            if (bestSynced is not null)
            {
                return bestSynced;
            }

            return candidates
                .OrderBy(c => c.DurationDiff)
                .Select(c => c.Result)
                .FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _actionLog?.Log("Warning", $"LRCLIB search fallback error: {ex.Message}");
            return null;
        }
    }

    private static LyricsResult? ParseLyricsObject(JsonElement el)
    {
        var syncedRaw = GetString(el, "syncedLyrics");
        var plainRaw = GetString(el, "plainLyrics");
        var matchedTitle = GetString(el, "trackName") ?? GetString(el, "name");
        var matchedArtist = GetString(el, "artistName");
        int? matchedDur = el.TryGetProperty("duration", out var dProp) && dProp.ValueKind == JsonValueKind.Number
            ? (int)Math.Round(dProp.GetDouble())
            : null;

        if (!string.IsNullOrWhiteSpace(syncedRaw))
        {
            var lines = ParseLrcLines(syncedRaw);
            if (lines.Count > 0)
            {
                return new LyricsResult(
                    IsSynced: true,
                    SyncedLines: lines,
                    PlainLyrics: plainRaw,
                    RawSyncedLrc: syncedRaw,
                    MatchedTitle: matchedTitle,
                    MatchedArtist: matchedArtist,
                    MatchedDurationSeconds: matchedDur);
            }
        }

        if (!string.IsNullOrWhiteSpace(plainRaw))
        {
            return new LyricsResult(
                IsSynced: false,
                SyncedLines: [],
                PlainLyrics: plainRaw.Trim(),
                RawSyncedLrc: null,
                MatchedTitle: matchedTitle,
                MatchedArtist: matchedArtist,
                MatchedDurationSeconds: matchedDur);
        }

        return null;
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

    public static List<LyricLine> ParseLrcLines(string rawLrc)
    {
        var lines = new List<LyricLine>();
        var regex = LrcTimestampRegex();

        foreach (var rawLine in rawLrc.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var matches = regex.Matches(rawLine);
            if (matches.Count == 0)
            {
                continue;
            }

            var text = regex.Replace(rawLine, string.Empty).Trim();
            if (string.IsNullOrEmpty(text))
            {
                text = "♪";
            }

            foreach (Match m in matches)
            {
                if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mins) ||
                    !int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs))
                {
                    continue;
                }

                var ms = 0;
                if (m.Groups[3].Success)
                {
                    var frac = m.Groups[3].Value;
                    ms = frac.Length switch
                    {
                        1 => int.Parse(frac, CultureInfo.InvariantCulture) * 100,
                        2 => int.Parse(frac, CultureInfo.InvariantCulture) * 10,
                        _ => int.Parse(frac[..3], CultureInfo.InvariantCulture)
                    };
                }

                var ts = new TimeSpan(0, 0, mins, secs, ms);
                lines.Add(new LyricLine(ts, text));
            }
        }

        lines.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return lines;
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

