using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using REM.Models;

namespace REM.Services;

public sealed class UpdateService : IDisposable
{
    public const string GitHubOwner = "NaruExE";
    public const string GitHubRepo = "Rem";
    private const string LatestReleaseApiUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
    private const string LocalManifestFileName = ".rem-manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly DatabaseService _database;
    private readonly ActionLogService _actionLog;
    private readonly HttpClient _apiClient;
    private readonly HttpClient _downloadClient;
    private readonly string _updateRootDir;
    private bool _disposed;

    public UpdateService(DatabaseService database, ActionLogService actionLog)
    {
        _database = database;
        _actionLog = actionLog;

        var rawVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        CurrentVersion = new Version(rawVersion.Major, Math.Max(0, rawVersion.Minor), Math.Max(0, rawVersion.Build));
        CurrentVersionDisplay = $"v{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";
        InstallDirectory = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        IsDevelopmentBuild = DetectDevelopmentBuild(InstallDirectory);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _updateRootDir = Path.Combine(localAppData, "REM", "Updates");
        Directory.CreateDirectory(_updateRootDir);

        _apiClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _apiClient.DefaultRequestHeaders.UserAgent.ParseAdd($"REM-Updater/{CurrentVersion}");
        _apiClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        _downloadClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _downloadClient.DefaultRequestHeaders.UserAgent.ParseAdd($"REM-Updater/{CurrentVersion}");
    }

    public Version CurrentVersion { get; }
    public string CurrentVersionDisplay { get; }
    public string InstallDirectory { get; }
    public bool IsDevelopmentBuild { get; }
    public UpdateCheckResult? LastCheckResult { get; private set; }

    public event EventHandler<UpdateCheckResult>? UpdateCheckCompleted;

    public (bool HasNotice, bool IsSuccess, string Message) ConsumePostUpdateNotice()
    {
        CleanupDirectoryIfExists(Path.Combine(_updateRootDir, "stage"));
        CleanupDirectoryIfExists(Path.Combine(_updateRootDir, "backup"));

        var successFile = Path.Combine(_updateRootDir, "last-update-success.txt");
        if (File.Exists(successFile))
        {
            var msg = File.ReadAllText(successFile, Encoding.UTF8).Trim();
            TryDeleteFile(successFile);
            if (!string.IsNullOrWhiteSpace(msg))
            {
                _actionLog.Log("Update", msg);
                return (true, true, msg);
            }
        }

        var errorFile = Path.Combine(_updateRootDir, "last-update-error.txt");
        if (File.Exists(errorFile))
        {
            var err = File.ReadAllText(errorFile, Encoding.UTF8).Trim();
            TryDeleteFile(errorFile);
            if (!string.IsNullOrWhiteSpace(err))
            {
                _actionLog.Log("Warning", $"Previous update rolled back: {err}");
                return (true, false, err);
            }
        }

        return (false, true, string.Empty);
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            using var resp = await _apiClient.GetAsync(LatestReleaseApiUrl, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var status = (int)resp.StatusCode;
                var errMsg = status is 403 or 429
                    ? "GitHub API rate limit reached. Try again in a few minutes."
                    : $"GitHub returned HTTP {status} while checking releases.";
                var errResult = CreateErrorResult(errMsg);
                LastCheckResult = errResult;
                UpdateCheckCompleted?.Invoke(this, errResult);
                return errResult;
            }

            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = GetString(root, "tag_name");
            var htmlUrl = GetString(root, "html_url");
            var body = GetString(root, "body");

            if (string.IsNullOrWhiteSpace(tagName) || !TryParseSemVer(tagName, out var remoteVersion))
            {
                var errResult = CreateErrorResult("Latest GitHub release does not have a valid semantic version tag.");
                LastCheckResult = errResult;
                UpdateCheckCompleted?.Invoke(this, errResult);
                return errResult;
            }

            string? zipUrl = null;
            long zipSize = 0;
            string? manifestUrl = null;

            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    var name = GetString(asset, "name");
                    var url = GetString(asset, "browser_download_url");
                    var size = asset.TryGetProperty("size", out var sp) && sp.TryGetInt64(out var sz) ? sz : 0;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.Equals(name, "update-manifest.json", StringComparison.OrdinalIgnoreCase))
                    {
                        manifestUrl = url;
                    }
                    else if (string.Equals(name, "REM-win-x64.zip", StringComparison.OrdinalIgnoreCase) ||
                             (zipUrl is null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                    {
                        zipUrl = url;
                        zipSize = size;
                    }
                }
            }

