using System.Diagnostics;
using System.Text;
using System.Text.Json;
using REM.Models;

namespace REM.Services;

public sealed record YtDlpResult(
    bool Success,
    string? StreamUrl,
    string? ErrorMessage,
    bool IsMissingExecutable,
    string? QualityBadge = null,
    string? ResolvedTitle = null,
    string? ResolvedChannel = null,
    TimeSpan? ResolvedDuration = null);

public sealed class YtDlpService
{
    private const string LatestYtDlpWindowsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);

    public string ManagedToolsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "REM",
        "Tools");

    public string CacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "REM",
        "yt-dlp-cache");

    public async Task<string?> EnsureExecutableAsync(CancellationToken cancellationToken = default)
    {
        var existing = ResolveExecutablePath();
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        await DownloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = ResolveExecutablePath();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            Directory.CreateDirectory(ManagedToolsDirectory);
            var targetPath = Path.Combine(ManagedToolsDirectory, "yt-dlp.exe");
            var tempPath = targetPath + ".download";

            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                using var http = new HttpClient(new BandwidthCountingHandler())
                {
                    Timeout = TimeSpan.FromMinutes(3)
                };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("REM/1.0");

                using var resp = await http.GetAsync(
                    LatestYtDlpWindowsUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                await using (var remoteStream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var localStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await remoteStream.CopyToAsync(localStream, cancellationToken).ConfigureAwait(false);
                }

                var info = new FileInfo(tempPath);
                if (!info.Exists || info.Length < 1_000_000)
                {
                    if (info.Exists) info.Delete();
                    return null;
                }

                File.Move(tempPath, targetPath, overwrite: true);
                return targetPath;
            }
            catch (OperationCanceledException)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                return null;
            }
        }
        finally
        {
            DownloadGate.Release();
        }
    }
    public string? ResolveExecutablePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Tools", "yt-dlp.exe"),
            Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "REM",
                "Tools",
                "yt-dlp.exe")
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var full = Path.Combine(dir.Trim(), "yt-dlp.exe");
                    if (File.Exists(full))
                    {
                        return full;
                    }
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        return null;
    }

    public string? DetectZenProfileDirectory()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var zenRoot = Path.Combine(appData, "zen");
            if (!Directory.Exists(zenRoot))
            {
                return null;
            }

            var installDefaults = new List<string>();
            var profileDefaults = new List<string>();
            var otherProfiles = new List<string>();

            var iniPath = Path.Combine(zenRoot, "profiles.ini");
            if (File.Exists(iniPath))
            {
                var currentSection = string.Empty;
                string? profPath = null;
                var isRelative = true;
                var isDefaultFlag = false;

                void FlushSection()
                {
                    if (string.IsNullOrWhiteSpace(profPath))
                    {
                        return;
                    }

                    var normalized = profPath.Trim().Replace('/', Path.DirectorySeparatorChar);
                    var resolved = isRelative ? Path.Combine(zenRoot, normalized) : normalized;
                    if (currentSection.StartsWith("Install", StringComparison.OrdinalIgnoreCase))
                    {
                        installDefaults.Add(resolved);
                    }
                    else if (isDefaultFlag)
                    {
                        profileDefaults.Add(resolved);
                    }
                    else
                    {
                        otherProfiles.Add(resolved);
                    }
                }

                foreach (var rawLine in File.ReadLines(iniPath))
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith(';') || line.StartsWith('#'))
                    {
                        continue;
                    }

                    if (line.StartsWith('[') && line.EndsWith(']'))
                    {
                        FlushSection();
                        currentSection = line[1..^1].Trim();
                        profPath = null;
                        isRelative = true;
                        isDefaultFlag = false;
                        continue;
                    }

                    var eqIdx = line.IndexOf('=');
                    if (eqIdx <= 0)
                    {
                        continue;
                    }

                    var key = line[..eqIdx].Trim();
                    var val = line[(eqIdx + 1)..].Trim();

                    if (currentSection.StartsWith("Install", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(key, "Default", StringComparison.OrdinalIgnoreCase))
                    {
                        profPath = val;
                        isRelative = true;
                    }
                    else if (string.Equals(key, "Path", StringComparison.OrdinalIgnoreCase))
                    {
                        profPath = val;
                    }
                    else if (string.Equals(key, "IsRelative", StringComparison.OrdinalIgnoreCase))
                    {
                        isRelative = val != "0";
                    }
                    else if (string.Equals(key, "Default", StringComparison.OrdinalIgnoreCase))
                    {
                        isDefaultFlag = val == "1";
                    }
                }

                FlushSection();
            }

            foreach (var candidate in installDefaults.Concat(profileDefaults).Concat(otherProfiles).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "cookies.sqlite")))
                {
                    return candidate;
                }
            }

            var profilesDir = Path.Combine(zenRoot, "Profiles");
            if (Directory.Exists(profilesDir))
            {
                var best = Directory.EnumerateDirectories(profilesDir)
                    .Select(dir => new { Dir = dir, CookieFile = Path.Combine(dir, "cookies.sqlite") })
                    .Where(x => File.Exists(x.CookieFile))
                    .OrderByDescending(x => File.GetLastWriteTimeUtc(x.CookieFile))
                    .FirstOrDefault();

                if (best is not null)
                {
                    return best.Dir;
                }
            }
        }
        catch
        {
            // Ignore unreadable profile folders.
        }

        return null;
    }

    public string? ResolveCookieBrowserArgument(
        string? browserKey,
        string? customProfilePath,
        out string statusMessage)
    {
        var key = (browserKey ?? "none").Trim().ToLowerInvariant();
        var custom = customProfilePath?.Trim();

        switch (key)
        {
            case "zen":
            {
                if (!string.IsNullOrWhiteSpace(custom))
                {
                    if (Directory.Exists(custom))
                    {
                        statusMessage = $"Zen Browser custom profile: {Path.GetFileName(custom.TrimEnd(Path.DirectorySeparatorChar))}";
                        return $"firefox:{custom}";
                    }

                    statusMessage = $"Custom Zen profile path not found: {custom}";
                    return null;
                }

                var zenProfile = DetectZenProfileDirectory();
                if (!string.IsNullOrWhiteSpace(zenProfile))
                {
                    statusMessage = $"Zen Browser detected ({Path.GetFileName(zenProfile)})";
                    return $"firefox:{zenProfile}";
                }

                statusMessage = "Zen Browser profile with cookies.sqlite not found in %APPDATA%\\zen.";
                return null;
            }
            case "firefox":
            case "chrome":
            case "edge":
            case "brave":
            {
                var displayName = char.ToUpperInvariant(key[0]) + key[1..];
                if (!string.IsNullOrWhiteSpace(custom))
                {
                    statusMessage = $"{displayName} (profile: {custom})";
                    return $"{key}:{custom}";
                }

                statusMessage = $"{displayName} default profile";
                return key;
            }
            default:
                statusMessage = "Disabled (Topics & Search only)";
                return null;
        }
    }

    public async Task<YtDlpResult> ExtractAudioStreamUrlAsync(
        string mediaUrl,
        bool isTwitch,
        CancellationToken cancellationToken,
        string? cookieBrowserArg = null,
        string qualityPreset = "max",
        bool includeMetadata = false)
    {
        var exePath = await EnsureExecutableAsync(cancellationToken).ConfigureAwait(false);
        if (exePath is null)
        {
            return new YtDlpResult(
                false,
                null,
                "yt-dlp.exe was not found and automatic download failed. Check your internet or place yt-dlp.exe in Tools\\.",
                IsMissingExecutable: true);
        }

        try { Directory.CreateDirectory(CacheDirectory); } catch { }

        var format = BuildFormatSelector(isTwitch, qualityPreset);
        var result = await RunExtractOnceAsync(
            exePath, mediaUrl, isTwitch, format, includeMetadata, cookieArg: null, cancellationToken).ConfigureAwait(false);

        if (!result.Success &&
            !cancellationToken.IsCancellationRequested &&
            !isTwitch &&
            !string.IsNullOrWhiteSpace(cookieBrowserArg))
        {
            result = await RunExtractOnceAsync(
                exePath, mediaUrl, isTwitch: false, format, includeMetadata, cookieBrowserArg, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<YtDlpResult> RunExtractOnceAsync(
        string exePath,
        string mediaUrl,
        bool isTwitch,
        string format,
        bool includeMetadata,
        string? cookieArg,
        CancellationToken cancellationToken)
    {
        var psi = CreateStartInfo(exePath);

        psi.ArgumentList.Add("--force-ipv4");
        psi.ArgumentList.Add("--cache-dir");
        psi.ArgumentList.Add(CacheDirectory);
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(format);
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--skip-download");
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add(includeMetadata
            ? "%(acodec)s|%(abr)s|%(duration)s|%(uploader)s|%(title)s|%(url)s"
            : "%(acodec)s|%(abr)s|%(url)s");

        if (!isTwitch && !string.IsNullOrWhiteSpace(cookieArg))
        {
            psi.ArgumentList.Add("--cookies-from-browser");
            psi.ArgumentList.Add(cookieArg);
        }

        psi.ArgumentList.Add(mediaUrl);

        var (exitCode, stdout, stderr, cancelled, errorMsg) = await RunProcessAsync(psi, cancellationToken).ConfigureAwait(false);
        if (cancelled)
        {
            return new YtDlpResult(false, null, "Extraction cancelled.", false);
        }

        if (errorMsg is not null)
        {
            return new YtDlpResult(false, null, errorMsg, false);
        }

        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            var errMsg = string.IsNullOrWhiteSpace(stderr)
                ? $"yt-dlp exited with code {exitCode}."
                : stderr;
            return new YtDlpResult(false, null, errMsg, false);
        }

        var firstLine = stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim();

        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return new YtDlpResult(false, null, "yt-dlp returned an empty stream URL.", false);
        }

        if (includeMetadata)
        {
            var parts = firstLine.Split('|', 6);
            if (parts.Length == 6 && Uri.IsWellFormedUriString(parts[5].Trim(), UriKind.Absolute))
            {
                var badge = FormatQualityBadge(parts[0], parts[1]);
                TimeSpan? dur = double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var secs) && secs > 0
                    ? TimeSpan.FromSeconds(secs)
                    : null;
                var channel = CleanMetaField(parts[3]);
                var title = CleanMetaField(parts[4]);
                return new YtDlpResult(true, parts[5].Trim(), null, false, badge, title, channel, dur);
            }
        }
        else
        {
            var parts = firstLine.Split('|', 3);
            if (parts.Length == 3 && Uri.IsWellFormedUriString(parts[2].Trim(), UriKind.Absolute))
            {
                var badge = FormatQualityBadge(parts[0], parts[1]);
                return new YtDlpResult(true, parts[2].Trim(), null, false, badge);
            }
        }

        return new YtDlpResult(true, firstLine, null, false);
    }

    private static string BuildFormatSelector(bool isTwitch, string qualityPreset)
    {
        if (isTwitch)
        {
            return "audio_only/bestaudio/best";
        }

        return qualityPreset.Trim().ToLowerInvariant() switch
        {
            "160" => "bestaudio[abr<=165][protocol^=http][protocol!*=m3u8]/bestaudio[abr<=165]/bestaudio/best",
            "128" => "bestaudio[abr<=132][protocol^=http][protocol!*=m3u8]/bestaudio[abr<=132]/bestaudio/best",
            "64" => "bestaudio[abr<=68][protocol^=http][protocol!*=m3u8]/bestaudio[abr<=68]/bestaudio/best",
            _ => "bestaudio[protocol^=http][protocol!*=m3u8][acodec^=opus]/bestaudio[protocol^=http][protocol!*=m3u8]/bestaudio/best"
        };
    }

    private static string? FormatQualityBadge(string rawCodec, string rawAbr)
    {
        var codec = CleanMetaField(rawCodec);
        if (!string.IsNullOrWhiteSpace(codec))
        {
            if (codec.StartsWith("opus", StringComparison.OrdinalIgnoreCase)) codec = "Opus";
            else if (codec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase) || codec.StartsWith("aac", StringComparison.OrdinalIgnoreCase)) codec = "AAC";
            else if (codec.StartsWith("vorbis", StringComparison.OrdinalIgnoreCase)) codec = "Vorbis";
            else codec = codec.ToUpperInvariant();
        }

        var kbpsText = double.TryParse(rawAbr?.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var abr) && abr > 0
            ? $"{Math.Round(abr):0} kbps"
            : null;

        if (!string.IsNullOrWhiteSpace(codec) && !string.IsNullOrWhiteSpace(kbpsText))
        {
            return $"{codec} · {kbpsText}";
        }

        return codec ?? kbpsText;
    }

    private static string? CleanMetaField(string? raw)
    {
        var trimmed = raw?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ||
               string.Equals(trimmed, "NA", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }

    public Task<YtDlpFeedResult> FetchYouTubeFeedAsync(
        string feedUrl,
        string cookieBrowserArg,
        int maxItems,
        CancellationToken cancellationToken,
        int startIndex = 1) =>
        FetchFlatPlaylistAsync(feedUrl, startIndex, maxItems, cookieBrowserArg, cancellationToken);

    public Task<YtDlpFeedResult> SearchYouTubePagedAsync(
        string query,
        int startIndex,
        int count,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult(new YtDlpFeedResult(true, [], null, false));
        }

        return FetchFlatPlaylistAsync(
            $"ytsearchall:{query.Trim()}",
            startIndex,
            count,
            cookieBrowserArg: null,
            cancellationToken);
    }

    private async Task<YtDlpFeedResult> FetchFlatPlaylistAsync(
        string target,
        int startIndex,
        int maxItems,
        string? cookieBrowserArg,
        CancellationToken cancellationToken)
    {
        var exePath = await EnsureExecutableAsync(cancellationToken).ConfigureAwait(false);
        if (exePath is null)
        {
            return new YtDlpFeedResult(
                false,
                [],
                "yt-dlp.exe was not found and automatic download failed. Check your internet or place yt-dlp.exe in Tools\\.",
                IsMissingExecutable: true);
        }

        var start = Math.Max(1, startIndex);
        var count = Math.Clamp(maxItems, 5, 60);
        var end = start + count - 1;

        var psi = CreateStartInfo(exePath);
        psi.ArgumentList.Add("--flat-playlist");
        psi.ArgumentList.Add("--dump-json");
        psi.ArgumentList.Add("--playlist-start");
        psi.ArgumentList.Add(start.ToString());
        psi.ArgumentList.Add("--playlist-end");
        psi.ArgumentList.Add(end.ToString());
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--ignore-errors");
        psi.ArgumentList.Add("--force-ipv4");
        if (!string.IsNullOrWhiteSpace(cookieBrowserArg))
        {
            psi.ArgumentList.Add("--cookies-from-browser");
            psi.ArgumentList.Add(cookieBrowserArg);
        }
        psi.ArgumentList.Add(target);

        var (exitCode, stdout, stderr, cancelled, errorMsg) = await RunProcessAsync(psi, cancellationToken).ConfigureAwait(false);
        if (cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (errorMsg is not null)
        {
            return new YtDlpFeedResult(false, [], errorMsg, false);
        }

        var items = new List<YouTubeSearchItem>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            foreach (var rawLine in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryParseFeedJsonLine(rawLine, out var item) && item is not null && seenIds.Add(item.VideoId))
                {
                    items.Add(item);
                }
            }
        }

        if (items.Count == 0 && exitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
        {
            return new YtDlpFeedResult(false, items, stderr, false);
        }

        return new YtDlpFeedResult(true, items, null, false);
    }

    private static bool TryParseFeedJsonLine(string jsonLine, out YouTubeSearchItem? item)
    {
        item = null;
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;

            var id = GetStringProperty(root, "id");
            var title = GetStringProperty(root, "title");
            if (string.IsNullOrWhiteSpace(id) ||
                string.IsNullOrWhiteSpace(title) ||
                string.Equals(title, "[Private video]", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(title, "[Deleted video]", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var channel = GetStringProperty(root, "channel")
                ?? GetStringProperty(root, "uploader")
                ?? GetStringProperty(root, "playlist_uploader")
                ?? "YouTube";

            TimeSpan? duration = null;
            if (root.TryGetProperty("duration", out var durProp) && durProp.ValueKind == JsonValueKind.Number)
            {
                var secs = durProp.GetDouble();
                if (secs > 0 && !double.IsNaN(secs) && !double.IsInfinity(secs))
                {
                    duration = TimeSpan.FromSeconds(secs);
                }
            }

            var url = GetStringProperty(root, "webpage_url") ?? GetStringProperty(root, "url");
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                url = $"https://www.youtube.com/watch?v={id}";
            }

            var thumbUrl = GetStringProperty(root, "thumbnail");
            if (string.IsNullOrWhiteSpace(thumbUrl) &&
                root.TryGetProperty("thumbnails", out var thumbs) &&
                thumbs.ValueKind == JsonValueKind.Array)
            {
                foreach (var thumb in thumbs.EnumerateArray())
                {
                    var candidate = GetStringProperty(thumb, "url");
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        thumbUrl = candidate;
                        if (thumb.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number && w.GetInt32() >= 300)
                        {
                            break;
                        }
                    }
                }
            }

            item = new YouTubeSearchItem(id, title, channel, duration, url, thumbUrl);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var value = prop.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        return null;
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

    private static async Task<(int ExitCode, string Stdout, string Stderr, bool Cancelled, string? ErrorMessage)> RunProcessAsync(
        ProcessStartInfo psi,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                return (-1, string.Empty, string.Empty, false, "Could not start yt-dlp.exe.");
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

            return (process.ExitCode, stdout, stderr, false, null);
        }
        catch (OperationCanceledException)
        {
            return (-1, string.Empty, string.Empty, true, null);
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, string.Empty, false, ex.Message);
        }
    }
}
