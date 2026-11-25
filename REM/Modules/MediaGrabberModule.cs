using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using REM.Models;
using REM.Services;
using REM.ViewModels;
using REM.Views;

namespace REM.Modules;

/// <summary>
/// Standalone "Media Grabber" module combining yt-dlp and lazy aria2c RPC downloading
/// with background queue execution, headless detachment, and hard child-process teardown on Kill.
/// </summary>
public sealed class MediaGrabberModule : IAppModule, IHeaderConfigurableModule, ICloseableModule, IVlcPlayMediaModule
{
    private readonly DatabaseService _database;
    private readonly YtDlpService _ytDlp;
    private readonly DialogService _dialogService;
    private readonly ActionLogService _actionLog;
    private readonly YtDlpDownloadService _ytDlpDownload;
    private readonly Aria2RpcService _aria2;
    private readonly DownloadQueueService _queue;

    private Control? _activeView;
    private MediaGrabberViewModel? _activeViewModel;
    private ModuleState _state = ModuleState.Stopped;
    private long _activeProfileId;
    private bool _disposed;

    public MediaGrabberModule(
        DatabaseService database,
        YtDlpService ytDlp,
        DialogService dialogService,
        ActionLogService actionLog)
    {
        _database = database;
        _ytDlp = ytDlp;
        _dialogService = dialogService;
        _actionLog = actionLog;

        _ytDlpDownload = new YtDlpDownloadService(ytDlp, actionLog);
        _aria2 = new Aria2RpcService(actionLog);
        _queue = new DownloadQueueService(database, _ytDlpDownload, _aria2, actionLog);

        _queue.JobsChanged += OnQueueChanged;
        _queue.JobUpdated += OnQueueJobUpdated;
        StatusSummary = "Stopped · Queue unloaded";
    }

    public string Id => "media-grabber";
    public string Name => "Media Grabber";
    public string Description => "Multi-threaded yt-dlp & aria2c downloader with background queue";
    public string IconGlyph => "D";
    public Geometry IconGeometry => IconCatalog.Download;
    public string AccentHex => "#10B981";

    public ModuleState State => _state;
    public bool HasActiveView => _activeView is not null;
    public string StatusSummary { get; private set; }

    public Action<string>? PlayCompletedMediaInVlcRequested { get; set; }

    public event EventHandler<ModuleState>? StateChanged;
    public event EventHandler? StatusSummaryChanged;

    public void SetActiveProfile(long profileId)
    {
        if (_activeProfileId == profileId)
        {
            return;
        }

        _activeProfileId = profileId;
        _ = LoadProfileQueueAsync(profileId);
    }

    private async Task LoadProfileQueueAsync(long profileId)
    {
        try
        {
            await _queue.LoadProfileAsync(profileId).ConfigureAwait(false);
            if (_activeViewModel is { } vm)
            {
                Dispatcher.UIThread.Post(() => vm.OnProfileLoaded());
            }
        }
        catch (Exception ex)
        {
            _actionLog.Log("Warning", $"Failed to load Media Grabber history: {ex.Message}");
        }
    }

    public async Task EnqueueFromDispatcherAsync(
        string url,
        string? title,
        string formatPreset,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_activeProfileId > 0 && _queue.ActiveProfileId != _activeProfileId)
        {
            await _queue.LoadProfileAsync(_activeProfileId, cancellationToken).ConfigureAwait(false);
        }

