using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using REM.Models;
using REM.Modules;
using REM.Services;

namespace REM.ViewModels;

public enum YouTubeFeedTabKind
{
    AccountFyp,
    AccountSubscriptions,
    Topic
}

public sealed partial class YouTubeSearchResultViewModel : ObservableObject, IDisposable
{
    private readonly Func<YouTubeSearchItem, Task> _onPlay;
    private readonly Func<YouTubeSearchItem, Task>? _onDownload;
    private bool _disposed;

    public YouTubeSearchResultViewModel(
        YouTubeSearchItem item,
        Func<YouTubeSearchItem, Task> onPlay,
        Func<YouTubeSearchItem, Task>? onDownload = null)
    {
        Item = item;
        _onPlay = onPlay;
        _onDownload = onDownload;
    }

    public YouTubeSearchItem Item { get; }
    public string Title => Item.Title;
    public string Channel => Item.Channel;
    public string DurationDisplay => Item.DurationDisplay;
    public string EffectiveThumbnailUrl => Item.EffectiveThumbnailUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnailBitmap;

    public bool HasThumbnail => ThumbnailBitmap is not null;

    public void SetThumbnail(Bitmap? bitmap)
    {
        if (_disposed)
        {
            bitmap?.Dispose();
            return;
        }

        var prev = ThumbnailBitmap;
        ThumbnailBitmap = bitmap;
        prev?.Dispose();
    }

    [RelayCommand]
    private Task PlayAsync() => _onPlay(Item);

    [RelayCommand]
    private Task DownloadAsync() => _onDownload is not null ? _onDownload(Item) : Task.CompletedTask;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ThumbnailBitmap?.Dispose();
        ThumbnailBitmap = null;
    }
}

public sealed partial class YouTubeFeedTabViewModel : ObservableObject
{
    private readonly Func<YouTubeFeedTabViewModel, Task> _onSelect;

    public YouTubeFeedTabViewModel(
        string name,
        YouTubeFeedTabKind kind,
        Func<YouTubeFeedTabViewModel, Task> onSelect)
    {
        Name = name;
        Kind = kind;
        _onSelect = onSelect;
    }

    public string Name { get; }
    public YouTubeFeedTabKind Kind { get; }

    [ObservableProperty]
    private bool _isSelected;

    [RelayCommand]
    private Task SelectAsync() => _onSelect(this);
}

public sealed partial class TwitchCategoryTabViewModel : ObservableObject
{
    private readonly Func<TwitchCategoryTabViewModel, Task> _onSelect;

    public TwitchCategoryTabViewModel(
        string name,
        string? gameId,
        bool isFollowingTab,
        Func<TwitchCategoryTabViewModel, Task> onSelect)
    {
        Name = name;
        GameId = gameId;
        IsFollowingTab = isFollowingTab;
        _onSelect = onSelect;
    }

    public string Name { get; }
    public string? GameId { get; set; }
    public bool IsFollowingTab { get; }

    [ObservableProperty]
    private bool _isSelected;

    [RelayCommand]
    private Task SelectAsync() => _onSelect(this);
}

public sealed partial class TwitchStreamCardViewModel : ObservableObject, IDisposable
{
    private readonly Func<TwitchLiveStream, Task> _onPlay;
    private bool _disposed;

    public TwitchStreamCardViewModel(TwitchLiveStream stream, Func<TwitchLiveStream, Task> onPlay)
    {
        Stream = stream;
        _onPlay = onPlay;
    }

    public TwitchLiveStream Stream { get; }
    public string UserName => Stream.UserName;
    public string GameName => Stream.GameName;
    public string Title => Stream.Title;
    public string ViewersDisplay => Stream.ViewersDisplay;
    public string Language => Stream.Language.ToUpperInvariant();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnailBitmap;

    public bool HasThumbnail => ThumbnailBitmap is not null;

    public void SetThumbnail(Bitmap? bitmap)
    {
        if (_disposed)
        {
            bitmap?.Dispose();
            return;
        }

        var prev = ThumbnailBitmap;
        ThumbnailBitmap = bitmap;
        prev?.Dispose();
    }

    [RelayCommand]
    private Task PlayAsync() => _onPlay(Stream);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ThumbnailBitmap?.Dispose();
        ThumbnailBitmap = null;
    }
}

public sealed partial class TwitchCategorySearchHitViewModel : ObservableObject
{
    private readonly Func<TwitchCategorySearchHitViewModel, Task> _onTogglePin;

    public TwitchCategorySearchHitViewModel(
        TwitchCategoryItem item,
        bool isPinned,
        Func<TwitchCategorySearchHitViewModel, Task> onTogglePin)
    {
        Item = item;
        _isPinned = isPinned;
        _onTogglePin = onTogglePin;
    }

    public TwitchCategoryItem Item { get; }
    public string Name => Item.Name;
    public string? GameId => Item.GameId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinLabel))]
    private bool _isPinned;

    public string PinLabel => IsPinned ? "Unpin" : "Pin";

    [RelayCommand]
    private Task TogglePinAsync() => _onTogglePin(this);
}

public sealed partial class AudioStreamerViewModel : ObservableObject, IDisposable
{
    private const int DefaultYouTubeFypCount = 18;
    private const int YouTubeFypPageStep = 18;
    private const int DefaultYouTubeTopicCount = 12;
    private const int YouTubeTopicPageStep = 12;
    private const int DefaultTwitchPageSize = 20;

    private static readonly string[] Languages =
    [
        "All", "en", "ar", "es", "fr", "de", "ja", "ko", "pt", "ru", "zh"
    ];

    private readonly AudioStreamerModule _module;
    private readonly DatabaseService _database;
    private readonly ActionLogService _actionLog;
    private readonly DialogService _dialogService;
    private readonly YtDlpService _ytDlp;
    private readonly YouTubeAudioService _youtube;
    private readonly TwitchHelixService _twitch;
    private readonly AudioPlaybackEngine _engine;
    private readonly IDownloadDispatcher _downloadDispatcher;
    private readonly Action _onOpenSettings;

    private long _profileId;
    private CancellationTokenSource? _workCts;
    private CancellationTokenSource? _thumbCts;
    private YouTubeSearchPager? _topicSearchPager;
    private YouTubeSearchPager? _textSearchPager;
    private bool _isSyncingSeek;
    private bool _youtubeFeedLoaded;
    private bool _twitchFeedLoaded;
    private int _youTubeLoadedLimit;
    private string? _initialTwitchCursor;
    private string? _twitchNextCursor;
    private bool _disposed;

    public AudioStreamerViewModel(
        AudioStreamerModule module,
        long profileId,
        DatabaseService database,
        ActionLogService actionLog,
        DialogService dialogService,
        YtDlpService ytDlp,
        YouTubeAudioService youtube,
        TwitchHelixService twitch,
        AudioPlaybackEngine engine,
        IDownloadDispatcher downloadDispatcher,
        Action onOpenSettings)
    {
        _module = module;
        _profileId = profileId;
        _database = database;
        _actionLog = actionLog;
        _dialogService = dialogService;
        _ytDlp = ytDlp;
        _youtube = youtube;
        _twitch = twitch;
        _engine = engine;
        _downloadDispatcher = downloadDispatcher;
        _onOpenSettings = onOpenSettings;

        AccentBrush = new SolidColorBrush(Color.Parse(_module.AccentHex));
        Volume = _engine.Volume;

        var savedPlatform = _database.GetSetting(DatabaseService.KeyAudioActivePlatform);
        _isTwitchActive = string.Equals(savedPlatform, "twitch", StringComparison.OrdinalIgnoreCase);

        _module.StateChanged += OnModuleStateChanged;
        _module.StatusSummaryChanged += OnModuleStatusSummaryChanged;
        _engine.PlaybackStateChanged += OnEnginePlaybackStateChanged;
        _engine.PositionChanged += OnEnginePositionChanged;
        _engine.PlaybackError += OnEnginePlaybackError;

        RefreshModuleHeaderStatus();
        SyncPlaybackFromEngine();
        LoadFromSettingsAndTabs(loadActiveFeed: true);
    }

