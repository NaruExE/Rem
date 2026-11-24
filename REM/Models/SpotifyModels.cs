namespace REM.Models;

public sealed record SpotifyTrackItem(
    string Id,
    string Uri,
    string Name,
    string Artists,
    string AlbumName,
    string? AlbumCoverUrl,
    long DurationMs,
    string? ContextUri = null)
{
    public string DurationDisplay
    {
        get
        {
            if (DurationMs <= 0)
            {
                return "0:00";
            }

            var ts = TimeSpan.FromMilliseconds(DurationMs);
            return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
        }
    }
}

public sealed record SpotifyPlaylistItem(
    string Id,
    string Uri,
    string Name,
    string Description,
    string OwnerName,
    int TrackCount,
    string? ImageUrl);

public sealed record SpotifyQueueEntry(
    string Uri,
    string Provider,
    SpotifyTrackItem? Track);

public sealed record GoLibrespotDeviceAuth(
    string Url,
    string Code,
    DateTimeOffset ExpiresAt);

public sealed record GoLibrespotTrack(
    string Uri,
    string Name,
    List<string> ArtistNames,
    string AlbumName,
    string? AlbumCoverUrl,
    long PositionMs,
    long DurationMs,
    string? Format,
    int? Bitrate)
{
    public string ArtistsDisplay => ArtistNames.Count > 0
        ? string.Join(", ", ArtistNames)
        : "Unknown Artist";
}

public sealed record GoLibrespotStatus(
    string Username,
    string DeviceId,
    string DeviceName,
    string? ContextUri,
    string? ContextName,
    bool Stopped,
    bool Paused,
    bool Buffering,
    int Volume,
    int VolumeSteps,
    bool RepeatContext,
    bool RepeatTrack,
    bool ShuffleContext,
    GoLibrespotTrack? Track,
    bool HasAuthoritativePosition = false);

public sealed record LastFmRecommendation(
    string Artist,
    string Title);

public sealed record LyricLine(
    TimeSpan Timestamp,
    string Text);

public sealed record LyricsResult(
    bool IsSynced,
    List<LyricLine> SyncedLines,
    string? PlainLyrics,
    string? RawSyncedLrc = null,
    string? MatchedTitle = null,
    string? MatchedArtist = null,
    int? MatchedDurationSeconds = null);

public sealed record CachedSpotifyLyrics(
    string TrackUri,
    bool IsSynced,
    string? SyncedLrc,
    string? PlainLyrics,
    int OffsetMs,
    string? SourceLabel);

public sealed record LrclibSearchCandidate(
    string Title,
    string Artist,
    string Album,
    int DurationSeconds,
    bool IsSynced,
    LyricsResult Lyrics)
{
    public string DurationDisplay => DurationSeconds > 0
        ? TimeSpan.FromSeconds(DurationSeconds).ToString(DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss")
        : string.Empty;
}

