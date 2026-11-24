using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using REM.Controls;
using REM.Models;
using REM.Modules;
using REM.Services;

namespace REM.ViewModels;

public enum MoviesTvTab
{
    Home,
    Movies,
    TvShows,
    Watchlist,
    Search
}

public enum MoviesTvScreen
{
    Browse,
    Detail,
    Player
}

public sealed partial class MoviePosterCardViewModel : ObservableObject
{
    private readonly Action<MoviePosterCardViewModel> _onOpenDetails;
    private readonly Action<MoviePosterCardViewModel> _onToggleWatchlist;

    public MoviePosterCardViewModel(
        int tmdbId,
        string mediaType,
        string title,
        string? posterPath,
        string? year,
        double rating,
        bool isInWatchlist,
        Action<MoviePosterCardViewModel> onOpenDetails,
        Action<MoviePosterCardViewModel> onToggleWatchlist)
    {
        TmdbId = tmdbId;
        MediaType = mediaType.Trim().ToLowerInvariant();
        Title = title;
        PosterPath = posterPath;
        Year = year;
        Rating = rating;
        _isInWatchlist = isInWatchlist;
        _onOpenDetails = onOpenDetails;
        _onToggleWatchlist = onToggleWatchlist;
    }

    public int TmdbId { get; }
    public string MediaType { get; }
    public string Title { get; }
    public string? PosterPath { get; }
    public string? Year { get; }
    public bool HasYear => !string.IsNullOrWhiteSpace(Year);
    public double Rating { get; }
    public bool HasRating => Rating > 0.05;
    public string RatingText => Rating > 0.05 ? Rating.ToString("0.0", CultureInfo.InvariantCulture) : string.Empty;
    public string MediaTypeBadge => string.Equals(MediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "TV" : "MOVIE";

    [ObservableProperty]
    private bool _isInWatchlist;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPosterBitmap))]
    private Bitmap? _posterBitmap;

    public bool HasPosterBitmap => PosterBitmap is not null;

    [RelayCommand]
    private void OpenDetails() => _onOpenDetails(this);

    [RelayCommand]
    private void ToggleWatchlist() => _onToggleWatchlist(this);
}

public sealed partial class ContinueWatchingCardViewModel : ObservableObject
{
    private readonly Action<ContinueWatchingCardViewModel> _onResumePlay;
    private readonly Action<ContinueWatchingCardViewModel> _onOpenDetails;
    private readonly Action<ContinueWatchingCardViewModel> _onRemove;

    public ContinueWatchingCardViewModel(
        MovieHistoryItem item,
        Action<ContinueWatchingCardViewModel> onResumePlay,
        Action<ContinueWatchingCardViewModel> onOpenDetails,
        Action<ContinueWatchingCardViewModel> onRemove)
    {
        Item = item;
        _onResumePlay = onResumePlay;
        _onOpenDetails = onOpenDetails;
        _onRemove = onRemove;
    }

    public MovieHistoryItem Item { get; }
    public int TmdbId => Item.TmdbId;
    public string MediaType => Item.MediaType;
    public string Title => Item.Title;
    public string? PosterPath => Item.PosterPath;

    public string BadgeText =>
        string.Equals(Item.MediaType, "tv", StringComparison.OrdinalIgnoreCase) &&
        Item.LastSeason > 0 &&
        Item.LastEpisode > 0
            ? $"S{Item.LastSeason:D2} E{Item.LastEpisode:D2}"
            : "MOVIE";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPosterBitmap))]
    private Bitmap? _posterBitmap;

    public bool HasPosterBitmap => PosterBitmap is not null;

    [RelayCommand]
    private void ResumePlay() => _onResumePlay(this);

    [RelayCommand]
    private void OpenDetails() => _onOpenDetails(this);

    [RelayCommand]
    private void Remove() => _onRemove(this);
}

public sealed partial class TvEpisodeRowViewModel : ObservableObject
{
    private readonly Action<TvEpisodeRowViewModel> _onPlay;

    public TvEpisodeRowViewModel(
        TmdbEpisode episode,
        bool isLastWatched,
        Action<TvEpisodeRowViewModel> onPlay)
    {
        Episode = episode;
        _isLastWatched = isLastWatched;
        _onPlay = onPlay;

        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(episode.AirDate))
        {
            parts.Add(episode.AirDate);
        }

        if (episode.RuntimeMinutes is > 0)
        {
            parts.Add($"{episode.RuntimeMinutes} min");
        }

        if (episode.Rating > 0.05)
        {
            parts.Add($"★ {episode.Rating.ToString("0.0", CultureInfo.InvariantCulture)}");
        }

        MetaText = string.Join(" · ", parts);
    }

    public TmdbEpisode Episode { get; }
    public string EpisodeCode => $"E{Episode.EpisodeNumber:D2}";
    public string Title => Episode.Name;
    public string? Overview => Episode.Overview;
    public bool HasOverview => !string.IsNullOrWhiteSpace(Episode.Overview);
    public string MetaText { get; }
    public bool HasMetaText => !string.IsNullOrWhiteSpace(MetaText);

    [ObservableProperty]
    private bool _isLastWatched;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStillBitmap))]
    private Bitmap? _stillBitmap;

    public bool HasStillBitmap => StillBitmap is not null;

    [RelayCommand]
    private void PlayEpisode() => _onPlay(this);
}

public sealed partial class MoviesTvViewModel : ObservableObject, IDisposable
{
    private readonly MoviesTvModule _module;
    private readonly DatabaseService _database;
    private readonly TmdbService _tmdb;
    private readonly DialogService _dialogService;
    private readonly ActionLogService _actionLog;
    private readonly SemaphoreSlim _posterGate = new(4, 4);

    private long _profileId;
    private int _currentCatalogPage = 1;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _posterCts;
    private CancellationTokenSource? _toastCts;
    private HashSet<(int TmdbId, string MediaType)> _watchlistKeys = [];
    private MoviesTvScreen _screenBeforePlayer = MoviesTvScreen.Browse;
    private TmdbMovieDetails? _loadedMovieDetails;
    private TmdbTvDetails? _loadedTvDetails;
    private IReadOnlyList<TmdbEpisode> _activeSeasonEpisodes = [];
    private int _playerSeason;
    private int _playerEpisode;
    private int _blockedCount;
    private bool _syncingSeason;
    private bool _disposed;