    public string Name => _module.Name;
    public string Description => _module.Description;
    public Geometry IconGeometry => _module.IconGeometry;
    public IBrush AccentBrush { get; }
    public IReadOnlyList<string> AvailableLanguages => Languages;

    public ObservableCollection<YouTubeFeedTabViewModel> YouTubeTabs { get; } = [];
    public ObservableCollection<YouTubeSearchResultViewModel> YouTubeFeedItems { get; } = [];
    public ObservableCollection<YouTubeSearchResultViewModel> SearchResults { get; } = [];

    public ObservableCollection<TwitchCategoryTabViewModel> CategoryTabs { get; } = [];
    public ObservableCollection<TwitchStreamCardViewModel> LiveStreams { get; } = [];
    public ObservableCollection<TwitchCategorySearchHitViewModel> CategorySearchResults { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsYouTubeMode))]
    [NotifyPropertyChangedFor(nameof(IsTwitchMode))]
    [NotifyPropertyChangedFor(nameof(SearchWatermark))]
    [NotifyPropertyChangedFor(nameof(ActivePlatformLabel))]
    [NotifyPropertyChangedFor(nameof(PlatformSwitchTooltip))]
    private bool _isTwitchActive;

    public bool IsYouTubeMode => !IsTwitchActive;
    public bool IsTwitchMode => IsTwitchActive;

    public string SearchWatermark => IsYouTubeMode
        ? "Search YouTube or paste a YouTube / Twitch URL..."
        : "Paste a Twitch / YouTube URL or search YouTube...";

    public string ActivePlatformLabel => IsTwitchActive ? "Twitch" : "YouTube";
    public string PlatformSwitchTooltip => IsTwitchActive
        ? "Showing Twitch · Click to switch to YouTube"
        : "Showing YouTube · Click to switch to Twitch";

    [ObservableProperty]
    private string _stateLabel = string.Empty;

    [ObservableProperty]
    private string _statusSummary = string.Empty;

    [ObservableProperty]
    private bool _isHeaderVisible;

    [ObservableProperty]
    private string _searchInput = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivityNotice))]
    private string _activityNotice = string.Empty;

    public bool HasActivityNotice => !string.IsNullOrEmpty(ActivityNotice);

    [ObservableProperty]
    private bool _hasSearchResults;

    [ObservableProperty]
    private string _lastSearchQuery = string.Empty;

    [ObservableProperty]
    private bool _showYouTubeThumbnails = true;

    [ObservableProperty]
    private bool _isYouTubeTopicAddOpen;

    [ObservableProperty]
    private string _newYouTubeTopicInput = string.Empty;

    [ObservableProperty]
    private bool _showYouTubeBrowserPrompt;

    [ObservableProperty]
    private string _youTubeBrowserPromptDetail = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasYouTubeFeedError))]
    private string _youTubeFeedError = string.Empty;

    public bool HasYouTubeFeedError => !string.IsNullOrEmpty(YouTubeFeedError);

    [ObservableProperty]
    private bool _canShowMoreYouTube;

    [ObservableProperty]
    private bool _canShowMoreYouTubeSearch;

    [ObservableProperty]
    private bool _canShowMoreTwitch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMoreButtonLabel))]
    private bool _isLoadingMore;

    public string ShowMoreButtonLabel => IsLoadingMore ? "Loading more..." : "Show More";

    [ObservableProperty]
    private string _selectedLanguage = "All";

    [ObservableProperty]
    private bool _showThumbnails;

    [ObservableProperty]
    private bool _isCategorySearchOpen;

    [ObservableProperty]
    private string _categorySearchQuery = string.Empty;

    [ObservableProperty]
    private bool _showTwitchSetupPrompt;

    [ObservableProperty]
    private bool _showFollowAuthPrompt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTwitchFeedError))]
    private string _twitchFeedError = string.Empty;

    public bool HasTwitchFeedError => !string.IsNullOrEmpty(TwitchFeedError);

    [ObservableProperty]
    private bool _hasCurrentTrack;

    [ObservableProperty]
    private string _currentTrackTitle = "Nothing playing";

    [ObservableProperty]
    private string _currentTrackChannel = "Browse YouTube FYP or Twitch Live";

    [ObservableProperty]
    private string _currentSourceLabel = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSeek))]
    private bool _isLiveTrack;

    public bool CanSeek => HasCurrentTrack && !IsLiveTrack && SeekMaxSeconds > 1;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private double _volume = 80;

    [ObservableProperty]
    private double _seekPositionSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSeek))]
    private double _seekMaxSeconds = 1;

    [ObservableProperty]
    private string _positionDisplay = "0:00";

    [ObservableProperty]
    private string _durationDisplay = "0:00";

    public Action? RequestCloseToDashboard { get; set; }

    partial void OnVolumeChanged(double value)
    {
        var intVol = Math.Clamp((int)Math.Round(value), 0, 100);
        _engine.Volume = intVol;
        _database.SaveSetting(DatabaseService.KeyAudioVolume, intVol.ToString());
    }

    partial void OnSeekPositionSecondsChanged(double value)
    {
        if (_isSyncingSeek || !CanSeek)
        {
            return;
        }

        _engine.SeekSeconds(value);
        PositionDisplay = FormatSeconds(value);
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        if (!IsTwitchMode)
        {
            return;
        }

        var activeTab = CategoryTabs.FirstOrDefault(t => t.IsSelected);
        if (activeTab is not null && !activeTab.IsFollowingTab)
        {
            _ = LoadStreamsForTabAsync(activeTab);
        }
    }

    public void SetProfile(long profileId)
    {
        if (_profileId == profileId)
        {
            return;
        }

        _profileId = profileId;
        _youtubeFeedLoaded = false;
        _twitchFeedLoaded = false;
        LoadFromSettingsAndTabs(loadActiveFeed: true);
    }

    public void RefreshFromSettings()
    {
        _youtubeFeedLoaded = false;
        _twitchFeedLoaded = false;
        LoadFromSettingsAndTabs(loadActiveFeed: true);
    }

    [RelayCommand]
    private void ToggleHeader() => IsHeaderVisible = !IsHeaderVisible;

    [RelayCommand]
    private void OpenSettings() => _onOpenSettings();

    [RelayCommand]
    private void TogglePlatform()
    {
        IsTwitchActive = !IsTwitchActive;
        _database.SaveSetting(DatabaseService.KeyAudioActivePlatform, IsTwitchActive ? "twitch" : "youtube");

        if (IsTwitchActive)
        {
            IsYouTubeTopicAddOpen = false;
            if (!_twitchFeedLoaded)
            {
                var active = CategoryTabs.FirstOrDefault(t => t.IsSelected) ?? CategoryTabs.FirstOrDefault();
                if (active is not null)
                {
                    _ = LoadStreamsForTabAsync(active);
                }
            }
        }
        else
        {
            IsCategorySearchOpen = false;
            if (!_youtubeFeedLoaded && !HasSearchResults)
            {
                var active = YouTubeTabs.FirstOrDefault(t => t.IsSelected) ?? YouTubeTabs.FirstOrDefault();
                if (active is not null)
                {
                    _ = LoadYouTubeTabAsync(active);
                }
            }
        }
    }

    [RelayCommand]
    private void MinimizeToHeadless()
    {
        var close = RequestCloseToDashboard;
        _module.RunHeadless();
        close?.Invoke();
    }

    [RelayCommand]
    private void PauseModule()
    {
        var close = RequestCloseToDashboard;
        _module.Pause();
        close?.Invoke();
    }

    [RelayCommand]
    private void Kill()
    {
        var close = RequestCloseToDashboard;
        _module.Kill();
        close?.Invoke();
    }

    [RelayCommand]
    private void BackToDashboard()
    {
        RequestCloseToDashboard?.Invoke();
    }

    [RelayCommand]
    private void ClearSearchResults()
    {
        ClearYouTubeSearchCards();
        LastSearchQuery = string.Empty;
    }

    [RelayCommand]
    private void ToggleYouTubeTopicAdd()
    {
        IsYouTubeTopicAddOpen = !IsYouTubeTopicAddOpen;
        if (!IsYouTubeTopicAddOpen)
        {
            NewYouTubeTopicInput = string.Empty;
        }
    }

    [RelayCommand]
    private async Task AddYouTubeTopicAsync()
    {
        var topic = NewYouTubeTopicInput.Trim();
        if (string.IsNullOrWhiteSpace(topic))
        {
            return;
        }

        _database.AddProfileYouTubeTopic(_profileId, topic);
        _actionLog.Log("YouTube", $"Pinned topic '{topic}'.");
        NewYouTubeTopicInput = string.Empty;
        IsYouTubeTopicAddOpen = false;

        ClearSearchResults();
        RebuildYouTubeTabs(topic);
        var tab = YouTubeTabs.FirstOrDefault(t => t.IsSelected);
        if (tab is not null)
        {
            await LoadYouTubeTabAsync(tab);
        }
    }

    [RelayCommand]
    private void ToggleCategorySearch()
    {
        IsCategorySearchOpen = !IsCategorySearchOpen;
        if (!IsCategorySearchOpen)
        {
            CategorySearchResults.Clear();
            CategorySearchQuery = string.Empty;
        }
    }

    [RelayCommand]
    private async Task SubmitSearchOrUrlAsync()
    {
        var input = SearchInput.Trim();
        if (string.IsNullOrWhiteSpace(input) || _disposed)
        {
            return;
        }

        var ct = ResetWorkToken();
        IsBusy = true;
        try
        {
            if (YouTubeAudioService.IsYouTubeUrl(input))
            {
                ClearSearchResults();
                ActivityNotice = "Extracting YouTube audio stream...";
                var allowFallback = ReadBoolSetting(DatabaseService.KeyAudioYtDlpFallback, defaultValue: true);
                var cookieArg = ResolveConfiguredCookieArg(out _);
                var qualityPreset = _database.GetSetting(DatabaseService.KeyAudioQualityPreset) ?? "max";
                var track = await _youtube.ExtractAudioOnlyAsync(
                    input, allowFallback, ct, cookieArg, qualityPreset: qualityPreset);
                await _engine.PlayAsync(track, ct);
                ActivityNotice = string.Empty;
                _actionLog.Log("Audio", $"Playing YouTube: {track.Title}");
            }
            else if (YouTubeAudioService.IsTwitchUrl(input))
            {
                ClearSearchResults();
                await ExtractAndPlayTwitchUrlAsync(input, titleOverride: null, channelOverride: null, gameOverride: "Twitch", ct);
            }
            else
            {
                if (IsTwitchActive)
                {
                    IsTwitchActive = false;
                    _database.SaveSetting(DatabaseService.KeyAudioActivePlatform, "youtube");
                }

                ActivityNotice = $"Searching YouTube for \"{input}\"...";
                DisposePager(ref _textSearchPager);
                _textSearchPager = _youtube.CreateSearchPager(input);
                var (hits, hasMoreSearch) = await _textSearchPager.FetchNextAsync(DefaultYouTubeTopicCount, ct);
                ClearYouTubeSearchCards(disposePager: false);
                foreach (var hit in hits)
                {
                    SearchResults.Add(new YouTubeSearchResultViewModel(hit, PlayYouTubeSearchItemAsync, PromptAndDownloadYouTubeItemAsync));
                }

                LastSearchQuery = input;
                HasSearchResults = SearchResults.Count > 0;
                CanShowMoreYouTubeSearch = HasSearchResults && hasMoreSearch;
                ActivityNotice = SearchResults.Count == 0 ? $"No YouTube results found for \"{input}\"." : string.Empty;

                if (HasSearchResults && ShowYouTubeThumbnails)
                {
                    _ = LoadYouTubeThumbnailsAsync(SearchResults.ToList());
                }
            }
        }
        catch (OperationCanceledException)
        {
            ActivityNotice = string.Empty;
        }
        catch (Exception ex)
        {
            ActivityNotice = string.Empty;
            if (!_disposed)
            {
                _actionLog.Log("Error", $"Audio Streamer input failed: {ex.Message}");
                await _dialogService.ShowErrorAsync("Stream Extraction Failed", "Could not load the requested audio.", ex.Message);
            }
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
            }
        }
    }

    [RelayCommand]
    private async Task SearchCategoriesAsync()
    {
        var query = CategorySearchQuery.Trim();
        if (string.IsNullOrWhiteSpace(query) || _disposed)
        {
            return;
        }

        if (!_twitch.HasClientCredentials)
        {
            await _dialogService.ShowWarningAsync(
                "Twitch Credentials Required",
                "Add your Twitch Client ID and Client Secret in Audio Streamer Settings → Twitch API to search categories.");
            return;
        }

        var ct = ResetWorkToken();
        IsBusy = true;
        try
        {
            var hits = await _twitch.SearchCategoriesAsync(query, ct);
            var pinned = new HashSet<string>(
                _database.GetProfileTwitchCategories(_profileId).Select(c => c.Name),
                StringComparer.OrdinalIgnoreCase);

            CategorySearchResults.Clear();
            foreach (var hit in hits)
            {
                CategorySearchResults.Add(new TwitchCategorySearchHitViewModel(
                    hit,
                    pinned.Contains(hit.Name),
                    TogglePinCategoryAsync));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _actionLog.Log("Error", $"Twitch category search failed: {ex.Message}");
                await _dialogService.ShowErrorAsync("Twitch Search Failed", "Could not search Twitch categories.", ex.Message);
            }
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
            }
        }
    }

    [RelayCommand]
    private async Task RefreshYouTubeFeedAsync()
    {
        ClearSearchResults();
        var active = YouTubeTabs.FirstOrDefault(t => t.IsSelected) ?? YouTubeTabs.FirstOrDefault();
        if (active is not null)
        {
            await LoadYouTubeTabAsync(active);
        }
    }

    [RelayCommand]
    private async Task RefreshFeedAsync()
    {
        var active = CategoryTabs.FirstOrDefault(t => t.IsSelected) ?? CategoryTabs.FirstOrDefault();
        if (active is not null)
        {
            await LoadStreamsForTabAsync(active);
        }
    }

    [RelayCommand]
    private async Task ShowMoreYouTubeSearchAsync()
    {
        if (_disposed || IsBusy || !CanShowMoreYouTubeSearch || _textSearchPager is null)
        {
            return;
        }

        var ct = ResetWorkToken();
        IsBusy = true;
        IsLoadingMore = true;
        try
        {
            ActivityNotice = $"Loading more results for \"{LastSearchQuery}\"...";
            var (fetched, hasMore) = await _textSearchPager.FetchNextAsync(DefaultYouTubeTopicCount, ct);
            ActivityNotice = string.Empty;

            var existingIds = new HashSet<string>(
                SearchResults.Select(i => i.Item.VideoId),
                StringComparer.Ordinal);
            var addedCards = new List<YouTubeSearchResultViewModel>();

            foreach (var item in fetched)
            {
                if (existingIds.Add(item.VideoId))
                {
                    var card = new YouTubeSearchResultViewModel(item, PlayYouTubeSearchItemAsync, PromptAndDownloadYouTubeItemAsync);
                    SearchResults.Add(card);
                    addedCards.Add(card);
                }
            }

            CanShowMoreYouTubeSearch = hasMore;
            if (addedCards.Count == 0 && !hasMore)
            {
                ActivityNotice = "Reached the end of YouTube search results.";
            }
            if (addedCards.Count > 0 && ShowYouTubeThumbnails)
            {
                _ = LoadYouTubeThumbnailsAsync(addedCards, resetExisting: false);
            }
        }
        catch (OperationCanceledException)
        {
            ActivityNotice = string.Empty;
        }
        catch (Exception ex)
        {
            ActivityNotice = string.Empty;
            _actionLog.Log("Warning", $"Show more YouTube search failed: {ex.Message}");
        }
        finally
        {
            if (!_disposed)
            {
                IsLoadingMore = false;
                IsBusy = false;
            }
        }
    }

    [RelayCommand]
    private async Task ShowMoreYouTubeAsync()
    {
        var active = YouTubeTabs.FirstOrDefault(t => t.IsSelected);
        if (_disposed || IsBusy || !CanShowMoreYouTube || active is null)
        {
            return;
        }

        var ct = ResetWorkToken();
        IsBusy = true;
        IsLoadingMore = true;
        try
        {
            List<YouTubeSearchItem> fetched;
            var hasMore = true;
            if (active.Kind is YouTubeFeedTabKind.AccountFyp or YouTubeFeedTabKind.AccountSubscriptions)
            {
                var cookieArg = ResolveConfiguredCookieArg(out _);
                if (string.IsNullOrWhiteSpace(cookieArg))
                {
                    CanShowMoreYouTube = false;
                    return;
                }

                var feedUrl = active.Kind == YouTubeFeedTabKind.AccountFyp
                    ? "https://www.youtube.com/feed/recommended"
                    : "https://www.youtube.com/feed/subscriptions";

                ActivityNotice = "Loading more videos from your YouTube feed...";
                var startIndex = Math.Max(1, _youTubeLoadedLimit + 1);
                var result = await _ytDlp.FetchYouTubeFeedAsync(feedUrl, cookieArg, YouTubeFypPageStep, ct, startIndex);
                ActivityNotice = string.Empty;

                if (!result.Success)
                {
                    if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                    {
                        YouTubeFeedError = result.ErrorMessage;
                    }

                    return;
                }

                fetched = result.Items;
                if (fetched.Count > 0)
                {
                    _youTubeLoadedLimit += YouTubeFypPageStep;
                    hasMore = true;
                }
                else if (active.Kind == YouTubeFeedTabKind.AccountFyp)
                {
                    var refreshResult = await _ytDlp.FetchYouTubeFeedAsync(feedUrl, cookieArg, 30, ct, startIndex: 1);
                    var seenNow = new HashSet<string>(YouTubeFeedItems.Select(i => i.Item.VideoId), StringComparer.Ordinal);
                    var unseen = refreshResult.Items.Where(i => seenNow.Add(i.VideoId)).ToList();
                    if (unseen.Count > 0)
                    {
                        fetched = unseen;
                        hasMore = true;
                    }
                    else
                    {
                        hasMore = false;
                        ActivityNotice = "Reached the end of available YouTube feed videos.";
                    }
                }
                else
                {
                    hasMore = false;
                    ActivityNotice = "Reached the end of your Subscriptions feed.";
                }
            }
            else
            {
                ActivityNotice = $"Loading more '{active.Name}' videos...";
                if (_topicSearchPager is null)
                {
                    _topicSearchPager = _youtube.CreateSearchPager(active.Name);
                    if (_youTubeLoadedLimit > 0)
                    {
                        await _topicSearchPager.FetchNextAsync(_youTubeLoadedLimit, ct);
                    }
                }

                (fetched, hasMore) = await _topicSearchPager.FetchNextAsync(YouTubeTopicPageStep, ct);
                ActivityNotice = string.Empty;
                _youTubeLoadedLimit += fetched.Count;
            }

            var existingIds = new HashSet<string>(
                YouTubeFeedItems.Select(i => i.Item.VideoId),
                StringComparer.Ordinal);
            var addedCards = new List<YouTubeSearchResultViewModel>();

            foreach (var item in fetched)
            {
                if (existingIds.Add(item.VideoId))
                {
                    var card = new YouTubeSearchResultViewModel(item, PlayYouTubeSearchItemAsync, PromptAndDownloadYouTubeItemAsync);
                    YouTubeFeedItems.Add(card);
                    addedCards.Add(card);
                }
            }

            CanShowMoreYouTube = hasMore;
            if (addedCards.Count > 0 && ShowYouTubeThumbnails)
            {
                _ = LoadYouTubeThumbnailsAsync(addedCards, resetExisting: false);
            }
        }
        catch (OperationCanceledException)
        {
            ActivityNotice = string.Empty;
        }
        catch (Exception ex)
        {
            ActivityNotice = string.Empty;
            _actionLog.Log("Warning", $"Show more YouTube failed: {ex.Message}");
        }
        finally
        {
            if (!_disposed)
            {
                IsLoadingMore = false;
                IsBusy = false;
            }
        }
    }

    [RelayCommand]
    private async Task ShowMoreTwitchAsync()
    {
        var active = CategoryTabs.FirstOrDefault(t => t.IsSelected);
        if (_disposed || IsBusy || !CanShowMoreTwitch || active is null || string.IsNullOrWhiteSpace(_twitchNextCursor))
        {
            return;
        }

        var ct = ResetWorkToken();
        IsBusy = true;
        IsLoadingMore = true;
        try
        {
            var existingUsers = new HashSet<string>(
                LiveStreams.Select(s => s.Stream.UserId),
                StringComparer.Ordinal);
            var addedCards = new List<TwitchStreamCardViewModel>();

            for (var attempt = 0; attempt < 3 && addedCards.Count == 0 && !string.IsNullOrWhiteSpace(_twitchNextCursor); attempt++)
            {
                TwitchStreamsPage page;
                if (active.IsFollowingTab)
                {
                    page = await _twitch.GetFollowedLiveStreamsAsync(ct, _twitchNextCursor);
                }
                else if (!string.IsNullOrWhiteSpace(active.GameId))
                {
                    var lang = string.Equals(SelectedLanguage, "All", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : SelectedLanguage;
                    page = await _twitch.GetLiveStreamsByGameIdAsync(active.GameId, lang, ct, _twitchNextCursor);
                }
                else
                {
                    _twitchNextCursor = null;
                    break;
                }

                foreach (var stream in page.Streams)
                {
                    if (existingUsers.Add(stream.UserId))
                    {
                        var card = new TwitchStreamCardViewModel(stream, PlayTwitchStreamCardAsync);
                        LiveStreams.Add(card);
                        addedCards.Add(card);
                    }
                }

                _twitchNextCursor = page.Streams.Count > 0 ? page.NextCursor : null;
            }

            CanShowMoreTwitch = !string.IsNullOrWhiteSpace(_twitchNextCursor);

            if (addedCards.Count > 0 && ShowThumbnails)
            {
                _ = LoadTwitchThumbnailsAsync(addedCards, resetExisting: false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _actionLog.Log("Warning", $"Show more Twitch failed: {ex.Message}");
        }
        finally
        {
            if (!_disposed)
            {
                IsLoadingMore = false;
                IsBusy = false;
            }
        }
    }

    public void ResetExpandedFeedsToDefault()
    {
        if (_disposed)
        {
            return;
        }

        var trimmedAny = false;
        var activeYt = YouTubeTabs.FirstOrDefault(t => t.IsSelected);
        var defaultYtCount = activeYt is { Kind: YouTubeFeedTabKind.Topic }
            ? DefaultYouTubeTopicCount
            : DefaultYouTubeFypCount;

        if (YouTubeFeedItems.Count > defaultYtCount)
        {
            for (var i = YouTubeFeedItems.Count - 1; i >= defaultYtCount; i--)
            {
                var item = YouTubeFeedItems[i];
                YouTubeFeedItems.RemoveAt(i);
                item.Dispose();
            }

            _youTubeLoadedLimit = defaultYtCount;
            DisposePager(ref _topicSearchPager);
            CanShowMoreYouTube = YouTubeFeedItems.Count > 0;
            trimmedAny = true;
        }

        if (LiveStreams.Count > DefaultTwitchPageSize)
        {
            for (var i = LiveStreams.Count - 1; i >= DefaultTwitchPageSize; i--)
            {
                var card = LiveStreams[i];
                LiveStreams.RemoveAt(i);
                card.Dispose();
            }

            _twitchNextCursor = _initialTwitchCursor;
            CanShowMoreTwitch = !string.IsNullOrWhiteSpace(_twitchNextCursor);
            trimmedAny = true;
        }

        if (trimmedAny)
        {
            MemoryTrimService.ScheduleTrim();
        }
    }

    [RelayCommand]
    private Task TogglePlayPauseAsync() => _engine.TogglePauseAsync();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _module.StateChanged -= OnModuleStateChanged;
        _module.StatusSummaryChanged -= OnModuleStatusSummaryChanged;
        _engine.PlaybackStateChanged -= OnEnginePlaybackStateChanged;
        _engine.PositionChanged -= OnEnginePositionChanged;
        _engine.PlaybackError -= OnEnginePlaybackError;
        RequestCloseToDashboard = null;

        CancelAndDispose(ref _workCts);
        CancelAndDispose(ref _thumbCts);
        DisposePager(ref _topicSearchPager);
        DisposePager(ref _textSearchPager);

        ClearLiveStreamCards();
        ClearYouTubeFeedCards();
        ClearYouTubeSearchCards();
        YouTubeTabs.Clear();
        CategoryTabs.Clear();
        CategorySearchResults.Clear();
    }

    private async Task PromptAndDownloadYouTubeItemAsync(YouTubeSearchItem item)
    {
        if (_disposed)
        {
            return;
        }

        var kindChoice = await _dialogService.ShowAsync(new DialogRequest(
            DialogKind.Confirm,
            "Download Media",
            $"Choose download type for \"{item.Title}\":",
            item.Channel,
            [
                new DialogButtonOption("audio", "Audio Only", IsPrimary: true),
                new DialogButtonOption("video", "Video + Audio"),
                new DialogButtonOption("cancel", "Cancel")
            ]));

        if (_disposed || kindChoice is null or "cancel")
        {
            return;
        }

        string? preset = null;
        if (kindChoice == "audio")
        {
            var audioChoice = await _dialogService.ShowAsync(new DialogRequest(
                DialogKind.Confirm,
                "Choose Audio Format",
                $"Select audio format for \"{item.Title}\":",
                null,
                [
                    new DialogButtonOption("mp3", DownloadFormatPresets.Mp3Audio, IsPrimary: true),
                    new DialogButtonOption("m4a", DownloadFormatPresets.M4aAudio),
                    new DialogButtonOption("cancel", "Cancel")
                ]));

            preset = audioChoice switch
            {
                "mp3" => DownloadFormatPresets.Mp3Audio,
                "m4a" => DownloadFormatPresets.M4aAudio,
                _ => null
            };
        }
        else if (kindChoice == "video")
        {
            var videoChoice = await _dialogService.ShowAsync(new DialogRequest(
                DialogKind.Confirm,
                "Choose Video Format",
                $"Select video quality for \"{item.Title}\":",
                null,
                [
                    new DialogButtonOption("best", DownloadFormatPresets.BestMp4, IsPrimary: true),
                    new DialogButtonOption("1080", DownloadFormatPresets.P1080),
                    new DialogButtonOption("cancel", "Cancel")
                ]));

            preset = videoChoice switch
            {
                "best" => DownloadFormatPresets.BestMp4,
                "1080" => DownloadFormatPresets.P1080,
                _ => null
            };
        }

        if (_disposed || string.IsNullOrWhiteSpace(preset))
        {
            return;
        }

        try
        {
            await _downloadDispatcher.EnqueueAsync(item.Url, item.Title, preset);
            var notice = $"Queued download ({preset}): {item.Title}";
            ActivityNotice = notice;
            _ = Task.Run(async () =>
            {
                await Task.Delay(4000).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed && string.Equals(ActivityNotice, notice, StringComparison.Ordinal))
                    {
                        ActivityNotice = string.Empty;
                    }
                });
            });
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                await _dialogService.ShowErrorAsync("Download Failed", "Could not queue download.", ex.Message);
            }
        }
    }

    private async Task PlayYouTubeSearchItemAsync(YouTubeSearchItem item)
    {
        if (_disposed)
        {
            return;
        }

        var ct = ResetWorkToken();
        IsBusy = true;
        try
        {
            ActivityNotice = $"Loading audio for \"{item.Title}\"...";
            var allowFallback = ReadBoolSetting(DatabaseService.KeyAudioYtDlpFallback, defaultValue: true);
            var cookieArg = ResolveConfiguredCookieArg(out _);
            var qualityPreset = _database.GetSetting(DatabaseService.KeyAudioQualityPreset) ?? "max";
            var track = await _youtube.ExtractAudioOnlyAsync(
                item.Url,
                allowFallback,
                ct,
                cookieArg,
                knownTitle: item.Title,
                knownChannel: item.Channel,
                knownDuration: item.Duration,
                qualityPreset: qualityPreset);
            await _engine.PlayAsync(track, ct);
            ActivityNotice = string.Empty;
            _actionLog.Log("Audio", $"Playing YouTube: {track.Title}");
        }
        catch (OperationCanceledException)
        {
            ActivityNotice = string.Empty;
        }
        catch (Exception ex)
        {
            ActivityNotice = string.Empty;
            if (!_disposed)
            {
                _actionLog.Log("Error", $"YouTube playback failed: {ex.Message}");
                await _dialogService.ShowErrorAsync("YouTube Playback Error", $"Failed to play \"{item.Title}\".", ex.Message);
            }
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
            }
        }
    }

    private async Task PlayTwitchStreamCardAsync(TwitchLiveStream stream)
    {
        if (_disposed)
        {
            return;
        }

        var ct = ResetWorkToken();
        IsBusy = true;
        try
        {
            await ExtractAndPlayTwitchUrlAsync(
                stream.StreamPageUrl,
                stream.Title,
                stream.UserName,
                stream.GameName,
                ct);
        }
        catch (OperationCanceledException)
        {
            ActivityNotice = string.Empty;
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
            }
        }
    }

    private async Task ExtractAndPlayTwitchUrlAsync(
        string url,
        string? titleOverride,
        string? channelOverride,
        string? gameOverride,
        CancellationToken ct)
    {
        ActivityNotice = "Extracting audio-only HLS via yt-dlp...";
        var qualityPreset = _database.GetSetting(DatabaseService.KeyAudioQualityPreset) ?? "max";
        var result = await _ytDlp.ExtractAudioStreamUrlAsync(url, isTwitch: true, ct, qualityPreset: qualityPreset);
        if (!result.Success || string.IsNullOrWhiteSpace(result.StreamUrl))
        {
            ActivityNotice = string.Empty;
            if (_disposed)
            {
                return;
            }

            if (result.IsMissingExecutable)
            {
                await _dialogService.ShowWarningAsync(
                    "yt-dlp.exe Missing",
                    "yt-dlp.exe is required to extract Twitch audio-only streams. Place yt-dlp.exe in the Tools folder next to REM.exe or add it to your PATH.",
                    result.ErrorMessage);
            }
            else
            {
                _actionLog.Log("Error", $"yt-dlp failed for '{url}': {result.ErrorMessage}");
                await _dialogService.ShowErrorAsync("Twitch Stream Error", "Could not extract audio from this Twitch stream.", result.ErrorMessage);
            }

            return;
        }

        var channel = channelOverride ?? ExtractTwitchLoginFromUrl(url);
        var title = string.IsNullOrWhiteSpace(titleOverride) ? $"{channel} (Live Audio)" : titleOverride;
        var source = string.IsNullOrWhiteSpace(gameOverride) ? "Twitch" : $"Twitch · {gameOverride}";
        if (!string.IsNullOrWhiteSpace(result.QualityBadge))
        {
            source = $"{source} · {result.QualityBadge}";
        }
        var track = new ExtractedAudioTrack(title, channel, result.StreamUrl, IsLive: true, Duration: null, source);

        await _engine.PlayAsync(track, ct);
        ActivityNotice = string.Empty;
        _actionLog.Log("Audio", $"Playing Twitch stream: {channel} — {title}");
    }

    private async Task TogglePinCategoryAsync(TwitchCategorySearchHitViewModel hit)
    {
        if (hit.IsPinned)
        {
            _database.RemoveProfileTwitchCategory(_profileId, hit.Name);
            hit.IsPinned = false;
            _actionLog.Log("Twitch", $"Unpinned category '{hit.Name}'.");
        }
        else
        {
            _database.AddProfileTwitchCategory(_profileId, hit.Name, hit.GameId);
            hit.IsPinned = true;
            _actionLog.Log("Twitch", $"Pinned category '{hit.Name}'.");
        }

        var selectedName = CategoryTabs.FirstOrDefault(t => t.IsSelected)?.Name;
        RebuildCategoryTabs(selectedName ?? hit.Name);
        var tab = CategoryTabs.FirstOrDefault(t => t.IsSelected);
        if (tab is not null)
        {
            await LoadStreamsForTabAsync(tab);
        }
    }

    private void LoadFromSettingsAndTabs(bool loadActiveFeed)
    {
        ShowThumbnails = ReadBoolSetting(DatabaseService.KeyAudioShowThumbnails, defaultValue: false);
        ShowYouTubeThumbnails = ReadBoolSetting(DatabaseService.KeyAudioShowYouTubeThumbnails, defaultValue: true);

        var selectedYouTubeTab = YouTubeTabs.FirstOrDefault(t => t.IsSelected)?.Name;
        RebuildYouTubeTabs(selectedYouTubeTab);

        var selectedTwitchTab = CategoryTabs.FirstOrDefault(t => t.IsSelected)?.Name;
        RebuildCategoryTabs(selectedTwitchTab);

        if (!loadActiveFeed)
        {
            return;
        }

        if (IsTwitchActive)
        {
            var active = CategoryTabs.FirstOrDefault(t => t.IsSelected) ?? CategoryTabs.FirstOrDefault();
            if (active is not null)
            {
                _ = LoadStreamsForTabAsync(active);
            }
        }
        else
        {
            var active = YouTubeTabs.FirstOrDefault(t => t.IsSelected) ?? YouTubeTabs.FirstOrDefault();
            if (active is not null)
            {
                _ = LoadYouTubeTabAsync(active);
            }
        }
    }

    private void RebuildYouTubeTabs(string? preferredName)
    {
        var savedTopics = _database.GetProfileYouTubeTopics(_profileId);
        YouTubeTabs.Clear();

        YouTubeTabs.Add(new YouTubeFeedTabViewModel("For You", YouTubeFeedTabKind.AccountFyp, LoadYouTubeTabAsync));
        YouTubeTabs.Add(new YouTubeFeedTabViewModel("Subscriptions", YouTubeFeedTabKind.AccountSubscriptions, LoadYouTubeTabAsync));

        foreach (var topic in savedTopics)
        {
            YouTubeTabs.Add(new YouTubeFeedTabViewModel(topic, YouTubeFeedTabKind.Topic, LoadYouTubeTabAsync));
        }

        var selected = YouTubeTabs.FirstOrDefault(t =>
                !string.IsNullOrWhiteSpace(preferredName) &&
                string.Equals(t.Name, preferredName, StringComparison.OrdinalIgnoreCase))
            ?? YouTubeTabs.FirstOrDefault();

        if (selected is not null)
        {
            selected.IsSelected = true;
        }
    }

    private async Task LoadYouTubeTabAsync(YouTubeFeedTabViewModel tab)
    {
        if (_disposed)
        {
            return;
        }

        foreach (var t in YouTubeTabs)
        {
            t.IsSelected = ReferenceEquals(t, tab);
        }

        ClearSearchResults();
        YouTubeFeedError = string.Empty;
        ShowYouTubeBrowserPrompt = false;
        YouTubeBrowserPromptDetail = string.Empty;
        CanShowMoreYouTube = false;

        var defaultCount = tab.Kind == YouTubeFeedTabKind.Topic ? DefaultYouTubeTopicCount : DefaultYouTubeFypCount;
        _youTubeLoadedLimit = defaultCount;

        var ct = ResetWorkToken();
        IsBusy = true;
        try
        {
            List<YouTubeSearchItem> items;
            var hasMoreTopic = true;
            DisposePager(ref _topicSearchPager);
            if (tab.Kind is YouTubeFeedTabKind.AccountFyp or YouTubeFeedTabKind.AccountSubscriptions)
            {
                var cookieArg = ResolveConfiguredCookieArg(out var statusMessage);
                if (string.IsNullOrWhiteSpace(cookieArg))
                {
                    ClearYouTubeFeedCards();
                    ShowYouTubeBrowserPrompt = true;
                    YouTubeBrowserPromptDetail = statusMessage;
                    _youtubeFeedLoaded = true;
                    return;
                }

                var feedUrl = tab.Kind == YouTubeFeedTabKind.AccountFyp
                    ? "https://www.youtube.com/feed/recommended"
                    : "https://www.youtube.com/feed/subscriptions";

                ActivityNotice = tab.Kind == YouTubeFeedTabKind.AccountFyp
                    ? "Loading your YouTube For You feed from browser session..."
                    : "Loading your YouTube Subscriptions feed from browser session...";

                var feedResult = await _ytDlp.FetchYouTubeFeedAsync(feedUrl, cookieArg, DefaultYouTubeFypCount, ct, startIndex: 1);
                ActivityNotice = string.Empty;

                if (feedResult.IsMissingExecutable)
                {
                    ClearYouTubeFeedCards();
                    YouTubeFeedError = feedResult.ErrorMessage ?? "yt-dlp.exe is missing.";
                    _youtubeFeedLoaded = true;
                    return;
                }

                if (!feedResult.Success)
                {
                    ClearYouTubeFeedCards();
                    YouTubeFeedError = string.IsNullOrWhiteSpace(feedResult.ErrorMessage)
                        ? "Could not load your YouTube account feed. Make sure you are signed in to YouTube in Zen/selected browser."
                        : feedResult.ErrorMessage;
                    _youtubeFeedLoaded = true;
                    return;
                }

                items = feedResult.Items;
            }
            else
            {
                ActivityNotice = $"Loading YouTube '{tab.Name}' mix...";
                _topicSearchPager = _youtube.CreateSearchPager(tab.Name);
                (items, hasMoreTopic) = await _topicSearchPager.FetchNextAsync(DefaultYouTubeTopicCount, ct);
                ActivityNotice = string.Empty;
            }

            ClearYouTubeFeedCards();
            foreach (var item in items)
            {
                YouTubeFeedItems.Add(new YouTubeSearchResultViewModel(item, PlayYouTubeSearchItemAsync, PromptAndDownloadYouTubeItemAsync));
            }

            _youtubeFeedLoaded = true;
            CanShowMoreYouTube = items.Count > 0 && hasMoreTopic;
            if (items.Count == 0)
            {
                YouTubeFeedError = tab.Kind == YouTubeFeedTabKind.AccountFyp
                    ? "Your YouTube For You feed returned no videos. Try refreshing or pinning a topic tab."
                    : $"No videos returned for '{tab.Name}'.";
            }
            else if (ShowYouTubeThumbnails)
            {
                _ = LoadYouTubeThumbnailsAsync(YouTubeFeedItems.ToList());
            }
        }
        catch (OperationCanceledException)
        {
            ActivityNotice = string.Empty;
        }
        catch (Exception ex)
        {
            ActivityNotice = string.Empty;
            ClearYouTubeFeedCards();
            YouTubeFeedError = ex.Message;
            _actionLog.Log("Warning", $"YouTube feed load error: {ex.Message}");
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
            }
        }
    }

    private void RebuildCategoryTabs(string? preferredName)
    {
        var saved = _database.GetProfileTwitchCategories(_profileId);
        CategoryTabs.Clear();

        foreach (var cat in saved)
        {
            CategoryTabs.Add(new TwitchCategoryTabViewModel(cat.Name, cat.GameId, false, LoadStreamsForTabAsync));
        }

        CategoryTabs.Add(new TwitchCategoryTabViewModel("Following (Live)", null, true, LoadStreamsForTabAsync));

        var selected = CategoryTabs.FirstOrDefault(t =>
                !string.IsNullOrWhiteSpace(preferredName) &&
                string.Equals(t.Name, preferredName, StringComparison.OrdinalIgnoreCase))
            ?? CategoryTabs.FirstOrDefault();

        if (selected is not null)
        {
            selected.IsSelected = true;
        }
    }

    private async Task LoadStreamsForTabAsync(TwitchCategoryTabViewModel tab)
    {
        if (_disposed)
        {
            return;
        }

        foreach (var t in CategoryTabs)
        {
            t.IsSelected = ReferenceEquals(t, tab);
        }

        TwitchFeedError = string.Empty;
        ShowTwitchSetupPrompt = false;
        ShowFollowAuthPrompt = false;
        CanShowMoreTwitch = false;
        _initialTwitchCursor = null;
        _twitchNextCursor = null;

        if (!_twitch.HasClientCredentials)
        {
            ClearLiveStreamCards();
            ShowTwitchSetupPrompt = true;
            _twitchFeedLoaded = true;
            return;
        }

        if (tab.IsFollowingTab && !_twitch.HasUserAuth)
        {
            ClearLiveStreamCards();
            ShowFollowAuthPrompt = true;
            _twitchFeedLoaded = true;
            return;
        }

        var ct = ResetWorkToken();
        IsBusy = true;
        try
        {
            TwitchStreamsPage page;
            if (tab.IsFollowingTab)
            {
                page = await _twitch.GetFollowedLiveStreamsAsync(ct);
            }
            else
            {
                var gameId = tab.GameId;
                if (string.IsNullOrWhiteSpace(gameId))
                {
                    var resolved = await _twitch.ResolveGameIdsAsync([tab.Name], ct);
                    resolved.TryGetValue(tab.Name, out gameId);
                    if (!string.IsNullOrWhiteSpace(gameId))
                    {
                        tab.GameId = gameId;
                        _database.AddProfileTwitchCategory(_profileId, tab.Name, gameId);
                    }
                }

                if (string.IsNullOrWhiteSpace(gameId))
                {
                    ClearLiveStreamCards();
                    TwitchFeedError = $"Could not resolve Twitch category '{tab.Name}'.";
                    return;
                }

                var lang = string.Equals(SelectedLanguage, "All", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : SelectedLanguage;
                page = await _twitch.GetLiveStreamsByGameIdAsync(gameId, lang, ct);
            }

            ClearLiveStreamCards();
            foreach (var s in page.Streams)
            {
                LiveStreams.Add(new TwitchStreamCardViewModel(s, PlayTwitchStreamCardAsync));
            }

            _twitchFeedLoaded = true;
            _initialTwitchCursor = page.NextCursor;
            _twitchNextCursor = page.NextCursor;
            CanShowMoreTwitch = page.Streams.Count > 0 && !string.IsNullOrWhiteSpace(page.NextCursor);

            if (page.Streams.Count == 0)
            {
                TwitchFeedError = tab.IsFollowingTab
                    ? "None of your followed channels are currently live."
                    : $"No live streams found for {tab.Name}.";
            }
            else if (ShowThumbnails)
            {
                _ = LoadTwitchThumbnailsAsync(LiveStreams.ToList());
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ClearLiveStreamCards();
            TwitchFeedError = ex.Message;
            _actionLog.Log("Warning", $"Twitch feed load error: {ex.Message}");
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
            }
        }
    }

    private async Task LoadYouTubeThumbnailsAsync(List<YouTubeSearchResultViewModel> cards, bool resetExisting = true)
    {
        var ct = resetExisting ? ResetThumbToken() : GetOrCreateThumbToken();
        foreach (var card in cards)
        {
            if (ct.IsCancellationRequested || _disposed)
            {
                break;
            }

            var bmp = await _youtube.LoadSmallThumbnailAsync(card.EffectiveThumbnailUrl, ct);
            if (bmp is not null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed || ct.IsCancellationRequested)
                    {
                        bmp.Dispose();
                        return;
                    }

                    card.SetThumbnail(bmp);
                });
            }
        }
    }

    private async Task LoadTwitchThumbnailsAsync(List<TwitchStreamCardViewModel> cards, bool resetExisting = true)
    {
        var ct = resetExisting ? ResetThumbToken() : GetOrCreateThumbToken();
        foreach (var card in cards)
        {
            if (ct.IsCancellationRequested || _disposed)
            {
                break;
            }

            var bmp = await _twitch.LoadSmallThumbnailAsync(card.Stream.ThumbnailUrl, ct);
            if (bmp is not null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed || ct.IsCancellationRequested)
                    {
                        bmp.Dispose();
                        return;
                    }

                    card.SetThumbnail(bmp);
                });
            }
        }
    }

    private string? ResolveConfiguredCookieArg(out string statusMessage)
    {
        var browserKey = _database.GetSetting(DatabaseService.KeyAudioYouTubeCookieBrowser);
        if (string.IsNullOrWhiteSpace(browserKey))
        {
            browserKey = _ytDlp.DetectZenProfileDirectory() is not null ? "zen" : "none";
        }

        var customProfile = _database.GetSetting(DatabaseService.KeyAudioYouTubeCookieProfile);
        return _ytDlp.ResolveCookieBrowserArgument(browserKey, customProfile, out statusMessage);
    }

    private void ClearYouTubeFeedCards()
    {
        CancelAndDispose(ref _thumbCts);
        foreach (var item in YouTubeFeedItems)
        {
            item.Dispose();
        }

        YouTubeFeedItems.Clear();
    }

    private void ClearYouTubeSearchCards(bool disposePager = true)
    {
        if (disposePager)
        {
            DisposePager(ref _textSearchPager);
        }

        CancelAndDispose(ref _thumbCts);
        foreach (var item in SearchResults)
        {
            item.Dispose();
        }

        SearchResults.Clear();
        HasSearchResults = false;
        CanShowMoreYouTubeSearch = false;
    }

    private static void DisposePager(ref YouTubeSearchPager? slot)
    {
        var current = Interlocked.Exchange(ref slot, null);
        if (current is not null)
        {
            _ = Task.Run(async () =>
            {
                try { await current.DisposeAsync().ConfigureAwait(false); } catch { }
            });
        }
    }

    private void ClearLiveStreamCards()
    {
        CancelAndDispose(ref _thumbCts);
        foreach (var card in LiveStreams)
        {
            card.Dispose();
        }

        LiveStreams.Clear();
    }

    private CancellationToken ResetWorkToken()
    {
        CancelAndDispose(ref _workCts);
        if (_disposed)
        {
            return new CancellationToken(canceled: true);
        }

        _workCts = new CancellationTokenSource();
        return _workCts.Token;
    }

    private CancellationToken ResetThumbToken()
    {
        CancelAndDispose(ref _thumbCts);
        if (_disposed)
        {
            return new CancellationToken(canceled: true);
        }

        _thumbCts = new CancellationTokenSource();
        return _thumbCts.Token;
    }

    private CancellationToken GetOrCreateThumbToken()
    {
        if (_disposed)
        {
            return new CancellationToken(canceled: true);
        }

        if (_thumbCts is null || _thumbCts.IsCancellationRequested)
        {
            CancelAndDispose(ref _thumbCts);
            _thumbCts = new CancellationTokenSource();
        }

        return _thumbCts.Token;
    }

    private static void CancelAndDispose(ref CancellationTokenSource? cts)
    {
        var current = Interlocked.Exchange(ref cts, null);
        if (current is null)
        {
            return;
        }

        try
        {
            current.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            current.Dispose();
        }
    }

    private bool ReadBoolSetting(string key, bool defaultValue)
    {
        var raw = _database.GetSetting(key);
        return bool.TryParse(raw, out var val) ? val : defaultValue;
    }

    private void OnModuleStateChanged(object? sender, ModuleState state) => RefreshModuleHeaderStatus();

    private void OnModuleStatusSummaryChanged(object? sender, EventArgs e) => RefreshModuleHeaderStatus();

    private void OnEnginePlaybackStateChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(SyncPlaybackFromEngine);

    private void OnEnginePositionChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(SyncPositionFromEngine);

    private void OnEnginePlaybackError(object? sender, string message) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                _ = _dialogService.ShowErrorAsync("Playback Error", message, _engine.CurrentTrack?.StreamUrl);
            }
        });

    private void RefreshModuleHeaderStatus()
    {
        StateLabel = _module.State switch
        {
            ModuleState.RunningUI => "Running (UI)",
            ModuleState.RunningHeadless => "Running (Headless)",
            ModuleState.Paused => "Paused",
            _ => "Stopped"
        };
        StatusSummary = _module.StatusSummary;
    }

    private void SyncPlaybackFromEngine()
    {
        var track = _engine.CurrentTrack;
        HasCurrentTrack = track is not null;
        if (track is null)
        {
            CurrentTrackTitle = "Nothing playing";
            CurrentTrackChannel = "Browse YouTube FYP or Twitch Live";
            CurrentSourceLabel = string.Empty;
            IsLiveTrack = false;
            IsPlaying = false;
            SeekMaxSeconds = 1;
            PositionDisplay = "0:00";
            DurationDisplay = "0:00";
            return;
        }

        CurrentTrackTitle = track.Title;
        CurrentTrackChannel = track.Channel;
        CurrentSourceLabel = track.IsLive ? $"{track.SourceLabel} · LIVE" : track.SourceLabel;
        IsLiveTrack = track.IsLive;
        IsPlaying = _engine.IsPlaying;
        SyncPositionFromEngine();
    }

    private void SyncPositionFromEngine()
    {
        _isSyncingSeek = true;
        try
        {
            var dur = Math.Max(1, _engine.DurationSeconds);
            SeekMaxSeconds = dur;
            SeekPositionSeconds = Math.Clamp(_engine.CurrentSeconds, 0, dur);
            PositionDisplay = FormatSeconds(_engine.CurrentSeconds);
            DurationDisplay = _engine.DurationSeconds > 0 ? FormatSeconds(_engine.DurationSeconds) : "0:00";
        }
        finally
        {
            _isSyncingSeek = false;
        }
    }

    private static string FormatSeconds(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return "0:00";
        }

        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    private static string ExtractTwitchLoginFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var seg = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(seg))
            {
                return seg;
            }
        }

        return "Twitch Stream";
    }
}
