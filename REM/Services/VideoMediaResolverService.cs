using System.Diagnostics;
using System.Text;
using REM.Models;

namespace REM.Services;

/// <summary>
/// Resolves local video files, direct HTTP/HLS/DASH streams (with optional Referer),
/// and embed/web links via local yt-dlp.exe strictly asynchronously.
/// </summary>
public sealed class VideoMediaResolverService
{
    private static readonly HashSet<string> DirectMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v", ".ts", ".flv", ".m3u8", ".mpd"
    };

    private static readonly HashSet<string> DirectStreamExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m3u8", ".mpd", ".mp4", ".webm", ".mkv", ".avi", ".mov", ".m4v", ".ts"
    };

    private readonly YtDlpService _ytDlp;
    private readonly ActionLogService _actionLog;

    public VideoMediaResolverService(YtDlpService ytDlp, ActionLogService actionLog)
    {
        _ytDlp = ytDlp;
        _actionLog = actionLog;
    }

    public async Task<ResolvedVideoMedia> ResolveAsync(
        string input,
        string? referer = null,
        string? titleOverride = null,
        int? maxHeight = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("Media path or URL cannot be empty.", nameof(input));
        }

        var cleaned = input.Trim().Trim('"').Trim();
        var cleanReferer = string.IsNullOrWhiteSpace(referer) ? null : referer.Trim();

        // 1. Local file path or file:// URI
        if (TryResolveLocalPath(cleaned, out var localPath))
        {
            var localTitle = string.IsNullOrWhiteSpace(titleOverride)
                ? Path.GetFileNameWithoutExtension(localPath)
                : titleOverride.Trim();
            return new ResolvedVideoMedia(
                PlayableUrl: localPath,
                Title: string.IsNullOrWhiteSpace(localTitle) ? Path.GetFileName(localPath) : localTitle,
                OriginalInput: cleaned,
                Referer: null,
                IsLocalFile: true,
                UsedYtDlp: false);
        }

        if (!Uri.TryCreate(cleaned, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"Unsupported or missing media source: '{cleaned}'.", nameof(input));
        }

        // 2. Direct stream / HLS / DASH URLs (.m3u8, .mpd, .mp4, .webm, etc.)
        var ext = Path.GetExtension(uri.AbsolutePath);
        if (!string.IsNullOrWhiteSpace(ext) && DirectStreamExtensions.Contains(ext))
        {
            var directName = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            var title = !string.IsNullOrWhiteSpace(titleOverride)
                ? titleOverride.Trim()
                : (string.IsNullOrWhiteSpace(directName) ? uri.Host : Uri.UnescapeDataString(directName));

            return new ResolvedVideoMedia(
                PlayableUrl: uri.AbsoluteUri,
                Title: title,
                OriginalInput: cleaned,
                Referer: cleanReferer,
                IsLocalFile: false,
                UsedYtDlp: false);
        }

        // 3. Web / embed / YouTube / Twitch VOD links -> silent yt-dlp.exe extraction
        var exePath = await _ytDlp.EnsureExecutableAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("yt-dlp.exe was not found and automatic download failed.");

        try
        {
            Directory.CreateDirectory(_ytDlp.CacheDirectory);
        }
        catch
        {
            // Continue even if cache directory creation is blocked.
        }

        var psi = new ProcessStartInfo(exePath)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(BuildFormatSelector(maxHeight));
        psi.ArgumentList.Add("--get-title");
        psi.ArgumentList.Add("--get-url");
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--force-ipv4");
        psi.ArgumentList.Add("--cache-dir");
        psi.ArgumentList.Add(_ytDlp.CacheDirectory);

        if (!string.IsNullOrWhiteSpace(cleanReferer))
        {
            psi.ArgumentList.Add("--referer");
            psi.ArgumentList.Add(cleanReferer);
        }

        psi.ArgumentList.Add(uri.AbsoluteUri);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start yt-dlp.exe.");
        }

        using var reg = cancellationToken.Register(() =>
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
                // Ignore kill race.
            }
        });

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
        var stderr = (await stderrTask.ConfigureAwait(false)).Trim();

        if (!string.IsNullOrEmpty(stdout))
        {
            BandwidthMonitor.RecordBytes(Encoding.UTF8.GetByteCount(stdout));
        }

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            var reason = string.IsNullOrWhiteSpace(stderr)
                ? $"yt-dlp exited with code {process.ExitCode}."
                : stderr;
            _actionLog.Log("Warning", $"Video yt-dlp resolve failed for '{uri.Host}': {reason}");
            throw new InvalidOperationException(reason);
        }

        var lines = stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        var urls = new List<string>(2);
        string? resolvedTitle = null;

        foreach (var line in lines)
        {
            if (Uri.TryCreate(line, UriKind.Absolute, out var candidate) &&
                (candidate.Scheme == Uri.UriSchemeHttp || candidate.Scheme == Uri.UriSchemeHttps))
            {
                urls.Add(candidate.AbsoluteUri);
            }
            else if (resolvedTitle is null)
            {
                resolvedTitle = line;
            }
        }

        if (urls.Count == 0)
        {
            throw new InvalidOperationException("yt-dlp did not return a playable video URL.");
        }

        var resolvedUrl = urls[0];
        var audioSlaveUrl = urls.Count > 1 ? urls[1] : null;
        var finalTitle = !string.IsNullOrWhiteSpace(titleOverride)
            ? titleOverride.Trim()
            : (resolvedTitle ?? uri.Host);

        _actionLog.Log("Video", $"Resolved web video via yt-dlp: '{finalTitle}'");
        return new ResolvedVideoMedia(
            PlayableUrl: resolvedUrl,
            Title: finalTitle,
            OriginalInput: cleaned,
            Referer: cleanReferer,
            IsLocalFile: false,
            UsedYtDlp: true,
            AudioSlaveUrl: audioSlaveUrl);
    }

    public async Task<string?> ResolveAudioOnlyUrlAsync(
        string input,
        string? referer = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var cleaned = input.Trim().Trim('"').Trim();
        if (File.Exists(cleaned) ||
            !Uri.TryCreate(cleaned, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var exePath = await _ytDlp.EnsureExecutableAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(_ytDlp.CacheDirectory);
        }
        catch
        {
        }

        var psi = new ProcessStartInfo(exePath)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("bestaudio[ext=m4a]/bestaudio[protocol^=http]/bestaudio/audio_only/worst");
        psi.ArgumentList.Add("--get-url");
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--force-ipv4");
        psi.ArgumentList.Add("--cache-dir");
        psi.ArgumentList.Add(_ytDlp.CacheDirectory);

        var cleanReferer = string.IsNullOrWhiteSpace(referer) ? null : referer.Trim();
        if (!string.IsNullOrWhiteSpace(cleanReferer))
        {
            psi.ArgumentList.Add("--referer");
            psi.ArgumentList.Add(cleanReferer);
        }

        psi.ArgumentList.Add(uri.AbsoluteUri);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            return null;
        }

        using var reg = cancellationToken.Register(() =>
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
        });

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

        foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var candidate) &&
                (candidate.Scheme == Uri.UriSchemeHttp || candidate.Scheme == Uri.UriSchemeHttps))
            {
                return candidate.AbsoluteUri;
            }
        }

        return null;
    }

    private static string BuildFormatSelector(int? maxHeight)
    {
        if (maxHeight is not { } h)
        {
            return "bestvideo[vcodec^=avc1]+bestaudio[ext=m4a]/bestvideo+bestaudio/best[ext=mp4]/best";
        }

        if (h <= 360)
        {
            return $"best[height<={h}][ext=mp4]/bestvideo[height<={h}][vcodec^=avc1]+bestaudio[ext=m4a]/bestvideo[height<={h}]+bestaudio/best[height<={h}]/worst";
        }

        return $"bestvideo[height<={h}][vcodec^=avc1]+bestaudio[ext=m4a]/bestvideo[height<={h}]+bestaudio/best[height<={h}][ext=mp4]/best[height<={h}]/best";
    }

    private static bool TryResolveLocalPath(string input, out string localPath)
    {
        localPath = string.Empty;
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            if (File.Exists(uri.LocalPath))
            {
                localPath = uri.LocalPath;
                return true;
            }

            return false;
        }

        if (File.Exists(input))
        {
            localPath = Path.GetFullPath(input);
            return true;
        }

        if (!input.Contains("://", StringComparison.Ordinal))
        {
            var ext = Path.GetExtension(input);
            if (!string.IsNullOrWhiteSpace(ext) && DirectMediaExtensions.Contains(ext))
            {
                throw new FileNotFoundException($"Local video file was not found: '{input}'.", input);
            }
        }

        return false;
    }
}
