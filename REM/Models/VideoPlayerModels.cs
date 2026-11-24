namespace REM.Models;

public enum VideoHeadlessMode
{
    AudioOnlyStream,
    KeepVideoStream
}

public enum VideoLoopMode
{
    Off,
    One,
    All
}

public sealed record VideoPlaylist(
    long Id,
    long ProfileId,
    string Name,
    DateTime CreatedAt);

public sealed record VideoPlaylistItem(
    long Id,
    long PlaylistId,
    string Title,
    string SourceUrl,
    string? Referer,
    long LastPositionMs,
    long DurationMs,
    int SortOrder);

public sealed record ResolvedVideoMedia(
    string PlayableUrl,
    string Title,
    string OriginalInput,
    string? Referer,
    bool IsLocalFile,
    bool UsedYtDlp,
    string? AudioSlaveUrl = null);

public sealed record VideoTrackOption(
    int Id,
    string Name);

public sealed record VideoPlaybackSnapshot(
    bool IsPlaying,
    bool IsPaused,
    bool IsBuffering,
    float BufferingPercent,
    long PositionMs,
    long DurationMs,
    int Volume,
    bool IsMuted,
    float PlaybackSpeed,
    string? CurrentTitle,
    string? CurrentSourceUrl,
    long? CurrentPlaylistItemId,
    int ActiveAudioTrackId,
    int ActiveSubtitleTrackId,
    long SubtitleDelayMs = 0,
    string QualityPreset = "best",
    VideoLoopMode LoopMode = VideoLoopMode.Off,
    bool IsShuffled = false,
    long? AbLoopStartMs = null,
    long? AbLoopEndMs = null,
    string AspectRatioLabel = "Default");
