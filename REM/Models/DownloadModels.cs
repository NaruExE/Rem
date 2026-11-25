using System.Diagnostics;

namespace REM.Models;

public enum DownloadEngineKind
{
    Auto,
    YtDlp,
    Aria2c
}

public enum DownloadJobStatus
{
    Queued,
    Probing,
    Downloading,
    Paused,
    Completed,
    Failed,
    Cancelled
}

public static class DownloadFormatPresets
{
    public const string BestMp4 = "Best MP4";
    public const string P1080 = "1080p";
    public const string P720 = "720p";
    public const string Mp3Audio = "MP3 Audio";
    public const string M4aAudio = "M4A Audio";
    public const string RawFile = "Raw File";

    public static readonly IReadOnlyList<string> All =
    [
        BestMp4,
        P1080,
        P720,
        Mp3Audio,
        M4aAudio,
        RawFile
    ];
}

public sealed record MediaGrabberSettings(
    string DefaultDownloadFolder,
    string YtDlpPath,
    string Aria2cPath,
    string FfmpegPath,
    int MaxConcurrentDownloads,
    int Aria2Connections);

public sealed record DownloadHistoryItem(
    long Id,
    long ProfileId,
    string Url,
    string Title,
    string Engine,
    string FormatPreset,
    string? FilePath,
    long FileSizeBytes,
    string Status,
    DateTime CreatedAt,
    DateTime? CompletedAt);

public sealed record YtDlpProbeResult(
    string Title,
    string? ThumbnailUrl,
    double? DurationSeconds,
    bool IsPlaylist,
    int EntryCount);

public sealed record DownloadProgressUpdate(
    double ProgressPercent,
    string SpeedText,
    long SpeedBytesPerSec,
    string EtaText,
    long FileSizeBytes,
    string? StatusText = null);

public sealed record Aria2StatusSnapshot(
    string Gid,
    string Status,
    long TotalLength,
    long CompletedLength,
    long DownloadSpeed,
    string? FilePath,
    string? ErrorMessage);

public sealed class DownloadJobState
{
    public long HistoryId { get; init; }
    public long ProfileId { get; init; }
    public string Url { get; init; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DownloadEngineKind Engine { get; set; }
    public string FormatPreset { get; set; } = DownloadFormatPresets.BestMp4;
    public string? FilePath { get; set; }
    public long FileSizeBytes { get; set; }
    public double ProgressPercent { get; set; }
    public string SpeedText { get; set; } = string.Empty;
    public long SpeedBytesPerSec { get; set; }
    public string EtaText { get; set; } = string.Empty;
    public DownloadJobStatus Status { get; set; } = DownloadJobStatus.Queued;
    public string? ErrorMessage { get; set; }
    public string? Aria2Gid { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
    public Process? ActiveProcess { get; set; }
    public CancellationTokenSource? JobCts { get; set; }

    public string EngineLabel => Engine == DownloadEngineKind.Aria2c ? "aria2c" : "yt-dlp";
}
