using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using REM.Models;

namespace REM.Services;

/// <summary>
/// Spawns yt-dlp.exe silently for media probing, live progress parsing, cancellation cleanup, and updates.
/// </summary>
public sealed partial class YtDlpDownloadService
{
    private const string LatestFfmpegZipUrl =
        "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    private static readonly SemaphoreSlim FfmpegDownloadGate = new(1, 1);

    private readonly YtDlpService _ytDlp;
    private readonly ActionLogService _actionLog;

    [GeneratedRegex(@"^(?<num>[\d\.]+)\s*(?<unit>[KMG]?i?B)/s$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpeedRegex();

    public YtDlpDownloadService(YtDlpService ytDlp, ActionLogService actionLog)
    {
        _ytDlp = ytDlp;
        _actionLog = actionLog;
    }

    public async Task<string?> ResolveYtDlpAsync(MediaGrabberSettings settings, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(settings.YtDlpPath))
        {
            var custom = settings.YtDlpPath.Trim().Trim('"');
            if (File.Exists(custom))
            {
                return custom;
            }
        }

        return await _ytDlp.EnsureExecutableAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool HasFfmpegAndFfprobe(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return false;
        }

        try
        {
            return File.Exists(Path.Combine(dir, "ffmpeg.exe")) &&
                   File.Exists(Path.Combine(dir, "ffprobe.exe"));
        }
        catch
        {
            return false;
        }
    }

    public static string? ResolveFfmpegDirectory(MediaGrabberSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.FfmpegPath))
        {
            var custom = settings.FfmpegPath.Trim().Trim('"');
            var dir = File.Exists(custom) ? Path.GetDirectoryName(custom) : custom;
            if (HasFfmpegAndFfprobe(dir))
            {
                return dir;
            }
        }

