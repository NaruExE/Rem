using System.Collections.Concurrent;
using System.Net.Http;

namespace REM.Services;

/// <summary>
/// Tracks total bytes downloaded by REM (HTTP APIs, thumbnails, yt-dlp output, LibVLC streams, and Soloist).
/// </summary>
public static class BandwidthMonitor
{
    private static long _trackedBytes;
    private static readonly ConcurrentDictionary<string, Func<long>> _byteProviders = new(StringComparer.Ordinal);

    public static void RecordBytes(long bytes)
    {
        if (bytes > 0)
        {
            Interlocked.Add(ref _trackedBytes, bytes);
        }
    }

    public static void RegisterByteProvider(string key, Func<long>? provider)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (provider is null)
        {
            _byteProviders.TryRemove(key, out _);
        }
        else
        {
            _byteProviders[key] = provider;
        }
    }

    public static void RegisterLibVlcBytesProvider(Func<long>? provider) =>
        RegisterByteProvider("libvlc", provider);

    public static long GetTotalDownloadedBytes()
    {
        var total = Interlocked.Read(ref _trackedBytes);
        foreach (var provider in _byteProviders.Values)
        {
            try
            {
                var bytes = provider();
                if (bytes > 0)
                {
                    total += bytes;
                }
            }
            catch
            {
                // Ignore provider errors during teardown.
            }
        }

        return total;
    }

    public static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond < 1 || double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond))
        {
            return "0 KB/s";
        }

        if (bytesPerSecond < 1024)
        {
            return $"{bytesPerSecond:F0} B/s";
        }

        if (bytesPerSecond < 1024 * 1024)
        {
            return $"{bytesPerSecond / 1024.0:F1} KB/s";
        }

        return $"{bytesPerSecond / (1024.0 * 1024.0):F2} MB/s";
    }

    public static string FormatCompactRate(double bytesPerSecond)
    {
        if (bytesPerSecond < 1 || double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond))
        {
            return "0K";
        }

        if (bytesPerSecond < 1024)
        {
            return $"{bytesPerSecond:F0}B";
        }

        if (bytesPerSecond < 1024 * 1024)
        {
            return $"{bytesPerSecond / 1024.0:F0}K";
        }

        return $"{bytesPerSecond / (1024.0 * 1024.0):F1}M";
    }

    public static string FormatTotal(long totalBytes)
    {
        if (totalBytes <= 0)
        {
            return "Session: 0 B";
        }

        if (totalBytes < 1024)
        {
            return $"Session: {totalBytes} B";
        }

        if (totalBytes < 1024 * 1024)
        {
            return $"Session: {totalBytes / 1024.0:F1} KB";
        }

        if (totalBytes < 1024L * 1024 * 1024)
        {
            return $"Session: {totalBytes / (1024.0 * 1024.0):F2} MB";
        }

        return $"Session: {totalBytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}

public sealed class BandwidthCountingHandler : DelegatingHandler
{
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(45),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        MaxConnectionsPerServer = 6,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    };

    public BandwidthCountingHandler() : base(SharedHandler)
    {
    }

    protected override void Dispose(bool disposing)
    {
        // Do not dispose SharedHandler when individual HttpClients are disposed.
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var contentLength = response.Content?.Headers.ContentLength;
        if (contentLength is > 0)
        {
            BandwidthMonitor.RecordBytes(contentLength.Value);
        }
        else if (response.Content is not null &&
                 request.Method != HttpMethod.Head)
        {
            try
            {
                await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                var len = response.Content.Headers.ContentLength;
                if (len is > 0)
                {
                    BandwidthMonitor.RecordBytes(len.Value);
                }
            }
            catch
            {
                // Ignore buffering errors on aborted/streaming responses.
            }
        }

        return response;
    }
}