            var remoteDisplay = $"v{remoteVersion.Major}.{remoteVersion.Minor}.{remoteVersion.Build}";
            var isNewer = remoteVersion > CurrentVersion;

            if (!isNewer)
            {
                var upToDate = new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.UpToDate,
                    CurrentVersion = CurrentVersionDisplay,
                    LatestTag = tagName,
                    LatestVersion = remoteDisplay,
                    ReleaseUrl = htmlUrl,
                    ReleaseNotes = body,
                    ZipDownloadUrl = zipUrl,
                    ZipTotalBytes = zipSize,
                    IsDevelopmentBuild = IsDevelopmentBuild
                };
                LastCheckResult = upToDate;
                UpdateCheckCompleted?.Invoke(this, upToDate);
                return upToDate;
            }

            if (string.IsNullOrWhiteSpace(zipUrl))
            {
                var errResult = CreateErrorResult($"Release {tagName} is missing REM-win-x64.zip.");
                LastCheckResult = errResult;
                UpdateCheckCompleted?.Invoke(this, errResult);
                return errResult;
            }

            UpdateManifest? remoteManifest = null;
            if (!string.IsNullOrWhiteSpace(manifestUrl))
            {
                try
                {
                    var manifestJson = await _apiClient.GetStringAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
                    remoteManifest = JsonSerializer.Deserialize<UpdateManifest>(manifestJson, JsonOptions);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _actionLog.Log("Warning", $"Could not fetch update manifest ({ex.Message}); full ZIP fallback available.");
                }
            }

            var (toDownload, toRemove, deltaCompBytes, deltaRawBytes) = await Task.Run(
                () => ComputeLocalDelta(remoteManifest, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            var available = new UpdateCheckResult
            {
                Status = UpdateCheckStatus.UpdateAvailable,
                CurrentVersion = CurrentVersionDisplay,
                LatestTag = tagName,
                LatestVersion = remoteDisplay,
                ReleaseUrl = htmlUrl,
                ReleaseNotes = body,
                ZipDownloadUrl = zipUrl,
                ZipTotalBytes = remoteManifest?.ZipSize > 0 ? remoteManifest.ZipSize : zipSize,
                Manifest = remoteManifest,
                FilesToDownload = toDownload,
                FilesToRemove = toRemove,
                DeltaCompressedBytes = deltaCompBytes,
                DeltaUncompressedBytes = deltaRawBytes,
                TotalManagedFiles = remoteManifest?.Files.Count ?? 0,
                IsDevelopmentBuild = IsDevelopmentBuild
            };

            LastCheckResult = available;
            UpdateCheckCompleted?.Invoke(this, available);
            return available;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var errResult = CreateErrorResult($"Update check failed: {ex.Message}");
            LastCheckResult = errResult;
            UpdateCheckCompleted?.Invoke(this, errResult);
            return errResult;
        }
    }

    public async Task<(bool Success, string? StagingDir, string? Error)> DownloadAndStageUpdateAsync(
        UpdateCheckResult update,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (update.Status != UpdateCheckStatus.UpdateAvailable || string.IsNullOrWhiteSpace(update.ZipDownloadUrl))
        {
            return (false, null, "No valid update payload is available to download.");
        }

        var stagingDir = Path.Combine(_updateRootDir, "stage");
        CleanupDirectoryIfExists(stagingDir);
        Directory.CreateDirectory(stagingDir);

        try
        {
            if (update.HasManifestDelta && update.FilesToDownload.Count > 0)
            {
                var (rangeOk, rangeErr) = await TryDownloadViaHttpRangeAsync(
                    update.ZipDownloadUrl,
                    update.FilesToDownload,
                    stagingDir,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                if (rangeOk)
                {
                    return (true, stagingDir, null);
                }

                _actionLog.Log("Update", $"HTTP Range delta download fell back to full ZIP ({rangeErr}).");
                CleanupDirectoryIfExists(stagingDir);
                Directory.CreateDirectory(stagingDir);
            }
            else if (update.HasManifestDelta && update.FilesToDownload.Count == 0)
            {
                // Metadata-only or removal-only update
                return (true, stagingDir, null);
            }

            var (zipOk, zipErr) = await DownloadAndExtractFromZipAsync(
                update,
                stagingDir,
                progress,
                cancellationToken).ConfigureAwait(false);

            if (!zipOk)
            {
                CleanupDirectoryIfExists(stagingDir);
                return (false, null, zipErr);
            }

            return (true, stagingDir, null);
        }
        catch (OperationCanceledException)
        {
            CleanupDirectoryIfExists(stagingDir);
            throw;
        }
        catch (Exception ex)
        {
            CleanupDirectoryIfExists(stagingDir);
            return (false, null, ex.Message);
        }
    }

    public bool LaunchUpdateHelperAndPrepareExit(
        UpdateCheckResult update,
        string stagingDir,
        out string? error)
    {
        error = null;
        if (!Directory.Exists(stagingDir))
        {
            error = "Update staging directory is missing.";
            return false;
        }

        try
        {
            var backupDir = Path.Combine(_updateRootDir, "backup");
            var planPath = Path.Combine(_updateRootDir, "update-plan.json");
            var scriptPath = Path.Combine(_updateRootDir, "apply-update.ps1");

            var removePaths = new List<string>(update.FilesToRemove.Count);
            foreach (var rel in update.FilesToRemove)
            {
                if (TryResolveSafeRelativePath(InstallDirectory, rel, out _))
                {
                    removePaths.Add(rel.Replace('/', Path.DirectorySeparatorChar));
                }
            }

            string? newManifestJson = null;
            if (update.Manifest is not null)
            {
                var localManifest = new LocalInstalledManifest
                {
                    Version = update.LatestVersion?.TrimStart('v', 'V') ?? update.Manifest.Version,
                    Tag = update.LatestTag ?? update.Manifest.Tag,
                    Files = update.Manifest.Files.Select(f => new LocalInstalledFile
                    {
                        Path = f.Path,
                        Size = f.Size,
                        Sha256 = f.Sha256
                    }).ToList()
                };
                newManifestJson = JsonSerializer.Serialize(localManifest, new JsonSerializerOptions { WriteIndented = true });
            }

            var plan = new
            {
                targetPid = Environment.ProcessId,
                installDir = InstallDirectory,
                stagingDir,
                backupDir,
                appExePath = Path.Combine(InstallDirectory, "REM.exe"),
                manifestPath = Path.Combine(InstallDirectory, LocalManifestFileName),
                stateDir = _updateRootDir,
                versionLabel = update.LatestTag ?? update.LatestVersion ?? "latest",
                newManifestJson,
                removeFiles = removePaths
            };

            File.WriteAllText(planPath, JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.WriteAllText(scriptPath, BuildApplyHelperPowerShellScript(), new UTF8Encoding(false));

            var needsElevation = !HasWriteAccessToInstallDirectory();
            var psi = new ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\" -PlanPath \"{planPath}\"",
                UseShellExecute = needsElevation,
                CreateNoWindow = !needsElevation,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            if (needsElevation)
            {
                psi.Verb = "runas";
            }

            var helper = Process.Start(psi);
            if (helper is null)
            {
                error = "Failed to start update helper process.";
                return false;
            }

            _actionLog.Log("Update", $"Staged update to {plan.versionLabel}; closing REM to apply files.");
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "Administrator permission is required to write to the REM folder (UAC cancelled).";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        }

        if (bytes >= 1024L)
        {
            return $"{bytes / 1024.0:F1} KB";
        }

        return $"{Math.Max(0, bytes)} B";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _apiClient.Dispose();
        _downloadClient.Dispose();
    }

    private (List<UpdateManifestFile> ToDownload, List<string> ToRemove, long DeltaCompBytes, long DeltaRawBytes) ComputeLocalDelta(
        UpdateManifest? remoteManifest,
        CancellationToken cancellationToken)
    {
        var toDownload = new List<UpdateManifestFile>();
        var toRemove = new List<string>();
        long compBytes = 0;
        long rawBytes = 0;

        if (remoteManifest is null || remoteManifest.Files.Count == 0)
        {
            return (toDownload, toRemove, 0, 0);
        }

        var remotePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in remoteManifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rel = NormalizeRelativePath(entry.Path);
            if (!TryResolveSafeRelativePath(InstallDirectory, rel, out var localPath))
            {
                throw new InvalidOperationException($"Unsafe manifest entry path rejected: {entry.Path}");
            }

            remotePaths.Add(rel);
            var upToDate = false;
            if (File.Exists(localPath))
            {
                var info = new FileInfo(localPath);
                if (info.Length == entry.Size)
                {
                    var localHash = ComputeSha256Hex(localPath);
                    if (string.Equals(localHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        upToDate = true;
                    }
                }
            }

            if (!upToDate)
            {
                toDownload.Add(entry);
                compBytes += entry.CompressedSize;
                rawBytes += entry.Size;
            }
        }

        var localManifestPath = Path.Combine(InstallDirectory, LocalManifestFileName);
        if (File.Exists(localManifestPath))
        {
            try
            {
                var localManifest = JsonSerializer.Deserialize<LocalInstalledManifest>(
                    File.ReadAllText(localManifestPath, Encoding.UTF8),
                    JsonOptions);

                if (localManifest?.Files is not null)
                {
                    foreach (var oldFile in localManifest.Files)
                    {
                        var oldRel = NormalizeRelativePath(oldFile.Path);
                        if (!remotePaths.Contains(oldRel) &&
                            TryResolveSafeRelativePath(InstallDirectory, oldRel, out var oldFull) &&
                            File.Exists(oldFull))
                        {
                            toRemove.Add(oldRel);
                        }
                    }
                }
            }
            catch
            {
                // Ignore malformed local manifest; stale cleanup is optional.
            }
        }

        return (toDownload, toRemove, compBytes, rawBytes);
    }

    private async Task<(bool Ok, string? Error)> TryDownloadViaHttpRangeAsync(
        string zipUrl,
        IReadOnlyList<UpdateManifestFile> files,
        string stagingDir,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        Uri resolvedUri;
        try
        {
            using var probeReq = new HttpRequestMessage(HttpMethod.Get, zipUrl);
            probeReq.Headers.Range = new RangeHeaderValue(0, 0);
            using var probeResp = await _downloadClient.SendAsync(
                probeReq,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (probeResp.StatusCode != HttpStatusCode.PartialContent)
            {
                return (false, $"HTTP {(int)probeResp.StatusCode} does not support Range requests");
            }

            resolvedUri = probeResp.RequestMessage?.RequestUri ?? new Uri(zipUrl);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        var totalBytes = files.Sum(f => Math.Max(0L, f.CompressedSize));
        var totalFiles = files.Count;
        long bytesDownloaded = 0;
        var completedFiles = 0;
        long lastReportTicks = 0;

        void ReportProgress(bool force)
        {
            if (progress is null) return;
            var now = Environment.TickCount64;
            if (!force && now - Volatile.Read(ref lastReportTicks) < 120) return;
            Volatile.Write(ref lastReportTicks, now);
            progress.Report(new UpdateProgress(
                "Downloading changed files...",
                Volatile.Read(ref completedFiles),
                totalFiles,
                Volatile.Read(ref bytesDownloaded),
                totalBytes));
        }

        ReportProgress(force: true);

        using var throttle = new SemaphoreSlim(4, 4);
        var tasks = files.Select(async file =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var rel = NormalizeRelativePath(file.Path);
                if (!TryResolveSafeRelativePath(stagingDir, rel, out var stagePath) ||
                    !TryResolveSafeRelativePath(InstallDirectory, rel, out _))
                {
                    throw new InvalidOperationException($"Unsafe manifest path: {file.Path}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(stagePath)!);

                if (file.Size == 0)
                {
                    await File.WriteAllBytesAsync(stagePath, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if (file.CompressedSize <= 0 || file.DataOffset < 0)
                    {
                        throw new InvalidOperationException($"Invalid byte range metadata for {rel}.");
                    }

                    await DownloadAndDecompressSingleRangeAsync(
                        resolvedUri,
                        file,
                        stagePath,
                        bytes =>
                        {
                            Interlocked.Add(ref bytesDownloaded, bytes);
                            ReportProgress(force: false);
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                var actualLen = new FileInfo(stagePath).Length;
                if (actualLen != file.Size)
                {
                    throw new InvalidOperationException($"Size mismatch for {rel}: expected {file.Size}, got {actualLen}.");
                }

                var actualHash = ComputeSha256Hex(stagePath);
                if (!string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"SHA-256 verification failed for {rel}.");
                }

                Interlocked.Increment(ref completedFiles);
                ReportProgress(force: false);
            }
            finally
            {
                throttle.Release();
            }
        }).ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
            ReportProgress(force: true);
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task DownloadAndDecompressSingleRangeAsync(
        Uri targetUri,
        UpdateManifestFile file,
        string stagePath,
        Action<int> onCompressedBytesRead,
        CancellationToken cancellationToken)
    {
        var rangeEnd = file.DataOffset + file.CompressedSize - 1;
        using var req = new HttpRequestMessage(HttpMethod.Get, targetUri);
        req.Headers.Range = new RangeHeaderValue(file.DataOffset, rangeEnd);

        using var resp = await _downloadClient.SendAsync(
            req,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (resp.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new InvalidOperationException($"Range GET for {file.Path} returned HTTP {(int)resp.StatusCode}.");
        }

        await using var netStream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var compressedBuffer = new MemoryStream(checked((int)Math.Min(file.CompressedSize, 8 * 1024 * 1024)));

        var chunk = new byte[32768];
        long totalRead = 0;
        if (file.CompressedSize <= 16 * 1024 * 1024)
        {
            while (totalRead < file.CompressedSize)
            {
                var n = await netStream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (n <= 0) break;
                compressedBuffer.Write(chunk, 0, n);
                totalRead += n;
                onCompressedBytesRead(n);
            }

            if (totalRead != file.CompressedSize)
            {
                throw new InvalidOperationException($"Truncated range response for {file.Path} ({totalRead}/{file.CompressedSize}).");
            }

            compressedBuffer.Position = 0;
            await WriteDecompressedAsync(compressedBuffer, file.CompressionMethod, stagePath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var tempRaw = stagePath + ".rawpart";
            try
            {
                await using (var rawFs = new FileStream(tempRaw, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                {
                    while (totalRead < file.CompressedSize)
                    {
                        var n = await netStream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                        if (n <= 0) break;
                        await rawFs.WriteAsync(chunk.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                        totalRead += n;
                        onCompressedBytesRead(n);
                    }
                }

                if (totalRead != file.CompressedSize)
                {
                    throw new InvalidOperationException($"Truncated range response for {file.Path} ({totalRead}/{file.CompressedSize}).");
                }

                await using var readFs = new FileStream(tempRaw, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
                await WriteDecompressedAsync(readFs, file.CompressionMethod, stagePath, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                TryDeleteFile(tempRaw);
            }
        }
    }

    private static async Task WriteDecompressedAsync(
        Stream compressedStream,
        int compressionMethod,
        string stagePath,
        CancellationToken cancellationToken)
    {
        await using var outFs = new FileStream(stagePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        if (compressionMethod == 8)
        {
            await using var deflate = new DeflateStream(compressedStream, CompressionMode.Decompress, leaveOpen: true);
            await deflate.CopyToAsync(outFs, 65536, cancellationToken).ConfigureAwait(false);
        }
        else if (compressionMethod == 0)
        {
            await compressedStream.CopyToAsync(outFs, 65536, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException($"Unsupported ZIP compression method {compressionMethod}.");
        }
    }

    private async Task<(bool Ok, string? Error)> DownloadAndExtractFromZipAsync(
        UpdateCheckResult update,
        string stagingDir,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var tempZip = Path.Combine(_updateRootDir, "REM-win-x64.download.zip");
        TryDeleteFile(tempZip);

        try
        {
            using var resp = await _downloadClient.GetAsync(
                update.ZipDownloadUrl!,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var totalBytes = resp.Content.Headers.ContentLength ?? update.ZipTotalBytes;
            long downloaded = 0;
            long lastReportTicks = 0;

            await using (var netStream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var outFs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                var buffer = new byte[65536];
                while (true)
                {
                    var n = await netStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (n <= 0) break;
                    await outFs.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                    downloaded += n;

                    var now = Environment.TickCount64;
                    if (progress is not null && now - lastReportTicks >= 120)
                    {
                        lastReportTicks = now;
                        progress.Report(new UpdateProgress("Downloading update archive...", 0, 1, downloaded, totalBytes));
                    }
                }
            }

            progress?.Report(new UpdateProgress("Verifying & extracting files...", 0, 1, totalBytes, totalBytes));

            await Task.Run(() =>
            {
                if (update.Manifest is { ZipSha256.Length: > 0 })
                {
                    var actualZipHash = ComputeSha256Hex(tempZip);
                    if (!string.Equals(actualZipHash, update.Manifest.ZipSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Downloaded archive SHA-256 verification failed.");
                    }
                }

                Dictionary<string, UpdateManifestFile>? wantedMap = null;
                if (update.HasManifestDelta && update.FilesToDownload.Count > 0)
                {
                    wantedMap = update.FilesToDownload.ToDictionary(
                        f => NormalizeRelativePath(f.Path),
                        f => f,
                        StringComparer.OrdinalIgnoreCase);
                }

                using var archive = ZipFile.OpenRead(tempZip);
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var rel = NormalizeRelativePath(entry.FullName);
                    if (string.IsNullOrWhiteSpace(rel) || rel.EndsWith('/'))
                    {
                        continue;
                    }

                    if (wantedMap is not null && !wantedMap.ContainsKey(rel))
                    {
                        continue;
                    }

                    if (!TryResolveSafeRelativePath(stagingDir, rel, out var stagePath) ||
                        !TryResolveSafeRelativePath(InstallDirectory, rel, out _))
                    {
                        throw new InvalidOperationException($"Unsafe path inside release ZIP: {entry.FullName}");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(stagePath)!);
                    entry.ExtractToFile(stagePath, overwrite: true);

                    if (wantedMap is not null && wantedMap.TryGetValue(rel, out var expected))
                    {
                        var actualHash = ComputeSha256Hex(stagePath);
                        if (!string.Equals(actualHash, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException($"SHA-256 mismatch for extracted file: {rel}");
                        }
                    }
                }
            }, cancellationToken).ConfigureAwait(false);

            progress?.Report(new UpdateProgress("Ready to apply update.", 1, 1, totalBytes, totalBytes));
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            TryDeleteFile(tempZip);
        }
    }

    private UpdateCheckResult CreateErrorResult(string message) =>
        new()
        {
            Status = UpdateCheckStatus.Error,
            CurrentVersion = CurrentVersionDisplay,
            IsDevelopmentBuild = IsDevelopmentBuild,
            ErrorMessage = message
        };

    private bool HasWriteAccessToInstallDirectory()
    {
        var probe = Path.Combine(InstallDirectory, $".rem-write-probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool DetectDevelopmentBuild(string installDir)
    {
        try
        {
            var probe3 = Path.GetFullPath(Path.Combine(installDir, "..", "..", "..", "REM.csproj"));
            if (File.Exists(probe3)) return true;
            var probe4 = Path.GetFullPath(Path.Combine(installDir, "..", "..", "..", "..", "REM.csproj"));
            return File.Exists(probe4);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseSemVer(string tagOrVersion, out Version version)
    {
        var cleaned = tagOrVersion.Trim().TrimStart('v', 'V');
        var dashIdx = cleaned.IndexOf('-');
        if (dashIdx >= 0)
        {
            cleaned = cleaned[..dashIdx];
        }

        if (Version.TryParse(cleaned, out var parsed))
        {
            version = new Version(parsed.Major, Math.Max(0, parsed.Minor), Math.Max(0, parsed.Build));
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static bool TryResolveSafeRelativePath(string rootDir, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var localRel = relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(localRel) || localRel.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var normalizedRoot = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, localRel));
        if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private static string ComputeSha256Hex(string filePath)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? GetString(JsonElement el, string propName) =>
        el.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static void CleanupDirectoryIfExists(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string BuildApplyHelperPowerShellScript() =>
        """
        param([Parameter(Mandatory = $true)][string]$PlanPath)
        $ErrorActionPreference = 'Stop'

        $plan = [System.IO.File]::ReadAllText($PlanPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        $installDir = [System.IO.Path]::GetFullPath([string]$plan.installDir).TrimEnd('\') + '\'
        $stagingDir = [System.IO.Path]::GetFullPath([string]$plan.stagingDir).TrimEnd('\') + '\'
        $backupDir = [System.IO.Path]::GetFullPath([string]$plan.backupDir).TrimEnd('\') + '\'
        $stateDir = [string]$plan.stateDir

        function Resolve-SafeSubPath([string]$rootWithSlash, [string]$rel) {
            if ([string]::IsNullOrWhiteSpace($rel) -or $rel.Contains('..') -or [System.IO.Path]::IsPathRooted($rel)) {
                throw "Unsafe relative path: $rel"
            }
            $full = [System.IO.Path]::GetFullPath((Join-Path $rootWithSlash $rel))
            if (-not $full.StartsWith($rootWithSlash, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Path escaped root: $rel"
            }
            return $full
        }

        function Copy-FileRetry([string]$src, [string]$dst) {
            $dir = Split-Path -Path $dst -Parent
            if (-not [string]::IsNullOrWhiteSpace($dir)) {
                [System.IO.Directory]::CreateDirectory($dir) | Out-Null
            }
            for ($i = 1; $i -le 12; $i++) {
                try {
                    [System.IO.File]::Copy($src, $dst, $true)
                    return
                }
                catch {
                    if ($i -eq 12) { throw }
                    Start-Sleep -Milliseconds 350
                }
            }
        }

        # Wait for the exiting REM process
        $pidToWait = [int]$plan.targetPid
        for ($i = 0; $i -lt 40; $i++) {
            $proc = Get-Process -Id $pidToWait -ErrorAction SilentlyContinue
            if (-not $proc -or $proc.HasExited) { break }
            if ($i -eq 25) {
                try { $proc.Kill() } catch { }
            }
            Start-Sleep -Milliseconds 250
        }
        Start-Sleep -Milliseconds 250

        [System.IO.Directory]::CreateDirectory($backupDir.TrimEnd('\')) | Out-Null
        $backedUp = New-Object 'System.Collections.Generic.List[object]'
        $created = New-Object 'System.Collections.Generic.List[string]'

        try {
            $stagedFiles = @(Get-ChildItem -LiteralPath $stagingDir.TrimEnd('\') -Recurse -File)
            foreach ($sf in $stagedFiles) {
                $rel = $sf.FullName.Substring($stagingDir.Length)
                $dest = Resolve-SafeSubPath $installDir $rel

                if (Test-Path -LiteralPath $dest -PathType Leaf) {
                    $bak = Resolve-SafeSubPath $backupDir $rel
                    Copy-FileRetry $dest $bak
                    $backedUp.Add([PSCustomObject]@{ Bak = $bak; Dest = $dest })
                }
                else {
                    $created.Add($dest)
                }

                Copy-FileRetry $sf.FullName $dest
            }

            if ($plan.removeFiles) {
                foreach ($remRel in $plan.removeFiles) {
                    $remDest = Resolve-SafeSubPath $installDir ([string]$remRel)
                    if (Test-Path -LiteralPath $remDest -PathType Leaf) {
                        $bak = Resolve-SafeSubPath $backupDir ([string]$remRel)
                        Copy-FileRetry $remDest $bak
                        $backedUp.Add([PSCustomObject]@{ Bak = $bak; Dest = $remDest })
                        [System.IO.File]::Delete($remDest)
                    }
                }
            }

            if ($plan.newManifestJson) {
                [System.IO.File]::WriteAllText([string]$plan.manifestPath, [string]$plan.newManifestJson, [System.Text.UTF8Encoding]::new($false))
            }

            [System.IO.File]::WriteAllText(
                (Join-Path $stateDir 'last-update-success.txt'),
                "Updated REM to $($plan.versionLabel).",
                [System.Text.UTF8Encoding]::new($false))
        }
        catch {
            foreach ($c in $created) {
                try { if (Test-Path -LiteralPath $c) { [System.IO.File]::Delete($c) } } catch { }
            }
            foreach ($pair in $backedUp) {
                try { Copy-FileRetry $pair.Bak $pair.Dest } catch { }
            }
            [System.IO.File]::WriteAllText(
                (Join-Path $stateDir 'last-update-error.txt'),
                $_.Exception.Message,
                [System.Text.UTF8Encoding]::new($false))
        }
        finally {
            try { if (Test-Path -LiteralPath $stagingDir) { [System.IO.Directory]::Delete($stagingDir, $true) } } catch { }
            try { if (Test-Path -LiteralPath $backupDir) { [System.IO.Directory]::Delete($backupDir, $true) } } catch { }
            if (Test-Path -LiteralPath ([string]$plan.appExePath) -PathType Leaf) {
                Start-Process -FilePath ([string]$plan.appExePath) -WorkingDirectory $installDir.TrimEnd('\') | Out-Null
            }
        }
        """;
}
