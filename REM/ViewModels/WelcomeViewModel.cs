using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using REM.Services;

namespace REM.ViewModels;

public sealed partial class WelcomeViewModel : ObservableObject, IDisposable
{
    private readonly AudioService _audioService;
    private readonly ActionLogService _actionLog;
    private readonly string _configuredAudioPath;
    private readonly Action _onCompleted;
    private readonly CancellationTokenSource _cts = new();
    private int _completed;
    private bool _disposed;

    public WelcomeViewModel(
        AudioService audioService,
        ImageAssetService imageAssets,
        ActionLogService actionLog,
        string configuredAudioPath,
        Action onCompleted)
    {
        _audioService = audioService;
        _actionLog = actionLog;
        _configuredAudioPath = configuredAudioPath;
        _onCompleted = onCompleted;

        WelcomeImage = imageAssets.GetBrandImage("rem-welcome.png", 180);
    }

    public Bitmap? WelcomeImage { get; }
    public bool HasWelcomeImage => WelcomeImage is not null;

    public string GreetingText => "Welcome to REM";
    public string SubtitleText => "RAM Efficient Manager";
    public string HintText => "RAM REM RAM REM RAM REM RAM REM";

    public async Task BeginAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var result = await _audioService.PlayDetailedAsync(_configuredAudioPath, _cts.Token);
            if (result.Status == AudioPlayStatus.NotFound && !string.IsNullOrWhiteSpace(_configuredAudioPath))
            {
                _actionLog.Log("Warning", $"Welcome audio not found at '{_configuredAudioPath}'.");
            }
            else if (result.Status == AudioPlayStatus.Failed)
            {
                _actionLog.Log("Warning", $"Welcome audio failed to play: {result.ErrorMessage}");
            }

            if (result.Status != AudioPlayStatus.Played && !_cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2.2), _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        CompleteTransition();
    }

    [RelayCommand]
    public void Skip()
    {
        if (_disposed)
        {
            return;
        }

        _cts.Cancel();
        _audioService.Stop();
        CompleteTransition();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _audioService.Stop();
        _cts.Dispose();
    }

    private void CompleteTransition()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0 || _disposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                _onCompleted();
            }
        });
    }
}
