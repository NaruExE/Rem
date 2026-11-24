using NAudio.Wave;

namespace REM.Services;

public enum AudioPlayStatus
{
    Played,
    NotFound,
    Failed,
    Cancelled
}

public sealed record AudioPlayResult(
    AudioPlayStatus Status,
    string? ResolvedPath,
    string? ErrorMessage);

public sealed class AudioService : IDisposable
{
    private readonly object _sync = new();
    private WaveOutEvent? _waveOut;
    private AudioFileReader? _reader;
    private bool _disposed;

    public bool IsPlaying
    {
        get
        {
            lock (_sync)
            {
                return _waveOut is { PlaybackState: PlaybackState.Playing };
            }
        }
    }

    public string? ResolveWelcomeAudioPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath) ? configuredPath : null;
        }

        var audioDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Audio");
        var wavPath = Path.Combine(audioDir, "welcome.wav");
        if (File.Exists(wavPath))
        {
            return wavPath;
        }

        var mp3Path = Path.Combine(audioDir, "welcome.mp3");
        if (File.Exists(mp3Path))
        {
            return mp3Path;
        }

        return null;
    }

    public async Task<bool> PlayAsync(string? configuredPath, CancellationToken cancellationToken)
    {
        var result = await PlayDetailedAsync(configuredPath, cancellationToken).ConfigureAwait(false);
        return result.Status == AudioPlayStatus.Played;
    }

    public async Task<AudioPlayResult> PlayDetailedAsync(
        string? configuredPath,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return new AudioPlayResult(AudioPlayStatus.Cancelled, null, null);
        }

        var resolvedPath = ResolveWelcomeAudioPath(configuredPath);
        if (resolvedPath is null)
        {
            var missingMsg = string.IsNullOrWhiteSpace(configuredPath)
                ? null
                : $"Configured audio file was not found: {configuredPath}";
            return new AudioPlayResult(AudioPlayStatus.NotFound, configuredPath, missingMsg);
        }

        Stop();

        var tcs = new TaskCompletionSource<AudioPlayResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        WaveOutEvent? waveOut = null;
        AudioFileReader? reader = null;

        try
        {
            reader = new AudioFileReader(resolvedPath);
            waveOut = new WaveOutEvent();

            void OnPlaybackStopped(object? _, StoppedEventArgs args)
            {
                if (args.Exception is not null)
                {
                    tcs.TrySetResult(new AudioPlayResult(AudioPlayStatus.Failed, resolvedPath, args.Exception.Message));
                }
                else
                {
                    tcs.TrySetResult(new AudioPlayResult(AudioPlayStatus.Played, resolvedPath, null));
                }
            }

            waveOut.PlaybackStopped += OnPlaybackStopped;
            waveOut.Init(reader);

            lock (_sync)
            {
                if (_disposed)
                {
                    waveOut.PlaybackStopped -= OnPlaybackStopped;
                    waveOut.Dispose();
                    reader.Dispose();
                    return new AudioPlayResult(AudioPlayStatus.Cancelled, resolvedPath, null);
                }

                _waveOut = waveOut;
                _reader = reader;
            }

            using var reg = cancellationToken.Register(() =>
            {
                Stop();
                tcs.TrySetResult(new AudioPlayResult(AudioPlayStatus.Cancelled, resolvedPath, null));
            });

            waveOut.Play();
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new AudioPlayResult(AudioPlayStatus.Failed, resolvedPath, ex.Message);
        }
        finally
        {
            CleanupHandles(waveOut, reader);
        }
    }

    public void Stop()
    {
        WaveOutEvent? waveOut;
        AudioFileReader? reader;
        lock (_sync)
        {
            waveOut = _waveOut;
            reader = _reader;
            _waveOut = null;
            _reader = null;
        }

        if (waveOut is not null)
        {
            try
            {
                waveOut.Stop();
            }
            catch
            {
                // Ignore audio device stop errors during cleanup.
            }

            waveOut.Dispose();
        }

        reader?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private void CleanupHandles(WaveOutEvent? waveOut, AudioFileReader? reader)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_waveOut, waveOut))
            {
                _waveOut = null;
            }

            if (ReferenceEquals(_reader, reader))
            {
                _reader = null;
            }
        }

        if (waveOut is not null)
        {
            try
            {
                waveOut.Dispose();
            }
            catch
            {
                // Ignore disposal errors.
            }
        }

        reader?.Dispose();
    }
}
