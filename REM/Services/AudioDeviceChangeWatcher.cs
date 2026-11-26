using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace REM.Services;

/// <summary>
/// Watches Windows default audio output device changes via NAudio MMDeviceEnumerator
/// with a 700ms debounce so Spotify Soloist can restart cleanly on the new headset/speaker.
/// </summary>
public sealed class AudioDeviceChangeWatcher : IMMNotificationClient, IDisposable
{
    private MMDeviceEnumerator? _enumerator;
    private CancellationTokenSource? _debounceCts;
    private string? _lastDeviceId;
    private bool _disposed;

    public event EventHandler<string>? DefaultRenderDeviceChanged;

    public AudioDeviceChangeWatcher()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            _enumerator = new MMDeviceEnumerator();
            try
            {
                using var current = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _lastDeviceId = current.ID;
            }
            catch
            {
            }

            _enumerator.RegisterEndpointNotificationCallback(this);
        }
        catch
        {
            _enumerator?.Dispose();
            _enumerator = null;
        }
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (_disposed || flow != DataFlow.Render || role != Role.Multimedia || string.IsNullOrWhiteSpace(defaultDeviceId))
        {
            return;
        }

        if (string.Equals(_lastDeviceId, defaultDeviceId, StringComparison.Ordinal))
        {
            return;
        }

        _lastDeviceId = defaultDeviceId;

        var cts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _debounceCts, cts);
        if (prev is not null)
        {
            try { prev.Cancel(); } catch { }
            prev.Dispose();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(700, cts.Token).ConfigureAwait(false);
                if (!cts.IsCancellationRequested && !_disposed)
                {
                    DefaultRenderDeviceChanged?.Invoke(this, defaultDeviceId);
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

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var cts = Interlocked.Exchange(ref _debounceCts, null);
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        var enumerator = Interlocked.Exchange(ref _enumerator, null);
        if (enumerator is not null)
        {
            try { enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
            try { enumerator.Dispose(); } catch { }
        }
    }
}
