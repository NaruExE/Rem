namespace REM.Models;

public enum MediaStudioMode
{
    DiscordCompress,
    TrimClip,
    ConvertVideo,
    ConvertAudio,
    ConvertImage
}

public enum MediaStudioJobStatus
{
    Queued,
    Probing,
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed record MediaProbeInfo(
    string FilePath,
    long FileSizeBytes,
    double DurationSeconds,
    int Width,
    int Height,
    string? VideoCodec,
    string? AudioCodec,
    bool HasVideo,
    bool HasAudio)
{
    public string FileName => Path.GetFileName(FilePath);

    public string FileSizeLabel => FormatBytes(FileSizeBytes);

    public string DurationLabel
    {
        get
        {
            if (DurationSeconds <= 0)
            {
                return "--:--";
            }

            var ts = TimeSpan.FromSeconds(DurationSeconds);
            return ts.TotalHours >= 1.0
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
    }

    public string ResolutionLabel =>
        Width > 0 && Height > 0 ? $"{Width}x{Height}" : (HasAudio ? "Audio only" : "Unknown");

    public string SummaryText
    {
        get
        {
            var parts = new List<string> { FileSizeLabel };
            if (DurationSeconds > 0)
            {
                parts.Add(DurationLabel);
            }
            if (Width > 0 && Height > 0)
            {
                parts.Add($"{Width}x{Height}");
            }
            if (!string.IsNullOrWhiteSpace(VideoCodec))
            {
                parts.Add(VideoCodec.ToUpperInvariant());
            }
            if (!string.IsNullOrWhiteSpace(AudioCodec))
            {
                parts.Add(AudioCodec.ToUpperInvariant());
            }

            return string.Join(" · ", parts);
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024.0 && unitIndex < units.Length - 1)
        {
            value /= 1024.0;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{bytes} B" : $"{value:F1} {units[unitIndex]}";
    }
}

public sealed record MediaStudioJobRequest(
    string InputPath,
    string OutputPath,
    MediaStudioMode Mode,
    double TargetSizeMb,
    string? TrimStart,
    string? TrimEnd,
    bool StreamCopy,
    string OutputFormat,
    int AudioBitrateKbps,
    int VideoCrf,
    int MaxWidth,
    bool StripMetadata);

public sealed class MediaStudioJobState
{
    public long Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string InputPath { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public string ModeLabel { get; init; } = string.Empty;
    public MediaStudioJobStatus Status { get; set; } = MediaStudioJobStatus.Queued;
    public double ProgressPercent { get; set; }
    public string SpeedText { get; set; } = string.Empty;
    public string EtaText { get; set; } = string.Empty;
    public string StatusText { get; set; } = "Queued";
    public long OutputSizeBytes { get; set; }
    public string? ErrorMessage { get; set; }
}
