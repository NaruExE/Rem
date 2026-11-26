using System.Text.Json.Serialization;

namespace REM.Models;

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Error
}

public sealed class UpdateManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = string.Empty;

    [JsonPropertyName("zipAsset")]
    public string ZipAsset { get; set; } = "REM-win-x64.zip";

    [JsonPropertyName("zipSize")]
    public long ZipSize { get; set; }

    [JsonPropertyName("zipSha256")]
    public string ZipSha256 { get; set; } = string.Empty;

    [JsonPropertyName("fileCount")]
    public int FileCount { get; set; }

    [JsonPropertyName("files")]
    public List<UpdateManifestFile> Files { get; set; } = [];
}

public sealed class UpdateManifestFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("dataOffset")]
    public long DataOffset { get; set; }

    [JsonPropertyName("compressedSize")]
    public long CompressedSize { get; set; }

    [JsonPropertyName("compressionMethod")]
    public int CompressionMethod { get; set; }
}

public sealed class LocalInstalledManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public List<LocalInstalledFile> Files { get; set; } = [];
}

public sealed class LocalInstalledFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class UpdateCheckResult
{
    public UpdateCheckStatus Status { get; init; }
    public string CurrentVersion { get; init; } = string.Empty;
    public string? LatestTag { get; init; }
    public string? LatestVersion { get; init; }
    public string? ReleaseUrl { get; init; }
    public string? ReleaseNotes { get; init; }
    public string? ZipDownloadUrl { get; init; }
    public long ZipTotalBytes { get; init; }
    public UpdateManifest? Manifest { get; init; }
    public IReadOnlyList<UpdateManifestFile> FilesToDownload { get; init; } = Array.Empty<UpdateManifestFile>();
    public IReadOnlyList<string> FilesToRemove { get; init; } = Array.Empty<string>();
    public long DeltaCompressedBytes { get; init; }
    public long DeltaUncompressedBytes { get; init; }
    public int TotalManagedFiles { get; init; }
    public bool IsDevelopmentBuild { get; init; }
    public string? ErrorMessage { get; init; }

    public bool HasManifestDelta => Manifest is not null && Manifest.Files.Count > 0;
}

public sealed record UpdateProgress(
    string Phase,
    int FilesCompleted,
    int TotalFiles,
    long BytesDownloaded,
    long TotalBytes)
{
    public double Percent => TotalBytes > 0
        ? Math.Clamp((double)BytesDownloaded * 100.0 / TotalBytes, 0, 100)
        : TotalFiles > 0
            ? Math.Clamp((double)FilesCompleted * 100.0 / TotalFiles, 0, 100)
            : 0;
}