        var localTools = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "REM",
            "Tools");

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Tools"),
            AppContext.BaseDirectory,
            localTools
        };

        foreach (var dir in candidates)
        {
            if (HasFfmpegAndFfprobe(dir))
            {
                return dir;
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (HasFfmpegAndFfprobe(dir.Trim()))
                {
                    return dir.Trim();
                }
            }
        }

        return null;
    }

    public async Task<string?> EnsureFfmpegDirectoryAsync(
        MediaGrabberSettings settings,
        Action<DownloadProgressUpdate>? onProgress,
        CancellationToken cancellationToken)
    {
        var existing = ResolveFfmpegDirectory(settings);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        await FfmpegDownloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = ResolveFfmpegDirectory(settings);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            Directory.CreateDirectory(_ytDlp.ManagedToolsDirectory);
            var tempZip = Path.Combine(_ytDlp.ManagedToolsDirectory, "ffmpeg.zip.download");
            try
            {
                if (File.Exists(tempZip))
                {
                    File.Delete(tempZip);
                }

                onProgress?.Invoke(new DownloadProgressUpdate(0, "Downloading ffmpeg + ffprobe...", 0, "--", 0, "ffmpeg setup"));

                using var http = new HttpClient(new BandwidthCountingHandler())
                {
                    Timeout = TimeSpan.FromMinutes(10)
                };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("REM/1.0");

                using var resp = await http.GetAsync(
                    LatestFfmpegZipUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                var totalLen = resp.Content.Headers.ContentLength ?? 0;
                await using (var remoteStream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var localStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    long copied = 0;
                    var lastReport = DateTime.UtcNow;
                    int read;
                    while ((read = await remoteStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await localStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        copied += read;
                        if (totalLen > 0 && (DateTime.UtcNow - lastReport).TotalMilliseconds >= 250)
                        {
                            lastReport = DateTime.UtcNow;
                            var pct = Math.Clamp(copied * 100.0 / totalLen, 0, 99.0);
                            onProgress?.Invoke(new DownloadProgressUpdate(pct, "Downloading ffmpeg...", 0, "--", totalLen, "ffmpeg setup"));
                        }
                    }
                }

                onProgress?.Invoke(new DownloadProgressUpdate(99, "Extracting ffmpeg + ffprobe...", 0, "--", totalLen, "ffmpeg setup"));

                await Task.Run(() =>
                {
                    using var archive = ZipFile.OpenRead(tempZip);
                    foreach (var entry in archive.Entries)
                    {
                        var fileName = Path.GetFileName(entry.FullName);
                        if (!string.Equals(fileName, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(fileName, "ffprobe.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var destPath = Path.Combine(_ytDlp.ManagedToolsDirectory, fileName);
                        var stagePath = destPath + ".tmp";
                        entry.ExtractToFile(stagePath, overwrite: true);
                        var (valid, sha, err) = ToolIntegrityHelper.ValidateExecutableAndHash(stagePath);
                        if (!valid)
                        {
                            try { File.Delete(stagePath); } catch { }
                            throw new InvalidOperationException($"Extracted {fileName} failed integrity check: {err}");
                        }

                        File.Move(stagePath, destPath, overwrite: true);
                        _actionLog.Log("MediaGrabber", $"Verified {fileName} SHA-256: {sha[..16]}...");
                    }
                }, cancellationToken).ConfigureAwait(false);

                if (HasFfmpegAndFfprobe(_ytDlp.ManagedToolsDirectory))
                {
                    _actionLog.Log("MediaGrabber", $"Downloaded ffmpeg.exe and ffprobe.exe to '{_ytDlp.ManagedToolsDirectory}'.");
                    return _ytDlp.ManagedToolsDirectory;
                }

                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _actionLog.Log("Warning", $"Automatic ffmpeg download failed: {ex.Message}");
                return null;
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }
        }
        finally
        {
            FfmpegDownloadGate.Release();
        }
    }

    public async Task<YtDlpProbeResult?> ProbeAsync(
        string url,
        MediaGrabberSettings settings,
        CancellationToken cancellationToken)
    {
        var exePath = await ResolveYtDlpAsync(settings, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return null;
        }

        try { Directory.CreateDirectory(_ytDlp.CacheDirectory); } catch { }

        var isPlaylist = url.Contains("list=", StringComparison.OrdinalIgnoreCase) ||
                         url.Contains("/playlist", StringComparison.OrdinalIgnoreCase);

        var psi = CreateStartInfo(exePath);
        psi.ArgumentList.Add("--dump-single-json");
        psi.ArgumentList.Add(isPlaylist ? "--flat-playlist" : "--no-playlist");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--skip-download");
        psi.ArgumentList.Add("--force-ipv4");
        psi.ArgumentList.Add("--cache-dir");
        psi.ArgumentList.Add(_ytDlp.CacheDirectory);
        psi.ArgumentList.Add(url);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            return null;
        }

        using var reg = cancellationToken.Register(() => TryKillProcessTree(process));

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
        _ = await stderrTask.ConfigureAwait(false);

        if (!string.IsNullOrEmpty(stdout))
        {
            BandwidthMonitor.RecordBytes(Encoding.UTF8.GetByteCount(stdout));
        }

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var title = root.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String
                ? tEl.GetString()?.Trim()
                : null;
            var thumb = root.TryGetProperty("thumbnail", out var thEl) && thEl.ValueKind == JsonValueKind.String
                ? thEl.GetString()?.Trim()
                : null;
            double? dur = root.TryGetProperty("duration", out var dEl) && dEl.ValueKind == JsonValueKind.Number
                ? dEl.GetDouble()
                : null;

            var type = root.TryGetProperty("_type", out var tyEl) ? tyEl.GetString() : null;
            var entryCount = 1;
            if (string.Equals(type, "playlist", StringComparison.OrdinalIgnoreCase) &&
                root.TryGetProperty("entries", out var entries) &&
                entries.ValueKind == JsonValueKind.Array)
            {
                entryCount = entries.GetArrayLength();
            }

            return new YtDlpProbeResult(
                Title: string.IsNullOrWhiteSpace(title) ? url : title,
                ThumbnailUrl: thumb,
                DurationSeconds: dur,
                IsPlaylist: string.Equals(type, "playlist", StringComparison.OrdinalIgnoreCase),
                EntryCount: Math.Max(1, entryCount));
        }
        catch
        {
            return null;
        }
    }

    public async Task<(bool Success, string? FinalFilePath, long FileSizeBytes, string? Error)> DownloadAsync(
        DownloadJobState job,
        MediaGrabberSettings settings,
        Action<DownloadProgressUpdate> onProgress,
        Action<Process> onProcessStarted,
        CancellationToken cancellationToken)
    {
        var exePath = await ResolveYtDlpAsync(settings, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return (false, null, 0, "yt-dlp.exe could not be found or downloaded.");
        }

        var outputDir = string.IsNullOrWhiteSpace(settings.DefaultDownloadFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "REM")
            : settings.DefaultDownloadFolder.Trim();
        Directory.CreateDirectory(outputDir);
        try { Directory.CreateDirectory(_ytDlp.CacheDirectory); } catch { }

        var startedUtc = DateTime.UtcNow.AddSeconds(-1);
        var psi = CreateStartInfo(exePath);
        psi.Environment["PYTHONUNBUFFERED"] = "1";

        AppendFormatArguments(psi.ArgumentList, job.FormatPreset);
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--force-ipv4");
        psi.ArgumentList.Add("--newline");
        psi.ArgumentList.Add("--progress");
        psi.ArgumentList.Add("--progress-template");
        psi.ArgumentList.Add(
            "download:REM_PROG|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s|%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s");
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("after_move:REM_FINAL|%(filepath)s");
        psi.ArgumentList.Add("-P");
        psi.ArgumentList.Add(outputDir);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("%(title).140s [%(id)s].%(ext)s");
        psi.ArgumentList.Add("--cache-dir");
        psi.ArgumentList.Add(_ytDlp.CacheDirectory);

        var ffmpegDir = await EnsureFfmpegDirectoryAsync(settings, onProgress, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(ffmpegDir))
        {
            psi.ArgumentList.Add("--ffmpeg-location");
            psi.ArgumentList.Add(ffmpegDir);
        }

        psi.ArgumentList.Add(job.Url);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            return (false, null, 0, "Failed to start yt-dlp.exe.");
        }

        onProcessStarted(process);
        using var reg = cancellationToken.Register(() => TryKillProcessTree(process));

        string? finalFilePath = null;
        string? destinationPath = null;
        long lastReportedBytes = 0;

        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                BandwidthMonitor.RecordBytes(Encoding.UTF8.GetByteCount(line));
                if (line.StartsWith("REM_PROG|", StringComparison.Ordinal))
                {
                    if (TryParseProgressLine(line, out var update, ref lastReportedBytes))
                    {
                        onProgress(update);
                    }
                }
                else if (line.StartsWith("REM_FINAL|", StringComparison.Ordinal))
                {
                    var candidate = line["REM_FINAL|".Length..].Trim();
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        finalFilePath = candidate;
                    }
                }
                else if (line.StartsWith("[download] Destination:", StringComparison.Ordinal))
                {
                    destinationPath = line["[download] Destination:".Length..].Trim();
                }
                else if (line.StartsWith("[Merger] Merging formats into \"", StringComparison.Ordinal) &&
                         line.EndsWith('"'))
                {
                    finalFilePath = line["[Merger] Merging formats into \"".Length..^1].Trim();
                }
                else if (line.StartsWith("[ExtractAudio] Destination:", StringComparison.Ordinal))
                {
                    finalFilePath = line["[ExtractAudio] Destination:".Length..].Trim();
                }
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            CleanupPartialFiles(outputDir, destinationPath, startedUtc);
            throw;
        }

        var stderr = (await stderrTask.ConfigureAwait(false)).Trim();

        if (process.ExitCode != 0)
        {
            CleanupPartialFiles(outputDir, destinationPath, startedUtc);
            var err = string.IsNullOrWhiteSpace(stderr)
                ? $"yt-dlp exited with code {process.ExitCode}."
                : stderr;
            _actionLog.Log("Warning", $"yt-dlp download failed: {err}");
            return (false, null, 0, err);
        }

        var resolvedFinal = finalFilePath ?? destinationPath;
        long finalSize = 0;
        if (!string.IsNullOrWhiteSpace(resolvedFinal) && File.Exists(resolvedFinal))
        {
            try { finalSize = new FileInfo(resolvedFinal).Length; } catch { }
        }
        else
        {
            resolvedFinal = TryFindRecentOutputFile(outputDir, startedUtc);
            if (!string.IsNullOrWhiteSpace(resolvedFinal) && File.Exists(resolvedFinal))
            {
                try { finalSize = new FileInfo(resolvedFinal).Length; } catch { }
            }
        }

        if (finalSize > lastReportedBytes)
        {
            BandwidthMonitor.RecordBytes(finalSize - lastReportedBytes);
        }

        return (true, resolvedFinal, finalSize, null);
    }

    public async Task<(bool Success, string Output)> UpdateYtDlpAsync(
        MediaGrabberSettings settings,
        CancellationToken cancellationToken)
    {
        var exePath = await ResolveYtDlpAsync(settings, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return (false, "yt-dlp.exe could not be found or downloaded.");
        }

        var psi = CreateStartInfo(exePath);
        psi.ArgumentList.Add("-U");

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            return (false, "Failed to launch yt-dlp.exe -U.");
        }

        using var reg = cancellationToken.Register(() => TryKillProcessTree(process));

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
        var stderr = (await stderrTask.ConfigureAwait(false)).Trim();
        var combined = string.Join(Environment.NewLine, new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return (process.ExitCode == 0, string.IsNullOrWhiteSpace(combined) ? $"Exited with code {process.ExitCode}." : combined);
    }

    private static void AppendFormatArguments(IList<string> args, string formatPreset)
    {
        switch (formatPreset)
        {
            case DownloadFormatPresets.P1080:
                args.Add("-f");
                args.Add("bv*[height<=1080][ext=mp4]+ba[ext=m4a]/bv*[height<=1080]+ba/b[height<=1080]/b");
                args.Add("--merge-output-format");
                args.Add("mp4");
                break;
            case DownloadFormatPresets.P720:
                args.Add("-f");
                args.Add("bv*[height<=720][ext=mp4]+ba[ext=m4a]/bv*[height<=720]+ba/b[height<=720]/b");
                args.Add("--merge-output-format");
                args.Add("mp4");
                break;
            case DownloadFormatPresets.Mp3Audio:
                args.Add("-x");
                args.Add("--audio-format");
                args.Add("mp3");
                args.Add("--audio-quality");
                args.Add("0");
                break;
            case DownloadFormatPresets.M4aAudio:
                args.Add("-x");
                args.Add("--audio-format");
                args.Add("m4a");
                args.Add("--audio-quality");
                args.Add("0");
                break;
            case DownloadFormatPresets.RawFile:
                args.Add("-f");
                args.Add("b/bv*+ba");
                break;
            default:
                args.Add("-f");
                args.Add("bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4]/bv*+ba/b");
                args.Add("--merge-output-format");
                args.Add("mp4");
                break;
        }
    }

    private static bool TryParseProgressLine(
        string line,
        out DownloadProgressUpdate update,
        ref long lastReportedBytes)
    {
        // Format: REM_PROG|percent|speed|eta|status|downloaded_bytes|total_bytes|total_bytes_estimate
        var parts = line.Split('|');
        if (parts.Length < 8)
        {
            update = default!;
            return false;
        }

        var rawPercent = parts[1].Trim().TrimEnd('%');
        var percent = double.TryParse(rawPercent, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)
            ? Math.Clamp(pct, 0, 100)
            : 0;

        var speedStr = NormalizeField(parts[2]);
        var etaStr = NormalizeField(parts[3]);
        var statusStr = NormalizeField(parts[4]);

        long.TryParse(parts[5].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dlBytes);
        long.TryParse(parts[6].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalBytes);
        long.TryParse(parts[7].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var estBytes);

        var fileSize = totalBytes > 0 ? totalBytes : (estBytes > 0 ? estBytes : dlBytes);
        if (dlBytes > lastReportedBytes)
        {
            BandwidthMonitor.RecordBytes(dlBytes - lastReportedBytes);
            lastReportedBytes = dlBytes;
        }

        var speedBytes = ParseSpeedBytesPerSec(speedStr);
        update = new DownloadProgressUpdate(
            ProgressPercent: percent,
            SpeedText: string.IsNullOrEmpty(speedStr) ? "--" : speedStr,
            SpeedBytesPerSec: speedBytes,
            EtaText: string.IsNullOrEmpty(etaStr) ? "--" : etaStr,
            FileSizeBytes: fileSize,
            StatusText: statusStr);
        return true;
    }

    private static string NormalizeField(string raw)
    {
        var trimmed = raw.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ||
               string.Equals(trimmed, "NA", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "Unknown", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : trimmed;
    }

    private static long ParseSpeedBytesPerSec(string speedStr)
    {
        if (string.IsNullOrWhiteSpace(speedStr))
        {
            return 0;
        }

        var match = SpeedRegex().Match(speedStr.Trim());
        if (!match.Success ||
            !double.TryParse(match.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return 0;
        }

        return match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "KIB" => (long)(value * 1024),
            "KB" => (long)(value * 1000),
            "MIB" => (long)(value * 1024 * 1024),
            "MB" => (long)(value * 1000 * 1000),
            "GIB" => (long)(value * 1024 * 1024 * 1024),
            "GB" => (long)(value * 1000 * 1000 * 1000),
            _ => (long)value
        };
    }

    private static void CleanupPartialFiles(string outputDir, string? destinationPath, DateTime startedUtc)
    {
        try
        {
            if (!Directory.Exists(outputDir))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(destinationPath))
            {
                var part = destinationPath + ".part";
                if (File.Exists(part)) File.Delete(part);
                var ytdl = destinationPath + ".ytdl";
                if (File.Exists(ytdl)) File.Delete(ytdl);
            }

            foreach (var file in Directory.EnumerateFiles(outputDir, "*.part"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) >= startedUtc)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static string? TryFindRecentOutputFile(string outputDir, DateTime startedUtc)
    {
        try
        {
            return Directory.EnumerateFiles(outputDir)
                .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) &&
                            !f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase) &&
                            File.GetLastWriteTimeUtc(f) >= startedUtc)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static ProcessStartInfo CreateStartInfo(string exePath) =>
        new(exePath)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

    public static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
