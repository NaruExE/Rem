using REM.Services;

namespace REM.Models;

public sealed record TmdbMediaSummary(
    int TmdbId,
    string MediaType,
    string Title,
    string? Overview,
    string? PosterPath,
    string? BackdropPath,
    string? ReleaseYear,
    double Rating)
{
    public string? PosterUrl => TmdbService.BuildImageUrl(PosterPath);
    public string? BackdropUrl => TmdbService.BuildImageUrl(BackdropPath);
}

public sealed record TmdbMediaPage(
    IReadOnlyList<TmdbMediaSummary> Items,
    int Page,
    int TotalPages)
{
    public bool HasMore => Page < TotalPages;
}

public sealed record TmdbMovieDetails(
    int TmdbId,
    string? ImdbId,
    string Title,
    string? Tagline,
    string? Overview,
    string? PosterPath,
    string? BackdropPath,
    string? ReleaseDate,
    int? RuntimeMinutes,
    double Rating,
    IReadOnlyList<string> Genres)
{
    public string? PosterUrl => TmdbService.BuildImageUrl(PosterPath);
    public string? BackdropUrl => TmdbService.BuildImageUrl(BackdropPath);
}

public sealed record TmdbSeasonSummary(
    int SeasonNumber,
    string Name,
    int EpisodeCount,
    string? AirDate,
    string? PosterPath,
    string? Overview)
{
    public string? PosterUrl => TmdbService.BuildImageUrl(PosterPath);
}

public sealed record TmdbTvDetails(
    int TmdbId,
    string? ImdbId,
    string Title,
    string? Tagline,
    string? Overview,
    string? PosterPath,
    string? BackdropPath,
    string? FirstAirDate,
    int NumberOfSeasons,
    int NumberOfEpisodes,
    double Rating,
    IReadOnlyList<string> Genres,
    IReadOnlyList<TmdbSeasonSummary> Seasons)
{
    public string? PosterUrl => TmdbService.BuildImageUrl(PosterPath);
    public string? BackdropUrl => TmdbService.BuildImageUrl(BackdropPath);
}

public sealed record TmdbEpisode(
    int EpisodeNumber,
    int SeasonNumber,
    string Name,
    string? Overview,
    string? AirDate,
    int? RuntimeMinutes,
    string? StillPath,
    double Rating)
{
    public string? StillUrl => TmdbService.BuildImageUrl(StillPath);
}

public sealed record TmdbSeasonDetails(
    int TmdbId,
    int SeasonNumber,
    string Name,
    string? Overview,
    string? PosterPath,
    IReadOnlyList<TmdbEpisode> Episodes)
{
    public string? PosterUrl => TmdbService.BuildImageUrl(PosterPath);
}

public sealed record MovieWatchlistItem(
    long Id,
    long ProfileId,
    int TmdbId,
    string? ImdbId,
    string MediaType,
    string Title,
    string? PosterPath,
    string? Year,
    double Rating,
    DateTime AddedAt)
{
    public string? PosterUrl => TmdbService.BuildImageUrl(PosterPath);
}

public sealed record MovieHistoryItem(
    long Id,
    long ProfileId,
    int TmdbId,
    string? ImdbId,
    string MediaType,
    string Title,
    string? PosterPath,
    int LastSeason,
    int LastEpisode,
    DateTime UpdatedAt)
{
    public string? PosterUrl => TmdbService.BuildImageUrl(PosterPath);
}

public sealed record MoviesTvSettings(
    string TmdbBearerToken,
    string MovieEmbedTemplate,
    string TvEmbedTemplate,
    IReadOnlyList<string> ExtraBlockedDomains);
