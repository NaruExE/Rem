using System.Collections.Concurrent;
using System.Diagnostics;
using REM.Models;

namespace REM.Services;

/// <summary>
/// Coordinates URL routing (yt-dlp vs aria2c), concurrency limits, SQLite download history,
/// and aria2 active polling strictly while jobs are running.
/// </summary>
public sealed class DownloadQueueService : IDisposable
{
    private static readonly HashSet<string> YtDlpHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com", "youtu.be", "music.youtube.com", "twitch.tv", "clips.twitch.tv",
        "twitter.com", "x.com", "tiktok.com", "instagram.com", "soundcloud.com",
        "reddit.com", "v.redd.it", "vimeo.com", "dailymotion.com", "bilibili.com",
        "kick.com", "rumble.com", "bandcamp.com", "facebook.com", "fb.watch"
    };

    private static readonly HashSet<string> DirectFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".xz", ".bz2", ".exe", ".msi", ".iso", ".img",
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v", ".mp3", ".flac", ".wav", ".m4a",
        ".pdf", ".epub", ".apk", ".dmg", ".deb", ".bin", ".torrent"
    };

    private readonly DatabaseService _database;
    private readonly YtDlpDownloadService _ytDlpDownload;
    private readonly Aria2RpcService _aria2;
    private readonly ActionLogService _actionLog;

    private readonly ConcurrentDictionary<long, DownloadJobState> _jobsById = new();
    private readonly ConcurrentQueue<long> _ytDlpPendingQueue = new();
    private readonly SemaphoreSlim _schedulerGate = new(1, 1);
    private readonly ConcurrentDictionary<string, long> _aria2LastBytesByGid = new(StringComparer.Ordinal);

    private long _activeProfileId;
    private int _runningYtDlpCount;
    private CancellationTokenSource? _aria2PollCts;
    private bool _disposed;

    public DownloadQueueService(
        DatabaseService database,
        YtDlpDownloadService ytDlpDownload,
        Aria2RpcService aria2,
        ActionLogService actionLog)
    {
        _database = database;
        _ytDlpDownload = ytDlpDownload;
        _aria2 = aria2;
        _actionLog = actionLog;
    }

    public long ActiveProfileId => _activeProfileId;
    public int ActiveCount => _jobsById.Values.Count(j => j.Status is DownloadJobStatus.Queued or DownloadJobStatus.Probing or DownloadJobStatus.Downloading);
    public long TotalSpeedBytesPerSec => _jobsById.Values
        .Where(j => j.Status == DownloadJobStatus.Downloading)
        .Sum(j => j.SpeedBytesPerSec);

    public event EventHandler? JobsChanged;
    public event EventHandler<DownloadJobState>? JobUpdated;

    public static DownloadEngineKind DetectEngine(
        string url,
        DownloadEngineKind overrideEngine,
        string formatPreset)
    {
        if (overrideEngine != DownloadEngineKind.Auto)
        {
            return overrideEngine;
        }

        var trimmed = url.Trim();
        if (trimmed.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            return DownloadEngineKind.Aria2c;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (string.Equals(uri.Scheme, "ftp", StringComparison.OrdinalIgnoreCase))
            {
                return DownloadEngineKind.Aria2c;
            }

            var host = uri.Host.ToLowerInvariant();
            if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
            if (YtDlpHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal)))
            {
                return DownloadEngineKind.YtDlp;
            }

            var ext = Path.GetExtension(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(ext) && DirectFileExtensions.Contains(ext))
            {
                return DownloadEngineKind.Aria2c;
            }
        }

        if (string.Equals(formatPreset, DownloadFormatPresets.RawFile, StringComparison.OrdinalIgnoreCase))
        {
            return DownloadEngineKind.Aria2c;
        }

        return DownloadEngineKind.YtDlp;
    }

    public async Task LoadProfileAsync(long profileId, CancellationToken cancellationToken = default)
    {
        if (_disposed || profileId <= 0) return;

        await ShutdownAllAsync().ConfigureAwait(false);
        _activeProfileId = profileId;
        _jobsById.Clear();

        var history = await _database.GetDownloadHistoryAsync(profileId, cancellationToken).ConfigureAwait(false);
        foreach (var item in history)
        {
            var status = Enum.TryParse<DownloadJobStatus>(item.Status, true, out var parsed)
                ? parsed
                : DownloadJobStatus.Failed;

            // Any stale "Downloading"/"Probing"/"Queued" rows from an earlier crashed session show as Cancelled/Retryable.
            if (status is DownloadJobStatus.Downloading or DownloadJobStatus.Probing or DownloadJobStatus.Queued)
            {
                status = DownloadJobStatus.Cancelled;
                await _database.UpdateDownloadHistoryAsync(item.Id, null, null, null, status.ToString(), null, cancellationToken)
                    .ConfigureAwait(false);
            }

            var engine = string.Equals(item.Engine, "aria2c", StringComparison.OrdinalIgnoreCase)
                ? DownloadEngineKind.Aria2c
                : DownloadEngineKind.YtDlp;

            _jobsById[item.Id] = new DownloadJobState
            {
                HistoryId = item.Id,
                ProfileId = item.ProfileId,
                Url = item.Url,
                Title = item.Title,
                Engine = engine,
                FormatPreset = item.FormatPreset,
                FilePath = item.FilePath,
                FileSizeBytes = item.FileSizeBytes,
                ProgressPercent = status == DownloadJobStatus.Completed ? 100 : 0,
                Status = status,
                CreatedAt = item.CreatedAt,
                CompletedAt = item.CompletedAt
            };
        }

        JobsChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<DownloadJobState> GetAllJobsSnapshot() =>
        _jobsById.Values.OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.HistoryId).ToList();

    public async Task<DownloadJobState> EnqueueAsync(
        string url,
        string? titleOverride,
        DownloadEngineKind engineOverride,
        string formatPreset,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var cleanedUrl = url.Trim();
        if (string.IsNullOrWhiteSpace(cleanedUrl))
        {
            throw new ArgumentException("Download URL cannot be empty.", nameof(url));
        }

        if (_activeProfileId <= 0)
        {
            throw new InvalidOperationException("No active profile is loaded.");
        }

        var engine = DetectEngine(cleanedUrl, engineOverride, formatPreset);
        var initialTitle = !string.IsNullOrWhiteSpace(titleOverride)
            ? titleOverride.Trim()
            : DeriveFallbackTitle(cleanedUrl);

        var historyId = await _database.InsertDownloadHistoryAsync(
            _activeProfileId,
            cleanedUrl,
            initialTitle,
            engine == DownloadEngineKind.Aria2c ? "aria2c" : "yt-dlp",
            formatPreset,
            nameof(DownloadJobStatus.Queued),
            cancellationToken).ConfigureAwait(false);

        var job = new DownloadJobState
        {
            HistoryId = historyId,
            ProfileId = _activeProfileId,
            Url = cleanedUrl,
            Title = initialTitle,
            Engine = engine,
            FormatPreset = formatPreset,
            Status = DownloadJobStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            JobCts = new CancellationTokenSource()
        };

        _jobsById[historyId] = job;
        JobsChanged?.Invoke(this, EventArgs.Empty);

        if (engine == DownloadEngineKind.Aria2c)
        {
            _ = StartAria2JobAsync(job);
        }
        else
        {
            _ytDlpPendingQueue.Enqueue(job.HistoryId);
            await PumpYtDlpQueueAsync().ConfigureAwait(false);
        }

        return job;
    }

    public async Task PauseJobAsync(long historyId)
    {
        if (!_jobsById.TryGetValue(historyId, out var job) || job.Engine != DownloadEngineKind.Aria2c)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(job.Aria2Gid))
        {
            try
            {
                await _aria2.PauseAsync(job.Aria2Gid, CancellationToken.None).ConfigureAwait(false);
                job.Status = DownloadJobStatus.Paused;
                job.SpeedText = string.Empty;
                job.SpeedBytesPerSec = 0;
                await _database.UpdateDownloadHistoryAsync(job.HistoryId, null, null, null, nameof(DownloadJobStatus.Paused), null)
                    .ConfigureAwait(false);
                JobUpdated?.Invoke(this, job);
            }
            catch (Exception ex)
            {
                _actionLog.Log("Warning", $"aria2 pause failed: {ex.Message}");
            }
        }
    }

    public async Task ResumeJobAsync(long historyId)
    {
        if (!_jobsById.TryGetValue(historyId, out var job) || job.Engine != DownloadEngineKind.Aria2c)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(job.Aria2Gid))
        {
            try
            {
                await _aria2.UnpauseAsync(job.Aria2Gid, CancellationToken.None).ConfigureAwait(false);
                job.Status = DownloadJobStatus.Downloading;
                await _database.UpdateDownloadHistoryAsync(job.HistoryId, null, null, null, nameof(DownloadJobStatus.Downloading), null)
                    .ConfigureAwait(false);
                EnsureAria2PollingRunning();
                JobUpdated?.Invoke(this, job);
            }
            catch (Exception ex)
            {
                _actionLog.Log("Warning", $"aria2 resume failed: {ex.Message}");
            }
        }
    }

    public async Task CancelJobAsync(long historyId)
    {
        if (!_jobsById.TryGetValue(historyId, out var job))
        {
            return;
        }

        if (job.Engine == DownloadEngineKind.Aria2c && !string.IsNullOrWhiteSpace(job.Aria2Gid))
        {
            await _aria2.RemoveAsync(job.Aria2Gid, CancellationToken.None).ConfigureAwait(false);
        }

        try { job.JobCts?.Cancel(); } catch { }
        if (job.ActiveProcess is { } proc)
        {
            YtDlpDownloadService.TryKillProcessTree(proc);
        }

        job.Status = DownloadJobStatus.Cancelled;
        job.SpeedText = string.Empty;
        job.SpeedBytesPerSec = 0;
        job.EtaText = string.Empty;

        await _database.UpdateDownloadHistoryAsync(job.HistoryId, null, null, null, nameof(DownloadJobStatus.Cancelled), null)
            .ConfigureAwait(false);
        JobUpdated?.Invoke(this, job);
        JobsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RetryJobAsync(long historyId)
    {
        if (!_jobsById.TryGetValue(historyId, out var job))
        {
            return;
        }

        job.ErrorMessage = null;
        job.ProgressPercent = 0;
        job.SpeedText = string.Empty;
        job.SpeedBytesPerSec = 0;
        job.EtaText = string.Empty;
        job.Aria2Gid = null;
        job.JobCts?.Dispose();
        job.JobCts = new CancellationTokenSource();
        job.Status = DownloadJobStatus.Queued;

        await _database.UpdateDownloadHistoryAsync(job.HistoryId, null, null, null, nameof(DownloadJobStatus.Queued), null)
            .ConfigureAwait(false);
        JobsChanged?.Invoke(this, EventArgs.Empty);

        if (job.Engine == DownloadEngineKind.Aria2c)
        {
            _ = StartAria2JobAsync(job);
        }
        else
        {
            _ytDlpPendingQueue.Enqueue(job.HistoryId);
            await PumpYtDlpQueueAsync().ConfigureAwait(false);
        }
    }

    public async Task ClearCompletedAsync()
    {
        if (_activeProfileId <= 0) return;

        await _database.ClearCompletedDownloadHistoryAsync(_activeProfileId).ConfigureAwait(false);
        foreach (var kvp in _jobsById.Where(x => x.Value.Status == DownloadJobStatus.Completed).ToList())
        {
            _jobsById.TryRemove(kvp.Key, out _);
        }

        JobsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task PauseAllAria2Async()
    {
        foreach (var job in _jobsById.Values.Where(j => j.Engine == DownloadEngineKind.Aria2c && j.Status == DownloadJobStatus.Downloading))
        {
            await PauseJobAsync(job.HistoryId).ConfigureAwait(false);
        }
    }

    public async Task ShutdownAllAsync()
    {
        StopAria2Polling();

        foreach (var job in _jobsById.Values)
        {
            if (job.Status is DownloadJobStatus.Queued or DownloadJobStatus.Probing or DownloadJobStatus.Downloading or DownloadJobStatus.Paused)
            {
                try { job.JobCts?.Cancel(); } catch { }
                if (job.ActiveProcess is { } proc)
                {
                    YtDlpDownloadService.TryKillProcessTree(proc);
                }

                job.Status = DownloadJobStatus.Cancelled;
                job.SpeedBytesPerSec = 0;
                job.SpeedText = string.Empty;
                try
                {
                    await _database.UpdateDownloadHistoryAsync(job.HistoryId, null, null, null, nameof(DownloadJobStatus.Cancelled), null)
                        .ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        while (_ytDlpPendingQueue.TryDequeue(out _)) { }
        await _aria2.ShutdownAsync().ConfigureAwait(false);
        JobsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { ShutdownAllAsync().GetAwaiter().GetResult(); } catch { }
        _schedulerGate.Dispose();
    }

    private async Task PumpYtDlpQueueAsync()
    {
        if (_disposed || _activeProfileId <= 0) return;

        var settings = await _database.GetMediaGrabberSettingsAsync(_activeProfileId).ConfigureAwait(false);
        var maxConcurrent = Math.Clamp(settings.MaxConcurrentDownloads, 1, 8);

        await _schedulerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            while (Volatile.Read(ref _runningYtDlpCount) < maxConcurrent &&
                   _ytDlpPendingQueue.TryDequeue(out var nextId))
            {
                if (!_jobsById.TryGetValue(nextId, out var job) || job.Status != DownloadJobStatus.Queued)
                {
                    continue;
                }

                Interlocked.Increment(ref _runningYtDlpCount);
                _ = Task.Run(() => RunYtDlpJobAsync(job, settings));
            }
        }
        finally
        {
            _schedulerGate.Release();
        }
    }

    private async Task RunYtDlpJobAsync(DownloadJobState job, MediaGrabberSettings settings)
    {
        var ct = job.JobCts?.Token ?? CancellationToken.None;
        try
        {
            job.Status = DownloadJobStatus.Probing;
            JobUpdated?.Invoke(this, job);

            var probe = await _ytDlpDownload.ProbeAsync(job.Url, settings, ct).ConfigureAwait(false);
            if (probe is not null && !string.IsNullOrWhiteSpace(probe.Title) &&
                (string.IsNullOrWhiteSpace(job.Title) || job.Title == DeriveFallbackTitle(job.Url)))
            {
                job.Title = probe.Title;
            }

            job.Status = DownloadJobStatus.Downloading;
            await _database.UpdateDownloadHistoryAsync(job.HistoryId, job.Title, null, null, nameof(DownloadJobStatus.Downloading), null, ct)
                .ConfigureAwait(false);
            JobUpdated?.Invoke(this, job);

            var (success, finalPath, finalSize, error) = await _ytDlpDownload.DownloadAsync(
                job,
                settings,
                progress =>
                {
                    job.ProgressPercent = progress.ProgressPercent;
                    job.SpeedText = progress.SpeedText;
                    job.SpeedBytesPerSec = progress.SpeedBytesPerSec;
                    job.EtaText = progress.EtaText;
                    if (progress.FileSizeBytes > 0) job.FileSizeBytes = progress.FileSizeBytes;
                    JobUpdated?.Invoke(this, job);
                },
                proc => job.ActiveProcess = proc,
                ct).ConfigureAwait(false);

            job.ActiveProcess = null;
            if (success)
            {
                job.Status = DownloadJobStatus.Completed;
                job.ProgressPercent = 100;
                job.FilePath = finalPath;
                if (finalSize > 0) job.FileSizeBytes = finalSize;
                job.SpeedText = string.Empty;
                job.SpeedBytesPerSec = 0;
                job.EtaText = string.Empty;
                job.CompletedAt = DateTime.UtcNow;

                await _database.UpdateDownloadHistoryAsync(
                    job.HistoryId,
                    job.Title,
                    job.FilePath,
                    job.FileSizeBytes,
                    nameof(DownloadJobStatus.Completed),
                    job.CompletedAt).ConfigureAwait(false);
            }
            else
            {
                job.Status = DownloadJobStatus.Failed;
                job.ErrorMessage = error;
                job.SpeedText = string.Empty;
                job.SpeedBytesPerSec = 0;
                await _database.UpdateDownloadHistoryAsync(
                    job.HistoryId,
                    job.Title,
                    null,
                    null,
                    nameof(DownloadJobStatus.Failed),
                    null).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            job.ActiveProcess = null;
            job.Status = DownloadJobStatus.Cancelled;
            job.SpeedText = string.Empty;
            job.SpeedBytesPerSec = 0;
            await _database.UpdateDownloadHistoryAsync(job.HistoryId, null, null, null, nameof(DownloadJobStatus.Cancelled), null)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            job.ActiveProcess = null;
            job.Status = DownloadJobStatus.Failed;
            job.ErrorMessage = ex.Message;
            job.SpeedText = string.Empty;
            job.SpeedBytesPerSec = 0;
            await _database.UpdateDownloadHistoryAsync(job.HistoryId, null, null, null, nameof(DownloadJobStatus.Failed), null)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _runningYtDlpCount);
            JobUpdated?.Invoke(this, job);
            JobsChanged?.Invoke(this, EventArgs.Empty);
            await PumpYtDlpQueueAsync().ConfigureAwait(false);
        }
    }

    private async Task StartAria2JobAsync(DownloadJobState job)
    {
        var ct = job.JobCts?.Token ?? CancellationToken.None;
        try
        {
            var settings = await _database.GetMediaGrabberSettingsAsync(job.ProfileId, ct).ConfigureAwait(false);
            var gid = await _aria2.AddUriAsync(job.Url, settings, ct).ConfigureAwait(false);
            job.Aria2Gid = gid;
            job.Status = DownloadJobStatus.Downloading;
            await _database.UpdateDownloadHistoryAsync(job.HistoryId, null, null, null, nameof(DownloadJobStatus.Downloading), null, ct)
                .ConfigureAwait(false);

            EnsureAria2PollingRunning();
            JobUpdated?.Invoke(this, job);
            JobsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            job.Status = DownloadJobStatus.Cancelled;
            JobUpdated?.Invoke(this, job);
        }
        catch (Exception ex)
        {
            job.Status = DownloadJobStatus.Failed;
            job.ErrorMessage = ex.Message;
            await _database.UpdateDownloadHistoryAsync(job.HistoryId, null, null, null, nameof(DownloadJobStatus.Failed), null)
                .ConfigureAwait(false);
            JobUpdated?.Invoke(this, job);
            JobsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void EnsureAria2PollingRunning()
    {
        if (_disposed || _aria2PollCts is not null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _aria2PollCts, cts, null) is not null)
        {
            cts.Dispose();
            return;
        }

        _ = Task.Run(() => PollAria2LoopAsync(cts.Token));
    }

    private void StopAria2Polling()
    {
        var cts = Interlocked.Exchange(ref _aria2PollCts, null);
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
    }

    private async Task PollAria2LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var hasAriaJobs = _jobsById.Values.Any(j =>
                    j.Engine == DownloadEngineKind.Aria2c &&
                    j.Status is DownloadJobStatus.Queued or DownloadJobStatus.Downloading or DownloadJobStatus.Paused);

                if (!hasAriaJobs)
                {
                    StopAria2Polling();
                    break;
                }

                var active = await _aria2.TellActiveAsync(ct).ConfigureAwait(false);
                var waiting = await _aria2.TellWaitingAsync(0, 50, ct).ConfigureAwait(false);
                var stopped = await _aria2.TellStoppedAsync(0, 50, ct).ConfigureAwait(false);

                foreach (var snap in active.Concat(waiting).Concat(stopped))
                {
                    await ApplyAria2SnapshotAsync(snap).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _actionLog.Log("Warning", $"aria2 poll stopped: {ex.Message}");
            StopAria2Polling();
        }
    }

    private async Task ApplyAria2SnapshotAsync(Aria2StatusSnapshot snap)
    {
        var job = _jobsById.Values.FirstOrDefault(j => string.Equals(j.Aria2Gid, snap.Gid, StringComparison.Ordinal));
        if (job is null) return;

        if (!string.IsNullOrWhiteSpace(snap.FilePath))
        {
            job.FilePath = snap.FilePath;
            var fileName = Path.GetFileName(snap.FilePath);
            if (!string.IsNullOrWhiteSpace(fileName) && !fileName.StartsWith("[METADATA]", StringComparison.OrdinalIgnoreCase))
            {
                job.Title = fileName;
            }
        }

        if (snap.TotalLength > 0)
        {
            job.FileSizeBytes = snap.TotalLength;
            job.ProgressPercent = Math.Clamp(snap.CompletedLength * 100.0 / snap.TotalLength, 0, 100);
        }

        if (snap.CompletedLength > 0)
        {
            var prev = _aria2LastBytesByGid.GetValueOrDefault(snap.Gid);
            if (snap.CompletedLength > prev)
            {
                BandwidthMonitor.RecordBytes(snap.CompletedLength - prev);
                _aria2LastBytesByGid[snap.Gid] = snap.CompletedLength;
            }
        }

        job.SpeedBytesPerSec = snap.DownloadSpeed;
        job.SpeedText = snap.DownloadSpeed > 0 ? BandwidthMonitor.FormatRate(snap.DownloadSpeed) : string.Empty;

        if (snap.DownloadSpeed > 0 && snap.TotalLength > snap.CompletedLength)
        {
            var remSec = (snap.TotalLength - snap.CompletedLength) / (double)snap.DownloadSpeed;
            job.EtaText = TimeSpan.FromSeconds(remSec).ToString(remSec >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
        }
        else
        {
            job.EtaText = string.Empty;
        }

        switch (snap.Status)
        {
            case "complete":
                if (job.Status != DownloadJobStatus.Completed)
                {
                    job.Status = DownloadJobStatus.Completed;
                    job.ProgressPercent = 100;
                    job.SpeedBytesPerSec = 0;
                    job.SpeedText = string.Empty;
                    job.EtaText = string.Empty;
                    job.CompletedAt = DateTime.UtcNow;
                    await _database.UpdateDownloadHistoryAsync(
                        job.HistoryId, job.Title, job.FilePath, job.FileSizeBytes, nameof(DownloadJobStatus.Completed), job.CompletedAt)
                        .ConfigureAwait(false);
                    JobsChanged?.Invoke(this, EventArgs.Empty);
                }
                break;

            case "error":
                if (job.Status != DownloadJobStatus.Failed)
                {
                    job.Status = DownloadJobStatus.Failed;
                    job.ErrorMessage = string.IsNullOrWhiteSpace(snap.ErrorMessage) ? "aria2 download failed." : snap.ErrorMessage;
                    job.SpeedBytesPerSec = 0;
                    job.SpeedText = string.Empty;
                    await _database.UpdateDownloadHistoryAsync(
                        job.HistoryId, job.Title, job.FilePath, job.FileSizeBytes, nameof(DownloadJobStatus.Failed), null)
                        .ConfigureAwait(false);
                    JobsChanged?.Invoke(this, EventArgs.Empty);
                }
                break;

            case "removed":
                if (job.Status != DownloadJobStatus.Cancelled)
                {
                    job.Status = DownloadJobStatus.Cancelled;
                    job.SpeedBytesPerSec = 0;
                    job.SpeedText = string.Empty;
                    JobsChanged?.Invoke(this, EventArgs.Empty);
                }
                break;

            case "paused":
                job.Status = DownloadJobStatus.Paused;
                job.SpeedBytesPerSec = 0;
                job.SpeedText = string.Empty;
                break;

            case "active":
                job.Status = DownloadJobStatus.Downloading;
                break;
        }

        JobUpdated?.Invoke(this, job);
    }

    private static string DeriveFallbackTitle(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var fileName = Path.GetFileName(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(fileName) && fileName.Length > 2)
            {
                return Uri.UnescapeDataString(fileName);
            }

            return uri.Host;
        }

        return url.Length > 60 ? url[..60] : url;
    }
}
