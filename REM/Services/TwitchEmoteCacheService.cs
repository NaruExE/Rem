using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia.Media.Imaging;
using REM.Models;

namespace REM.Services;

/// <summary>
/// Bounded, RAM-conscious cache for official Twitch and 7TV emotes.
/// Every bitmap is decoded at <= 24px height and disposed explicitly on toggle-off or Kill.
/// </summary>
public sealed class TwitchEmoteCacheService : IDisposable
{
    private const int MaxCachedBitmapsPerProvider = 220;

    private readonly ActionLogService _actionLog;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _downloadGate = new(4, 4);
    private readonly SemaphoreSlim _sevenTvLoadGate = new(1, 1);

    private readonly ConcurrentDictionary<string, SevenTvEmote> _channelSevenTv = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SevenTvEmote> _globalSevenTv = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Bitmap> _twitchBitmaps = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Bitmap> _sevenTvBitmaps = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _inFlight = new(StringComparer.Ordinal);

    private bool _globalSevenTvLoaded;
    private string? _loadedChannelUserId;
    private bool _disposed;

    public TwitchEmoteCacheService(ActionLogService actionLog)
    {
        _actionLog = actionLog;
        _http = new HttpClient(new BandwidthCountingHandler())
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public int ChannelEmoteCount => _channelSevenTv.Count;
    public int GlobalEmoteCount => _globalSevenTv.Count;

    public static string BuildTwitchEmoteUrl(string emoteId) =>
        $"https://static-cdn.jtvnw.net/emoticons/v2/{Uri.EscapeDataString(emoteId)}/static/dark/1.0";

    public async Task LoadSevenTvEmotesAsync(string? broadcasterUserId, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        await _sevenTvLoadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            if (!_globalSevenTvLoaded)
            {
                await FetchSevenTvSetAsync(
                    "https://7tv.io/v3/emote-sets/global",
                    isUserEndpoint: false,
                    _globalSevenTv,
                    cancellationToken).ConfigureAwait(false);
                _globalSevenTvLoaded = _globalSevenTv.Count > 0;
            }

            var cleanId = string.IsNullOrWhiteSpace(broadcasterUserId) ? null : broadcasterUserId.Trim();
            if (!string.Equals(_loadedChannelUserId, cleanId, StringComparison.Ordinal))
            {
                _channelSevenTv.Clear();
                _loadedChannelUserId = cleanId;

                if (!string.IsNullOrWhiteSpace(cleanId))
                {
                    await FetchSevenTvSetAsync(
                        $"https://7tv.io/v3/users/twitch/{Uri.EscapeDataString(cleanId)}",
                        isUserEndpoint: true,
                        _channelSevenTv,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _sevenTvLoadGate.Release();
        }
    }

    public bool TryResolveSevenTvEmote(string token, out SevenTvEmote? emote)
    {
        if (string.IsNullOrEmpty(token))
        {
            emote = null;
            return false;
        }

        if (_channelSevenTv.TryGetValue(token, out var ch))
        {
            emote = ch;
            return true;
        }

        if (_globalSevenTv.TryGetValue(token, out var gl))
        {
            emote = gl;
            return true;
        }

        emote = null;
        return false;
    }

    public Bitmap? TryGetCachedBitmap(string key, bool isSevenTv)
    {
        var map = isSevenTv ? _sevenTvBitmaps : _twitchBitmaps;
        return map.TryGetValue(key, out var bmp) ? bmp : null;
    }

    public Task<Bitmap?> GetOrLoadBitmapAsync(string key, string url, bool isSevenTv, CancellationToken cancellationToken)
    {
        if (_disposed || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(url))
        {
            return Task.FromResult<Bitmap?>(null);
        }

        var map = isSevenTv ? _sevenTvBitmaps : _twitchBitmaps;
        if (map.TryGetValue(key, out var existing))
        {
            return Task.FromResult<Bitmap?>(existing);
        }

        var flightKey = $"{(isSevenTv ? "7" : "t")}:{key}";
        return _inFlight.GetOrAdd(flightKey, _ => LoadBitmapCoreAsync(flightKey, key, url, isSevenTv, cancellationToken));
    }

    /// <summary>
    /// Disposes all cached Twitch emote bitmaps. Caller must clear UI segment references first.
    /// </summary>
    public void ClearTwitchBitmaps()
    {
        foreach (var kvp in _twitchBitmaps)
        {
            if (_twitchBitmaps.TryRemove(kvp.Key, out var bmp))
            {
                bmp.Dispose();
            }
        }
    }

    /// <summary>
    /// Clears all 7TV emote maps and disposes cached 7TV bitmaps. Caller must clear UI segment references first.
    /// </summary>
    public void ClearSevenTvAll()
    {
        _channelSevenTv.Clear();
        _globalSevenTv.Clear();
        _globalSevenTvLoaded = false;
        _loadedChannelUserId = null;

        foreach (var kvp in _sevenTvBitmaps)
        {
            if (_sevenTvBitmaps.TryRemove(kvp.Key, out var bmp))
            {
                bmp.Dispose();
            }
        }
    }

    public void ClearChannelSevenTvOnly()
    {
        _channelSevenTv.Clear();
        _loadedChannelUserId = null;
    }

    public void ClearAll()
    {
        ClearTwitchBitmaps();
        ClearSevenTvAll();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearAll();
        _downloadGate.Dispose();
        _sevenTvLoadGate.Dispose();
        _http.Dispose();
    }

    private async Task<Bitmap?> LoadBitmapCoreAsync(
        string flightKey,
        string key,
        string url,
        bool isSevenTv,
        CancellationToken cancellationToken)
    {
        try
        {
            await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return null;
                }

                var map = isSevenTv ? _sevenTvBitmaps : _twitchBitmaps;
                if (map.TryGetValue(key, out var cached))
                {
                    return cached;
                }

                var bytes = await _http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
                if (_disposed || bytes.Length == 0)
                {
                    return null;
                }

                using var ms = new MemoryStream(bytes, writable: false);
                var bmp = Bitmap.DecodeToHeight(ms, 24);
                if (map.Count >= MaxCachedBitmapsPerProvider)
                {
                    // Cap growth; bitmaps stay cached for current watch session until ClearAll / Kill / toggle-off.
                    return bmp;
                }

                return map.GetOrAdd(key, bmp);
            }
            finally
            {
                _downloadGate.Release();
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            _inFlight.TryRemove(flightKey, out _);
        }
    }

    private async Task FetchSevenTvSetAsync(
        string url,
        bool isUserEndpoint,
        ConcurrentDictionary<string, SevenTvEmote> target,
        CancellationToken cancellationToken)
    {
        try
        {
            using var resp = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return;
            }

            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            JsonElement emotesArray;
            if (isUserEndpoint)
            {
                if (!doc.RootElement.TryGetProperty("emote_set", out var emoteSet) ||
                    emoteSet.ValueKind != JsonValueKind.Object ||
                    !emoteSet.TryGetProperty("emotes", out emotesArray))
                {
                    return;
                }
            }
            else if (!doc.RootElement.TryGetProperty("emotes", out emotesArray))
            {
                return;
            }

            if (emotesArray.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in emotesArray.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var imageUrl = ResolveSevenTvEmoteUrl(item, id);
                target[name] = new SevenTvEmote(id, name, imageUrl);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _actionLog.Log("Warning", $"7TV emote fetch skipped ({url}): {ex.Message}");
        }
    }

    private static string ResolveSevenTvEmoteUrl(JsonElement item, string id)
    {
        if (item.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("host", out var host) &&
            host.ValueKind == JsonValueKind.Object)
        {
            var hostUrl = host.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
            if (!string.IsNullOrWhiteSpace(hostUrl))
            {
                if (hostUrl.StartsWith("//", StringComparison.Ordinal))
                {
                    hostUrl = $"https:{hostUrl}";
                }

                var bestFile = "1x.webp";
                if (host.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                {
                    string? png1x = null;
                    string? webp1x = null;
                    foreach (var file in files.EnumerateArray())
                    {
                        var fileName = file.TryGetProperty("name", out var fnEl) ? fnEl.GetString() : null;
                        if (string.Equals(fileName, "1x.png", StringComparison.OrdinalIgnoreCase))
                        {
                            png1x = fileName;
                        }
                        else if (string.Equals(fileName, "1x.webp", StringComparison.OrdinalIgnoreCase))
                        {
                            webp1x = fileName;
                        }
                    }

                    bestFile = png1x ?? webp1x ?? "1x.webp";
                }

                return $"{hostUrl.TrimEnd('/')}/{bestFile}";
            }
        }

        return $"https://cdn.7tv.app/emote/{id}/1x.webp";
    }
}
