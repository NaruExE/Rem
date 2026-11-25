using Avalonia.Controls;
using Avalonia.Media;
using REM.Controls;
using REM.Services;
using REM.ViewModels;
using REM.Views;

namespace REM.Modules;

/// <summary>
/// Standalone Movies & TV module integrating TMDB v3 catalog/details,
/// profile-scoped watchlist & continue-watching SQLite history, and a hardened lazy WebView2 host.
/// </summary>
public sealed class MoviesTvModule : IAppModule, IHeaderConfigurableModule, ICloseableModule, IFullscreenModule
{
    private readonly DatabaseService _database;
    private readonly TmdbService _tmdb;
    private readonly DialogService _dialogService;
    private readonly ActionLogService _actionLog;

    private Control? _activeView;
    private MoviesTvViewModel? _activeViewModel;
    private Action<bool>? _onFullscreenChanged;
    private WebView2Host? _webViewHost;
    private ModuleState _state = ModuleState.Stopped;
    private long _activeProfileId;
    private bool _disposed;

    public MoviesTvModule(
        DatabaseService database,
        TmdbService tmdb,
        DialogService dialogService,
        ActionLogService actionLog)
    {
        _database = database;
        _tmdb = tmdb;
        _dialogService = dialogService;
        _actionLog = actionLog;

        StatusSummary = "Stopped · WebView2 unloaded";
    }

    public string Id => "movies-tv";
    public string Name => "Movies & TV";
    public string Description => "TMDB catalog, watchlist & hardened WebView2 player";
    public string IconGlyph => "M";
    public Geometry IconGeometry => IconCatalog.MoviesTv;
    public string AccentHex => "#E50914";

    public ModuleState State => _state;
    public bool HasActiveView => _activeView is not null;
    public string StatusSummary { get; private set; }

    public TmdbService Tmdb => _tmdb;
    public DatabaseService Database => _database;
    public long ActiveProfileId => _activeProfileId;

    public event EventHandler<ModuleState>? StateChanged;
    public event EventHandler? StatusSummaryChanged;

    public void SetActiveProfile(long profileId)
    {
        if (_activeProfileId != profileId)
        {
            _activeProfileId = profileId;
            _activeViewModel?.SetProfile(profileId);
        }
    }

    public void RegisterWebViewHost(WebView2Host? host)
    {
        _webViewHost = host;
    }

    public async Task<string?> BuildMovieEmbedUrlAsync(
        int tmdbId,
        string? imdbId,
        CancellationToken cancellationToken = default)
    {
        var settings = await _database.GetMoviesTvSettingsAsync(cancellationToken).ConfigureAwait(false);
        return TmdbService.BuildEmbedUrl(settings.MovieEmbedTemplate, tmdbId, imdbId);
    }

    public async Task<string?> BuildTvEmbedUrlAsync(
        int tmdbId,
        string? imdbId,
        int season,
        int episode,
        CancellationToken cancellationToken = default)
    {
        var settings = await _database.GetMoviesTvSettingsAsync(cancellationToken).ConfigureAwait(false);
        return TmdbService.BuildEmbedUrl(settings.TvEmbedTemplate, tmdbId, imdbId, season, episode);
    }

    public Control OpenUI()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_activeView is null || _activeViewModel is null)
        {
            _activeViewModel = new MoviesTvViewModel(
                this,
                _activeProfileId,
                _database,
                _tmdb,
                _dialogService,
                _actionLog);

            _activeViewModel.IsHeaderVisible = _database.LoadSettings().ShowAppHeaderBar;
            _activeViewModel.FullscreenChanged += OnViewModelFullscreenChanged;

            _activeView = new MoviesTvView
            {
                DataContext = _activeViewModel
            };
        }

        SetState(ModuleState.RunningUI);
        UpdateStatusSummary();
        return _activeView;
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

    public void AttachFullscreenCallback(Action<bool> onFullscreenChanged)
    {
        _onFullscreenChanged = onFullscreenChanged;
    }

    private void OnViewModelFullscreenChanged(object? sender, bool isFullscreen) =>
        _onFullscreenChanged?.Invoke(isFullscreen);

    public void RunHeadless()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        DisposeView();
        ShutdownWebViewHost();
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
        ShutdownWebViewHost();
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
        ShutdownWebViewHost();
        _tmdb.ClearPosterCache();

        GC.Collect(2, GCCollectionMode.Optimized);
        GC.WaitForPendingFinalizers();
        MemoryTrimService.ScheduleTrim();

        SetState(ModuleState.Stopped);
        StatusSummary = "Stopped · WebView2 & posters disposed";
        StatusSummaryChanged?.Invoke(this, EventArgs.Empty);
        _actionLog.Log("MoviesTV", "Killed Movies & TV module and cleared poster/WebView2 memory.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeView();
        ShutdownWebViewHost();
        _tmdb.ClearPosterCache();
        GC.SuppressFinalize(this);
    }

    private void ShutdownWebViewHost()
    {
        var host = _webViewHost;
        _webViewHost = null;
        if (host is null)
        {
            return;
        }

        try
        {
            host.Shutdown();
        }
        catch
        {
            // Ignore WebView2 shutdown race during teardown.
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
        StatusSummary = _state switch
        {
            ModuleState.RunningUI => "Ready · TMDB & WebView2 backend loaded",
            ModuleState.RunningHeadless => "Headless · WebView2 unloaded",
            ModuleState.Paused => "Paused · WebView2 unloaded",
            _ => "Stopped · WebView2 unloaded"
        };

        StatusSummaryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DisposeView()
    {
        if (_activeViewModel is not null)
        {
            _activeViewModel.FullscreenChanged -= OnViewModelFullscreenChanged;
        }

        _onFullscreenChanged?.Invoke(false);

        if (_activeView is MoviesTvView moviesView)
        {
            moviesView.ShutdownHost();
        }

        if (_activeView is not null)
        {
            _activeView.DataContext = null;
            _activeView = null;
        }

        _activeViewModel?.Dispose();
        _activeViewModel = null;
    }
}
