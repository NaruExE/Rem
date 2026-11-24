using System.Net.Http.Headers;
using System.Text.Json;
using Avalonia.Media.Imaging;
using REM.Models;

namespace REM.Services;

/// <summary>
/// Plain HttpClient + System.Text.Json TMDB v3 client using profile/app-configured
/// Bearer Read Access Token, with w342 poster URL helpers and RAM-capped bitmap cache.
/// </summary>
public sealed class TmdbService : IDisposable
{
    public const string ApiBaseUrl = "https://api.themoviedb.org/3";
    public const string ImageBaseUrl = "https://image.tmdb.org/t/p/w342";

    private readonly DatabaseService _database;
    private readonly ActionLogService _actionLog;
    private readonly HttpClient _http;
    private readonly Dictionary<string, Bitmap> _posterCache = new(StringComparer.Ordinal);
    private readonly object _posterSync = new();
    private bool _disposed;

    public TmdbService(DatabaseService database, ActionLogService actionLog)
    {
        _database = database;
        _actionLog = actionLog;

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("REM-Client/1.0");
    }

    public static string? BuildImageUrl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"{ImageBaseUrl}/{trimmed.TrimStart('/')}";
    }

    public static string? BuildEmbedUrl(
        string? template,
        int tmdbId,
        string? imdbId,
        int? season = null,
        int? episode = null)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var raw = template.Trim();
        var cleanImdb = imdbId?.Trim() ?? string.Empty;

        if (raw.Contains("{imdb_id}", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(cleanImdb))
        {
            return null;
        }

        if (raw.Contains("{tmdb_id}", StringComparison.OrdinalIgnoreCase) && tmdbId <= 0)
        {
            return null;
        }

        if (raw.Contains("{season}", StringComparison.OrdinalIgnoreCase) && (!season.HasValue || season.Value <= 0))
        {
            return null;
        }

        if (raw.Contains("{episode}", StringComparison.OrdinalIgnoreCase) && (!episode.HasValue || episode.Value <= 0))
        {
            return null;
        }

        raw = raw
            .Replace("{imdb_id}", Uri.EscapeDataString(cleanImdb), StringComparison.OrdinalIgnoreCase)
            .Replace("{tmdb_id}", tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{season}", (season ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{episode}", (episode ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return uri.AbsoluteUri;
    }

    public Task<TmdbMediaPage> GetTrendingAllPageAsync(
        int page = 1,
        CancellationToken cancellationToken = default) =>
        FetchMediaPageAsync($"/trending/all/week?page={Math.Max(1, page)}", defaultMediaType: null, cancellationToken);

    public Task<TmdbMediaPage> GetTrendingMoviesPageAsync(
        int page = 1,
        CancellationToken cancellationToken = default) =>
        FetchMediaPageAsync($"/trending/movie/week?page={Math.Max(1, page)}", defaultMediaType: "movie", cancellationToken);

    public Task<TmdbMediaPage> GetTrendingTvPageAsync(
        int page = 1,
        CancellationToken cancellationToken = default) =>
        FetchMediaPageAsync($"/trending/tv/week?page={Math.Max(1, page)}", defaultMediaType: "tv", cancellationToken);

    public Task<TmdbMediaPage> SearchMultiPageAsync(
        string query,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult(new TmdbMediaPage([], 1, 1));
        }

        var encoded = Uri.EscapeDataString(query.Trim());
        return FetchMediaPageAsync(
            $"/search/multi?query={encoded}&include_adult=false&page={Math.Max(1, page)}",
            defaultMediaType: null,
            cancellationToken);
    }

    public async Task<IReadOnlyList<TmdbMediaSummary>> GetTrendingAllAsync(
        int page = 1,
        CancellationToken cancellationToken = default) =>
        (await GetTrendingAllPageAsync(page, cancellationToken).ConfigureAwait(false)).Items;

    public async Task<IReadOnlyList<TmdbMediaSummary>> GetTrendingMoviesAsync(
        int page = 1,
        CancellationToken cancellationToken = default) =>
        (await GetTrendingMoviesPageAsync(page, cancellationToken).ConfigureAwait(false)).Items;

    public async Task<IReadOnlyList<TmdbMediaSummary>> GetTrendingTvAsync(
        int page = 1,
        CancellationToken cancellationToken = default) =>
        (await GetTrendingTvPageAsync(page, cancellationToken).ConfigureAwait(false)).Items;

    public async Task<IReadOnlyList<TmdbMediaSummary>> SearchMultiAsync(
        string query,
        int page = 1,
        CancellationToken cancellationToken = default) =>
        (await SearchMultiPageAsync(query, page, cancellationToken).ConfigureAwait(false)).Items;

    public async Task<TmdbMovieDetails> GetMovieDetailsAsync(
        int tmdbId,
        CancellationToken cancellationToken = default)
    {
        using var doc = await SendTmdbGetAsync(
            $"/movie/{tmdbId}?append_to_response=external_ids",
            cancellationToken).ConfigureAwait(false);

        var root = doc.RootElement;
        return new TmdbMovieDetails(
            TmdbId: TryGetInt(root, "id") ?? tmdbId,
            ImdbId: ExtractImdbId(root),
            Title: TryGetString(root, "title") ?? TryGetString(root, "original_title") ?? $"Movie #{tmdbId}",
            Tagline: TryGetString(root, "tagline"),
            Overview: TryGetString(root, "overview"),
            PosterPath: TryGetString(root, "poster_path"),
            BackdropPath: TryGetString(root, "backdrop_path"),
            ReleaseDate: TryGetString(root, "release_date"),
            RuntimeMinutes: TryGetInt(root, "runtime"),
            Rating: TryGetDouble(root, "vote_average"),
            Genres: ExtractGenres(root));
    }

    public async Task<TmdbTvDetails> GetTvDetailsAsync(
        int tmdbId,
        CancellationToken cancellationToken = default)
    {
        using var doc = await SendTmdbGetAsync(
            $"/tv/{tmdbId}?append_to_response=external_ids",
            cancellationToken).ConfigureAwait(false);

        var root = doc.RootElement;
        var seasons = new List<TmdbSeasonSummary>();
        if (root.TryGetProperty("seasons", out var seasonsEl) && seasonsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in seasonsEl.EnumerateArray())
            {
                seasons.Add(new TmdbSeasonSummary(
                    SeasonNumber: TryGetInt(item, "season_number") ?? 0,
                    Name: TryGetString(item, "name") ?? $"Season {TryGetInt(item, "season_number") ?? 0}",
                    EpisodeCount: TryGetInt(item, "episode_count") ?? 0,
                    AirDate: TryGetString(item, "air_date"),
                    PosterPath: TryGetString(item, "poster_path"),
                    Overview: TryGetString(item, "overview")));
            }
        }

        return new TmdbTvDetails(
            TmdbId: TryGetInt(root, "id") ?? tmdbId,
            ImdbId: ExtractImdbId(root),
            Title: TryGetString(root, "name") ?? TryGetString(root, "original_name") ?? $"Series #{tmdbId}",
            Tagline: TryGetString(root, "tagline"),
            Overview: TryGetString(root, "overview"),
            PosterPath: TryGetString(root, "poster_path"),
            BackdropPath: TryGetString(root, "backdrop_path"),
            FirstAirDate: TryGetString(root, "first_air_date"),
            NumberOfSeasons: TryGetInt(root, "number_of_seasons") ?? seasons.Count(s => s.SeasonNumber > 0),
            NumberOfEpisodes: TryGetInt(root, "number_of_episodes") ?? 0,
            Rating: TryGetDouble(root, "vote_average"),
            Genres: ExtractGenres(root),
            Seasons: seasons);
    }

    public async Task<TmdbSeasonDetails> GetTvSeasonAsync(
        int tmdbId,
        int seasonNumber,
        CancellationToken cancellationToken = default)
    {
        using var doc = await SendTmdbGetAsync(
            $"/tv/{tmdbId}/season/{seasonNumber}",
            cancellationToken).ConfigureAwait(false);

        var root = doc.RootElement;
        var episodes = new List<TmdbEpisode>();
        if (root.TryGetProperty("episodes", out var epsEl) && epsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var ep in epsEl.EnumerateArray())
            {
                episodes.Add(new TmdbEpisode(
                    EpisodeNumber: TryGetInt(ep, "episode_number") ?? 0,
                    SeasonNumber: TryGetInt(ep, "season_number") ?? seasonNumber,
                    Name: TryGetString(ep, "name") ?? $"Episode {TryGetInt(ep, "episode_number") ?? 0}",
                    Overview: TryGetString(ep, "overview"),
                    AirDate: TryGetString(ep, "air_date"),
                    RuntimeMinutes: TryGetInt(ep, "runtime"),
                    StillPath: TryGetString(ep, "still_path"),
                    Rating: TryGetDouble(ep, "vote_average")));
            }
        }

        return new TmdbSeasonDetails(
            TmdbId: tmdbId,
            SeasonNumber: TryGetInt(root, "season_number") ?? seasonNumber,
            Name: TryGetString(root, "name") ?? $"Season {seasonNumber}",
            Overview: TryGetString(root, "overview"),
            PosterPath: TryGetString(root, "poster_path"),
            Episodes: episodes);
    }

    public async Task<Bitmap?> GetPosterBitmapAsync(
        string? posterPath,
        int decodeWidth = 240,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(posterPath))
        {
            return null;
        }

        var url = BuildImageUrl(posterPath);
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var key = $"{url}@{decodeWidth}";
        lock (_posterSync)
        {
            if (_posterCache.TryGetValue(key, out var existing))
            {
                return existing;
            }
        }

        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0 || _disposed)
            {
                return null;
            }

            BandwidthMonitor.RecordBytes(bytes.Length);
            using var ms = new MemoryStream(bytes, writable: false);
            var bitmap = Bitmap.DecodeToWidth(ms, Math.Clamp(decodeWidth, 64, 512));

            lock (_posterSync)
            {
                if (_disposed)
                {
                    bitmap.Dispose();
                    return null;
                }

                if (_posterCache.TryGetValue(key, out var raceWinner))
                {
                    bitmap.Dispose();
                    return raceWinner;
                }

                // Do not evict/dispose bitmaps while visible in Browse grids after "Show More";
                // ClearPosterCache() disposes all bitmaps cleanly on tab switch, view change, and Kill.
                _posterCache[key] = bitmap;
                return bitmap;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    public void ClearPosterCache()
    {
        lock (_posterSync)
        {
            foreach (var bmp in _posterCache.Values)
            {
                try { bmp.Dispose(); } catch { }
            }

            _posterCache.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearPosterCache();
        _http.Dispose();
    }

    private async Task<TmdbMediaPage> FetchMediaPageAsync(
        string relativePathAndQuery,
        string? defaultMediaType,
        CancellationToken cancellationToken)
    {
        using var doc = await SendTmdbGetAsync(relativePathAndQuery, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        var page = TryGetInt(root, "page") ?? 1;
        var totalPages = TryGetInt(root, "total_pages") ?? 1;
        var results = new List<TmdbMediaSummary>();

        if (!root.TryGetProperty("results", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return new TmdbMediaPage(results, page, totalPages);
        }

        foreach (var item in array.EnumerateArray())
        {
            var id = TryGetInt(item, "id") ?? 0;
            if (id <= 0)
            {
                continue;
            }

            var mediaType = TryGetString(item, "media_type") ?? defaultMediaType ?? string.Empty;
            if (!string.Equals(mediaType, "movie", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase))
            {
                // Multi-search returns "person" entries too; skip them cleanly.
                continue;
            }

            var normalizedType = mediaType.ToLowerInvariant();
            var title = normalizedType == "tv"
                ? (TryGetString(item, "name") ?? TryGetString(item, "original_name") ?? TryGetString(item, "title"))
                : (TryGetString(item, "title") ?? TryGetString(item, "original_title") ?? TryGetString(item, "name"));

            var dateStr = normalizedType == "tv"
                ? TryGetString(item, "first_air_date")
                : TryGetString(item, "release_date");

            var year = dateStr is { Length: >= 4 } ? dateStr.Substring(0, 4) : null;

            results.Add(new TmdbMediaSummary(
                TmdbId: id,
                MediaType: normalizedType,
                Title: string.IsNullOrWhiteSpace(title) ? $"TMDB #{id}" : title,
                Overview: TryGetString(item, "overview"),
                PosterPath: TryGetString(item, "poster_path"),
                BackdropPath: TryGetString(item, "backdrop_path"),
                ReleaseYear: year,
                Rating: TryGetDouble(item, "vote_average")));
        }

        return new TmdbMediaPage(results, page, totalPages);
    }

    private async Task<JsonDocument> SendTmdbGetAsync(
        string relativePathAndQuery,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var token = (await _database.GetSettingAsync(DatabaseService.KeyTmdbBearerToken, cancellationToken)
            .ConfigureAwait(false))?.Trim();

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("TMDB Read Access Token is not configured yet.");
        }

        var url = $"{ApiBaseUrl}{relativePathAndQuery}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length > 0)
        {
            BandwidthMonitor.RecordBytes(bytes.Length);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = System.Text.Encoding.UTF8.GetString(bytes);
            _actionLog.Log("Warning", $"TMDB HTTP {(int)response.StatusCode} on {relativePathAndQuery}");
            throw new HttpRequestException($"TMDB request failed ({(int)response.StatusCode}): {body}");
        }

        return JsonDocument.Parse(bytes);
    }

    private static string? ExtractImdbId(JsonElement root)
    {
        if (root.TryGetProperty("external_ids", out var ext) && ext.ValueKind == JsonValueKind.Object)
        {
            var nested = TryGetString(ext, "imdb_id");
            if (!string.IsNullOrWhiteSpace(nested))
            {
                return nested.Trim();
            }
        }

        var direct = TryGetString(root, "imdb_id");
        return string.IsNullOrWhiteSpace(direct) ? null : direct.Trim();
    }

    private static IReadOnlyList<string> ExtractGenres(JsonElement root)
    {
        if (!root.TryGetProperty("genres", out var genresEl) || genresEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<string>();
        foreach (var g in genresEl.EnumerateArray())
        {
            var name = TryGetString(g, "name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                list.Add(name);
            }
        }

        return list;
    }

    private static string? TryGetString(JsonElement obj, string prop)
    {
        if (obj.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
        {
            var s = val.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        return null;
    }

    private static int? TryGetInt(JsonElement obj, string prop)
    {
        if (obj.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out var i))
        {
            return i;
        }

        return null;
    }

    private static double TryGetDouble(JsonElement obj, string prop)
    {
        if (obj.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number && val.TryGetDouble(out var d))
        {
            return Math.Round(d, 1);
        }

        return 0.0;
    }
}
