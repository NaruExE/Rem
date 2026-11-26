using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using REM.Services;
using REM.ViewModels;
using REM.Views;

namespace REM.Modules;

/// <summary>
/// Watch-Party Sync module enabling CGNAT-friendly Cloudflare Tunnel or Direct LAN/Radmin rooms
/// synced across REM's VLC Video Player + YouTube streams, with headless background sync.
/// </summary>
public sealed class WatchPartyModule : IAppModule, IHeaderConfigurableModule, ICloseableModule, IVlcOpenUiModule
{
    private readonly DatabaseService _database;
    private readonly VideoPlayerModule _videoPlayerModule;
    private readonly ActionLogService _actionLog;
    private readonly WatchPartyService _service;

    private Control? _activeView;
    private WatchPartyViewModel? _activeViewModel;
    private ModuleState _state = ModuleState.Stopped;
    private bool _disposed;

    public WatchPartyModule(
        DatabaseService database,
        VideoPlayerModule videoPlayerModule,
        ActionLogService actionLog)
    {
        _database = database;
        _videoPlayerModule = videoPlayerModule;
        _actionLog = actionLog;
        _service = new WatchPartyService(videoPlayerModule, actionLog);
        _service.RequestOpenVlcUi = RequestOpenVlcUi;

        _service.StateChanged += OnServiceUpdated;
        _service.PeersChanged += OnPeersUpdated;
        _service.StatusTextChanged += OnStatusUpdated;
        StatusSummary = "Stopped · Room disconnected";
    }

    public string Id => "watch-party";
    public string Name => "Watch Party Sync";
    public string Description => "Sync VLC & YouTube playback with friends (CGNAT-ready tunnel)";
    public string IconGlyph => "W";
    public Geometry IconGeometry => IconCatalog.Users;
    public string AccentHex => "#EC4899";

    public ModuleState State => _state;
    public bool HasActiveView => _activeView is not null;
    public string StatusSummary { get; private set; }

    public Action? OpenVlcUiRequested { get; set; }

    public event EventHandler<ModuleState>? StateChanged;
    public event EventHandler? StatusSummaryChanged;

    public Control OpenUI()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_activeView is null || _activeViewModel is null)
        {
            _activeViewModel = new WatchPartyViewModel(
                this,
                _database,
                _service,
                _videoPlayerModule.Engine,
                _actionLog);
            _activeViewModel.IsHeaderVisible = _database.LoadSettings().ShowAppHeaderBar;
            _activeView = new WatchPartyView
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
            _service.LeaveOrStopAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        GC.Collect(2, GCCollectionMode.Optimized);
        SetState(ModuleState.Stopped);
        StatusSummary = "Stopped · Tunnel & room closed";
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
        _service.StateChanged -= OnServiceUpdated;
        _service.PeersChanged -= OnPeersUpdated;
        _service.StatusTextChanged -= OnStatusUpdated;
        DisposeView();
        _service.Dispose();
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

    internal void RequestOpenVlcUi() => OpenVlcUiRequested?.Invoke();

    private void OnServiceUpdated(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            Dispatcher.UIThread.Post(UpdateStatusSummary);
        }
    }

    private void OnPeersUpdated(object? sender, IReadOnlyList<string> peers)
    {
        if (!_disposed)
        {
            Dispatcher.UIThread.Post(UpdateStatusSummary);
        }
    }

    private void OnStatusUpdated(object? sender, string status)
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
        if (_state == ModuleState.Stopped && !_service.IsConnected)
        {
            StatusSummary = "Stopped · Room disconnected";
        }
        else if (_service.IsConnected)
        {
            var peerCount = _service.GetPeerNames().Count;
            var prefix = _state == ModuleState.RunningHeadless ? "Headless" : (_service.Role == REM.Models.WatchPartyRole.Host ? "Hosting" : "Connected");
            StatusSummary = $"{prefix} · {peerCount} in room";
        }
        else
        {
            StatusSummary = _state switch
            {
                ModuleState.RunningUI => "Ready · Host or join a Watch Party",
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
