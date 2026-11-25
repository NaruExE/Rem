using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using REM.Models;
using REM.Services;
using REM.ViewModels;
using REM.Views;

namespace REM.Modules;

/// <summary>
/// Standalone "Compress &amp; Convert" FFmpeg Studio module supporting Discord target-size encoding,
/// lossless/re-encoded clipping, video/audio/image conversion, background headless runs, and clean teardown.
/// </summary>
public sealed class MediaStudioModule : IAppModule, IHeaderConfigurableModule, ICloseableModule, IVlcPlayMediaModule
{
    private readonly DatabaseService _database;
    private readonly YtDlpService _ytDlp;
    private readonly ActionLogService _actionLog;
    private readonly FfmpegStudioService _studio;

    private Control? _activeView;
    private MediaStudioViewModel? _activeViewModel;
    private ModuleState _state = ModuleState.Stopped;
    private bool _disposed;

    public MediaStudioModule(
        DatabaseService database,
        YtDlpService ytDlp,
        ActionLogService actionLog)
    {
        _database = database;
        _ytDlp = ytDlp;
        _actionLog = actionLog;
        _studio = new FfmpegStudioService(database, ytDlp, actionLog);

        _studio.JobsChanged += OnJobsChanged;
        _studio.JobUpdated += OnJobUpdated;
        StatusSummary = "Stopped · FFmpeg unloaded";
    }

    public string Id => "media-studio";
    public string Name => "Compress & Convert";
    public string Description => "FFmpeg compressor (Discord MB), clipper & format converter";
    public string IconGlyph => "C";
    public Geometry IconGeometry => IconCatalog.Scissors;
    public string AccentHex => "#10B981";

    public ModuleState State => _state;
    public bool HasActiveView => _activeView is not null;
    public string StatusSummary { get; private set; }

    public Action<string>? PlayCompletedMediaInVlcRequested { get; set; }

    public event EventHandler<ModuleState>? StateChanged;
    public event EventHandler? StatusSummaryChanged;

    public Control OpenUI()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_activeView is null || _activeViewModel is null)
        {
            _activeViewModel = new MediaStudioViewModel(this, _database, _studio, _actionLog);
            _activeViewModel.IsHeaderVisible = _database.LoadSettings().ShowAppHeaderBar;
            _activeView = new MediaStudioView
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
            _studio.ShutdownAllAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        GC.Collect(2, GCCollectionMode.Optimized);
        SetState(ModuleState.Stopped);
        StatusSummary = "Stopped · FFmpeg terminated";
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
        _studio.JobsChanged -= OnJobsChanged;
        _studio.JobUpdated -= OnJobUpdated;
        DisposeView();
        _studio.Dispose();
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

    private void OnJobsChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            Dispatcher.UIThread.Post(UpdateStatusSummary);
        }
    }

    private void OnJobUpdated(object? sender, MediaStudioJobState job)
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
        var active = _studio.ActiveCount;
        if (_state == ModuleState.Stopped && active == 0)
        {
            StatusSummary = "Stopped · FFmpeg unloaded";
        }
        else if (active > 0)
        {
            var prefix = _state == ModuleState.RunningHeadless ? "Headless" : "Encoding";
            StatusSummary = $"{prefix} · {active} active job{(active == 1 ? "" : "s")}";
        }
        else
        {
            StatusSummary = _state switch
            {
                ModuleState.RunningUI => "Ready · Drop media to compress or convert",
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
