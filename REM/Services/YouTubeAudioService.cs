using Avalonia.Media.Imaging;
using REM.Models;
using YoutubeExplode;
using YoutubeExplode.Search;
using YoutubeExplode.Videos.Streams;

namespace REM.Services;

public sealed class YouTubeSearchPager : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly YtDlpService _ytDlp;
    private readonly string _query;
    private readonly IAsyncEnumerator<VideoSearchResult> _enumerator;
    private readonly HashSet<string> _seenVideoIds = new(StringComparer.Ordinal);
    private bool _explodeDone;
    private int _ytDlpNextStart;
    private bool _exhausted;
    private bool _disposed;

    internal YouTubeSearchPager(YoutubeClient client, YtDlpService ytDlp, string query)
    {
        _ytDlp = ytDlp;
        _query = query;
        _enumerator = client.Search.GetVideosAsync(query, _cts.Token).GetAsyncEnumerator(_cts.Token);
    }

    public async Task<(List<YouTubeSearchItem> Items, bool HasMore)> FetchNextAsync(
        int count,
        CancellationToken cancellationToken)
    {
        var targetCount = Math.Max(1, count);
        var items = new List<YouTubeSearchItem>(targetCount);
        if (_disposed || _exhausted)
        {
            return (items, false);
        }

        using var reg = cancellationToken.Register(() =>
        {
            try { _cts.Cancel(); } catch { }
        });

        // Phase 1: Pull fast results from YoutubeExplode until its stream finishes.
        while (!_explodeDone && items.Count < targetCount)
        {
            bool moved;
            try
            {
                moved = await _enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _disposed)
            {
                throw;
            }
            catch
            {
                moved = false;
            }

            if (!moved)
            {
                _explodeDone = true;
                break;
            }

            var video = _enumerator.Current;
            var videoId = video.Id.Value;
            if (!_seenVideoIds.Add(videoId))
            {
                continue;
            }

            items.Add(new YouTubeSearchItem(
                videoId,
                video.Title,
                video.Author.ChannelTitle,
                video.Duration,
                video.Url,
                $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg"));
        }

        // Phase 2: Seamlessly continue past YoutubeExplode's limit using yt-dlp ytsearchall.
        if (_explodeDone && items.Count < targetCount && !_exhausted && !cancellationToken.IsCancellationRequested)
        {
            if (_ytDlpNextStart <= 0)
            {
                _ytDlpNextStart = Math.Max(1, _seenVideoIds.Count - 4);
            }

            var batchSize = Math.Max(24, targetCount * 2);
            for (var attempt = 0; attempt < 3 && items.Count < targetCount && !_exhausted; attempt++)
            {
                var res = await _ytDlp.SearchYouTubePagedAsync(_query, _ytDlpNextStart, batchSize, cancellationToken)
                    .ConfigureAwait(false);

                if (!res.Success)
                {
                    // Transient error: keep HasMore true so user can retry Show More.
                    break;
                }

                if (res.Items.Count == 0)
                {
                    _exhausted = true;
                    break;
                }

                _ytDlpNextStart += batchSize;

                foreach (var entry in res.Items)
                {
                    if (_seenVideoIds.Add(entry.VideoId))
                    {
                        items.Add(entry);
                        if (items.Count >= targetCount)
                        {
                            break;
                        }
                    }
                }
            }
        }

        return (items, !_exhausted);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { await _enumerator.DisposeAsync().ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}

public sealed class YouTubeAudioService : IDisposable
{
    private readonly YtDlpService _ytDlp;
    private readonly ActionLogService _actionLog;
    private readonly HttpClient _http;
    private YoutubeClient? _client;
    private DateTimeOffset _youtubeExplodeCooldownUntil = DateTimeOffset.MinValue;
    private bool _disposed;

    public YouTubeAudioService(YtDlpService ytDlp, ActionLogService actionLog)
    {
        _ytDlp = ytDlp;
        _actionLog = actionLog;
        _http = new HttpClient(new BandwidthCountingHandler())
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
    }

    private YoutubeClient Client => _client ??= new YoutubeClient(_http);

    public void ReleaseIdleClient() => _client = null;

    public static bool IsYouTubeUrl(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        return host.Contains("youtube.com", StringComparison.Ordinal) ||
               host == "youtu.be" ||
               host.EndsWith(".youtu.be", StringComparison.Ordinal);
    }

    public static bool IsTwitchUrl(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        return host == "twitch.tv" || host.EndsWith(".twitch.tv", StringComparison.Ordinal);
    }

    public Task<List<YouTubeSearchItem>> SearchTop5Async(string query, CancellationToken cancellationToken) =>
        SearchVideosAsync(query, 5, cancellationToken);

    public YouTubeSearchPager CreateSearchPager(string query) => new(Client, _ytDlp, query);

    public async Task<List<YouTubeSearchItem>> SearchVideosAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var max = Math.Max(1, limit);
        var results = new List<YouTubeSearchItem>(Math.Min(max, 50));
        await foreach (var video in Client.Search.GetVideosAsync(query, cancellationToken).ConfigureAwait(false))
        {
            var videoId = video.Id.Value;
            results.Add(new YouTubeSearchItem(
                videoId,
                video.Title,
                video.Author.ChannelTitle,
                video.Duration,
                video.Url,
                $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg"));

            if (results.Count >= max)
            {
                break;
            }
        }

        return results;
    }

    public async Task<ExtractedAudioTrack> ExtractAudioOnlyAsync(
        string url,
        bool allowYtDlpFallback,
        CancellationToken cancellationToken,
        string? cookieBrowserArg = null,
        string? knownTitle = null,
        string? knownChannel = null,
        TimeSpan? knownDuration = null,
        string qualityPreset = "max")
    {
        var hasKnownMeta = !string.IsNullOrWhiteSpace(knownTitle);
        var title = knownTitle ?? string.Empty;
        var channel = knownChannel ?? "YouTube";
        var duration = knownDuration;
        var isLive = hasKnownMeta && duration is null;

        var skipYoutubeExplode = allowYtDlpFallback && DateTimeOffset.UtcNow < _youtubeExplodeCooldownUntil;
        if (!skipYoutubeExplode)
        {
            var videoId = YoutubeExplode.Videos.VideoId.TryParse(url);
            if (videoId is null || !hasKnownMeta)
            {
                using var metaCts = allowYtDlpFallback
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                    : null;
                metaCts?.CancelAfter(TimeSpan.FromMilliseconds(3500));
                var metaToken = metaCts?.Token ?? cancellationToken;

                try
                {
                    var video = await Client.Videos.GetAsync(url, metaToken).ConfigureAwait(false);
                    videoId = video.Id;
                    title = video.Title;
                    channel = video.Author.ChannelTitle;
                    duration = video.Duration;
                    isLive = video.Duration is null;
                    hasKnownMeta = true;
                }
                catch (Exception ex) when (allowYtDlpFallback && !cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
                {
                    _youtubeExplodeCooldownUntil = DateTimeOffset.UtcNow.AddMinutes(10);
                    _actionLog.Log("Warning", $"YoutubeExplode metadata failed ({ex.Message}); switching to fast yt-dlp path.");
                }
                catch (OperationCanceledException) when (allowYtDlpFallback && !cancellationToken.IsCancellationRequested)
                {
                    _youtubeExplodeCooldownUntil = DateTimeOffset.UtcNow.AddMinutes(10);
                    _actionLog.Log("Warning", "YoutubeExplode metadata timed out; switching to fast yt-dlp path.");
                }
            }

            if (videoId is { } vid && DateTimeOffset.UtcNow >= _youtubeExplodeCooldownUntil)
            {
                using var manifestCts = allowYtDlpFallback
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                    : null;
                manifestCts?.CancelAfter(TimeSpan.FromMilliseconds(3500));
                var manifestToken = manifestCts?.Token ?? cancellationToken;

                try
                {
                    var manifest = await Client.Videos.Streams.GetManifestAsync(vid, manifestToken).ConfigureAwait(false);
                    var audioStream = SelectAudioStream(manifest.GetAudioOnlyStreams(), qualityPreset);
                    if (audioStream is not null)
                    {
                        var kbps = (int)Math.Round(audioStream.Bitrate.KiloBitsPerSecond);
                        var codec = FormatExplodeCodec(audioStream.AudioCodec);
                        var label = kbps > 0 ? $"YouTube · {codec} {kbps} kbps" : $"YouTube · {codec}";
                        return new ExtractedAudioTrack(
                            string.IsNullOrWhiteSpace(title) ? "YouTube Audio" : title,
                            string.IsNullOrWhiteSpace(channel) ? "YouTube" : channel,
                            audioStream.Url,
                            isLive,
                            duration,
                            label);
                    }

                    if (!allowYtDlpFallback)
                    {
                        throw new InvalidOperationException("No audio-only stream was found for this YouTube video.");
                    }
                }
                catch (Exception ex) when (allowYtDlpFallback && !cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
                {
                    _youtubeExplodeCooldownUntil = DateTimeOffset.UtcNow.AddMinutes(10);
                    _actionLog.Log("Warning", $"YoutubeExplode failed ({ex.Message}); falling back to yt-dlp for '{(string.IsNullOrWhiteSpace(title) ? url : title)}'.");
                }
                catch (OperationCanceledException) when (allowYtDlpFallback && !cancellationToken.IsCancellationRequested)
                {
                    _youtubeExplodeCooldownUntil = DateTimeOffset.UtcNow.AddMinutes(10);
                    _actionLog.Log("Warning", $"YoutubeExplode timed out; falling back to yt-dlp for '{(string.IsNullOrWhiteSpace(title) ? url : title)}'.");
                }
            }
        }

        var fallback = await _ytDlp.ExtractAudioStreamUrlAsync(
            url,
            isTwitch: false,
            cancellationToken,
            cookieBrowserArg,
            qualityPreset,
            includeMetadata: !hasKnownMeta).ConfigureAwait(false);

        if (!fallback.Success || string.IsNullOrWhiteSpace(fallback.StreamUrl))
        {
            throw new InvalidOperationException(fallback.ErrorMessage ?? "YouTube audio extraction failed.");
        }

        var finalTitle = !string.IsNullOrWhiteSpace(title) ? title : (fallback.ResolvedTitle ?? "YouTube Audio");
        var finalChannel = !string.IsNullOrWhiteSpace(knownChannel) ? knownChannel : (fallback.ResolvedChannel ?? channel);
        var finalDuration = duration ?? fallback.ResolvedDuration;
        var finalIsLive = hasKnownMeta ? isLive : finalDuration is null;
        var sourceLabel = string.IsNullOrWhiteSpace(fallback.QualityBadge)
            ? "YouTube"
            : $"YouTube · {fallback.QualityBadge}";

        return new ExtractedAudioTrack(
            finalTitle,
            finalChannel,
            fallback.StreamUrl,
            finalIsLive,
            finalDuration,
            sourceLabel);
    }

    private static IAudioStreamInfo? SelectAudioStream(IEnumerable<AudioOnlyStreamInfo> streams, string qualityPreset)
    {
        var list = streams.ToList();
        if (list.Count == 0)
        {
            return null;
        }

        var capKbps = qualityPreset.Trim().ToLowerInvariant() switch
        {
            "160" => 165.0,
            "128" => 132.0,
            "64" => 68.0,
            _ => double.MaxValue
        };

        var matching = list.Where(s => s.Bitrate.KiloBitsPerSecond <= capKbps).ToList();
        var pool = matching.Count > 0 ? matching : list;

        return pool
            .OrderByDescending(s => s.Bitrate.BitsPerSecond)
            .ThenByDescending(s => s.AudioCodec.Contains("opus", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    private static string FormatExplodeCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
        {
            return "Audio";
        }

        if (codec.Contains("opus", StringComparison.OrdinalIgnoreCase)) return "Opus";
        if (codec.Contains("mp4a", StringComparison.OrdinalIgnoreCase) || codec.Contains("aac", StringComparison.OrdinalIgnoreCase)) return "AAC";
        return codec.ToUpperInvariant();
    }

    public async Task<Bitmap?> LoadSmallThumbnailAsync(string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url) || _disposed)
        {
            return null;
        }

        try
        {
            var bytes = await _http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
            using var ms = new MemoryStream(bytes, writable: false);
            return Bitmap.DecodeToWidth(ms, 160);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
    }
}
