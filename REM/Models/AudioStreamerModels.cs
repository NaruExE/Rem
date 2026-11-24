namespace REM.Models;

public sealed record YouTubeSearchItem(
    string VideoId,
    string Title,
    string Channel,
    TimeSpan? Duration,
    string Url,
    string? ThumbnailUrl = null)
{
    public string EffectiveThumbnailUrl => !string.IsNullOrWhiteSpace(ThumbnailUrl)
        ? ThumbnailUrl
        : (!string.IsNullOrWhiteSpace(VideoId) ? $"https://i.ytimg.com/vi/{VideoId}/mqdefault.jpg" : string.Empty);

    public string DurationDisplay => Duration is { } d
        ? (d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss"))
        : "LIVE";
}

public sealed record YtDlpFeedResult(
    bool Success,
    List<YouTubeSearchItem> Items,
    string? ErrorMessage,
    bool IsMissingExecutable);

public sealed record ExtractedAudioTrack(
    string Title,
    string Channel,
    string StreamUrl,
    bool IsLive,
    TimeSpan? Duration,
    string SourceLabel);

public sealed record TwitchCategoryItem(
    string Name,
    string? GameId);

public sealed record TwitchLiveStream(
    string UserId,
    string UserLogin,
    string UserName,
    string GameName,
    string Title,
    int ViewerCount,
    string Language,
    string? ThumbnailUrl)
{
    public string StreamPageUrl => $"https://www.twitch.tv/{UserLogin}";
    public string ViewersDisplay => ViewerCount >= 1000
        ? $"{ViewerCount / 1000.0:F1}K viewers"
        : $"{ViewerCount} viewers";
}

public sealed record TwitchStreamsPage(
    List<TwitchLiveStream> Streams,
    string? NextCursor);

