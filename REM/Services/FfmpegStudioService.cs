using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using REM.Models;

namespace REM.Services;

/// <summary>
/// Lazy FFmpeg &amp; FFprobe studio engine running sequential encodes cleanly in the background
/// with target-size bitrate math, stream-copy clipping, format conversion, and hard process-tree teardown.
/// </summary>
public sealed class FfmpegStudioService : IDisposable
{
    private readonly DatabaseService _database;
    private readonly YtDlpService _ytDlp;
    private readonly ActionLogService _actionLog;
    private readonly object _jobsLock = new();
    private readonly List<MediaStudioJobState> _jobs = [];
    private readonly Dictionary<long, CancellationTokenSource> _jobCtsById = [];
    private readonly Channel<(MediaStudioJobState Job, MediaStudioJobRequest Request)> _queue =
        Channel.CreateUnbounded<(MediaStudioJobState, MediaStudioJobRequest)>();
    private readonly SemaphoreSlim _workerGate = new(1, 1);

    private CancellationTokenSource _shutdownCts = new();
    private Process? _activeProcess;
    private long _nextJobId;
    private int _activeCount;
    private bool _workerStarted;
    private bool _disposed;

    public FfmpegStudioService(
        DatabaseService database,
        YtDlpService ytDlp,
        ActionLogService actionLog)
    {
        _database = database;
        _ytDlp = ytDlp;
        _actionLog = actionLog;
    }

    public event EventHandler? JobsChanged;
    public event EventHandler<MediaStudioJobState>? JobUpdated;

    public int ActiveCount => Volatile.Read(ref _activeCount);

    public IReadOnlyList<MediaStudioJobState> GetJobsSnapshot()
    {
        lock (_jobsLock)
        {
            return _jobs.ToList();
        }
    }

    public async Task<MediaProbeInfo?> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        var dir = await EnsureFfmpegDirectoryAsync(null, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }

        var ffprobePath = Path.Combine(dir, "ffprobe.exe");
        var fileInfo = new FileInfo(filePath);
        if (!File.Exists(ffprobePath))
        {
            return new MediaProbeInfo(filePath, fileInfo.Exists ? fileInfo.Length : 0, 0, 0, 0, null, null, false, false);
        }

        var psi = CreateStartInfo(ffprobePath);
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("quiet");
        psi.ArgumentList.Add("-print_format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("-show_format");
        psi.ArgumentList.Add("-show_streams");
        psi.ArgumentList.Add(filePath);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            return null;
        }

