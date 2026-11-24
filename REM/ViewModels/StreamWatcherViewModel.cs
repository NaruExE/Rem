using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using REM.Models;
using REM.Modules;
using REM.Services;

namespace REM.ViewModels;

public sealed partial class StreamWatcherViewModel : ObservableObject, IDisposable
{
    private const int DefaultYouTubeFypCount = 18;
    private const int YouTubeFypPageStep = 18;
    private const int DefaultYouTubeTopicCount = 12;
    private const int YouTubeTopicPageStep = 12;
    private const int DefaultTwitchPageSize = 20;
    private const int MaxChatMessages = 180;

    private static readonly string[] Languages =
    [
        "All", "en", "ar", "es", "fr", "de", "ja", "ko", "pt", "ru", "zh"
    ];

    private readonly StreamWatcherModule _module;
    private readonly DatabaseService _database;
    private readonly ActionLogService _actionLog;
    private readonly DialogService _dialogService;
    private readonly YtDlpService _ytDlp;
    private readonly YouTubeAudioService _youtube;
    private readonly TwitchHelixService _twitch;
    private readonly VideoPlayerService _engine;
    private readonly TwitchChatService _chatService;
    private readonly TwitchEmoteCacheService _emoteCache;
    private readonly Action _onOpenSettings;

    private long _profileId;
    private TaskCompletionSource<bool> _surfaceAttachedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _workCts;
    private CancellationTokenSource? _thumbCts;
    private CancellationTokenSource? _chatWorkCts;
    private CancellationTokenSource? _toastCts;
    private YouTubeSearchPager? _textSearchPager;
    private YouTubeSearchPager? _topicSearchPager;
    private string? _initialTwitchCursor;
    private string? _twitchNextCursor;
    private int _youTubeLoadedLimit = DefaultYouTubeFypCount;
    private bool _youtubeFeedLoaded;
    private bool _twitchFeedLoaded;

    private string? _activeTwitchChannelLogin;
    private string? _activeTwitchBroadcasterId;
    private bool _isScrubbing;
    private bool _syncingVolume;
    private bool _syncingSpeed;
    private bool _syncingQuality;
    private bool _suppressRamToggles;
    private bool _disposed;

    public StreamWatcherViewModel(
        StreamWatcherModule module,
        long profileId,
        DatabaseService database,
        ActionLogService actionLog,
        DialogService dialogService,
        YtDlpService ytDlp,
        YouTubeAudioService youtube,
        TwitchHelixService twitch,
        VideoPlayerService engine,
        TwitchChatService chatService,
        TwitchEmoteCacheService emoteCache,
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
        _chatService = chatService;
        _emoteCache = emoteCache;
        _onOpenSettings = onOpenSettings;

        AccentBrush = new SolidColorBrush(Color.Parse(module.AccentHex));

        SpeedOptions =
        [
            new(0.5f, "0.5x"),
            new(1.0f, "1x"),
            new(1.25f, "1.25x"),
            new(1.5f, "1.5x"),
            new(2.0f, "2x")
        ];

        QualityOptions =
        [
            new("best", "Auto"),
            new("1080", "1080p"),
            new("720", "720p"),
            new("480", "480p"),
            new("360", "360p"),
            new("240", "240p")
        ];

        _suppressRamToggles = true;
        _showThumbnails = ReadBoolSetting(DatabaseService.KeyWatchShowThumbnails, defaultValue: true);
        _isChatEnabled = ReadBoolSetting(DatabaseService.KeyWatchChatEnabled, defaultValue: true);
        _isTwitchEmotesEnabled = ReadBoolSetting(DatabaseService.KeyWatchTwitchEmotesEnabled, defaultValue: true);
        _isSevenTvEmotesEnabled = ReadBoolSetting(DatabaseService.KeyWatchSevenTvEmotesEnabled, defaultValue: true);
        _suppressRamToggles = false;

        var activePlatform = _database.GetSetting(DatabaseService.KeyWatchActivePlatform);
        _isTwitchActive = string.Equals(activePlatform, "twitch", StringComparison.OrdinalIgnoreCase);

        _module.StateChanged += OnModuleStateChanged;
        _module.StatusSummaryChanged += OnModuleStatusSummaryChanged;
        _engine.PlaybackStateChanged += OnEnginePlaybackStateChanged;
        _engine.PositionChanged += OnEnginePositionChanged;
        _engine.DurationChanged += OnEngineDurationChanged;
        _engine.BufferingChanged += OnEngineBufferingChanged;
        _engine.PlaybackError += OnEnginePlaybackError;

        _chatService.MessageReceived += OnChatMessageReceived;
        _chatService.SystemNoticeReceived += OnChatSystemNoticeReceived;
        _chatService.ConnectionStateChanged += OnChatConnectionStateChanged;

        RefreshModuleHeaderStatus();
        SyncFromSnapshot(_engine.GetSnapshot());

        if (!string.IsNullOrWhiteSpace(_engine.CurrentSourceUrl))
        {
            _isWatchMode = true;
            if (YouTubeAudioService.IsTwitchUrl(_engine.CurrentSourceUrl))
            {
                _isCurrentSourceTwitch = true;
                _activeTwitchChannelLogin = ExtractTwitchLoginFromUrl(_engine.CurrentSourceUrl);
                _ = EnsureChatForActiveStreamAsync();
            }
        }

        LoadFromSettingsAndTabs(loadActiveFeed: !_isWatchMode);
    }

    public string Name => _module.Name;
    public IBrush AccentBrush { get; }
    public Geometry IconGeometry => _module.IconGeometry;
    public IReadOnlyList<string> AvailableLanguages => Languages;
    public IReadOnlyList<VideoSpeedOption> SpeedOptions { get; }
    public IReadOnlyList<VideoQualityOption> QualityOptions { get; }

    public ObservableCollection<YouTubeFeedTabViewModel> YouTubeTabs { get; } = [];
    public ObservableCollection<YouTubeSearchResultViewModel> YouTubeFeedItems { get; } = [];
    public ObservableCollection<YouTubeSearchResultViewModel> SearchResults { get; } = [];
    public ObservableCollection<TwitchCategoryTabViewModel> CategoryTabs { get; } = [];
    public ObservableCollection<TwitchStreamCardViewModel> LiveStreams { get; } = [];
    public ObservableCollection<TwitchCategorySearchHitViewModel> CategorySearchResults { get; } = [];
    public ObservableCollection<TwitchChatMessage> ChatMessages { get; } = [];

