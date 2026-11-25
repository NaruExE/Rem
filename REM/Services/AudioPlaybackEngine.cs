using LibVLCSharp.Shared;
using REM.Models;

namespace REM.Services;

public sealed class AudioPlaybackEngine : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ActionLogService _actionLog;

    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private Media? _currentMedia;
    private CancellationTokenSource? _idleUnloadCts;
    private long _archivedReadBytes;
    private long _currentMediaLastReadBytes;
    private bool _coreInitialized;
    private int _volume = 80;
    private bool _disposed;

    public AudioPlaybackEngine(ActionLogService actionLog)
    {
        _actionLog = actionLog;
        BandwidthMonitor.RegisterLibVlcBytesProvider(GetCumulativeReadBytes);
    }

    public ExtractedAudioTrack? CurrentTrack { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool IsPaused { get; private set; }
    public bool IsLooping { get; private set; }
    public bool IsLive => CurrentTrack?.IsLive ?? false;
    public double CurrentSeconds { get; private set; }
    public double DurationSeconds { get; private set; }

    public void ToggleLoop()
    {
        IsLooping = !IsLooping;
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SkipRelativeSeconds(double deltaSeconds)
    {
        if (IsLive || DurationSeconds <= 0)
        {
            return;
        }

        SeekSeconds(Math.Max(0, CurrentSeconds + deltaSeconds));
    }

    public int Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            _volume = clamped;
            var player = _player;
            if (player is not null)
            {
                try
                {
                    player.Volume = clamped;
                }
                catch
                {
                    // Ignore if player is tearing down.
                }
            }
        }
    }

    public event EventHandler? PlaybackStateChanged;
    public event EventHandler? PositionChanged;
    public event EventHandler<string>? PlaybackError;

    public async Task PlayAsync(ExtractedAudioTrack track, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                EnsureInitializedLocked();

                try
                {
                    _player!.Stop();
                }
                catch
                {
                    // Ignore stop error on idle player.
                }

                SnapshotCurrentMediaReadBytesLocked();
                _currentMedia?.Dispose();
                _currentMedia = null;

                var media = new Media(_libVlc!, new Uri(track.StreamUrl));
                media.AddOption(":no-video");
                media.AddOption(":adaptive-logic=highest");

                _currentMedia = media;
                CurrentTrack = track;
                CurrentSeconds = 0;
                DurationSeconds = track.Duration?.TotalSeconds ?? 0;

                _player!.Volume = _volume;
                _player.Play(media);
                IsPlaying = true;
                IsPaused = false;
            }, cancellationToken).ConfigureAwait(false);

            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TogglePauseAsync()
    {
        if (_disposed || _player is null || CurrentTrack is null)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                if (_player is null)
                {
                    return;
                }

                if (_player.IsPlaying)
                {
                    _player.SetPause(true);
                    IsPlaying = false;
                    IsPaused = true;
                }
                else
                {
                    _player.SetPause(false);
                    IsPlaying = true;
                    IsPaused = false;
                }
            }).ConfigureAwait(false);

            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PauseIfPlayingAsync()
    {
        if (_disposed || _player is null)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                if (_player is { IsPlaying: true })
                {
                    _player.SetPause(true);
                }

                IsPlaying = false;
                if (CurrentTrack is not null)
                {
                    IsPaused = true;
                }
            }).ConfigureAwait(false);

            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResumeIfPausedAsync()
    {
        if (_disposed || _player is null || !IsPaused)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                if (_player is not null && CurrentTrack is not null)
                {
                    _player.SetPause(false);
                    IsPlaying = true;
                    IsPaused = false;
                }
            }).ConfigureAwait(false);

            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SeekSeconds(double targetSeconds)
    {
        var player = _player;
        if (_disposed || player is null || IsLive || DurationSeconds <= 0)
        {
            return;
        }

        var clamped = Math.Clamp(targetSeconds, 0, DurationSeconds);
        try
        {
            player.Time = (long)(clamped * 1000.0);
            CurrentSeconds = clamped;
            PositionChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Ignore seek failure if stream is unseekable.
        }
    }

    public void StopAndDisposeEngine()
    {
        _gate.Wait();
        try
        {
            DisposeEngineLocked();
        }
        finally
        {
            _gate.Release();
        }

        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        BandwidthMonitor.RegisterLibVlcBytesProvider(null);
        _gate.Wait();
        try
        {
            DisposeEngineLocked();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private long GetCumulativeReadBytes()
    {
        var archived = Interlocked.Read(ref _archivedReadBytes);
        var media = Volatile.Read(ref _currentMedia);
        if (media is null)
        {
            return archived;
        }

        try
        {
            var stats = media.Statistics;
            long read = Math.Max(stats.ReadBytes, stats.DemuxReadBytes);
            var current = Interlocked.Read(ref _currentMediaLastReadBytes);
            if (read > current)
            {
                Interlocked.Exchange(ref _currentMediaLastReadBytes, read);
                current = read;
            }

            return archived + current;
        }
        catch
        {
            return archived + Interlocked.Read(ref _currentMediaLastReadBytes);
        }
    }

    private void SnapshotCurrentMediaReadBytesLocked()
    {
        var media = _currentMedia;
        long latest = Interlocked.Exchange(ref _currentMediaLastReadBytes, 0);
        if (media is not null)
        {
            try
            {
                var stats = media.Statistics;
                long read = Math.Max(stats.ReadBytes, stats.DemuxReadBytes);
                if (read > latest)
                {
                    latest = read;
                }
            }
            catch
            {
                // Ignore if native media is tearing down.
            }
        }

        if (latest > 0)
        {
            Interlocked.Add(ref _archivedReadBytes, latest);
        }
    }

    private void EnsureInitializedLocked()
    {
        if (!_coreInitialized)
        {
            Core.Initialize();
            _coreInitialized = true;
        }

        _libVlc ??= new LibVLC(
            "--no-video",
            "--quiet",
            "--no-osd",
            "--network-caching=2500",
            "--adaptive-logic=highest",
            "--http-reconnect");

        if (_player is null)
        {
            _player = new MediaPlayer(_libVlc);
            _player.Playing += OnPlayerPlaying;
            _player.Paused += OnPlayerPaused;
            _player.Stopped += OnPlayerStopped;
            _player.EndReached += OnPlayerEndReached;
            _player.EncounteredError += OnPlayerError;
            _player.TimeChanged += OnPlayerTimeChanged;
            _player.LengthChanged += OnPlayerLengthChanged;
        }
    }

    private void ScheduleIdleUnload()
    {
        CancelIdleUnload();
        if (_disposed || _libVlc is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _idleUnloadCts, cts);
        if (prev is not null)
        {
            try { prev.Cancel(); } catch { }
            prev.Dispose();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(10), cts.Token).ConfigureAwait(false);
                if (!cts.IsCancellationRequested && !_disposed && !IsPlaying && _libVlc is not null)
                {
                    StopAndDisposeEngine();
                    _actionLog.Log("Audio", "Auto-unloaded idle LibVLC audio engine after 10 minutes.");
                    MemoryTrimService.ScheduleTrim(trimOsWorkingSet: true);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        });
    }

    private void CancelIdleUnload()
    {
        var cts = Interlocked.Exchange(ref _idleUnloadCts, null);
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
    }

    private void DisposeEngineLocked()
    {
        CancelIdleUnload();
        if (_player is not null)
        {
            _player.Playing -= OnPlayerPlaying;
            _player.Paused -= OnPlayerPaused;
            _player.Stopped -= OnPlayerStopped;
            _player.EndReached -= OnPlayerEndReached;
            _player.EncounteredError -= OnPlayerError;
            _player.TimeChanged -= OnPlayerTimeChanged;
            _player.LengthChanged -= OnPlayerLengthChanged;

            try
            {
                _player.Stop();
            }
            catch
            {
                // Ignore stop failure during teardown.
            }

            _player.Dispose();
            _player = null;
        }

        SnapshotCurrentMediaReadBytesLocked();
        _currentMedia?.Dispose();
        _currentMedia = null;

        _libVlc?.Dispose();
        _libVlc = null;

        CurrentTrack = null;
        IsPlaying = false;
        IsPaused = false;
        CurrentSeconds = 0;
        DurationSeconds = 0;
    }

    private void OnPlayerPlaying(object? sender, EventArgs e)
    {
        CancelIdleUnload();
        IsPlaying = true;
        IsPaused = false;
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlayerPaused(object? sender, EventArgs e)
    {
        IsPlaying = false;
        IsPaused = true;
        ScheduleIdleUnload();
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlayerStopped(object? sender, EventArgs e)
    {
        IsPlaying = false;
        ScheduleIdleUnload();
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlayerEndReached(object? sender, EventArgs e)
    {
        var track = CurrentTrack;
        if (IsLooping && !_disposed && track is { IsLive: false })
        {
            IsPlaying = false;
            CurrentSeconds = 0;
            _ = Task.Run(async () =>
            {
                try
                {
                    await PlayAsync(track, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            });
            return;
        }

        IsPlaying = false;
        IsPaused = false;
        CurrentSeconds = 0;
        ScheduleIdleUnload();
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlayerError(object? sender, EventArgs e)
    {
        IsPlaying = false;
        _actionLog.Log("Error", $"LibVLC playback error on '{CurrentTrack?.Title ?? "unknown"}'.");
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        PlaybackError?.Invoke(this, "LibVLC encountered a playback or stream error.");
    }

    private void OnPlayerTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        CurrentSeconds = Math.Max(0, e.Time / 1000.0);
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlayerLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        if (e.Length > 0)
        {
            DurationSeconds = e.Length / 1000.0;
            PositionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