        using var reg = cancellationToken.Register(() => TryKillProcessTree(process));
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        _ = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return new MediaProbeInfo(filePath, fileInfo.Exists ? fileInfo.Length : 0, 0, 0, 0, null, null, false, false);
        }

        return ParseFfprobeJson(filePath, fileInfo.Exists ? fileInfo.Length : 0, stdout);
    }

    public MediaStudioJobState EnqueueJob(MediaStudioJobRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        EnsureWorkerRunning();

        var id = Interlocked.Increment(ref _nextJobId);
        var job = new MediaStudioJobState
        {
            Id = id,
            Title = Path.GetFileName(request.OutputPath),
            InputPath = request.InputPath,
            OutputPath = request.OutputPath,
            ModeLabel = BuildModeLabel(request),
            Status = MediaStudioJobStatus.Queued,
            StatusText = "Queued"
        };

        var jobCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        lock (_jobsLock)
        {
            _jobs.Insert(0, job);
            _jobCtsById[id] = jobCts;
        }

        _queue.Writer.TryWrite((job, request));
        JobsChanged?.Invoke(this, EventArgs.Empty);
        return job;
    }

    public void CancelJob(long jobId)
    {
        CancellationTokenSource? cts = null;
        MediaStudioJobState? job = null;
        lock (_jobsLock)
        {
            job = _jobs.FirstOrDefault(j => j.Id == jobId);
            _jobCtsById.TryGetValue(jobId, out cts);
        }

        if (job is null)
        {
            return;
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
        }

        if (job.Status == MediaStudioJobStatus.Queued)
        {
            job.Status = MediaStudioJobStatus.Cancelled;
            job.StatusText = "Cancelled";
            job.ProgressPercent = 0;
            JobUpdated?.Invoke(this, job);
            JobsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ClearFinishedJobs()
    {
        lock (_jobsLock)
        {
            _jobs.RemoveAll(j => j.Status is MediaStudioJobStatus.Completed or MediaStudioJobStatus.Failed or MediaStudioJobStatus.Cancelled);
        }

        JobsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ShutdownAllAsync()
    {
        CancellationTokenSource oldCts;
        lock (_jobsLock)
        {
            oldCts = _shutdownCts;
            _shutdownCts = new CancellationTokenSource();
            foreach (var cts in _jobCtsById.Values)
            {
                try { cts.Cancel(); } catch { }
            }

            foreach (var job in _jobs)
            {
                if (job.Status is MediaStudioJobStatus.Queued or MediaStudioJobStatus.Probing or MediaStudioJobStatus.Running)
                {
                    job.Status = MediaStudioJobStatus.Cancelled;
                    job.StatusText = "Stopped";
                }
            }
        }

        try { oldCts.Cancel(); } catch { }
        oldCts.Dispose();

        TryKillProcessTree(_activeProcess);
        _activeProcess = null;

        // Wait briefly for sequential worker lock to drain so no active ffmpeg remains.
        if (await _workerGate.WaitAsync(1500).ConfigureAwait(false))
        {
            _workerGate.Release();
        }

        JobsChanged?.Invoke(this, EventArgs.Empty);
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Writer.TryComplete();

        try { _shutdownCts.Cancel(); } catch { }
        _shutdownCts.Dispose();

        lock (_jobsLock)
        {
            foreach (var cts in _jobCtsById.Values)
            {
                try { cts.Cancel(); cts.Dispose(); } catch { }
            }

            _jobCtsById.Clear();
        }

        TryKillProcessTree(_activeProcess);
        _activeProcess = null;
        _workerGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private void EnsureWorkerRunning()
    {
        lock (_jobsLock)
        {
            if (_workerStarted || _disposed)
            {
                return;
            }

            _workerStarted = true;
        }

        _ = Task.Run(ProcessQueueLoopAsync);
    }

    private async Task ProcessQueueLoopAsync()
    {
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (_queue.Reader.TryRead(out var item))
            {
                if (_disposed)
                {
                    return;
                }

                await _workerGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await ExecuteJobAsync(item.Job, item.Request).ConfigureAwait(false);
                }
                finally
                {
                    _workerGate.Release();
                }
            }
        }
    }

    private async Task ExecuteJobAsync(MediaStudioJobState job, MediaStudioJobRequest request)
    {
        CancellationTokenSource? jobCts;
        lock (_jobsLock)
        {
            if (!_jobCtsById.TryGetValue(job.Id, out jobCts))
            {
                return;
            }
        }

        if (jobCts.IsCancellationRequested || job.Status == MediaStudioJobStatus.Cancelled)
        {
            CleanupCancelledJob(job, deleteOutput: false);
            return;
        }

        var ct = jobCts.Token;
        Interlocked.Increment(ref _activeCount);
        try
        {
            job.Status = MediaStudioJobStatus.Probing;
            job.StatusText = "Locating FFmpeg & inspecting media...";
            JobUpdated?.Invoke(this, job);

            var ffmpegDir = await EnsureFfmpegDirectoryAsync(
                status =>
                {
                    job.StatusText = status;
                    JobUpdated?.Invoke(this, job);
                },
                ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(ffmpegDir))
            {
                throw new InvalidOperationException("Could not find or download ffmpeg.exe / ffprobe.exe.");
            }

            var ffmpegExe = Path.Combine(ffmpegDir, "ffmpeg.exe");
            var probe = await ProbeAsync(request.InputPath, ct).ConfigureAwait(false);

            var outputDir = Path.GetDirectoryName(request.OutputPath);
            if (!string.IsNullOrWhiteSpace(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            var (args, expectedDurationSec, modeSummary) = BuildFfmpegArguments(request, probe);

            job.Status = MediaStudioJobStatus.Running;
            job.StatusText = modeSummary;
            job.ProgressPercent = 0;
            JobUpdated?.Invoke(this, job);

            var psi = CreateStartInfo(ffmpegExe);
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to launch ffmpeg.exe.");
            }

            _activeProcess = process;
            using var cancelReg = ct.Register(() => TryKillProcessTree(process));

            var stderrBuffer = new List<string>(24);
            var stderrTask = Task.Run(async () =>
            {
                while (!process.StandardError.EndOfStream)
                {
                    var line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        lock (stderrBuffer)
                        {
                            if (stderrBuffer.Count >= 24)
                            {
                                stderrBuffer.RemoveAt(0);
                            }

                            stderrBuffer.Add(line);
                        }
                    }
                }
            }, CancellationToken.None);

            var lastReport = DateTime.UtcNow;
            var currentOutTimeUs = 0L;
            var currentSpeed = string.Empty;

            while (!process.StandardOutput.EndOfStream)
            {
                var line = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
                {
                    if (long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
                    {
                        currentOutTimeUs = us;
                    }
                }
                else if (line.StartsWith("out_time_ms=", StringComparison.Ordinal))
                {
                    // Note: ffmpeg reports out_time_ms in microseconds despite the name in -progress output.
                    if (long.TryParse(line.AsSpan("out_time_ms=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
                    {
                        currentOutTimeUs = us;
                    }
                }
                else if (line.StartsWith("speed=", StringComparison.Ordinal))
                {
                    currentSpeed = line["speed=".Length..].Trim();
                }
                else if (line.StartsWith("progress=", StringComparison.Ordinal) &&
                         (DateTime.UtcNow - lastReport).TotalMilliseconds >= 220)
                {
                    lastReport = DateTime.UtcNow;
                    UpdateJobProgress(job, expectedDurationSec, currentOutTimeUs, currentSpeed);
                }
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
            _activeProcess = null;

            if (ct.IsCancellationRequested)
            {
                CleanupCancelledJob(job, deleteOutput: true);
                return;
            }

            if (process.ExitCode != 0 || !File.Exists(request.OutputPath))
            {
                string tail;
                lock (stderrBuffer)
                {
                    tail = stderrBuffer.Count > 0 ? stderrBuffer[^1] : $"ffmpeg exited with code {process.ExitCode}";
                }

                throw new InvalidOperationException(tail);
            }

            var outInfo = new FileInfo(request.OutputPath);
            job.OutputSizeBytes = outInfo.Exists ? outInfo.Length : 0;
            job.ProgressPercent = 100;
            job.EtaText = "Done";
            job.Status = MediaStudioJobStatus.Completed;
            job.StatusText = $"Completed · {MediaProbeInfo.FormatBytes(job.OutputSizeBytes)}";
            JobUpdated?.Invoke(this, job);
            JobsChanged?.Invoke(this, EventArgs.Empty);
            _actionLog.Log("MediaStudio", $"Finished '{job.Title}' ({MediaProbeInfo.FormatBytes(job.OutputSizeBytes)}).");
        }
        catch (OperationCanceledException)
        {
            CleanupCancelledJob(job, deleteOutput: true);
        }
        catch (Exception ex)
        {
            TryDeletePartialFile(request.OutputPath);
            job.Status = MediaStudioJobStatus.Failed;
            job.ErrorMessage = ex.Message;
            job.StatusText = $"Failed: {ex.Message}";
            JobUpdated?.Invoke(this, job);
            JobsChanged?.Invoke(this, EventArgs.Empty);
            _actionLog.Log("Warning", $"FFmpeg Studio failed for '{job.Title}': {ex.Message}");
        }
        finally
        {
            _activeProcess = null;
            Interlocked.Decrement(ref _activeCount);
            lock (_jobsLock)
            {
                if (_jobCtsById.Remove(job.Id, out var removed))
                {
                    removed.Dispose();
                }
            }
        }
    }

    private void UpdateJobProgress(MediaStudioJobState job, double expectedDurationSec, long outTimeUs, string speedText)
    {
        if (expectedDurationSec > 0.1 && outTimeUs > 0)
        {
            var encodedSec = outTimeUs / 1_000_000.0;
            var pct = Math.Clamp(encodedSec / expectedDurationSec * 100.0, 0, 99.5);
            job.ProgressPercent = pct;

            if (TryParseSpeedMultiplier(speedText, out var mult) && mult > 0.05)
            {
                var remainingSec = Math.Max(0, (expectedDurationSec - encodedSec) / mult);
                job.EtaText = FormatEta(remainingSec);
            }
        }

        if (!string.IsNullOrWhiteSpace(speedText) && speedText != "N/A")
        {
            job.SpeedText = speedText;
        }

        JobUpdated?.Invoke(this, job);
    }

    private static bool TryParseSpeedMultiplier(string? speedText, out double mult)
    {
        mult = 0;
        if (string.IsNullOrWhiteSpace(speedText))
        {
            return false;
        }

        var trimmed = speedText.Trim().TrimEnd('x', 'X');
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out mult);
    }

    private static string FormatEta(double seconds)
    {
        if (seconds < 1)
        {
            return "<1s";
        }

        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalMinutes >= 1 ? $"{(int)ts.TotalMinutes}m {ts.Seconds:D2}s" : $"{ts.Seconds}s";
    }

    private void CleanupCancelledJob(MediaStudioJobState job, bool deleteOutput)
    {
        if (deleteOutput)
        {
            TryDeletePartialFile(job.OutputPath);
        }

        job.Status = MediaStudioJobStatus.Cancelled;
        job.StatusText = "Cancelled";
        job.ProgressPercent = 0;
        JobUpdated?.Invoke(this, job);
        JobsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<string?> EnsureFfmpegDirectoryAsync(Action<string>? onStatus, CancellationToken ct)
    {
        var settings = await _database.GetMediaGrabberSettingsAsync(0, ct).ConfigureAwait(false);
        var existing = YtDlpDownloadService.ResolveFfmpegDirectory(settings);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var downloader = new YtDlpDownloadService(_ytDlp, _actionLog);
        return await downloader.EnsureFfmpegDirectoryAsync(
            settings,
            update => onStatus?.Invoke(update.StatusText ?? update.SpeedText),
            ct).ConfigureAwait(false);
    }

    private static (List<string> Args, double ExpectedDurationSec, string Summary) BuildFfmpegArguments(
        MediaStudioJobRequest req,
        MediaProbeInfo? probe)
    {
        var args = new List<string> { "-hide_banner", "-progress", "pipe:1", "-nostats", "-y" };
        var probeDur = probe?.DurationSeconds ?? 0;
        var hasTrimStart = TryParseTimestamp(req.TrimStart, out var startSec) && startSec >= 0;
        var hasTrimEnd = TryParseTimestamp(req.TrimEnd, out var endSec) && endSec > 0;

        double expectedDur = probeDur;
        if (req.Mode == MediaStudioMode.TrimClip || hasTrimStart || hasTrimEnd)
        {
            var effectiveStart = hasTrimStart ? startSec : 0;
            var effectiveEnd = hasTrimEnd ? endSec : probeDur;
            if (effectiveEnd > effectiveStart)
            {
                expectedDur = effectiveEnd - effectiveStart;
            }
        }

        if (req.Mode == MediaStudioMode.ConvertImage)
        {
            args.Add("-i");
            args.Add(req.InputPath);
            if (req.MaxWidth > 0)
            {
                args.Add("-vf");
                args.Add($"scale='min({req.MaxWidth},iw)':-1");
            }

            if (req.StripMetadata)
            {
                args.Add("-map_metadata");
                args.Add("-1");
            }

            var fmt = req.OutputFormat.ToLowerInvariant();
            if (fmt == "webp")
            {
                args.Add("-quality");
                args.Add("85");
            }
            else if (fmt is "jpg" or "jpeg")
            {
                args.Add("-q:v");
                args.Add("3");
            }

            args.Add(req.OutputPath);
            return (args, 0.5, $"Converting image to {fmt.ToUpperInvariant()}...");
        }

        if (req.Mode == MediaStudioMode.TrimClip)
        {
            if (hasTrimStart)
            {
                args.Add("-ss");
                args.Add(FormatFfmpegTimestamp(startSec));
            }

            if (hasTrimEnd && endSec > (hasTrimStart ? startSec : 0))
            {
                args.Add("-to");
                args.Add(FormatFfmpegTimestamp(endSec));
            }

            args.Add("-i");
            args.Add(req.InputPath);

            if (req.StreamCopy)
            {
                args.Add("-c");
                args.Add("copy");
                args.Add("-avoid_negative_ts");
                args.Add("make_zero");
            }
            else
            {
                args.Add("-c:v");
                args.Add("libx264");
                args.Add("-preset");
                args.Add("fast");
                args.Add("-crf");
                args.Add("21");
                args.Add("-pix_fmt");
                args.Add("yuv420p");
                args.Add("-c:a");
                args.Add("aac");
                args.Add("-b:a");
                args.Add("160k");
                args.Add("-movflags");
                args.Add("+faststart");
            }

            if (req.StripMetadata)
            {
                args.Add("-map_metadata");
                args.Add("-1");
            }

            args.Add(req.OutputPath);
            return (args, expectedDur, req.StreamCopy ? "Clipping (lossless stream copy)..." : "Clipping & re-encoding...");
        }

        if (req.Mode == MediaStudioMode.DiscordCompress)
        {
            if (expectedDur <= 0.1)
            {
                throw new InvalidOperationException("Cannot compute target bitrate without knowing media duration.");
            }

            if (hasTrimStart)
            {
                args.Add("-ss");
                args.Add(FormatFfmpegTimestamp(startSec));
            }

            if (hasTrimEnd && endSec > (hasTrimStart ? startSec : 0))
            {
                args.Add("-to");
                args.Add(FormatFfmpegTimestamp(endSec));
            }

            args.Add("-i");
            args.Add(req.InputPath);

            var targetMb = Math.Clamp(req.TargetSizeMb, 1.0, 500.0);
            var usableBits = targetMb * 0.93 * 1024.0 * 1024.0 * 8.0;
            var totalKbps = usableBits / expectedDur / 1000.0;
            var hasAudio = probe?.HasAudio ?? true;

            var audioKbps = !hasAudio ? 0 : totalKbps switch
            {
                >= 950 => 128,
                >= 450 => 96,
                >= 220 => 64,
                _ => 48
            };

            var videoKbps = Math.Max(110, (int)Math.Floor(totalKbps - audioKbps));
            var maxWidth = req.MaxWidth > 0 ? req.MaxWidth : 0;
            if (maxWidth == 0 && probe is { Width: > 0 })
            {
                if (videoKbps < 520 && probe.Width > 854)
                {
                    maxWidth = 854;
                }
                else if (videoKbps < 1300 && probe.Width > 1280)
                {
                    maxWidth = 1280;
                }
            }

            if (maxWidth > 0)
            {
                args.Add("-vf");
                args.Add($"scale='min({maxWidth},iw)':-2");
            }

            args.Add("-c:v");
            args.Add("libx264");
            args.Add("-preset");
            args.Add("fast");
            args.Add("-b:v");
            args.Add($"{videoKbps}k");
            args.Add("-maxrate");
            args.Add($"{(int)(videoKbps * 1.15)}k");
            args.Add("-bufsize");
            args.Add($"{videoKbps * 2}k");
            args.Add("-pix_fmt");
            args.Add("yuv420p");
            args.Add("-movflags");
            args.Add("+faststart");

            if (hasAudio && audioKbps > 0)
            {
                args.Add("-c:a");
                args.Add("aac");
                args.Add("-b:a");
                args.Add($"{audioKbps}k");
            }
            else
            {
                args.Add("-an");
            }

            if (req.StripMetadata)
            {
                args.Add("-map_metadata");
                args.Add("-1");
            }

            args.Add(req.OutputPath);
            return (args, expectedDur, $"Compressing under {targetMb:0.#} MB ({videoKbps}k video)...");
        }

        if (req.Mode == MediaStudioMode.ConvertAudio)
        {
            args.Add("-i");
            args.Add(req.InputPath);
            args.Add("-vn");

            var fmt = req.OutputFormat.ToLowerInvariant();
            var kbps = Math.Clamp(req.AudioBitrateKbps, 64, 320);
            switch (fmt)
            {
                case "mp3":
                    args.Add("-c:a");
                    args.Add("libmp3lame");
                    args.Add("-b:a");
                    args.Add($"{kbps}k");
                    break;
                case "m4a":
                    args.Add("-c:a");
                    args.Add("aac");
                    args.Add("-b:a");
                    args.Add($"{kbps}k");
                    break;
                case "opus":
                    args.Add("-c:a");
                    args.Add("libopus");
                    args.Add("-b:a");
                    args.Add($"{kbps}k");
                    break;
                case "flac":
                    args.Add("-c:a");
                    args.Add("flac");
                    break;
                case "wav":
                    args.Add("-c:a");
                    args.Add("pcm_s16le");
                    break;
            }

            if (req.StripMetadata)
            {
                args.Add("-map_metadata");
                args.Add("-1");
            }

            args.Add(req.OutputPath);
            return (args, expectedDur, $"Converting audio to {fmt.ToUpperInvariant()}...");
        }

        // ConvertVideo
        args.Add("-i");
        args.Add(req.InputPath);

        var videoFmt = req.OutputFormat.ToLowerInvariant();
        if (videoFmt == "gif")
        {
            var gifWidth = req.MaxWidth > 0 ? req.MaxWidth : 480;
            args.Add("-vf");
            args.Add($"fps=15,scale='min({gifWidth},iw)':-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse");
            args.Add("-loop");
            args.Add("0");
        }
        else
        {
            if (req.MaxWidth > 0)
            {
                args.Add("-vf");
                args.Add($"scale='min({req.MaxWidth},iw)':-2");
            }

            var crf = Math.Clamp(req.VideoCrf, 14, 34);
            if (videoFmt == "webm")
            {
                args.Add("-c:v");
                args.Add("libvpx-vp9");
                args.Add("-crf");
                args.Add(crf.ToString(CultureInfo.InvariantCulture));
                args.Add("-b:v");
                args.Add("0");
                args.Add("-deadline");
                args.Add("realtime");
                args.Add("-cpu-used");
                args.Add("4");
                args.Add("-c:a");
                args.Add("libopus");
                args.Add("-b:a");
                args.Add("128k");
            }
            else
            {
                args.Add("-c:v");
                args.Add("libx264");
                args.Add("-preset");
                args.Add("fast");
                args.Add("-crf");
                args.Add(crf.ToString(CultureInfo.InvariantCulture));
                args.Add("-pix_fmt");
                args.Add("yuv420p");
                args.Add("-c:a");
                args.Add("aac");
                args.Add("-b:a");
                args.Add("160k");
                if (videoFmt == "mp4")
                {
                    args.Add("-movflags");
                    args.Add("+faststart");
                }
            }
        }

        if (req.StripMetadata)
        {
            args.Add("-map_metadata");
            args.Add("-1");
        }

        args.Add(req.OutputPath);
        return (args, expectedDur, $"Converting video to {videoFmt.ToUpperInvariant()}...");
    }

    private static string BuildModeLabel(MediaStudioJobRequest req) => req.Mode switch
    {
        MediaStudioMode.DiscordCompress => $"Discord < {req.TargetSizeMb:0.#} MB",
        MediaStudioMode.TrimClip => req.StreamCopy ? "Trim (Copy)" : "Trim (Re-encode)",
        MediaStudioMode.ConvertVideo => $"Video -> {req.OutputFormat.ToUpperInvariant()}",
        MediaStudioMode.ConvertAudio => $"Audio -> {req.OutputFormat.ToUpperInvariant()}",
        MediaStudioMode.ConvertImage => $"Image -> {req.OutputFormat.ToUpperInvariant()}",
        _ => "Convert"
    };

    private static bool TryParseTimestamp(string? raw, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var direct))
        {
            seconds = direct;
            return true;
        }

        var parts = trimmed.Split(':');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var m) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
        {
            seconds = m * 60.0 + s;
            return true;
        }

        if (parts.Length == 3 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m3) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s3))
        {
            seconds = h * 3600.0 + m3 * 60.0 + s3;
            return true;
        }

        return false;
    }

    private static string FormatFfmpegTimestamp(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    private static MediaProbeInfo ParseFfprobeJson(string filePath, long fileSize, string json)
    {
        double duration = 0;
        int width = 0;
        int height = 0;
        string? videoCodec = null;
        string? audioCodec = null;
        bool hasVideo = false;
        bool hasAudio = false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("format", out var fmt) &&
                fmt.TryGetProperty("duration", out var durEl) &&
                double.TryParse(durEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDur))
            {
                duration = parsedDur;
            }

            if (doc.RootElement.TryGetProperty("streams", out var streams) &&
                streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var codecType = stream.TryGetProperty("codec_type", out var ctEl) ? ctEl.GetString() : null;
                    var codecName = stream.TryGetProperty("codec_name", out var cnEl) ? cnEl.GetString() : null;

                    if (string.Equals(codecType, "video", StringComparison.OrdinalIgnoreCase) && !hasVideo)
                    {
                        hasVideo = true;
                        videoCodec = codecName;
                        if (stream.TryGetProperty("width", out var wEl) && wEl.TryGetInt32(out var w))
                        {
                            width = w;
                        }
                        if (stream.TryGetProperty("height", out var hEl) && hEl.TryGetInt32(out var h))
                        {
                            height = h;
                        }
                    }
                    else if (string.Equals(codecType, "audio", StringComparison.OrdinalIgnoreCase) && !hasAudio)
                    {
                        hasAudio = true;
                        audioCodec = codecName;
                    }
                }
            }
        }
        catch
        {
        }

        return new MediaProbeInfo(filePath, fileSize, duration, width, height, videoCodec, audioCodec, hasVideo, hasAudio);
    }

    private static ProcessStartInfo CreateStartInfo(string exePath) => new()
    {
        FileName = exePath,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    private static void TryKillProcessTree(Process? process)
    {
        if (process is null)
        {
            return;
        }

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

    private static void TryDeletePartialFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