    public Action? RequestCloseToDashboard { get; set; }
    public event EventHandler<bool>? FullscreenChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowModuleHeader))]
    private bool _isHeaderVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowModuleHeader))]
    [NotifyPropertyChangedFor(nameof(ShowTopBrowseOrWatchBar))]
    private bool _isFullscreen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBrowseMiniPlayer))]
    [NotifyPropertyChangedFor(nameof(ShowChatSidebar))]
    [NotifyPropertyChangedFor(nameof(ShowTopBrowseOrWatchBar))]
    [NotifyPropertyChangedFor(nameof(ShowWatchBottomBar))]
    private bool _isWatchMode;

    public bool ShowModuleHeader => IsHeaderVisible && !IsFullscreen;
    public bool ShowTopBrowseOrWatchBar => !IsFullscreen;
    public bool ShowBrowseMiniPlayer => !IsWatchMode && HasMedia;
    public bool ShowChatSidebar => IsWatchMode && IsCurrentSourceTwitch && IsChatEnabled;
    public bool ShowWatchBottomBar => IsWatchMode && IsControlsVisible;

    [ObservableProperty]
    private string _stateLabel = "Running (UI)";

    [ObservableProperty]
    private string _statusSummary = string.Empty;

    [ObservableProperty]
    private string _searchInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsYouTubeMode))]
    [NotifyPropertyChangedFor(nameof(IsTwitchMode))]
    [NotifyPropertyChangedFor(nameof(ActivePlatformLabel))]
    [NotifyPropertyChangedFor(nameof(SearchWatermark))]
    [NotifyPropertyChangedFor(nameof(PlatformSwitchTooltip))]
    private bool _isTwitchActive;

    public bool IsYouTubeMode => !IsTwitchActive;
    public bool IsTwitchMode => IsTwitchActive;
    public string ActivePlatformLabel => IsTwitchActive ? "Twitch" : "YouTube";
    public string SearchWatermark => IsTwitchActive
        ? "Search YouTube or paste Twitch / YouTube / stream URL..."
        : "Search YouTube or paste any YouTube / Twitch / video URL...";
    public string PlatformSwitchTooltip => IsTwitchActive
        ? "Switch to YouTube Discovery (FYP, Subscriptions, Topics & Search)"
        : "Switch to Twitch Live Discovery";

    [ObservableProperty]
    private bool _isBusy;

    partial void OnIsBusyChanged(bool value)
    {
        if (value)
        {
            IsControlsVisible = true;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMoreButtonLabel))]
    private bool _isLoadingMore;

    public string ShowMoreButtonLabel => IsLoadingMore ? "Loading more..." : "Show More";

    [ObservableProperty]
    private bool _canShowMoreYouTubeSearch;

    [ObservableProperty]
    private bool _canShowMoreYouTube;

    [ObservableProperty]
    private bool _canShowMoreTwitch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivityNotice))]
    private string _activityNotice = string.Empty;

    public bool HasActivityNotice => !string.IsNullOrWhiteSpace(ActivityNotice);

    [ObservableProperty]
    private string _selectedLanguage = "All";

    [ObservableProperty]
    private bool _isCategorySearchOpen;

    [ObservableProperty]
    private string _categorySearchQuery = string.Empty;

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

    public bool HasYouTubeFeedError => !string.IsNullOrWhiteSpace(YouTubeFeedError);

    [ObservableProperty]
    private bool _hasSearchResults;

    [ObservableProperty]
    private string _lastSearchQuery = string.Empty;

    [ObservableProperty]
    private bool _showTwitchSetupPrompt;

    [ObservableProperty]
    private bool _showFollowAuthPrompt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTwitchFeedError))]
    private string _twitchFeedError = string.Empty;

    public bool HasTwitchFeedError => !string.IsNullOrWhiteSpace(TwitchFeedError);

    // RAM-saving feature toggles
    [ObservableProperty]
    private bool _showThumbnails = true;

    partial void OnShowThumbnailsChanged(bool value)
    {
        if (_suppressRamToggles || _disposed)
        {
            return;
        }

        _database.SaveSetting(DatabaseService.KeyWatchShowThumbnails, value ? "true" : "false");
        if (!value)
        {
            CancelAndDispose(ref _thumbCts);
            foreach (var card in YouTubeFeedItems) card.SetThumbnail(null);
            foreach (var card in SearchResults) card.SetThumbnail(null);
            foreach (var card in LiveStreams) card.SetThumbnail(null);
            MemoryTrimService.ScheduleTrim();
        }
        else
        {
            if (YouTubeFeedItems.Count > 0) _ = LoadYouTubeThumbnailsAsync(YouTubeFeedItems.ToList());
            if (SearchResults.Count > 0) _ = LoadYouTubeThumbnailsAsync(SearchResults.ToList());
            if (LiveStreams.Count > 0) _ = LoadTwitchThumbnailsAsync(LiveStreams.ToList());
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChatSidebar))]
    [NotifyPropertyChangedFor(nameof(ChatToggleLabel))]
    private bool _isChatEnabled = true;

    public string ChatToggleLabel => IsChatEnabled ? "Chat: On" : "Chat: Off";

    partial void OnIsChatEnabledChanged(bool value)
    {
        if (_suppressRamToggles || _disposed)
        {
            return;
        }

        _database.SaveSetting(DatabaseService.KeyWatchChatEnabled, value ? "true" : "false");
        if (!value)
        {
            _ = DisableAndFreeChatAsync();
        }
        else if (IsWatchMode && IsCurrentSourceTwitch)
        {
            _ = EnsureChatForActiveStreamAsync();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TwitchEmotesLabel))]
    private bool _isTwitchEmotesEnabled = true;

    public string TwitchEmotesLabel => IsTwitchEmotesEnabled ? "TTV: On" : "TTV: Off";

    partial void OnIsTwitchEmotesEnabledChanged(bool value)
    {
        if (_suppressRamToggles || _disposed)
        {
            return;
        }

        _database.SaveSetting(DatabaseService.KeyWatchTwitchEmotesEnabled, value ? "true" : "false");
        if (!value)
        {
            DetachAndClearEmotes(clearTwitch: true, clearSevenTv: false);
        }
        else
        {
            _ = HydrateVisibleMessageEmotesAsync(reloadSevenTv: false);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SevenTvEmotesLabel))]
    private bool _isSevenTvEmotesEnabled = true;

    public string SevenTvEmotesLabel => IsSevenTvEmotesEnabled ? "7TV: On" : "7TV: Off";

    partial void OnIsSevenTvEmotesEnabledChanged(bool value)
    {
        if (_suppressRamToggles || _disposed)
        {
            return;
        }

        _database.SaveSetting(DatabaseService.KeyWatchSevenTvEmotesEnabled, value ? "true" : "false");
        if (!value)
        {
            DetachAndClearEmotes(clearTwitch: false, clearSevenTv: true);
        }
        else
        {
            _ = HydrateVisibleMessageEmotesAsync(reloadSevenTv: true);
        }
    }

    // Player properties
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChatSidebar))]
    private bool _isCurrentSourceTwitch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBrowseMiniPlayer))]
    [NotifyPropertyChangedFor(nameof(CanSeek))]
    private bool _hasMedia;

    partial void OnHasMediaChanged(bool value)
    {
        if (!value)
        {
            IsControlsVisible = true;
        }
    }

    [ObservableProperty]
    private bool _isPlaying;

    partial void OnIsPlayingChanged(bool value)
    {
        if (!value)
        {
            IsControlsVisible = true;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWatchBottomBar))]
    private bool _isControlsVisible = true;

    [ObservableProperty]
    private string _currentTitle = "No video loaded";

    [ObservableProperty]
    private string _currentChannelSubtitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusBanner))]
    private string _statusBannerText = string.Empty;

    public bool HasStatusBanner => !string.IsNullOrWhiteSpace(StatusBannerText);

    [ObservableProperty]
    private double _seekPositionSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSeek))]
    private double _seekMaxSeconds = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSeek))]
    private bool _isLiveStream;

    public bool CanSeek => HasMedia && !IsLiveStream && SeekMaxSeconds > 1.5;

    [ObservableProperty]
    private string _positionDisplay = "0:00";

    [ObservableProperty]
    private string _durationDisplay = "0:00";

    [ObservableProperty]
    private int _volume = 80;

    partial void OnVolumeChanged(int value)
    {
        if (_syncingVolume || _disposed)
        {
            return;
        }

        _ = _engine.SetVolumeAsync(value);
    }

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private VideoSpeedOption? _selectedSpeedOption;

    partial void OnSelectedSpeedOptionChanged(VideoSpeedOption? value)
    {
        if (_syncingSpeed || value is null || _disposed)
        {
            return;
        }

        _ = _engine.SetPlaybackSpeedAsync(value.Rate);
    }

    [ObservableProperty]
    private VideoQualityOption? _selectedQualityOption;

    partial void OnSelectedQualityOptionChanged(VideoQualityOption? value)
    {
        if (_syncingQuality || value is null || _disposed)
        {
            return;
        }

        _ = SwitchQualityAsync(value);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoopTooltip))]
    private bool _isLoopOne;

    public string LoopTooltip => IsLoopOne ? "Loop: On (L)" : "Loop: Off (L)";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomAspectRatio))]
    private string _aspectRatioLabel = "Default";

    public bool IsCustomAspectRatio => !string.Equals(AspectRatioLabel, "Default", StringComparison.Ordinal);

    // Chat state
    [ObservableProperty]
    private string _chatStatusText = "Chat idle";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowReconnectForChat))]
    [NotifyPropertyChangedFor(nameof(ChatInputWatermark))]
    private bool _canSendChat;

    public bool ShowReconnectForChat => IsCurrentSourceTwitch && IsChatEnabled && !CanSendChat;
    public string ChatInputWatermark => CanSendChat ? "Send a message..." : "Connect Twitch above to chat...";

    [ObservableProperty]
    private bool _isReconnectingTwitch;

    [ObservableProperty]
    private string _chatInputText = string.Empty;

    [ObservableProperty]
    private bool _isChatAutoScrollPaused;

    partial void OnSelectedLanguageChanged(string value)
    {
        if (!IsTwitchMode || _disposed)
        {
            return;
        }

        var activeTab = CategoryTabs.FirstOrDefault(t => t.IsSelected);
        if (activeTab is not null && !activeTab.IsFollowingTab)
        {
            _ = LoadStreamsForTabAsync(activeTab);
        }
    }

    public async Task<MediaPlayer> EnsurePlayerReadyForSurfaceAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _engine.EnsurePlayerReadyAsync();
    }

    public void NotifySurfaceAttached(bool reattachVideoTrack)
    {
        if (_disposed)
        {
            return;
        }

        _surfaceAttachedTcs.TrySetResult(true);
        if (reattachVideoTrack && (_engine.IsHeadlessAudioOnly || _engine.CurrentSourceUrl is not null))
        {
            _ = _engine.ReattachVideoSurfaceAsync();
        }
    }

    public void NotifySurfaceDetached()
    {
        _engine.MarkSurfaceDetached();
        if (_surfaceAttachedTcs.Task.IsCompleted)
        {
            _surfaceAttachedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void ShowControls() => IsControlsVisible = true;

    public void HideControlsIfEligible()
    {
        if (!_disposed && IsWatchMode && HasMedia && IsPlaying && !IsBusy && !_isScrubbing)
        {
            IsControlsVisible = false;
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
        LoadFromSettingsAndTabs(loadActiveFeed: !IsWatchMode);
    }

    public void RefreshFromSettings()
    {
        _youtubeFeedLoaded = false;
        _twitchFeedLoaded = false;
        LoadFromSettingsAndTabs(loadActiveFeed: !IsWatchMode);

        if (IsWatchMode && IsCurrentSourceTwitch && IsChatEnabled)
        {
            _ = EnsureChatForActiveStreamAsync();
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

    public void BeginScrub() => _isScrubbing = true;

    public void UpdateScrubPreview(double seconds)
    {
        if (_isScrubbing)
        {
            PositionDisplay = VideoPlayerViewModel.FormatDuration((long)(Math.Max(0, seconds) * 1000.0));
        }
    }

    public async Task CommitScrubAsync(double seconds)
    {
        _isScrubbing = false;
        var targetMs = (long)Math.Round(Math.Max(0, seconds) * 1000.0);
        await _engine.SeekAsync(targetMs);
    }

    public async Task SeekRelativeAsync(long deltaMs)
    {
        if (_disposed || !CanSeek)
        {
            return;
        }

        await _engine.SeekAsync(_engine.PositionMs + deltaMs);
    }

    public void AdjustVolume(int delta)
    {
        if (!_disposed)
        {
            Volume = Math.Clamp(Volume + delta, 0, 100);
        }
    }

    public void StepSpeed(int direction)
    {
        if (_disposed || SpeedOptions.Count == 0)
        {
            return;
        }

        var idx = -1;
        for (var i = 0; i < SpeedOptions.Count; i++)
        {
            if (ReferenceEquals(SpeedOptions[i], SelectedSpeedOption))
            {
                idx = i;
                break;
            }
        }

        if (idx < 0) idx = 1;
        var nextIdx = Math.Clamp(idx + direction, 0, SpeedOptions.Count - 1);
        SelectedSpeedOption = SpeedOptions[nextIdx];
        ShowTransientBanner($"Speed: {SelectedSpeedOption.Label}");
    }

    [RelayCommand]
    private void ToggleHeader() => IsHeaderVisible = !IsHeaderVisible;

    [RelayCommand]
    private void OpenSettings() => _onOpenSettings();

    [RelayCommand]
    private void BackToDashboard() => RequestCloseToDashboard?.Invoke();

    [RelayCommand]
    private Task MinimizeToHeadlessAsync() => _module.PromptAndRunHeadlessAsync();

    [RelayCommand]
    private void PauseModule() => _module.Pause();

    [RelayCommand]
    private void Kill() => _module.Kill();

    [RelayCommand]
    private void ToggleFullscreen()
    {
        IsFullscreen = !IsFullscreen;
        FullscreenChanged?.Invoke(this, IsFullscreen);
    }

    [RelayCommand]
    private void ExitFullscreen()
    {
        if (!IsFullscreen)
        {
            return;
        }

        IsFullscreen = false;
        FullscreenChanged?.Invoke(this, false);
    }

    [RelayCommand]
    private void BackToBrowse()
    {
        if (_disposed)
        {
            return;
        }

        ExitFullscreen();
        IsWatchMode = false;
        IsControlsVisible = true;
        _ = _engine.DetachVideoSurfaceAsync(VideoHeadlessMode.KeepVideoStream);
        _ = DisableAndFreeChatAsync();

        if (!_youtubeFeedLoaded && !IsTwitchActive)
        {
            var active = YouTubeTabs.FirstOrDefault(t => t.IsSelected) ?? YouTubeTabs.FirstOrDefault();
            if (active is not null) _ = LoadYouTubeTabAsync(active);
        }
        else if (!_twitchFeedLoaded && IsTwitchActive)
        {
            var active = CategoryTabs.FirstOrDefault(t => t.IsSelected) ?? CategoryTabs.FirstOrDefault();
            if (active is not null) _ = LoadStreamsForTabAsync(active);
        }
    }

    [RelayCommand]
    private void ReturnToPlayer()
    {
        if (_disposed || !HasMedia)
        {
            return;
        }

        IsWatchMode = true;
        IsControlsVisible = true;
        if (IsCurrentSourceTwitch && IsChatEnabled)
        {
            _ = EnsureChatForActiveStreamAsync();
        }
    }

    [RelayCommand]
    private void ToggleChatEnabled() => IsChatEnabled = !IsChatEnabled;

    [RelayCommand]
    private void ToggleTwitchEmotes() => IsTwitchEmotesEnabled = !IsTwitchEmotesEnabled;

    [RelayCommand]
    private void ToggleSevenTvEmotes() => IsSevenTvEmotesEnabled = !IsSevenTvEmotesEnabled;

    [RelayCommand]
    private void TogglePlatform()
    {
        IsTwitchActive = !IsTwitchActive;
        _database.SaveSetting(DatabaseService.KeyWatchActivePlatform, IsTwitchActive ? "twitch" : "youtube");

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

        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            if (YouTubeAudioService.IsTwitchUrl(input))
            {
                var login = ExtractTwitchLoginFromUrl(input);
                await StartWatchingStreamAsync(
                    url: input,
                    title: $"{login} (Twitch Live)",
                    channelLabel: $"twitch.tv/{login}",
                    isTwitch: true,
                    twitchLogin: login,
                    twitchUserId: null);
            }
            else
            {
                await StartWatchingStreamAsync(
                    url: input,
                    title: null,
                    channelLabel: uri.Host,
                    isTwitch: false,
                    twitchLogin: null,
                    twitchUserId: null);
            }

            return;
        }

        var ct = ResetWorkToken();
        IsBusy = true;
        try
        {
            if (IsTwitchActive)
            {
                IsTwitchActive = false;
                _database.SaveSetting(DatabaseService.KeyWatchActivePlatform, "youtube");
            }

            if (IsWatchMode)
            {
                BackToBrowse();
            }

            ActivityNotice = $"Searching YouTube for \"{input}\"...";
            DisposePager(ref _textSearchPager);
            _textSearchPager = _youtube.CreateSearchPager(input);
            var (hits, hasMoreSearch) = await _textSearchPager.FetchNextAsync(DefaultYouTubeTopicCount, ct);
            ClearYouTubeSearchCards(disposePager: false);
            foreach (var hit in hits)
            {
                SearchResults.Add(new YouTubeSearchResultViewModel(hit, PlayYouTubeSearchItemAsync));
            }

            LastSearchQuery = input;
            HasSearchResults = SearchResults.Count > 0;
            CanShowMoreYouTubeSearch = HasSearchResults && hasMoreSearch;
            ActivityNotice = SearchResults.Count == 0 ? $"No YouTube results found for \"{input}\"." : string.Empty;

            if (HasSearchResults && ShowThumbnails)
            {
                _ = LoadYouTubeThumbnailsAsync(SearchResults.ToList());
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
                _actionLog.Log("Error", $"Stream Watcher search failed: {ex.Message}");
                await _dialogService.ShowErrorAsync("Search Error", "Could not search YouTube.", ex.Message);
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
                "Add your Twitch Client ID and Client Secret in Settings → Twitch API to search categories.");
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

            var existingIds = new HashSet<string>(SearchResults.Select(i => i.Item.VideoId), StringComparer.Ordinal);
            var addedCards = new List<YouTubeSearchResultViewModel>();
            foreach (var item in fetched)
            {
                if (existingIds.Add(item.VideoId))
                {
                    var card = new YouTubeSearchResultViewModel(item, PlayYouTubeSearchItemAsync);
                    SearchResults.Add(card);
                    addedCards.Add(card);
                }
            }

            CanShowMoreYouTubeSearch = hasMore;
            if (addedCards.Count == 0 && !hasMore)
            {
                ActivityNotice = "Reached the end of YouTube search results.";
            }

            if (addedCards.Count > 0 && ShowThumbnails)
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

            var existingIds = new HashSet<string>(YouTubeFeedItems.Select(i => i.Item.VideoId), StringComparer.Ordinal);
            var addedCards = new List<YouTubeSearchResultViewModel>();
            foreach (var item in fetched)
            {
                if (existingIds.Add(item.VideoId))
                {
                    var card = new YouTubeSearchResultViewModel(item, PlayYouTubeSearchItemAsync);
                    YouTubeFeedItems.Add(card);
                    addedCards.Add(card);
                }
            }

            CanShowMoreYouTube = hasMore;
            if (addedCards.Count > 0 && ShowThumbnails)
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
            var existingUsers = new HashSet<string>(LiveStreams.Select(s => s.Stream.UserId), StringComparer.Ordinal);
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

    // Player commands
    [RelayCommand]
    private Task TogglePlayPauseAsync() => _engine.TogglePauseAsync();

    [RelayCommand]
    private Task ToggleMuteAsync() => _engine.ToggleMuteAsync();

    [RelayCommand]
    private Task SeekBack10Async() => SeekRelativeAsync(-10000);

    [RelayCommand]
    private Task SeekForward10Async() => SeekRelativeAsync(10000);

    [RelayCommand]
    private void ToggleLoop()
    {
        if (_disposed)
        {
            return;
        }

        var next = _engine.LoopMode == VideoLoopMode.One ? VideoLoopMode.Off : VideoLoopMode.One;
        _engine.SetLoopMode(next);
        ShowTransientBanner(next == VideoLoopMode.One ? "Loop: On" : "Loop: Off");
    }

    [RelayCommand]
    private async Task CycleAspectRatioAsync()
    {
        if (_disposed)
        {
            return;
        }

        var label = await _engine.CycleAspectRatioAsync();
        ShowTransientBanner($"Aspect Ratio: {label}");
    }

    [RelayCommand]
    private async Task FrameStepAsync()
    {
        if (_disposed || !HasMedia)
        {
            return;
        }

        if (await _engine.NextFrameAsync())
        {
            ShowTransientBanner("Frame +1");
        }
    }

    [RelayCommand]
    private async Task TakeSnapshotAsync()
    {
        if (_disposed || !HasMedia)
        {
            ShowTransientBanner("Play a stream or video before taking a snapshot.");
            return;
        }

        var path = await _engine.TakeSnapshotAsync();
        ShowTransientBanner(path is not null
            ? $"Snapshot saved: {Path.GetFileName(path)}"
            : "Could not save snapshot (not available in audio-only headless mode).");
    }

    [RelayCommand]
    private async Task SendChatMessageAsync()
    {
        if (_disposed || !IsCurrentSourceTwitch || !CanSendChat)
        {
            return;
        }

        var text = ChatInputText.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        ChatInputText = string.Empty;
        try
        {
            await _chatService.SendMessageAsync(text);
        }
        catch (Exception ex)
        {
            AppendSystemNotice($"Failed to send message: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ReconnectTwitchForChatAsync()
    {
        if (_disposed || IsReconnectingTwitch)
        {
            return;
        }

        if (!_twitch.HasClientCredentials)
        {
            _onOpenSettings();
            return;
        }

        IsReconnectingTwitch = true;
        try
        {
            var (ok, login, err) = await _twitch.AuthenticateUserAsync(CancellationToken.None);
            if (!ok)
            {
                if (!string.IsNullOrWhiteSpace(err))
                {
                    await _dialogService.ShowWarningAsync("Twitch Chat Auth", err);
                }

                return;
            }

            AppendSystemNotice($"Connected as {login} with chat permissions.");
            if (IsWatchMode && IsCurrentSourceTwitch && IsChatEnabled)
            {
                await EnsureChatForActiveStreamAsync();
            }
        }
        finally
        {
            if (!_disposed)
            {
                IsReconnectingTwitch = false;
            }
        }
    }

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
        _engine.DurationChanged -= OnEngineDurationChanged;
        _engine.BufferingChanged -= OnEngineBufferingChanged;
        _engine.PlaybackError -= OnEnginePlaybackError;

        _chatService.MessageReceived -= OnChatMessageReceived;
        _chatService.SystemNoticeReceived -= OnChatSystemNoticeReceived;
        _chatService.ConnectionStateChanged -= OnChatConnectionStateChanged;

        RequestCloseToDashboard = null;

        CancelAndDispose(ref _workCts);
        CancelAndDispose(ref _thumbCts);
        CancelAndDispose(ref _chatWorkCts);
        CancelToastTimer();
        DisposePager(ref _topicSearchPager);
        DisposePager(ref _textSearchPager);

        ClearAllChatMessages();
        ClearLiveStreamCards();
        ClearYouTubeFeedCards();
        ClearYouTubeSearchCards();
        YouTubeTabs.Clear();
        CategoryTabs.Clear();
        CategorySearchResults.Clear();
    }

    private Task PlayYouTubeSearchItemAsync(YouTubeSearchItem item) =>
        StartWatchingStreamAsync(
            url: item.Url,
            title: item.Title,
            channelLabel: item.Channel,
            isTwitch: false,
            twitchLogin: null,
            twitchUserId: null);

    private Task PlayTwitchStreamCardAsync(TwitchLiveStream stream) =>
        StartWatchingStreamAsync(
            url: stream.StreamPageUrl,
            title: string.IsNullOrWhiteSpace(stream.Title) ? stream.UserName : stream.Title,
            channelLabel: string.IsNullOrWhiteSpace(stream.GameName) ? stream.UserName : $"{stream.UserName} · {stream.GameName}",
            isTwitch: true,
            twitchLogin: stream.UserLogin,
            twitchUserId: stream.UserId);

    private async Task StartWatchingStreamAsync(
        string url,
        string? title,
        string channelLabel,
        bool isTwitch,
        string? twitchLogin,
        string? twitchUserId)
    {
        if (_disposed)
        {
            return;
        }

        var ct = ResetWorkToken();
        IsWatchMode = true;
        IsCurrentSourceTwitch = isTwitch;
        CurrentChannelSubtitle = channelLabel;
        _activeTwitchChannelLogin = isTwitch ? twitchLogin : null;
        _activeTwitchBroadcasterId = isTwitch ? twitchUserId : null;

        if (!isTwitch)
        {
            _ = DisableAndFreeChatAsync(clearOnlyKeepToggle: true);
        }
        else if (IsChatEnabled)
        {
            _ = EnsureChatForActiveStreamAsync();
        }

        IsBusy = true;
        StatusBannerText = isTwitch
            ? $"Loading {twitchLogin ?? "Twitch stream"}..."
            : "Resolving video stream...";

        try
        {
            await WaitForSurfaceAttachedAsync();
            var resolved = await _engine.PlaySourceAsync(
                input: url,
                referer: null,
                titleOverride: title,
                startPositionMs: 0,
                playlistItemId: null,
                cancellationToken: ct);
            ShowTransientBanner($"Watching: {resolved.Title}");
        }
        catch (OperationCanceledException)
        {
            StatusBannerText = string.Empty;
        }
        catch (Exception ex)
        {
            StatusBannerText = $"Play failed: {ex.Message}";
            _actionLog.Log("Warning", $"Stream Watcher playback error: {ex.Message}");
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
            }
        }
    }

    private async Task WaitForSurfaceAttachedAsync()
    {
        var timeout = Task.Delay(1500);
        await Task.WhenAny(_surfaceAttachedTcs.Task, timeout);
    }

    private async Task EnsureChatForActiveStreamAsync()
    {
        if (_disposed || !IsWatchMode || !IsCurrentSourceTwitch || !IsChatEnabled ||
            string.IsNullOrWhiteSpace(_activeTwitchChannelLogin))
        {
            return;
        }

        CancelAndDispose(ref _chatWorkCts);
        var cts = new CancellationTokenSource();
        _chatWorkCts = cts;
        var ct = cts.Token;

        var channelLogin = _activeTwitchChannelLogin;
        var broadcasterId = _activeTwitchBroadcasterId;

        ClearAllChatMessages();
        _emoteCache.ClearChannelSevenTvOnly();
        ChatMessages.Add(CreateSystemNoticeMessage($"Joining #{channelLogin} chat..."));

        try
        {
            var (token, ircLogin, displayName, hasChatScopes) = _twitch.GetChatCredentials();
            var useAuth = hasChatScopes && !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(ircLogin);
            CanSendChat = useAuth;

            var connectTask = _chatService.ConnectAsync(
                channelLogin,
                useAuth ? token : null,
                useAuth ? ircLogin : null,
                displayName,
                ct);

            Task? sevenTvTask = null;
            if (IsSevenTvEmotesEnabled)
            {
                sevenTvTask = Task.Run(async () =>
                {
                    if (string.IsNullOrWhiteSpace(broadcasterId))
                    {
                        broadcasterId = await _twitch.ResolveUserIdByLoginAsync(channelLogin, ct).ConfigureAwait(false);
                        _activeTwitchBroadcasterId = broadcasterId;
                    }

                    await _emoteCache.LoadSevenTvEmotesAsync(broadcasterId, ct).ConfigureAwait(false);
                }, ct);
            }

            await connectTask;
            if (sevenTvTask is not null)
            {
                try { await sevenTvTask; } catch { }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed && !ct.IsCancellationRequested)
            {
                AppendSystemNotice($"Chat error: {ex.Message}");
            }
        }
    }

    private async Task DisableAndFreeChatAsync(bool clearOnlyKeepToggle = false)
    {
        CancelAndDispose(ref _chatWorkCts);
        ClearAllChatMessages();

        try
        {
            await _chatService.DisconnectAsync();
        }
        catch
        {
        }

        _emoteCache.ClearAll();
        if (!clearOnlyKeepToggle)
        {
            MemoryTrimService.ScheduleTrim();
        }
    }

    private void DetachAndClearEmotes(bool clearTwitch, bool clearSevenTv)
    {
        foreach (var msg in ChatMessages)
        {
            var changed = false;
            foreach (var seg in msg.Segments)
            {
                if (!seg.IsEmote || seg.EmoteBitmap is null)
                {
                    continue;
                }

                if ((clearTwitch && !seg.IsSevenTv) || (clearSevenTv && seg.IsSevenTv))
                {
                    seg.EmoteBitmap = null;
                    changed = true;
                }
            }

            if (changed)
            {
                msg.NotifyEmotesUpdated();
            }
        }

        if (clearTwitch)
        {
            _emoteCache.ClearTwitchBitmaps();
        }

        if (clearSevenTv)
        {
            _emoteCache.ClearSevenTvAll();
        }

        MemoryTrimService.ScheduleTrim();
    }

    private async Task HydrateVisibleMessageEmotesAsync(bool reloadSevenTv)
    {
        if (_disposed || !IsWatchMode || !IsCurrentSourceTwitch || !IsChatEnabled)
        {
            return;
        }

        var ct = _chatWorkCts?.Token ?? CancellationToken.None;
        if (reloadSevenTv && IsSevenTvEmotesEnabled)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_activeTwitchBroadcasterId) &&
                    !string.IsNullOrWhiteSpace(_activeTwitchChannelLogin))
                {
                    _activeTwitchBroadcasterId = await _twitch.ResolveUserIdByLoginAsync(_activeTwitchChannelLogin, ct);
                }

                await _emoteCache.LoadSevenTvEmotesAsync(_activeTwitchBroadcasterId, ct);
            }
            catch
            {
            }
        }

        var snapshot = ChatMessages.ToList();
        foreach (var msg in snapshot)
        {
            foreach (var seg in msg.Segments)
            {
                if (!seg.IsEmote || seg.EmoteBitmap is not null || string.IsNullOrWhiteSpace(seg.EmoteKey))
                {
                    continue;
                }

                if (seg.IsSevenTv && !IsSevenTvEmotesEnabled) continue;
                if (!seg.IsSevenTv && !IsTwitchEmotesEnabled) continue;

                string? url = null;
                if (seg.IsSevenTv)
                {
                    if (_emoteCache.TryResolveSevenTvEmote(seg.Text, out var stv) && stv is not null)
                    {
                        url = stv.ImageUrl;
                    }
                }
                else
                {
                    url = TwitchEmoteCacheService.BuildTwitchEmoteUrl(seg.EmoteKey);
                }

                if (!string.IsNullOrWhiteSpace(url))
                {
                    await DownloadAndApplySegmentBitmapAsync(msg, seg, seg.EmoteKey, url, seg.IsSevenTv, ct);
                }
            }
        }
    }

    private void OnChatMessageReceived(object? sender, RawTwitchChatMessage raw)
    {
        if (_disposed || !IsWatchMode || !IsCurrentSourceTwitch || !IsChatEnabled)
        {
            return;
        }

        var ct = _chatWorkCts?.Token ?? CancellationToken.None;
        var pendingDownloads = new List<(TwitchChatSegment Segment, string Key, string Url, bool IsSevenTv)>();
        var segments = BuildChatSegments(raw, pendingDownloads);
        var msg = new TwitchChatMessage(raw.Id, raw.UserName, raw.ColorHex, raw.BadgeTag, isSystemNotice: false, segments);

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || !IsWatchMode || !IsChatEnabled)
            {
                return;
            }

            ChatMessages.Add(msg);
            while (ChatMessages.Count > MaxChatMessages)
            {
                ChatMessages.RemoveAt(0);
            }

            if (pendingDownloads.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    foreach (var (seg, key, url, isSevenTv) in pendingDownloads)
                    {
                        if (ct.IsCancellationRequested || _disposed)
                        {
                            break;
                        }

                        await DownloadAndApplySegmentBitmapAsync(msg, seg, key, url, isSevenTv, ct).ConfigureAwait(false);
                    }
                }, ct);
            }
        });
    }

    private async Task DownloadAndApplySegmentBitmapAsync(
        TwitchChatMessage msg,
        TwitchChatSegment seg,
        string key,
        string url,
        bool isSevenTv,
        CancellationToken ct)
    {
        if (_disposed || (isSevenTv && !IsSevenTvEmotesEnabled) || (!isSevenTv && !IsTwitchEmotesEnabled))
        {
            return;
        }

        var bmp = await _emoteCache.GetOrLoadBitmapAsync(key, url, isSevenTv, ct).ConfigureAwait(false);
        if (bmp is null || _disposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || (isSevenTv && !IsSevenTvEmotesEnabled) || (!isSevenTv && !IsTwitchEmotesEnabled))
            {
                return;
            }

            seg.EmoteBitmap = bmp;
            msg.NotifyEmotesUpdated();
        });
    }

    private List<TwitchChatSegment> BuildChatSegments(
        RawTwitchChatMessage raw,
        List<(TwitchChatSegment Segment, string Key, string Url, bool IsSevenTv)> pendingDownloads)
    {
        var text = raw.Text;
        var segments = new List<TwitchChatSegment>();
        if (string.IsNullOrEmpty(text))
        {
            return segments;
        }

        if (raw.TwitchEmotes.Count == 0)
        {
            AppendTextWithSevenTvEmotes(text, segments, pendingDownloads);
            return segments;
        }

        // Convert Unicode scalar indices (IRCv3 emotes tag) to UTF-16 char indices safely.
        var scalarToChar = BuildScalarToUtf16Map(text);
        var scalarLen = scalarToChar.Length - 1;
        var lastCharIdx = 0;

        foreach (var emote in raw.TwitchEmotes)
        {
            if (emote.StartIndex < 0 || emote.EndIndex >= scalarLen || emote.EndIndex < emote.StartIndex)
            {
                continue;
            }

            var startChar = scalarToChar[emote.StartIndex];
            var endCharExclusive = scalarToChar[emote.EndIndex + 1];
            if (startChar < lastCharIdx || endCharExclusive > text.Length)
            {
                continue;
            }

            if (startChar > lastCharIdx)
            {
                AppendTextWithSevenTvEmotes(text[lastCharIdx..startChar], segments, pendingDownloads);
            }

            var emoteCode = text[startChar..endCharExclusive];
            var cached = IsTwitchEmotesEnabled ? _emoteCache.TryGetCachedBitmap(emote.EmoteId, isSevenTv: false) : null;
            var seg = new TwitchChatSegment(emoteCode, isEmote: true, emoteKey: emote.EmoteId, isSevenTv: false, emoteBitmap: cached);
            segments.Add(seg);

            if (IsTwitchEmotesEnabled && cached is null)
            {
                pendingDownloads.Add((seg, emote.EmoteId, TwitchEmoteCacheService.BuildTwitchEmoteUrl(emote.EmoteId), false));
            }

            lastCharIdx = endCharExclusive;
        }

        if (lastCharIdx < text.Length)
        {
            AppendTextWithSevenTvEmotes(text[lastCharIdx..], segments, pendingDownloads);
        }

        return segments;
    }

    private void AppendTextWithSevenTvEmotes(
        string slice,
        List<TwitchChatSegment> segments,
        List<(TwitchChatSegment Segment, string Key, string Url, bool IsSevenTv)> pendingDownloads)
    {
        if (string.IsNullOrEmpty(slice))
        {
            return;
        }

        var idx = 0;
        var plainStart = 0;

        while (idx < slice.Length)
        {
            if (char.IsWhiteSpace(slice[idx]))
            {
                idx++;
                continue;
            }

            var tokenStart = idx;
            while (idx < slice.Length && !char.IsWhiteSpace(slice[idx]))
            {
                idx++;
            }

            var token = slice[tokenStart..idx];
            if (_emoteCache.TryResolveSevenTvEmote(token, out var stv) && stv is not null)
            {
                if (tokenStart > plainStart)
                {
                    segments.Add(new TwitchChatSegment(slice[plainStart..tokenStart], isEmote: false, emoteKey: null, isSevenTv: false));
                }

                var cached = IsSevenTvEmotesEnabled ? _emoteCache.TryGetCachedBitmap(stv.Id, isSevenTv: true) : null;
                var seg = new TwitchChatSegment(token, isEmote: true, emoteKey: stv.Id, isSevenTv: true, emoteBitmap: cached);
                segments.Add(seg);

                if (IsSevenTvEmotesEnabled && cached is null)
                {
                    pendingDownloads.Add((seg, stv.Id, stv.ImageUrl, true));
                }

                plainStart = idx;
            }
        }

        if (plainStart < slice.Length)
        {
            segments.Add(new TwitchChatSegment(slice[plainStart..], isEmote: false, emoteKey: null, isSevenTv: false));
        }
    }

    private static int[] BuildScalarToUtf16Map(string text)
    {
        var map = new List<int>(text.Length + 1);
        for (var i = 0; i < text.Length; i++)
        {
            map.Add(i);
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
        }

        map.Add(text.Length);
        return [.. map];
    }

    private void OnChatSystemNoticeReceived(object? sender, string notice)
    {
        if (_disposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => AppendSystemNotice(notice));
    }

    private void OnChatConnectionStateChanged(object? sender, (bool IsConnected, bool CanSend, string StatusText) state)
    {
        if (_disposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                CanSendChat = state.CanSend;
                ChatStatusText = state.StatusText;
            }
        });
    }

    private void AppendSystemNotice(string text)
    {
        if (_disposed || !IsWatchMode || !IsChatEnabled)
        {
            return;
        }

        ChatMessages.Add(CreateSystemNoticeMessage(text));
        while (ChatMessages.Count > MaxChatMessages)
        {
            ChatMessages.RemoveAt(0);
        }
    }

    private static TwitchChatMessage CreateSystemNoticeMessage(string text) =>
        new(
            id: Guid.NewGuid().ToString("N"),
            userName: string.Empty,
            colorHex: "#8B93A6",
            badgeTag: null,
            isSystemNotice: true,
            segments: [new TwitchChatSegment(text, isEmote: false, emoteKey: null, isSevenTv: false)]);

    private void ClearAllChatMessages()
    {
        foreach (var msg in ChatMessages)
        {
            foreach (var seg in msg.Segments)
            {
                seg.EmoteBitmap = null;
            }
        }

        ChatMessages.Clear();
    }

    private async Task SwitchQualityAsync(VideoQualityOption option)
    {
        if (_disposed)
        {
            return;
        }

        IsBusy = true;
        StatusBannerText = $"Switching quality to {option.Label}...";

        try
        {
            var liveSwitched = await _engine.SetQualityPresetAsync(option.Key);
            ShowTransientBanner(liveSwitched
                ? $"Quality: {option.Label}"
                : $"Quality set to {option.Label}");
        }
        catch (Exception ex)
        {
            ShowTransientBanner($"Quality switch failed: {ex.Message}");
            _actionLog.Log("Warning", $"Stream Watcher quality switch failed: {ex.Message}");
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
            }
        }
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

                if (feedResult.IsMissingExecutable || !feedResult.Success)
                {
                    ClearYouTubeFeedCards();
                    YouTubeFeedError = string.IsNullOrWhiteSpace(feedResult.ErrorMessage)
                        ? "Could not load your YouTube account feed. Make sure you are signed in to YouTube in your selected browser."
                        : feedResult.ErrorMessage;
                    _youtubeFeedLoaded = true;
                    return;
                }

                items = feedResult.Items;
            }
            else
            {
                ActivityNotice = $"Loading YouTube '{tab.Name}' videos...";
                _topicSearchPager = _youtube.CreateSearchPager(tab.Name);
                (items, hasMoreTopic) = await _topicSearchPager.FetchNextAsync(DefaultYouTubeTopicCount, ct);
                ActivityNotice = string.Empty;
            }

            ClearYouTubeFeedCards();
            foreach (var item in items)
            {
                YouTubeFeedItems.Add(new YouTubeSearchResultViewModel(item, PlayYouTubeSearchItemAsync));
            }

            _youtubeFeedLoaded = true;
            CanShowMoreYouTube = items.Count > 0 && hasMoreTopic;
            if (items.Count == 0)
            {
                YouTubeFeedError = tab.Kind == YouTubeFeedTabKind.AccountFyp
                    ? "Your YouTube For You feed returned no videos."
                    : $"No videos returned for '{tab.Name}'.";
            }
            else if (ShowThumbnails)
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
            _actionLog.Log("Warning", $"Stream Watcher YouTube feed error: {ex.Message}");
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
            _actionLog.Log("Warning", $"Stream Watcher Twitch feed error: {ex.Message}");
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
            if (ct.IsCancellationRequested || _disposed || !ShowThumbnails)
            {
                break;
            }

            var bmp = await _youtube.LoadSmallThumbnailAsync(card.EffectiveThumbnailUrl, ct);
            if (bmp is not null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed || ct.IsCancellationRequested || !ShowThumbnails)
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
            if (ct.IsCancellationRequested || _disposed || !ShowThumbnails)
            {
                break;
            }

            var bmp = await _twitch.LoadSmallThumbnailAsync(card.Stream.ThumbnailUrl, ct);
            if (bmp is not null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed || ct.IsCancellationRequested || !ShowThumbnails)
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
        foreach (var item in YouTubeFeedItems) item.Dispose();
        YouTubeFeedItems.Clear();
    }

    private void ClearYouTubeSearchCards(bool disposePager = true)
    {
        if (disposePager) DisposePager(ref _textSearchPager);
        CancelAndDispose(ref _thumbCts);
        foreach (var item in SearchResults) item.Dispose();
        SearchResults.Clear();
        HasSearchResults = false;
        CanShowMoreYouTubeSearch = false;
    }

    private void ClearLiveStreamCards()
    {
        CancelAndDispose(ref _thumbCts);
        foreach (var card in LiveStreams) card.Dispose();
        LiveStreams.Clear();
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

    private CancellationToken ResetWorkToken()
    {
        CancelAndDispose(ref _workCts);
        if (_disposed) return new CancellationToken(canceled: true);
        _workCts = new CancellationTokenSource();
        return _workCts.Token;
    }

    private CancellationToken ResetThumbToken()
    {
        CancelAndDispose(ref _thumbCts);
        if (_disposed) return new CancellationToken(canceled: true);
        _thumbCts = new CancellationTokenSource();
        return _thumbCts.Token;
    }

    private CancellationToken GetOrCreateThumbToken()
    {
        if (_disposed) return new CancellationToken(canceled: true);
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
        if (current is null) return;
        try { current.Cancel(); }
        catch (ObjectDisposedException) { }
        finally { current.Dispose(); }
    }

    private bool ReadBoolSetting(string key, bool defaultValue)
    {
        var raw = _database.GetSetting(key);
        return bool.TryParse(raw, out var val) ? val : defaultValue;
    }

    private void SyncFromSnapshot(VideoPlaybackSnapshot snap)
    {
        IsPlaying = snap.IsPlaying;
        HasMedia = !string.IsNullOrWhiteSpace(snap.CurrentSourceUrl);
        CurrentTitle = string.IsNullOrWhiteSpace(snap.CurrentTitle) ? "No video loaded" : snap.CurrentTitle;
        IsLiveStream = HasMedia && snap.DurationMs <= 0;

        var durSec = Math.Max(1.0, snap.DurationMs / 1000.0);
        SeekMaxSeconds = durSec;
        DurationDisplay = VideoPlayerViewModel.FormatDuration(snap.DurationMs);

        if (!_isScrubbing)
        {
            SeekPositionSeconds = Math.Clamp(snap.PositionMs / 1000.0, 0, durSec);
            PositionDisplay = VideoPlayerViewModel.FormatDuration(snap.PositionMs);
        }

        _syncingVolume = true;
        try
        {
            Volume = snap.Volume;
            IsMuted = snap.IsMuted;
        }
        finally
        {
            _syncingVolume = false;
        }

        _syncingSpeed = true;
        try
        {
            SelectedSpeedOption = SpeedOptions.FirstOrDefault(o => Math.Abs(o.Rate - snap.PlaybackSpeed) < 0.05f)
                ?? SpeedOptions[1];
        }
        finally
        {
            _syncingSpeed = false;
        }

        _syncingQuality = true;
        try
        {
            SelectedQualityOption = QualityOptions.FirstOrDefault(q =>
                    string.Equals(q.Key, snap.QualityPreset, StringComparison.OrdinalIgnoreCase))
                ?? QualityOptions[0];
        }
        finally
        {
            _syncingQuality = false;
        }

        if (snap.IsBuffering && !IsBusy)
        {
            StatusBannerText = $"Buffering {snap.BufferingPercent:0}%";
        }
        else if (!IsBusy && StatusBannerText.StartsWith("Buffering ", StringComparison.Ordinal))
        {
            StatusBannerText = string.Empty;
        }

        IsLoopOne = snap.LoopMode == VideoLoopMode.One;
        AspectRatioLabel = snap.AspectRatioLabel;
    }

    private void ShowTransientBanner(string message)
    {
        CancelToastTimer();
        StatusBannerText = message;

        var cts = new CancellationTokenSource();
        _toastCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3500, cts.Token).ConfigureAwait(false);
                if (!cts.IsCancellationRequested && !_disposed)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!cts.IsCancellationRequested && string.Equals(StatusBannerText, message, StringComparison.Ordinal))
                        {
                            StatusBannerText = string.Empty;
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void CancelToastTimer()
    {
        var old = Interlocked.Exchange(ref _toastCts, null);
        if (old is not null)
        {
            try { old.Cancel(); } catch { }
            finally { old.Dispose(); }
        }
    }

    private void OnModuleStateChanged(object? sender, ModuleState state) => RefreshModuleHeaderStatus();
    private void OnModuleStatusSummaryChanged(object? sender, EventArgs e) => RefreshModuleHeaderStatus();

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

    private void OnEnginePlaybackStateChanged(object? sender, VideoPlaybackSnapshot snap)
    {
        if (!_disposed)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                {
                    SyncFromSnapshot(snap);
                }
            });
        }
    }

    private void OnEnginePositionChanged(object? sender, long posMs)
    {
        if (_disposed || _isScrubbing)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _isScrubbing) return;
            SeekPositionSeconds = Math.Clamp(posMs / 1000.0, 0, SeekMaxSeconds);
            PositionDisplay = VideoPlayerViewModel.FormatDuration(posMs);
        });
    }

    private void OnEngineDurationChanged(object? sender, long durMs)
    {
        if (_disposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            IsLiveStream = HasMedia && durMs <= 0;
            SeekMaxSeconds = Math.Max(1.0, durMs / 1000.0);
            DurationDisplay = VideoPlayerViewModel.FormatDuration(durMs);
        });
    }

    private void OnEngineBufferingChanged(object? sender, float percent)
    {
        if (_disposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            if (percent < 99.5f && !IsBusy)
            {
                StatusBannerText = $"Buffering {percent:0}%";
            }
            else if (!IsBusy && StatusBannerText.StartsWith("Buffering ", StringComparison.Ordinal))
            {
                StatusBannerText = string.Empty;
            }
        });
    }

    private void OnEnginePlaybackError(object? sender, string error)
    {
        if (!_disposed)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed) ShowTransientBanner(error);
            });
        }
    }

    private static string ExtractTwitchLoginFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var seg = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(seg))
            {
                return seg.ToLowerInvariant();
            }
        }

        return "twitch";
    }
}