        if (_state == ModuleState.Stopped)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed && _state == ModuleState.Stopped)
                {
                    SetState(ModuleState.RunningHeadless);
                    UpdateStatusSummary();
                }
            });
        }

        await _queue.EnqueueAsync(url, title, DownloadEngineKind.Auto, formatPreset, cancellationToken).ConfigureAwait(false);
        _actionLog.Log("MediaGrabber", $"Queued '{title ?? url}' ({formatPreset}) from stream module.");
    }

    public Control OpenUI()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_activeView is null || _activeViewModel is null)
        {
            _activeViewModel = new MediaGrabberViewModel(
                this,
                _activeProfileId,
                _database,
                _queue,
                _ytDlpDownload,
                _dialogService,
                _actionLog);

            _activeViewModel.IsHeaderVisible = _database.LoadSettings().ShowAppHeaderBar;
            _activeView = new MediaGrabberView
            {
                DataContext = _activeViewModel
            };
        }

        SetState(ModuleState.RunningUI);
        UpdateStatusSummary();
        return _activeView;
    }

    public void RunHeadless()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        DisposeView();
        SetState(ModuleState.RunningHeadless);
        UpdateStatusSummary();
        MemoryTrimService.ScheduleTrim();
    }

    public void Pause()
    {
        if (_disposed || _state == ModuleState.Stopped)
        {
            return;
        }

        DisposeView();
        _ = _queue.PauseAllAria2Async();
        SetState(ModuleState.Paused);
        UpdateStatusSummary();
        MemoryTrimService.ScheduleTrim();
    }

    public void Kill()
    {
        if (_disposed)
        {
            return;
        }

        DisposeView();

        try
        {
            _queue.ShutdownAllAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        GC.Collect(2, GCCollectionMode.Optimized);
        SetState(ModuleState.Stopped);
        StatusSummary = "Stopped · Queue & aria2c disposed";
        StatusSummaryChanged?.Invoke(this, EventArgs.Empty);
        MemoryTrimService.ScheduleTrim();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.JobsChanged -= OnQueueChanged;
        _queue.JobUpdated -= OnQueueJobUpdated;
        DisposeView();

        _queue.Dispose();
        _aria2.Dispose();
        GC.SuppressFinalize(this);
    }

    public void SetHeaderVisibility(bool visible)
    {
        if (_activeViewModel is not null)
        {
            _activeViewModel.IsHeaderVisible = visible;
        }
    }

    public void AttachCloseCallback(Action closeToDashboard)
    {
        if (_activeViewModel is not null)
        {
            _activeViewModel.RequestCloseToDashboard = closeToDashboard;
        }
    }

    internal void RequestPlayInVlc(string filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            PlayCompletedMediaInVlcRequested?.Invoke(filePath);
        }
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            Dispatcher.UIThread.Post(UpdateStatusSummary);
        }
    }

    private void OnQueueJobUpdated(object? sender, DownloadJobState job)
    {
        if (!_disposed)
        {
            Dispatcher.UIThread.Post(UpdateStatusSummary);
        }
    }

    private void SetState(ModuleState next)
    {
        if (_state == next)
        {
            return;
        }

        _state = next;
        StateChanged?.Invoke(this, next);
    }

    private void UpdateStatusSummary()
    {
        var active = _queue.ActiveCount;
        var totalSpeed = _queue.TotalSpeedBytesPerSec;

        if (_state == ModuleState.Stopped && active == 0)
        {
            StatusSummary = "Stopped · Queue unloaded";
        }
        else if (active > 0)
        {
            var speedLabel = totalSpeed > 0 ? $" · ↓ {BandwidthMonitor.FormatRate(totalSpeed)}" : string.Empty;
            var prefix = _state == ModuleState.RunningHeadless ? "Headless" : "Downloading";
            StatusSummary = $"{prefix} · {active} active{(active == 1 ? "" : "s")}{speedLabel}";
        }
        else
        {
            StatusSummary = _state switch
            {
                ModuleState.RunningUI => "Ready · Paste URL to grab",
                ModuleState.RunningHeadless => "Headless · Idle",
                ModuleState.Paused => "Paused",
                _ => "Stopped"
            };
        }

        StatusSummaryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DisposeView()
    {
        if (_activeView is not null)
        {
            _activeView.DataContext = null;
            _activeView = null;
        }

        _activeViewModel?.Dispose();
        _activeViewModel = null;
    }
}
