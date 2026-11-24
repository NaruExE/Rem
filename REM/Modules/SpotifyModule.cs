using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using REM.Models;
using REM.Services;
using REM.ViewModels;
using REM.Views;

namespace REM.Modules;

public sealed class SpotifyModule : IAppModule
{
    private readonly DatabaseService _database;
    private readonly ActionLogService _actionLog;
    private readonly DialogService _dialogService;
    private readonly ImageAssetService _imageAssets;
    private readonly SpotifySoloistService _soloist;
    private readonly SpotifyWebApiService _webApi;
    private readonly LastFmService _lastFm;
    private readonly LrclibService _lrclib;

    private Control? _activeView;
    private SpotifyViewModel? _activeViewModel;
    private ModuleState _state = ModuleState.Stopped;
    private bool _disposed;

    public SpotifyModule(
        DatabaseService database,
        ActionLogService actionLog,
        DialogService dialogService,
        ImageAssetService imageAssets,
        SpotifySoloistService soloist,
        SpotifyWebApiService webApi,
        LastFmService lastFm,
        LrclibService lrclib)
    {
        _database = database;
        _actionLog = actionLog;
        _dialogService = dialogService;
        _imageAssets = imageAssets;
        _soloist = soloist;
        _webApi = webApi;
        _lastFm = lastFm;
        _lrclib = lrclib;

        _soloist.StatusChanged += OnSoloistStatusChanged;
        StatusSummary = "Stopped · Spotify Soloist unloaded";
    }

    public string Id => "spotify";
    public string Name => "Spotify";
    public string Description => "Spotify, but REM instead of RAM";
    public string IconGlyph => "S";
    public Geometry IconGeometry => IconCatalog.Spotify;
    public string AccentHex => "#1DB954";

    public ModuleState State => _state;
    public bool HasActiveView => _activeView is not null;
    public string StatusSummary { get; private set; }

    public Action<string>? OpenModuleSettingsRequested { get; set; }

    public event EventHandler<ModuleState>? StateChanged;
    public event EventHandler? StatusSummaryChanged;

    public void RefreshSettings()
    {
        _activeViewModel?.RefreshFromSettings();
    }

    public Control OpenUI()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_activeView is null || _activeViewModel is null)
        {
            _activeViewModel = new SpotifyViewModel(
                this,
                _database,
                _actionLog,
                _dialogService,
                _imageAssets,
                _soloist,
                _webApi,
                _lastFm,
                _lrclib,
                () => OpenModuleSettingsRequested?.Invoke(Id));

            _activeViewModel.IsHeaderVisible = _database.LoadSettings().ShowAppHeaderBar;

            _activeView = new SpotifyView
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
        _ = ResumeIfPausedForHeadlessAsync();
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
        _ = _soloist.PauseAsync();
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
        _soloist.StopDaemon();
        SetState(ModuleState.Stopped);
        StatusSummary = "Stopped · Soloist terminated";
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
        _soloist.StatusChanged -= OnSoloistStatusChanged;
        DisposeView();
        _soloist.StopDaemon();
        GC.SuppressFinalize(this);
    }

    internal void SetHeaderVisibility(bool visible)
    {
        if (_activeViewModel is not null)
        {
            _activeViewModel.IsHeaderVisible = visible;
        }
    }

    internal void AttachCloseCallback(Action closeToDashboard)
    {
        if (_activeViewModel is not null)
        {
            _activeViewModel.RequestCloseToDashboard = closeToDashboard;
        }
    }

    private async Task ResumeIfPausedForHeadlessAsync()
    {
        var status = _soloist.CurrentStatus;
        if (status is { Paused: true, Stopped: false })
        {
            await _soloist.PlayAsync().ConfigureAwait(false);
        }
    }

    private void OnSoloistStatusChanged(object? sender, GoLibrespotStatus? status)
    {
        if (_disposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(UpdateStatusSummary);
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
        var status = _soloist.CurrentStatus;
        var track = status?.Track;
        if (track is null || status is { Stopped: true })
        {
            StatusSummary = _state switch
            {
                ModuleState.RunningUI => _soloist.IsDaemonRunning ? "Soloist active · Ready to play" : "Starting Spotify Soloist...",
                ModuleState.RunningHeadless => "Headless · Soloist idle",
                ModuleState.Paused => "Paused",
                _ => "Stopped · Soloist terminated"
            };
        }
        else
        {
            var mode = _state == ModuleState.RunningHeadless ? "Headless" :
                       _state == ModuleState.Paused || status?.Paused == true ? "Paused" :
                       "Playing";
            StatusSummary = $"{mode} · {track.Name} — {track.ArtistsDisplay}";
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