    public MoviesTvViewModel(
        MoviesTvModule module,
        long profileId,
        DatabaseService database,
        TmdbService tmdb,
        DialogService dialogService,
        ActionLogService actionLog)
    {
        _module = module;
        _profileId = profileId;
        _database = database;
        _tmdb = tmdb;
        _dialogService = dialogService;
        _actionLog = actionLog;

        AccentBrush = new SolidColorBrush(Color.Parse(module.AccentHex));

        _module.StateChanged += OnModuleStateChanged;
        _module.StatusSummaryChanged += OnModuleStatusSummaryChanged;

        SyncFromModule();
        _ = LoadBrowseAsync(MoviesTvTab.Home);
    }

    public string Name => _module.Name;
    public IBrush AccentBrush { get; }
    public Geometry IconGeometry => _module.IconGeometry;

    public Action? RequestCloseToDashboard { get; set; }
    public event EventHandler<bool>? FullscreenChanged;
    public Func<string, IReadOnlyList<string>, Task>? NavigateWebViewRequested { get; set; }
    public Action? ShutdownWebViewRequested { get; set; }

    public ObservableCollection<ContinueWatchingCardViewModel> ContinueWatchingItems { get; } = [];
    public ObservableCollection<MoviePosterCardViewModel> CatalogItems { get; } = [];
    public ObservableCollection<TmdbSeasonSummary> DetailSeasons { get; } = [];
    public ObservableCollection<TvEpisodeRowViewModel> DetailEpisodes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowModuleHeader))]
    private bool _isHeaderVisible = true;

    [ObservableProperty]
    private string _stateLabel = "Running (UI)";

    [ObservableProperty]
    private string _statusSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeTab))]
    [NotifyPropertyChangedFor(nameof(IsMoviesTab))]
    [NotifyPropertyChangedFor(nameof(IsTvTab))]
    [NotifyPropertyChangedFor(nameof(IsWatchlistTab))]
    [NotifyPropertyChangedFor(nameof(IsSearchTab))]
    [NotifyPropertyChangedFor(nameof(ShowContinueWatching))]
    private MoviesTvTab _activeTab = MoviesTvTab.Home;

    public bool IsHomeTab => ActiveTab == MoviesTvTab.Home;
    public bool IsMoviesTab => ActiveTab == MoviesTvTab.Movies;
    public bool IsTvTab => ActiveTab == MoviesTvTab.TvShows;
    public bool IsWatchlistTab => ActiveTab == MoviesTvTab.Watchlist;
    public bool IsSearchTab => ActiveTab == MoviesTvTab.Search;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowseView))]
    [NotifyPropertyChangedFor(nameof(IsDetailView))]
    [NotifyPropertyChangedFor(nameof(IsPlayerView))]
    [NotifyPropertyChangedFor(nameof(ShowModuleHeader))]
    [NotifyPropertyChangedFor(nameof(ShowTopBar))]
    private MoviesTvScreen _activeScreen = MoviesTvScreen.Browse;

    public bool IsBrowseView => ActiveScreen == MoviesTvScreen.Browse;
    public bool IsDetailView => ActiveScreen == MoviesTvScreen.Detail;
    public bool IsPlayerView => ActiveScreen == MoviesTvScreen.Player;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowModuleHeader))]
    private bool _isFullscreen;

    public bool ShowModuleHeader => IsHeaderVisible && !IsPlayerView && !IsFullscreen;
    public bool ShowTopBar => !IsPlayerView && !IsFullscreen;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private string _sectionTitle = "Trending This Week";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyMessage))]
    private string _emptyMessage = string.Empty;

    public bool HasEmptyMessage => !string.IsNullOrWhiteSpace(EmptyMessage) && !IsLoading;

    [ObservableProperty]
    private bool _needsConfiguration;

    [ObservableProperty]
    private bool _canShowMoreCatalog;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMoreCatalogLabel))]
    private bool _isLoadingMoreCatalog;

    public string ShowMoreCatalogLabel => IsLoadingMoreCatalog ? "Loading more..." : "Show More";

    public bool HasContinueWatching => ContinueWatchingItems.Count > 0;
    public bool ShowContinueWatching => IsHomeTab && HasContinueWatching;
    public bool HasCatalogItems => CatalogItems.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusToast))]
    private string _statusToast = string.Empty;

    public bool HasStatusToast => !string.IsNullOrWhiteSpace(StatusToast);

    // Detail View State
    [ObservableProperty]
    private string _detailTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetailTagline))]
    private string _detailTagline = string.Empty;

    public bool HasDetailTagline => !string.IsNullOrWhiteSpace(DetailTagline);

    [ObservableProperty]
    private string _detailOverview = string.Empty;

    [ObservableProperty]
    private string _detailMetaLine = string.Empty;

    [ObservableProperty]
    private string _detailRatingText = string.Empty;

    [ObservableProperty]
    private string _detailGenresText = string.Empty;

    [ObservableProperty]
    private bool _detailIsInWatchlist;

    [ObservableProperty]
    private bool _isDetailTvShow;

    [ObservableProperty]
    private string _detailPlayButtonText = "Play Movie";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetailBackdrop))]
    private Bitmap? _detailBackdropBitmap;

    public bool HasDetailBackdrop => DetailBackdropBitmap is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetailPoster))]
    private Bitmap? _detailPosterBitmap;

    public bool HasDetailPoster => DetailPosterBitmap is not null;

    [ObservableProperty]
    private TmdbSeasonSummary? _selectedSeason;

    partial void OnSelectedSeasonChanged(TmdbSeasonSummary? value)
    {
        if (_syncingSeason || value is null || _loadedTvDetails is null || _disposed)
        {
            return;
        }

        _ = LoadSeasonEpisodesAsync(_loadedTvDetails.TmdbId, value.SeasonNumber);
    }

    private int _detailLastSeason = 1;
    private int _detailLastEpisode = 1;

    // Player View State
    [ObservableProperty]
    private string _playerTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlayerEpisodeLabel))]
    private string _playerEpisodeLabel = string.Empty;

    public bool HasPlayerEpisodeLabel => !string.IsNullOrWhiteSpace(PlayerEpisodeLabel);

    [ObservableProperty]
    private bool _isPlayerTvShow;

    [ObservableProperty]
    private bool _canGoPrevEpisode;

    [ObservableProperty]
    private bool _canGoNextEpisode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlockedAttempts))]
    private string _playerBlockedCountText = string.Empty;

    public bool HasBlockedAttempts => !string.IsNullOrWhiteSpace(PlayerBlockedCountText);

    private string? _currentPlayerUrl;

    // Settings Modal State
    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private string _settingsTmdbToken = string.Empty;

    [ObservableProperty]
    private string _settingsMovieTemplate = string.Empty;

    [ObservableProperty]
    private string _settingsTvTemplate = string.Empty;

    [ObservableProperty]
    private string _settingsExtraBlockedDomains = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSettingsHint))]
    private string _settingsHintMessage = string.Empty;

    public bool HasSettingsHint => !string.IsNullOrWhiteSpace(SettingsHintMessage);

    public void RegisterHost(WebView2Host? host) => _module.RegisterWebViewHost(host);

    public void SetProfile(long profileId)
    {
        if (_profileId == profileId)
        {
            return;
        }

        _profileId = profileId;
        ShutdownWebView();
        ExitFullscreen();
        ActiveScreen = MoviesTvScreen.Browse;
        _ = LoadBrowseAsync(MoviesTvTab.Home);
    }

    public void ReportBlockedAttempt(string reason)
    {
        if (_disposed)
        {
            return;
        }

        _blockedCount++;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                PlayerBlockedCountText = _blockedCount == 1
                    ? "1 popup/ad blocked"
                    : $"{_blockedCount} popups/ads blocked";
            }
        });
    }

    [RelayCommand]
    private void MinimizeToHeadless() => _module.RunHeadless();

    [RelayCommand]
    private void PauseModule() => _module.Pause();

    [RelayCommand]
    private void Kill() => _module.Kill();

    [RelayCommand]
    private void BackToDashboard() => RequestCloseToDashboard?.Invoke();

    [RelayCommand]
    private void ToggleHeader() => IsHeaderVisible = !IsHeaderVisible;

    [RelayCommand]
    private Task SelectHomeTabAsync() => LoadBrowseAsync(MoviesTvTab.Home);

    [RelayCommand]
    private Task SelectMoviesTabAsync() => LoadBrowseAsync(MoviesTvTab.Movies);

    [RelayCommand]
    private Task SelectTvTabAsync() => LoadBrowseAsync(MoviesTvTab.TvShows);

    [RelayCommand]
    private Task SelectWatchlistTabAsync() => LoadBrowseAsync(MoviesTvTab.Watchlist);

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            await LoadBrowseAsync(MoviesTvTab.Home);
            return;
        }

        await LoadBrowseAsync(MoviesTvTab.Search);
    }

    [RelayCommand]
    private Task ClearSearchAsync()
    {
        SearchQuery = string.Empty;
        return LoadBrowseAsync(MoviesTvTab.Home);
    }

    [RelayCommand]
    private void BackToBrowse()
    {
        ClearDetailBitmaps();
        _tmdb.ClearPosterCache();
        ActiveScreen = MoviesTvScreen.Browse;
        _ = LoadBrowseAsync(ActiveTab);
    }

    [RelayCommand]
    private async Task ToggleDetailWatchlistAsync()
    {
        if (_profileId <= 0)
        {
            return;
        }

        if (_loadedMovieDetails is { } movie)
        {
            await ToggleWatchlistEntryAsync(
                movie.TmdbId,
                movie.ImdbId,
                "movie",
                movie.Title,
                movie.PosterPath,
                movie.ReleaseDate is { Length: >= 4 } ? movie.ReleaseDate[..4] : null,
                movie.Rating);
        }
        else if (_loadedTvDetails is { } tv)
        {
            await ToggleWatchlistEntryAsync(
                tv.TmdbId,
                tv.ImdbId,
                "tv",
                tv.Title,
                tv.PosterPath,
                tv.FirstAirDate is { Length: >= 4 } ? tv.FirstAirDate[..4] : null,
                tv.Rating);
        }
    }

    [RelayCommand]
    private async Task PlayDetailAsync()
    {
        if (_loadedMovieDetails is { } movie)
        {
            await StartMoviePlaybackAsync(movie);
        }
        else if (_loadedTvDetails is { } tv)
        {
            var season = SelectedSeason?.SeasonNumber ?? _detailLastSeason;
            var episode = _detailLastEpisode > 0 ? _detailLastEpisode : 1;
            await StartTvEpisodePlaybackAsync(tv, season > 0 ? season : 1, episode);
        }
    }

    [RelayCommand]
    private void ClosePlayer()
    {
        ShutdownWebView();
        ExitFullscreen();
        ActiveScreen = _screenBeforePlayer == MoviesTvScreen.Detail && (_loadedMovieDetails is not null || _loadedTvDetails is not null)
            ? MoviesTvScreen.Detail
            : MoviesTvScreen.Browse;

        if (ActiveScreen == MoviesTvScreen.Browse)
        {
            _ = LoadBrowseAsync(ActiveTab);
        }
        else
        {
            _ = ReloadDetailImagesAsync();
        }
    }

    [RelayCommand]
    private async Task PrevEpisodeAsync()
    {
        if (!IsPlayerTvShow || _loadedTvDetails is null || _activeSeasonEpisodes.Count == 0)
        {
            return;
        }

        var idx = FindEpisodeIndex(_playerEpisode);
        if (idx > 0)
        {
            await StartTvEpisodePlaybackAsync(_loadedTvDetails, _playerSeason, _activeSeasonEpisodes[idx - 1].EpisodeNumber);
        }
    }

    [RelayCommand]
    private async Task NextEpisodeAsync()
    {
        if (!IsPlayerTvShow || _loadedTvDetails is null || _activeSeasonEpisodes.Count == 0)
        {
            return;
        }

        var idx = FindEpisodeIndex(_playerEpisode);
        if (idx >= 0 && idx + 1 < _activeSeasonEpisodes.Count)
        {
            await StartTvEpisodePlaybackAsync(_loadedTvDetails, _playerSeason, _activeSeasonEpisodes[idx + 1].EpisodeNumber);
        }
    }

    [RelayCommand]
    private async Task ReloadPlayerAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentPlayerUrl) || NavigateWebViewRequested is null)
        {
            return;
        }

        var settings = await _database.GetMoviesTvSettingsAsync();
        await NavigateWebViewRequested(_currentPlayerUrl, settings.ExtraBlockedDomains);
    }

    [RelayCommand]
    private void ToggleFullscreen()
    {
        IsFullscreen = !IsFullscreen;
        FullscreenChanged?.Invoke(this, IsFullscreen);
    }

    [RelayCommand]
    public void ExitFullscreen()
    {
        if (!IsFullscreen)
        {
            return;
        }

        IsFullscreen = false;
        FullscreenChanged?.Invoke(this, false);
    }

    [RelayCommand]
    private Task OpenSettingsModalAsync() => OpenSettingsModalWithHintAsync(string.Empty);

    [RelayCommand]
    private void CloseSettingsModal()
    {
        IsSettingsOpen = false;
        SettingsHintMessage = string.Empty;
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        await _database.SaveMoviesTvSettingsAsync(
            SettingsTmdbToken,
            SettingsMovieTemplate,
            SettingsTvTemplate,
            SettingsExtraBlockedDomains);

        IsSettingsOpen = false;
        SettingsHintMessage = string.Empty;
        ShowToast("Movies & TV settings saved.");

        if (ActiveScreen == MoviesTvScreen.Browse)
        {
            await LoadBrowseAsync(ActiveTab);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelTokenSource(ref _loadCts);
        CancelTokenSource(ref _posterCts);
        CancelTokenSource(ref _toastCts);

        _module.StateChanged -= OnModuleStateChanged;
        _module.StatusSummaryChanged -= OnModuleStatusSummaryChanged;

        ShutdownWebView();
        ClearAllBitmaps();
        _tmdb.ClearPosterCache();
        _posterGate.Dispose();
    }

    private async Task OpenSettingsModalWithHintAsync(string hint)
    {
        if (IsPlayerView)
        {
            ShutdownWebView();
            ExitFullscreen();
            ActiveScreen = MoviesTvScreen.Browse;
        }

        var settings = await _database.GetMoviesTvSettingsAsync();
        var rawBlocked = await _database.GetSettingAsync(DatabaseService.KeyMoviesBlockedAdDomains);

        SettingsTmdbToken = settings.TmdbBearerToken;
        SettingsMovieTemplate = settings.MovieEmbedTemplate;
        SettingsTvTemplate = settings.TvEmbedTemplate;
        SettingsExtraBlockedDomains = rawBlocked ?? string.Empty;
        SettingsHintMessage = hint;
        IsSettingsOpen = true;
    }

    private async Task LoadBrowseAsync(MoviesTvTab tab)
    {
        if (_disposed)
        {
            return;
        }

        ShutdownWebView();
        ExitFullscreen();
        ActiveTab = tab;
        ActiveScreen = MoviesTvScreen.Browse;

        var cts = ReplaceTokenSource(ref _loadCts);
        CancelTokenSource(ref _posterCts);
        var posterCts = ReplaceTokenSource(ref _posterCts);

        ClearAllBitmaps();
        _tmdb.ClearPosterCache();

        IsLoading = true;
        EmptyMessage = string.Empty;
        NeedsConfiguration = false;
        CanShowMoreCatalog = false;
        _currentCatalogPage = 1;

        try
        {
            var settings = await _database.GetMoviesTvSettingsAsync(cts.Token);
            var watchlist = await _database.GetMovieWatchlistAsync(_profileId, cts.Token);
            _watchlistKeys = watchlist
                .Select(w => (w.TmdbId, w.MediaType.ToLowerInvariant()))
                .ToHashSet();

            var history = tab == MoviesTvTab.Home
                ? await _database.GetMovieHistoryAsync(_profileId, 20, cts.Token)
                : [];

            ContinueWatchingItems.Clear();
            foreach (var h in history)
            {
                ContinueWatchingItems.Add(new ContinueWatchingCardViewModel(
                    h,
                    card => _ = ResumeContinueWatchingAsync(card),
                    card => _ = OpenDetailsAsync(card.TmdbId, card.MediaType),
                    card => _ = RemoveContinueWatchingAsync(card)));
            }

            OnPropertyChanged(nameof(HasContinueWatching));
            OnPropertyChanged(nameof(ShowContinueWatching));

            CatalogItems.Clear();

            if (tab == MoviesTvTab.Watchlist)
            {
                SectionTitle = "Your Watchlist";
                foreach (var w in watchlist)
                {
                    CatalogItems.Add(new MoviePosterCardViewModel(
                        w.TmdbId,
                        w.MediaType,
                        w.Title,
                        w.PosterPath,
                        w.Year,
                        w.Rating,
                        isInWatchlist: true,
                        card => _ = OpenDetailsAsync(card.TmdbId, card.MediaType),
                        card => _ = ToggleCardWatchlistAsync(card)));
                }

                EmptyMessage = CatalogItems.Count == 0
                    ? "Your watchlist is empty. Bookmark any movie or TV show to save it here."
                    : string.Empty;
            }
            else if (string.IsNullOrWhiteSpace(settings.TmdbBearerToken))
            {
                SectionTitle = tab switch
                {
                    MoviesTvTab.Movies => "Trending Movies",
                    MoviesTvTab.TvShows => "Trending TV Shows",
                    MoviesTvTab.Search => $"Search: {SearchQuery.Trim()}",
                    _ => "Trending This Week"
                };
                NeedsConfiguration = true;
                EmptyMessage = "Add your TMDB Read Access Token in Settings to browse trending movies, TV shows, and search results.";
            }
            else
            {
                TmdbMediaPage pageResult;
                switch (tab)
                {
                    case MoviesTvTab.Movies:
                        SectionTitle = "Trending Movies";
                        pageResult = await _tmdb.GetTrendingMoviesPageAsync(1, cts.Token);
                        break;
                    case MoviesTvTab.TvShows:
                        SectionTitle = "Trending TV Shows";
                        pageResult = await _tmdb.GetTrendingTvPageAsync(1, cts.Token);
                        break;
                    case MoviesTvTab.Search:
                        SectionTitle = $"Search: \"{SearchQuery.Trim()}\"";
                        pageResult = await _tmdb.SearchMultiPageAsync(SearchQuery, 1, cts.Token);
                        break;
                    default:
                        SectionTitle = "Trending This Week";
                        pageResult = await _tmdb.GetTrendingAllPageAsync(1, cts.Token);
                        break;
                }

                foreach (var m in pageResult.Items)
                {
                    var key = (m.TmdbId, m.MediaType.ToLowerInvariant());
                    CatalogItems.Add(new MoviePosterCardViewModel(
                        m.TmdbId,
                        m.MediaType,
                        m.Title,
                        m.PosterPath,
                        m.ReleaseYear,
                        m.Rating,
                        _watchlistKeys.Contains(key),
                        card => _ = OpenDetailsAsync(card.TmdbId, card.MediaType),
                        card => _ = ToggleCardWatchlistAsync(card)));
                }

                _currentCatalogPage = pageResult.Page;
                CanShowMoreCatalog = pageResult.HasMore;

                if (CatalogItems.Count == 0)
                {
                    EmptyMessage = tab == MoviesTvTab.Search
                        ? $"No movies or TV shows matched \"{SearchQuery.Trim()}\"."
                        : "No results returned by TMDB.";
                }
            }

            OnPropertyChanged(nameof(HasCatalogItems));
            IsLoading = false;
            OnPropertyChanged(nameof(HasEmptyMessage));

            _ = LoadBrowsePostersAsync(posterCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            IsLoading = false;
            EmptyMessage = ex.Message;
            OnPropertyChanged(nameof(HasEmptyMessage));
            _actionLog.Log("Warning", $"Movies & TV browse failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ShowMoreCatalogAsync()
    {
        if (_disposed ||
            IsLoading ||
            IsLoadingMoreCatalog ||
            !CanShowMoreCatalog ||
            ActiveTab == MoviesTvTab.Watchlist)
        {
            return;
        }

        var cts = ReplaceTokenSource(ref _loadCts);
        var posterToken = GetOrCreatePosterToken();
        IsLoadingMoreCatalog = true;

        try
        {
            var existingKeys = CatalogItems
                .Select(c => (c.TmdbId, c.MediaType.ToLowerInvariant()))
                .ToHashSet();

            var addedCards = new List<MoviePosterCardViewModel>();
            var hasMore = true;

            for (var attempt = 0; attempt < 3 && addedCards.Count == 0 && hasMore; attempt++)
            {
                var nextPage = _currentCatalogPage + 1;
                var pageResult = ActiveTab switch
                {
                    MoviesTvTab.Movies => await _tmdb.GetTrendingMoviesPageAsync(nextPage, cts.Token),
                    MoviesTvTab.TvShows => await _tmdb.GetTrendingTvPageAsync(nextPage, cts.Token),
                    MoviesTvTab.Search => await _tmdb.SearchMultiPageAsync(SearchQuery, nextPage, cts.Token),
                    _ => await _tmdb.GetTrendingAllPageAsync(nextPage, cts.Token)
                };

                foreach (var m in pageResult.Items)
                {
                    var key = (m.TmdbId, m.MediaType.ToLowerInvariant());
                    if (!existingKeys.Add(key))
                    {
                        continue;
                    }

                    var card = new MoviePosterCardViewModel(
                        m.TmdbId,
                        m.MediaType,
                        m.Title,
                        m.PosterPath,
                        m.ReleaseYear,
                        m.Rating,
                        _watchlistKeys.Contains(key),
                        c => _ = OpenDetailsAsync(c.TmdbId, c.MediaType),
                        c => _ = ToggleCardWatchlistAsync(c));

                    CatalogItems.Add(card);
                    addedCards.Add(card);
                }

                _currentCatalogPage = pageResult.Page;
                hasMore = pageResult.HasMore;
            }

            CanShowMoreCatalog = hasMore;
            OnPropertyChanged(nameof(HasCatalogItems));

            if (addedCards.Count > 0)
            {
                _ = LoadCardPostersAsync(addedCards, posterToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowToast($"Could not load more: {ex.Message}");
            _actionLog.Log("Warning", $"Movies & TV show more failed: {ex.Message}");
        }
        finally
        {
            if (!_disposed)
            {
                IsLoadingMoreCatalog = false;
            }
        }
    }

    private async Task LoadCardPostersAsync(IReadOnlyList<MoviePosterCardViewModel> cards, CancellationToken ct)
    {
        var tasks = new List<Task>(cards.Count);
        foreach (var card in cards)
        {
            if (string.IsNullOrWhiteSpace(card.PosterPath))
            {
                continue;
            }

            tasks.Add(LoadPosterIntoAsync(card.PosterPath, 240, bmp => card.PosterBitmap = bmp, ct));
        }

        await Task.WhenAll(tasks);
    }

    private CancellationToken GetOrCreatePosterToken()
    {
        if (_disposed)
        {
            return new CancellationToken(canceled: true);
        }

        if (_posterCts is null || _posterCts.IsCancellationRequested)
        {
            _posterCts?.Dispose();
            _posterCts = new CancellationTokenSource();
        }

        return _posterCts.Token;
    }

    private async Task LoadBrowsePostersAsync(CancellationToken ct)
    {
        var cwCopy = ContinueWatchingItems.ToList();
        var catCopy = CatalogItems.ToList();

        var tasks = new List<Task>(cwCopy.Count + catCopy.Count);

        foreach (var cw in cwCopy)
        {
            if (string.IsNullOrWhiteSpace(cw.PosterPath))
            {
                continue;
            }

            tasks.Add(LoadPosterIntoAsync(cw.PosterPath, 200, bmp => cw.PosterBitmap = bmp, ct));
        }

        foreach (var card in catCopy)
        {
            if (string.IsNullOrWhiteSpace(card.PosterPath))
            {
                continue;
            }

            tasks.Add(LoadPosterIntoAsync(card.PosterPath, 240, bmp => card.PosterBitmap = bmp, ct));
        }

        await Task.WhenAll(tasks);
    }

    private async Task OpenDetailsAsync(int tmdbId, string mediaType)
    {
        if (_disposed)
        {
            return;
        }

        ShutdownWebView();
        var cts = ReplaceTokenSource(ref _loadCts);
        CancelTokenSource(ref _posterCts);
        var posterCts = ReplaceTokenSource(ref _posterCts);

        ClearBrowseBitmaps();
        ClearDetailBitmaps();
        _tmdb.ClearPosterCache();

        IsLoading = true;
        ActiveScreen = MoviesTvScreen.Detail;

        try
        {
            var normalized = mediaType.Trim().ToLowerInvariant();
            var isWatchlisted = await _database.IsInMovieWatchlistAsync(_profileId, tmdbId, normalized, cts.Token);
            var history = await _database.GetMovieHistoryAsync(_profileId, 50, cts.Token);
            var matchHistory = history.FirstOrDefault(h => h.TmdbId == tmdbId && string.Equals(h.MediaType, normalized, StringComparison.OrdinalIgnoreCase));

            DetailSeasons.Clear();
            DetailEpisodes.Clear();
            _loadedMovieDetails = null;
            _loadedTvDetails = null;

            if (normalized == "tv")
            {
                var tv = await _tmdb.GetTvDetailsAsync(tmdbId, cts.Token);
                _loadedTvDetails = tv;

                IsDetailTvShow = true;
                DetailTitle = tv.Title;
                DetailTagline = tv.Tagline ?? string.Empty;
                DetailOverview = string.IsNullOrWhiteSpace(tv.Overview) ? "No synopsis available." : tv.Overview;
                DetailRatingText = tv.Rating > 0.05 ? tv.Rating.ToString("0.0", CultureInfo.InvariantCulture) : "N/A";
                DetailGenresText = tv.Genres.Count > 0 ? string.Join(" · ", tv.Genres) : "TV Series";
                var year = tv.FirstAirDate is { Length: >= 4 } ? tv.FirstAirDate[..4] : "Unknown";
                DetailMetaLine = $"{year} · {tv.NumberOfSeasons} Seasons · {tv.NumberOfEpisodes} Episodes";
                DetailIsInWatchlist = isWatchlisted;

                var orderedSeasons = tv.Seasons
                    .Where(s => s.SeasonNumber > 0)
                    .OrderBy(s => s.SeasonNumber)
                    .ToList();
                if (orderedSeasons.Count == 0)
                {
                    orderedSeasons = tv.Seasons.ToList();
                }

                _detailLastSeason = matchHistory is { LastSeason: > 0 } ? matchHistory.LastSeason : (orderedSeasons.FirstOrDefault()?.SeasonNumber ?? 1);
                _detailLastEpisode = matchHistory is { LastEpisode: > 0 } ? matchHistory.LastEpisode : 1;
                DetailPlayButtonText = $"Play S{_detailLastSeason:D2} E{_detailLastEpisode:D2}";

                _syncingSeason = true;
                try
                {
                    foreach (var s in orderedSeasons)
                    {
                        DetailSeasons.Add(s);
                    }

                    SelectedSeason = DetailSeasons.FirstOrDefault(s => s.SeasonNumber == _detailLastSeason)
                        ?? DetailSeasons.FirstOrDefault();
                }
                finally
                {
                    _syncingSeason = false;
                }

                IsLoading = false;
                _ = LoadDetailHeroBitmapsAsync(tv.BackdropPath, tv.PosterPath, posterCts.Token);
                if (SelectedSeason is not null)
                {
                    await LoadSeasonEpisodesAsync(tv.TmdbId, SelectedSeason.SeasonNumber);
                }
            }
            else
            {
                var movie = await _tmdb.GetMovieDetailsAsync(tmdbId, cts.Token);
                _loadedMovieDetails = movie;

                IsDetailTvShow = false;
                DetailTitle = movie.Title;
                DetailTagline = movie.Tagline ?? string.Empty;
                DetailOverview = string.IsNullOrWhiteSpace(movie.Overview) ? "No synopsis available." : movie.Overview;
                DetailRatingText = movie.Rating > 0.05 ? movie.Rating.ToString("0.0", CultureInfo.InvariantCulture) : "N/A";
                DetailGenresText = movie.Genres.Count > 0 ? string.Join(" · ", movie.Genres) : "Movie";
                var year = movie.ReleaseDate is { Length: >= 4 } ? movie.ReleaseDate[..4] : "Unknown";
                DetailMetaLine = movie.RuntimeMinutes is > 0 ? $"{year} · {movie.RuntimeMinutes} min" : year;
                DetailIsInWatchlist = isWatchlisted;
                DetailPlayButtonText = "Play Movie";

                IsLoading = false;
                _ = LoadDetailHeroBitmapsAsync(movie.BackdropPath, movie.PosterPath, posterCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            IsLoading = false;
            ShowToast($"Failed to load details: {ex.Message}");
            ActiveScreen = MoviesTvScreen.Browse;
        }
    }

    private async Task LoadDetailHeroBitmapsAsync(string? backdropPath, string? posterPath, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(backdropPath))
        {
            await LoadPosterIntoAsync(backdropPath, 342, bmp => DetailBackdropBitmap = bmp, ct);
        }

        if (!string.IsNullOrWhiteSpace(posterPath))
        {
            await LoadPosterIntoAsync(posterPath, 240, bmp => DetailPosterBitmap = bmp, ct);
        }
    }

    private async Task ReloadDetailImagesAsync()
    {
        var cts = ReplaceTokenSource(ref _posterCts);
        if (_loadedMovieDetails is { } movie)
        {
            await LoadDetailHeroBitmapsAsync(movie.BackdropPath, movie.PosterPath, cts.Token);
        }
        else if (_loadedTvDetails is { } tv)
        {
            await LoadDetailHeroBitmapsAsync(tv.BackdropPath, tv.PosterPath, cts.Token);
            foreach (var row in DetailEpisodes.ToList())
            {
                if (!string.IsNullOrWhiteSpace(row.Episode.StillPath))
                {
                    _ = LoadPosterIntoAsync(row.Episode.StillPath, 220, bmp => row.StillBitmap = bmp, cts.Token);
                }
            }
        }
    }

    private async Task LoadSeasonEpisodesAsync(int tmdbId, int seasonNumber)
    {
        var cts = ReplaceTokenSource(ref _loadCts);
        var posterCts = ReplaceTokenSource(ref _posterCts);

        try
        {
            foreach (var oldRow in DetailEpisodes)
            {
                oldRow.StillBitmap = null;
            }

            DetailEpisodes.Clear();
            var seasonDetails = await _tmdb.GetTvSeasonAsync(tmdbId, seasonNumber, cts.Token);
            _activeSeasonEpisodes = seasonDetails.Episodes;

            foreach (var ep in seasonDetails.Episodes)
            {
                var isLast = seasonNumber == _detailLastSeason && ep.EpisodeNumber == _detailLastEpisode;
                DetailEpisodes.Add(new TvEpisodeRowViewModel(
                    ep,
                    isLast,
                    row =>
                    {
                        if (_loadedTvDetails is { } tv)
                        {
                            _ = StartTvEpisodePlaybackAsync(tv, row.Episode.SeasonNumber, row.Episode.EpisodeNumber);
                        }
                    }));
            }

            if (_loadedTvDetails is { } heroTv && DetailBackdropBitmap is null && DetailPosterBitmap is null)
            {
                _ = LoadDetailHeroBitmapsAsync(heroTv.BackdropPath, heroTv.PosterPath, posterCts.Token);
            }

            foreach (var row in DetailEpisodes.ToList())
            {
                if (!string.IsNullOrWhiteSpace(row.Episode.StillPath))
                {
                    _ = LoadPosterIntoAsync(row.Episode.StillPath, 220, bmp => row.StillBitmap = bmp, posterCts.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowToast($"Could not load Season {seasonNumber}: {ex.Message}");
        }
    }

    private async Task ResumeContinueWatchingAsync(ContinueWatchingCardViewModel card)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (string.Equals(card.MediaType, "tv", StringComparison.OrdinalIgnoreCase))
            {
                var tv = await _tmdb.GetTvDetailsAsync(card.TmdbId);
                _loadedTvDetails = tv;
                var season = card.Item.LastSeason > 0 ? card.Item.LastSeason : 1;
                var episode = card.Item.LastEpisode > 0 ? card.Item.LastEpisode : 1;

                var seasonDetails = await _tmdb.GetTvSeasonAsync(tv.TmdbId, season);
                _activeSeasonEpisodes = seasonDetails.Episodes;
                await StartTvEpisodePlaybackAsync(tv, season, episode, MoviesTvScreen.Browse);
            }
            else
            {
                var movie = await _tmdb.GetMovieDetailsAsync(card.TmdbId);
                _loadedMovieDetails = movie;
                await StartMoviePlaybackAsync(movie, MoviesTvScreen.Browse);
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Could not resume playback: {ex.Message}");
        }
    }

    private async Task StartMoviePlaybackAsync(TmdbMovieDetails movie, MoviesTvScreen? returnScreen = null)
    {
        var settings = await _database.GetMoviesTvSettingsAsync();
        var embedUrl = TmdbService.BuildEmbedUrl(settings.MovieEmbedTemplate, movie.TmdbId, movie.ImdbId);
        if (string.IsNullOrWhiteSpace(embedUrl))
        {
            await OpenSettingsModalWithHintAsync(
                "Configure your Movie Embed URL Template first (supporting {tmdb_id} and {imdb_id}).");
            return;
        }

        await _database.UpsertMovieHistoryAsync(
            _profileId,
            movie.TmdbId,
            movie.ImdbId,
            "movie",
            movie.Title,
            movie.PosterPath,
            lastSeason: 0,
            lastEpisode: 0);

        _screenBeforePlayer = returnScreen ?? MoviesTvScreen.Detail;
        _playerSeason = 0;
        _playerEpisode = 0;

        await EnterPlayerViewAsync(movie.Title, string.Empty, isTv: false, embedUrl, settings.ExtraBlockedDomains);
    }

    private async Task StartTvEpisodePlaybackAsync(
        TmdbTvDetails tv,
        int season,
        int episode,
        MoviesTvScreen? returnScreen = null)
    {
        var settings = await _database.GetMoviesTvSettingsAsync();
        var embedUrl = TmdbService.BuildEmbedUrl(settings.TvEmbedTemplate, tv.TmdbId, tv.ImdbId, season, episode);
        if (string.IsNullOrWhiteSpace(embedUrl))
        {
            await OpenSettingsModalWithHintAsync(
                "Configure your TV Embed URL Template first (supporting {tmdb_id}, {imdb_id}, {season}, {episode}).");
            return;
        }

        if (_activeSeasonEpisodes.Count == 0 || _activeSeasonEpisodes[0].SeasonNumber != season)
        {
            try
            {
                var details = await _tmdb.GetTvSeasonAsync(tv.TmdbId, season);
                _activeSeasonEpisodes = details.Episodes;
            }
            catch
            {
                _activeSeasonEpisodes = [];
            }
        }

        await _database.UpsertMovieHistoryAsync(
            _profileId,
            tv.TmdbId,
            tv.ImdbId,
            "tv",
            tv.Title,
            tv.PosterPath,
            lastSeason: season,
            lastEpisode: episode);

        _detailLastSeason = season;
        _detailLastEpisode = episode;
        DetailPlayButtonText = $"Play S{season:D2} E{episode:D2}";
        foreach (var row in DetailEpisodes)
        {
            row.IsLastWatched = row.Episode.SeasonNumber == season && row.Episode.EpisodeNumber == episode;
        }

        _screenBeforePlayer = returnScreen ?? MoviesTvScreen.Detail;
        _playerSeason = season;
        _playerEpisode = episode;

        var epTitle = _activeSeasonEpisodes.FirstOrDefault(e => e.EpisodeNumber == episode)?.Name;
        var label = string.IsNullOrWhiteSpace(epTitle)
            ? $"S{season:D2} E{episode:D2}"
            : $"S{season:D2} E{episode:D2} · {epTitle}";

        await EnterPlayerViewAsync(tv.Title, label, isTv: true, embedUrl, settings.ExtraBlockedDomains);
    }

    private async Task EnterPlayerViewAsync(
        string title,
        string episodeLabel,
        bool isTv,
        string embedUrl,
        IReadOnlyList<string> extraBlockedDomains)
    {
        CancelTokenSource(ref _posterCts);
        ClearAllBitmaps();
        _tmdb.ClearPosterCache();

        _blockedCount = 0;
        _currentPlayerUrl = embedUrl;
        PlayerTitle = title;
        PlayerEpisodeLabel = episodeLabel;
        PlayerBlockedCountText = string.Empty;
        IsPlayerTvShow = isTv;
        UpdateEpisodeNavButtons();

        ActiveScreen = MoviesTvScreen.Player;

        if (NavigateWebViewRequested is not null)
        {
            await NavigateWebViewRequested(embedUrl, extraBlockedDomains);
        }

        _actionLog.Log("MoviesTV", $"Playing {(isTv ? $"{title} {episodeLabel}" : title)} in hardened WebView2.");
    }

    private void UpdateEpisodeNavButtons()
    {
        if (!IsPlayerTvShow || _activeSeasonEpisodes.Count == 0)
        {
            CanGoPrevEpisode = false;
            CanGoNextEpisode = false;
            return;
        }

        var idx = FindEpisodeIndex(_playerEpisode);
        CanGoPrevEpisode = idx > 0;
        CanGoNextEpisode = idx >= 0 && idx + 1 < _activeSeasonEpisodes.Count;
    }

    private int FindEpisodeIndex(int episodeNumber)
    {
        for (var i = 0; i < _activeSeasonEpisodes.Count; i++)
        {
            if (_activeSeasonEpisodes[i].EpisodeNumber == episodeNumber)
            {
                return i;
            }
        }

        return -1;
    }

    private async Task ToggleCardWatchlistAsync(MoviePosterCardViewModel card)
    {
        var added = await ToggleWatchlistEntryAsync(
            card.TmdbId,
            imdbId: null,
            card.MediaType,
            card.Title,
            card.PosterPath,
            card.Year,
            card.Rating);

        card.IsInWatchlist = added;

        if (ActiveTab == MoviesTvTab.Watchlist && !added)
        {
            card.PosterBitmap = null;
            CatalogItems.Remove(card);
            OnPropertyChanged(nameof(HasCatalogItems));
            if (CatalogItems.Count == 0)
            {
                EmptyMessage = "Your watchlist is empty. Bookmark any movie or TV show to save it here.";
                OnPropertyChanged(nameof(HasEmptyMessage));
            }
        }
    }

    private async Task<bool> ToggleWatchlistEntryAsync(
        int tmdbId,
        string? imdbId,
        string mediaType,
        string title,
        string? posterPath,
        string? year,
        double rating)
    {
        if (_profileId <= 0)
        {
            return false;
        }

        var normalized = mediaType.Trim().ToLowerInvariant();
        var exists = await _database.IsInMovieWatchlistAsync(_profileId, tmdbId, normalized);
        if (exists)
        {
            await _database.RemoveFromMovieWatchlistAsync(_profileId, tmdbId, normalized);
            _watchlistKeys.Remove((tmdbId, normalized));
            if (DetailTitle == title)
            {
                DetailIsInWatchlist = false;
            }
            ShowToast($"Removed '{title}' from Watchlist");
            return false;
        }

        await _database.AddToMovieWatchlistAsync(_profileId, tmdbId, imdbId, normalized, title, posterPath, year, rating);
        _watchlistKeys.Add((tmdbId, normalized));
        if (DetailTitle == title)
        {
            DetailIsInWatchlist = true;
        }
        ShowToast($"Saved '{title}' to Watchlist");
        return true;
    }

    private async Task RemoveContinueWatchingAsync(ContinueWatchingCardViewModel card)
    {
        await _database.RemoveMovieHistoryAsync(_profileId, card.TmdbId, card.MediaType);
        card.PosterBitmap = null;
        ContinueWatchingItems.Remove(card);
        OnPropertyChanged(nameof(HasContinueWatching));
        OnPropertyChanged(nameof(ShowContinueWatching));
    }

    private async Task LoadPosterIntoAsync(string path, int decodeWidth, Action<Bitmap?> setter, CancellationToken ct)
    {
        var acquired = false;
        try
        {
            await _posterGate.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            if (ct.IsCancellationRequested || _disposed)
            {
                return;
            }

            var bmp = await _tmdb.GetPosterBitmapAsync(path, decodeWidth, ct).ConfigureAwait(false);
            if (!ct.IsCancellationRequested && !_disposed && bmp is not null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!ct.IsCancellationRequested && !_disposed)
                    {
                        setter(bmp);
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (acquired)
            {
                _posterGate.Release();
            }
        }
    }

    private void ShutdownWebView()
    {
        _currentPlayerUrl = null;
        ShutdownWebViewRequested?.Invoke();
    }

    private void ClearAllBitmaps()
    {
        ClearBrowseBitmaps();
        ClearDetailBitmaps();
    }

    private void ClearBrowseBitmaps()
    {
        foreach (var cw in ContinueWatchingItems)
        {
            cw.PosterBitmap = null;
        }

        foreach (var item in CatalogItems)
        {
            item.PosterBitmap = null;
        }
    }

    private void ClearDetailBitmaps()
    {
        DetailBackdropBitmap = null;
        DetailPosterBitmap = null;
        foreach (var ep in DetailEpisodes)
        {
            ep.StillBitmap = null;
        }
    }

    private void ShowToast(string message)
    {
        CancelTokenSource(ref _toastCts);
        StatusToast = message;
        var cts = new CancellationTokenSource();
        _toastCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3200, cts.Token).ConfigureAwait(false);
                if (!cts.IsCancellationRequested && !_disposed)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!cts.IsCancellationRequested && string.Equals(StatusToast, message, StringComparison.Ordinal))
                        {
                            StatusToast = string.Empty;
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private static CancellationTokenSource ReplaceTokenSource(ref CancellationTokenSource? slot)
    {
        var next = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref slot, next);
        if (prev is not null)
        {
            try { prev.Cancel(); } catch { }
            finally { prev.Dispose(); }
        }

        return next;
    }

    private static void CancelTokenSource(ref CancellationTokenSource? slot)
    {
        var prev = Interlocked.Exchange(ref slot, null);
        if (prev is not null)
        {
            try { prev.Cancel(); } catch { }
            finally { prev.Dispose(); }
        }
    }

    private void SyncFromModule()
    {
        StateLabel = _module.State switch
        {
            ModuleState.RunningUI => "Running (UI)",
            ModuleState.RunningHeadless => "Headless",
            ModuleState.Paused => "Paused",
            _ => "Stopped"
        };
        StatusSummary = _module.StatusSummary;
    }

    private void OnModuleStateChanged(object? sender, ModuleState state)
    {
        if (_disposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                SyncFromModule();
            }
        });
    }

    private void OnModuleStatusSummaryChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                StatusSummary = _module.StatusSummary;
            }
        });
    }
}
