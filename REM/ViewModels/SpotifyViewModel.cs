using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using REM.Models;
using REM.Modules;
using REM.Services;

namespace REM.ViewModels;

public enum SpotifyNavSection
{
    HomeFyp,
    Search,
    LikedSongs,
    Playlist
}

public enum SpotifyRepeatMode
{
    Off,
    Context,
    Track
}

public sealed partial class SpotifyTrackRowViewModel : ObservableObject, IDisposable
{
    private readonly Func<SpotifyTrackItem, Task> _onPlay;
    private readonly Func<SpotifyTrackRowViewModel, Task> _onToggleLike;
    private bool _disposed;

    public SpotifyTrackRowViewModel(
        int index,
        SpotifyTrackItem item,
        bool showCover,
        Func<SpotifyTrackItem, Task> onPlay,
        Func<SpotifyTrackRowViewModel, Task> onToggleLike)
    {
        Index = index;
        Item = item;
        _showCover = showCover;
        _onPlay = onPlay;
        _onToggleLike = onToggleLike;
    }

    public int Index { get; }
    public SpotifyTrackItem Item { get; }
    public string Title => Item.Name;
    public string Artists => Item.Artists;
    public string AlbumName => Item.AlbumName;
    public string DurationDisplay => Item.DurationDisplay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    private Bitmap? _coverBitmap;
    private bool _ownsCover = true;
    public string? CoverCacheKey { get; private set; }

    [ObservableProperty]
    private bool _showCover = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LikeTooltip))]
    [NotifyPropertyChangedFor(nameof(LikeMenuHeader))]
    private bool _isLiked;

    public bool HasCover => ShowCover && CoverBitmap is not null;
    public string LikeTooltip => IsLiked ? "Remove from Liked Songs" : "Save to Liked Songs";
    public string LikeMenuHeader => IsLiked ? "Remove from Liked Songs" : "Save to Liked Songs";

    partial void OnShowCoverChanged(bool value) => OnPropertyChanged(nameof(HasCover));

    public void SetCover(Bitmap? bmp, bool ownsCover = true, string? cacheKey = null)
    {
        if (_disposed)
        {
            if (ownsCover) bmp?.Dispose();
            return;
        }

        var prev = CoverBitmap;
        var prevOwned = _ownsCover;
        CoverBitmap = bmp;
        _ownsCover = ownsCover;
        CoverCacheKey = bmp is null ? null : cacheKey;
        if (prevOwned && !ReferenceEquals(prev, bmp))
        {
            prev?.Dispose();
        }
    }

    [RelayCommand]
    private Task PlayAsync() => _onPlay(Item);

    [RelayCommand]
    private Task ToggleLikeAsync() => _onToggleLike(this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var prev = CoverBitmap;
        var owned = _ownsCover;
        CoverBitmap = null;
        CoverCacheKey = null;
        if (owned)
        {
            prev?.Dispose();
        }
    }
}

public sealed partial class SpotifyPlaylistNavViewModel : ObservableObject, IDisposable
{
    private readonly Func<SpotifyPlaylistNavViewModel, Task> _onSelect;
    private readonly Action<SpotifyPlaylistNavViewModel> _onTogglePin;
    private bool _disposed;

    public SpotifyPlaylistNavViewModel(
        SpotifyPlaylistItem item,
        bool isPinned,
        bool showIcon,
        Func<SpotifyPlaylistNavViewModel, Task> onSelect,
        Action<SpotifyPlaylistNavViewModel> onTogglePin)
    {
        Item = item;
        _isPinned = isPinned;
        _showIcon = showIcon;
        _onSelect = onSelect;
        _onTogglePin = onTogglePin;
    }

    public SpotifyPlaylistItem Item { get; }
    public string Name => Item.Name;
    public string TrackCountText => Item.TrackCount > 0 ? $"{Item.TrackCount} tracks" : Item.OwnerName;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCoverIcon))]
    private bool _showIcon = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCoverIcon))]
    private Bitmap? _coverBitmap;

    public bool ShowCoverIcon => ShowIcon && CoverBitmap is not null;

    public void SetCover(Bitmap? bmp)
    {
        if (_disposed)
        {
            bmp?.Dispose();
            return;
        }

        var prev = CoverBitmap;
        CoverBitmap = bmp;
        prev?.Dispose();
    }

    [RelayCommand]
    private Task SelectAsync() => _onSelect(this);

    [RelayCommand]
    private void TogglePin() => _onTogglePin(this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CoverBitmap?.Dispose();
        CoverBitmap = null;
    }
}

public sealed partial class SpotifyLyricLineViewModel : ObservableObject
{
    private readonly Func<SpotifyLyricLineViewModel, Task> _onClick;

    public SpotifyLyricLineViewModel(LyricLine line, Func<SpotifyLyricLineViewModel, Task> onClick)
    {
        Timestamp = line.Timestamp;
        Text = line.Text;
        _onClick = onClick;
    }

    public TimeSpan Timestamp { get; }
    public string Text { get; }
    public string TimestampDisplay => Timestamp.TotalHours >= 1
        ? Timestamp.ToString(@"h\:mm\:ss")
        : Timestamp.ToString(@"m\:ss");

    [ObservableProperty]
    private bool _isActive;

    [RelayCommand]
    private Task ClickAsync() => _onClick(this);
}

public sealed partial class SpotifyLyricCandidateRowViewModel : ObservableObject
{
    private readonly Func<LrclibSearchCandidate, Task> _onSelect;

    public SpotifyLyricCandidateRowViewModel(
        LrclibSearchCandidate candidate,
        Func<LrclibSearchCandidate, Task> onSelect)
    {
        Candidate = candidate;
        _onSelect = onSelect;
    }

    public LrclibSearchCandidate Candidate { get; }
    public string Title => Candidate.Title;
    public string Artist => string.IsNullOrWhiteSpace(Candidate.Artist) ? "Unknown Artist" : Candidate.Artist;
    public string Subtitle => string.IsNullOrWhiteSpace(Candidate.DurationDisplay)
        ? Artist
        : $"{Artist} · {Candidate.DurationDisplay}";
    public string Badge => Candidate.IsSynced ? "SYNCED" : "PLAIN";

    [RelayCommand]
    private Task SelectAsync() => _onSelect(Candidate);
}

public sealed partial class SpotifyQueueRowViewModel : ObservableObject
{
    private readonly Func<SpotifyQueueEntry, Task> _onPlay;

    public SpotifyQueueRowViewModel(SpotifyQueueEntry entry, Func<SpotifyQueueEntry, Task> onPlay)
    {
        Entry = entry;
        _onPlay = onPlay;
        Title = entry.Track?.Name ?? entry.Uri.Split(':').LastOrDefault() ?? entry.Uri;
        Artists = entry.Track?.Artists ?? (entry.Provider == "queue" ? "Queued track" : "Up Next");
        ProviderLabel = entry.Provider.ToUpperInvariant();
    }

    public SpotifyQueueEntry Entry { get; }
    public string Title { get; }
    public string Artists { get; }
    public string ProviderLabel { get; }

    [RelayCommand]
    private Task PlayAsync() => _onPlay(Entry);
}

public sealed partial class SpotifyViewModel : ObservableObject, IDisposable
{
    private static readonly ObservableCollection<SpotifyLyricLineViewModel> EmptyLyricLines = [];
    private static readonly IBrush DefaultAmbientGlow = CreateDefaultGlow();

    private readonly SpotifyModule _module;
    private readonly DatabaseService _database;
    private readonly ActionLogService _actionLog;
    private readonly DialogService _dialogService;
    private readonly ImageAssetService _imageAssets;
    private readonly SpotifySoloistService _soloist;
    private readonly SpotifyWebApiService _webApi;
    private readonly LastFmService _lastFm;
    private readonly LrclibService _lrclib;
    private readonly RemMixGeneratorService _mixGenerator;
    private readonly IDownloadDispatcher? _downloadDispatcher;
    private readonly Action _onOpenSettings;
    private readonly DispatcherTimer _positionTimer;

    private CancellationTokenSource? _workCts;
    private CancellationTokenSource? _lyricsCts;
    private CancellationTokenSource? _artCts;
    private CancellationTokenSource? _rowCoverCts;
    private CancellationTokenSource? _playlistCoverCts;
    private CancellationTokenSource? _detailCoverCts;
    private CancellationTokenSource? _mixCoverCts;
    private CancellationTokenSource? _toastCts;

    private readonly List<SpotifyTrackRowViewModel> _allCollectionRows = [];
    private string? _lastTrackUri;
    private GoLibrespotTrack? _lastTrackMeta;
    private string? _lastAlbumArtUrl;
    private string? _activeCollectionContextUri;
    private long _anchorPositionMs;
    private DateTimeOffset _anchorTimestamp = DateTimeOffset.UtcNow;
    private DateTimeOffset _seekGraceUntil = DateTimeOffset.MinValue;
    private int _lyricsRequestId;
    private int _activeLyricIndex = -1;
    private bool _lastTrackLyricsHadArtist;
    private bool _lastTrackHasLoadedLyrics;
    private int _homeCoverDecodeWidth = 160;
    private bool _homeShelvesLoaded;
    private bool _isSyncingVolume;
    private bool _isUserScrubbing;
    private bool _disposed;

    public SpotifyViewModel(
        SpotifyModule module,
        DatabaseService database,
        ActionLogService actionLog,
        DialogService dialogService,
        ImageAssetService imageAssets,
        SpotifySoloistService soloist,
        SpotifyWebApiService webApi,
        LastFmService lastFm,
        LrclibService lrclib,
        RemMixGeneratorService mixGenerator,
        IDownloadDispatcher? downloadDispatcher,
        Action onOpenSettings)
    {
        _module = module;
        _database = database;
        _actionLog = actionLog;
        _dialogService = dialogService;
        _imageAssets = imageAssets;
        _soloist = soloist;
        _webApi = webApi;
        _lastFm = lastFm;
        _lrclib = lrclib;
        _mixGenerator = mixGenerator;
        _downloadDispatcher = downloadDispatcher;
        _onOpenSettings = onOpenSettings;

        AccentBrush = new SolidColorBrush(Color.Parse(_module.AccentHex));
        AmbientGlowBrush = DefaultAmbientGlow;
        InitializeLyricsCustomization();
        SeedRemMixCards();
        ApplyDisplaySettings(triggerReload: false);

        var savedVol = int.TryParse(_database.GetSetting(DatabaseService.KeySpotifyVolume), out var v) ? Math.Clamp(v, 0, 100) : 80;
        _isSyncingVolume = true;
        Volume = savedVol;
        _isSyncingVolume = false;

        _module.StateChanged += OnModuleStateChanged;
        _module.StatusSummaryChanged += OnModuleStatusSummaryChanged;
        _soloist.StatusChanged += OnSoloistStatusChanged;
        _soloist.AuthStateChanged += OnSoloistAuthChanged;
        _soloist.QueueUpdated += OnSoloistQueueUpdated;
        _soloist.PlaybackError += OnSoloistPlaybackError;
        _soloist.ConnectionStateChanged += OnSoloistConnectionChanged;

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _positionTimer.Tick += (_, _) => OnPositionTimerTick();

        RefreshModuleHeaderStatus();
        RefreshConnectionBadges();
        _ = InitializeAsync();
    }

    public string Name => _module.Name;
    public Geometry IconGeometry => _module.IconGeometry;
    public IBrush AccentBrush { get; }

    public ObservableCollection<SpotifyTrackRowViewModel> FypTracks { get; } = [];
    public ObservableCollection<SpotifyTrackRowViewModel> HomeRecentTracks { get; } = [];
    public ObservableCollection<SpotifyArtistCardViewModel> HomeTopArtists { get; } = [];
    public ObservableCollection<SpotifyAlbumCardViewModel> HomeAlbums { get; } = [];
    public ObservableCollection<SpotifyTrackRowViewModel> SearchTracks { get; } = [];
    public ObservableCollection<SpotifyTrackRowViewModel> CollectionTracks { get; } = [];
    public ObservableCollection<SpotifyTrackRowViewModel> DetailTracks { get; } = [];
    public ObservableCollection<SpotifyPlaylistNavViewModel> Playlists { get; } = [];
    public ObservableCollection<SpotifyLyricLineViewModel> LyricLines { get; } = [];
    public ObservableCollection<SpotifyLyricLineViewModel> FullscreenLyricLines => IsLyricsFullscreen ? LyricLines : EmptyLyricLines;
    public ObservableCollection<SpotifyLyricCandidateRowViewModel> LyricsSearchResults { get; } = [];
    public ObservableCollection<SpotifyQueueRowViewModel> QueueEntries { get; } = [];

    [ObservableProperty]
    private bool _hasFypTracks;

    [ObservableProperty]
    private bool _hasHomeRecentTracks;

    [ObservableProperty]
    private bool _hasHomeTopArtists;

    [ObservableProperty]
    private bool _hasHomeAlbums;

    public event EventHandler<int>? ActiveLyricChanged;
    public Action? RequestCloseToDashboard { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveAmbientGlowBrush))]
    private IBrush _ambientGlowBrush;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveAmbientGlowBrush))]
    private bool _showAmbientGlow = true;

    public IBrush EffectiveAmbientGlowBrush => ShowAmbientGlow ? AmbientGlowBrush : Brushes.Transparent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveMixCoverBitmap))]
    private bool _showPlaylistIcons = true;

    [ObservableProperty]
    private bool _showTrackCovers = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRemCompanion))]
    private bool _showRemArt = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRemCompanion))]
    private Bitmap? _remCompanionBitmap;

    public bool ShowRemCompanion => ShowRemArt && RemCompanionBitmap is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullscreenLyricLines))]
    private bool _isLyricsFullscreen;

    [ObservableProperty]
    private bool _hasQueueEntries;

    [ObservableProperty]
    private string _queueStatusText = "Up Next is empty (play a playlist or right-click a track to Add to Queue)";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToastMessage))]
    private string _toastMessage = string.Empty;

    public bool HasToastMessage => !string.IsNullOrEmpty(ToastMessage);

    [ObservableProperty]
    private bool _isHeaderVisible;

    [ObservableProperty]
    private string _stateLabel = string.Empty;

    [ObservableProperty]
    private string _statusSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCollectionLoadingPlaceholder))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCollectionFilter))]
    [NotifyPropertyChangedFor(nameof(CollectionFilterSummary))]
    private string _collectionFilterQuery = string.Empty;

    public bool HasCollectionFilter => !string.IsNullOrWhiteSpace(CollectionFilterQuery);

    public string CollectionFilterSummary => _allCollectionRows.Count > 0 && HasCollectionFilter
        ? $"{CollectionTracks.Count} of {_allCollectionRows.Count} songs"
        : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCollectionPaginationStatus))]
    private string _collectionPaginationStatus = string.Empty;

    public bool HasCollectionPaginationStatus => !string.IsNullOrWhiteSpace(CollectionPaginationStatus);

    public bool ShowCollectionLoadingPlaceholder => IsBusy && IsCollectionSection && CollectionTracks.Count == 0 && !HasFeedMessage;

    partial void OnCollectionFilterQueryChanged(string value)
    {
        ApplyCollectionFilter();
        OnPropertyChanged(nameof(CollectionFilterSummary));
    }

    [RelayCommand]
    private void ClearCollectionFilter() => CollectionFilterQuery = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeSection))]
    [NotifyPropertyChangedFor(nameof(IsSearchSection))]
    [NotifyPropertyChangedFor(nameof(IsCollectionSection))]
    private SpotifyNavSection _activeSection = SpotifyNavSection.HomeFyp;

    public bool IsHomeSection => ActiveSection == SpotifyNavSection.HomeFyp && ActiveDetailPage == SpotifyDetailPage.None;
    public bool IsSearchSection => ActiveSection == SpotifyNavSection.Search && ActiveDetailPage == SpotifyDetailPage.None;
    public bool IsCollectionSection => ActiveSection is SpotifyNavSection.LikedSongs or SpotifyNavSection.Playlist && ActiveDetailPage == SpotifyDetailPage.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeClassicLayout))]
    private bool _isHomeShelvesLayout = true;

    public bool IsHomeClassicLayout => !IsHomeShelvesLayout;

    [ObservableProperty]
    private string _collectionTitle = "Liked Songs";

    [ObservableProperty]
    private string _collectionSubtitle = string.Empty;

    [ObservableProperty]
    private bool _hasWebApiAuth;

    [ObservableProperty]
    private string _fypSubtitle = "Connect Spotify Client ID in Settings for personalized recommendations";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeedMessage))]
    private string _feedMessage = string.Empty;

    public bool HasFeedMessage => !string.IsNullOrEmpty(FeedMessage);

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private string _engineStatusText = "Starting Spotify Soloist...";

    [ObservableProperty]
    private bool _needsSoloistApiKey;

    [ObservableProperty]
    private bool _needsConnectPairing;

    [ObservableProperty]
    private bool _isDrawerOpen = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQueueDrawerActive))]
    private bool _isLyricsDrawerActive = true;

    public bool IsQueueDrawerActive => !IsLyricsDrawerActive;

    [ObservableProperty]
    private bool _isSyncedLyrics;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlainLyrics))]
    private string _plainLyrics = string.Empty;

    public bool HasPlainLyrics => !IsSyncedLyrics && !string.IsNullOrWhiteSpace(PlainLyrics);

    [ObservableProperty]
    private string _lyricsStatusText = "Play a track to load synced lyrics from LRCLIB";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LyricsOffsetDisplay))]
    private int _lyricsOffsetMs;

    public string LyricsOffsetDisplay => LyricsOffsetMs == 0
        ? "0.0s"
        : $"{(LyricsOffsetMs > 0 ? "+" : string.Empty)}{LyricsOffsetMs / 1000.0:0.0}s";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncPickButtonText))]
    private bool _isSyncPickMode;

    public string SyncPickButtonText => IsSyncPickMode ? "Cancel Sync" : "Sync Here";

    [ObservableProperty]
    private bool _isLyricsSearchOpen;

    [ObservableProperty]
    private string _lyricsSearchQuery = string.Empty;

    [ObservableProperty]
    private bool _hasCurrentTrack;

    [ObservableProperty]
    private string _currentTrackTitle = "Nothing playing";

    [ObservableProperty]
    private string _currentTrackArtist = "Select REM on Spotify Connect once, or pick a track";

    [ObservableProperty]
    private string _currentTrackAlbum = string.Empty;

    [ObservableProperty]
    private string _currentAudioSpec = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentAlbumArt))]
    private Bitmap? _currentAlbumArt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentAlbumArt))]
    private bool _showPlayBarCover = true;

    [ObservableProperty]
    private double _listFadeOpacity = 1.0;

    public bool HasCurrentAlbumArt => ShowPlayBarCover && CurrentAlbumArt is not null;

    [ObservableProperty]
    private bool _isCurrentTrackLiked;

    private int _lastDisplayedSecond = -1;

    [ObservableProperty]
    private bool _isPlaying;

    partial void OnIsPlayingChanged(bool value)
    {
        if (_disposed)
        {
            _positionTimer.Stop();
            return;
        }

        if (value)
        {
            if (!_positionTimer.IsEnabled)
            {
                _positionTimer.Start();
            }
        }
        else
        {
            _positionTimer.Stop();
        }
    }

    [ObservableProperty]
    private bool _isShuffled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRepeatActive))]
    [NotifyPropertyChangedFor(nameof(RepeatLabel))]
    [NotifyPropertyChangedFor(nameof(RepeatTooltip))]
    private SpotifyRepeatMode _repeatMode = SpotifyRepeatMode.Off;

    public bool IsRepeatActive => RepeatMode != SpotifyRepeatMode.Off;
    public string RepeatLabel => RepeatMode == SpotifyRepeatMode.Track ? "1" : string.Empty;
    public string RepeatTooltip => RepeatMode switch
    {
        SpotifyRepeatMode.Context => "Repeat: Playlist/Context",
        SpotifyRepeatMode.Track => "Repeat: Current Track",
        _ => "Repeat: Off"
    };

    [ObservableProperty]
    private double _volume = 80;

    [ObservableProperty]
    private double _seekPositionSeconds;

    [ObservableProperty]
    private double _seekMaxSeconds = 1;

    [ObservableProperty]
    private string _positionDisplay = "0:00";

    [ObservableProperty]
    private string _durationDisplay = "0:00";

    partial void OnVolumeChanged(double value)
    {
        if (_isSyncingVolume || _disposed)
        {
            return;
        }

        var intVol = Math.Clamp((int)Math.Round(value), 0, 100);
        _database.SaveSetting(DatabaseService.KeySpotifyVolume, intVol.ToString());
        _ = _soloist.SetVolumeAsync(intVol);
    }

    public void RefreshFromSettings()
    {
        ApplyDisplaySettings(triggerReload: true);
        RefreshConnectionBadges();
        _ = InitializeAsync();
    }

    public void BeginScrub() => _isUserScrubbing = true;

    public void UpdateScrubPreview(double seconds)
    {
        _isUserScrubbing = true;
        SeekPositionSeconds = Math.Clamp(seconds, 0, Math.Max(1, SeekMaxSeconds));
        PositionDisplay = FormatSeconds(SeekPositionSeconds);
    }

    public async Task CommitScrubAsync(double seconds)
    {
        var clamped = Math.Clamp(seconds, 0, Math.Max(1, SeekMaxSeconds));
        SeekPositionSeconds = clamped;
        PositionDisplay = FormatSeconds(clamped);
        _anchorPositionMs = (long)(clamped * 1000);
        _anchorTimestamp = DateTimeOffset.UtcNow;
        _seekGraceUntil = DateTimeOffset.UtcNow.AddMilliseconds(900);
        _isUserScrubbing = false;

        await _soloist.SeekAsync(_anchorPositionMs);
        SyncActiveLyricIndex(_anchorPositionMs);
    }

    [RelayCommand]
    private void ToggleHeader() => IsHeaderVisible = !IsHeaderVisible;

    [RelayCommand]
    private void OpenSettings() => _onOpenSettings();

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
    private void BackToDashboard() => RequestCloseToDashboard?.Invoke();

    [RelayCommand]
    private async Task RetryEngineAsync() => await InitializeAsync();

    [RelayCommand]
    private Task ActivateDeviceAsync() => _soloist.ActivateAsync();

    internal void OnViewReturned()
    {
        if (_disposed)
        {
            return;
        }

        if (IsHomeSection)
        {
            if (FypTracks.Count == 0)
            {
                _ = LoadFypAsync();
            }
            else if (IsHomeShelvesLayout && !_homeShelvesLoaded)
            {
                var items = FypTracks.Select(r => r.Item).ToList();
                var ct = ResetWorkToken();
                _ = PopulateHomeShelvesAsync([], items, ct);
            }

            if (ShowPlaylistIcons && RemMixCards.Any(c => c.CoverBitmap is null))
            {
                _ = LoadRemMixCardCoversAsync(IsHomeShelvesLayout ? _homeCoverDecodeWidth : 160);
            }
        }
        else if (ActiveSection == SpotifyNavSection.LikedSongs && CollectionTracks.Count == 0)
        {
            _ = LoadLikedSongsAsync();
        }
        else if (ActiveSection == SpotifyNavSection.Playlist && CollectionTracks.Count == 0)
        {
            if (_activePlaylistItem is not null)
            {
                _ = SelectExternalPlaylistAsync(_activePlaylistItem);
            }
            else
            {
                _ = SelectHomeAsync();
            }
        }
        else if (ActiveDetailPage != SpotifyDetailPage.None &&
                 DetailTracks.Count == 0 &&
                 SavedAlbums.Count == 0 &&
                 TopArtists.Count == 0)
        {
            ResetToStandardSection();
            _ = SelectHomeAsync();
        }
    }

    private void ClearHomeShelvesOnly()
    {
        ClearTrackCollection(HomeRecentTracks);
        ClearArtistCards(HomeTopArtists);
        ClearAlbumCards(HomeAlbums);
        HasHomeRecentTracks = false;
        HasHomeTopArtists = false;
        HasHomeAlbums = false;
        _homeShelvesLoaded = false;
    }

    private void ReleaseHomeSectionAssets(bool clearFypRows)
    {
        CancelAndDispose(ref _mixCoverCts);
        CancelAndDispose(ref _detailCoverCts);
        ClearHomeShelvesOnly();

        foreach (var card in RemMixCards)
        {
            card.SetCover(null);
        }

        if (clearFypRows)
        {
            ClearTrackCollection(FypTracks);
            HasFypTracks = false;
        }
        else
        {
            foreach (var row in FypTracks)
            {
                row.SetCover(null);
            }

            ClearUnusedSharedCovers(null);
        }
    }

    private void ReleaseHomeSectionAssetsIfOnHome()
    {
        if (ActiveSection == SpotifyNavSection.HomeFyp)
        {
            ReleaseHomeSectionAssets(clearFypRows: false);
        }
    }

    [RelayCommand]
    private async Task SelectHomeAsync()
    {
        ClearAllCollectionRows();
        ResetToStandardSection();
        ActiveSection = SpotifyNavSection.HomeFyp;
        DeselectPlaylists();
        if (FypTracks.Count == 0)
        {
            await LoadFypAsync();
            return;
        }

        if (IsHomeShelvesLayout)
        {
            if (!_homeShelvesLoaded)
            {
                var ct = ResetWorkToken();
                await PopulateHomeShelvesAsync([], FypTracks.Select(r => r.Item).ToList(), ct);
            }
            else if (ShowTrackCovers && FypTracks.Any(r => r.CoverBitmap is null && !string.IsNullOrWhiteSpace(r.Item.AlbumCoverUrl)))
            {
                _ = LoadRowCoversAsync(FypTracks.ToList(), decodeWidth: _homeCoverDecodeWidth);
            }
        }
        else
        {
            ClearHomeShelvesOnly();
            if (ShowTrackCovers && FypTracks.Any(r => r.CoverBitmap is null && !string.IsNullOrWhiteSpace(r.Item.AlbumCoverUrl)))
            {
                _ = LoadRowCoversAsync(FypTracks.ToList(), decodeWidth: 44);
            }
        }

        if (ShowPlaylistIcons && RemMixCards.Any(c => c.CoverBitmap is null))
        {
            _ = LoadRemMixCardCoversAsync(IsHomeShelvesLayout ? _homeCoverDecodeWidth : 160);
        }
    }

    [RelayCommand]
    private void SelectSearch()
    {
        ReleaseHomeSectionAssetsIfOnHome();
        ResetToStandardSection();
        ActiveSection = SpotifyNavSection.Search;
        DeselectPlaylists();
    }

    [RelayCommand]
    private async Task SelectLikedSongsAsync()
    {
        ReleaseHomeSectionAssetsIfOnHome();
        ResetToStandardSection();
        ActiveSection = SpotifyNavSection.LikedSongs;
        DeselectPlaylists();
        await LoadLikedSongsAsync();
    }

    [RelayCommand]
    private async Task RefreshFypAsync() => await LoadFypAsync(forceRefresh: true);

    [RelayCommand]
    private async Task SearchAsync()
    {
        var query = SearchQuery.Trim();
        if (string.IsNullOrWhiteSpace(query) || _disposed)
        {
            return;
        }

        ReleaseHomeSectionAssetsIfOnHome();
        ResetToStandardSection();
        ActiveSection = SpotifyNavSection.Search;
        DeselectPlaylists();

        if (!_webApi.IsConnected)
        {
            FeedMessage = "Connect your Spotify Client ID in Settings → Spotify & APIs to search Spotify (search uses PKCE OAuth with limit=10).";
            return;
        }

        var ct = ResetWorkToken();
        IsBusy = true;
        FeedMessage = string.Empty;
        try
        {
            var bundle = await _webApi.SearchAllAsync(query, 10, ct);
            ClearTrackCollection(SearchTracks);
            for (var i = 0; i < bundle.Tracks.Count; i++)
            {
                SearchTracks.Add(new SpotifyTrackRowViewModel(i + 1, bundle.Tracks[i], ShowTrackCovers, PlayTrackItemAsync, ToggleRowLikeAsync));
            }

            ClearArtistCards(SearchArtists);
            foreach (var artist in bundle.Artists)
            {
                SearchArtists.Add(new SpotifyArtistCardViewModel(artist, OpenArtistFromCardAsync));
            }

            ClearAlbumCards(SearchAlbums);
            foreach (var album in bundle.Albums)
            {
                SearchAlbums.Add(new SpotifyAlbumCardViewModel(album, OpenAlbumFromCardAsync, PlayAlbumCardAsync));
            }

            ClearPlaylistCards(SearchPlaylists);
            foreach (var pl in bundle.Playlists)
            {
                SearchPlaylists.Add(new SpotifyPlaylistCardViewModel(pl, OpenSearchedPlaylistAsync, PlayPlaylistFromMenuAsync, FollowSearchedPlaylistAsync));
            }

            NotifySearchVisibility();

            if (bundle.Tracks.Count == 0 && bundle.Artists.Count == 0 && bundle.Albums.Count == 0 && bundle.Playlists.Count == 0)
            {
                FeedMessage = !string.IsNullOrWhiteSpace(_webApi.LastApiError)
                    ? _webApi.LastApiError
                    : $"No Spotify results found for \"{query}\".";
            }
            else
            {
                var rows = SearchTracks.ToList();
                _ = SyncRowLikedStatesAsync(rows);
                if (ShowTrackCovers)
                {
                    _ = LoadRowCoversAsync(rows);
                }

                _ = LoadArtistCardPhotosAsync(SearchArtists.ToList());
                _ = LoadAlbumCardCoversAsync(SearchAlbums.ToList());
                _ = LoadPlaylistCardCoversAsync(SearchPlaylists.ToList());
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FeedMessage = ex.Message;
            _actionLog.Log("Warning", $"Spotify search failed: {ex.Message}");
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
    private async Task PlayActiveCollectionAsync()
    {
        var items = ActiveDetailPage != SpotifyDetailPage.None
            ? DetailTracks.Select(r => r.Item).ToList()
            : ActiveSection switch
            {
                SpotifyNavSection.HomeFyp => FypTracks.Select(r => r.Item).ToList(),
                SpotifyNavSection.Search => SearchTracks.Select(r => r.Item).ToList(),
                _ => CollectionTracks.Select(r => r.Item).ToList()
            };

        if (items.Count > 0)
        {
            await _soloist.PlayTrackListAsync(items, 0);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_activeCollectionContextUri))
        {
            await _soloist.PlayAsync(_activeCollectionContextUri);
        }
    }

    [RelayCommand]
    private Task TogglePlayPauseAsync() => _soloist.PlayPauseAsync();

    [RelayCommand]
    private Task NextTrackAsync() => _soloist.NextAsync();

    [RelayCommand]
    private Task PrevTrackAsync() => _soloist.PrevAsync();

    [RelayCommand]
    private async Task ToggleShuffleAsync()
    {
        var next = !IsShuffled;
        IsShuffled = next;
        await _soloist.SetShuffleAsync(next);
    }

    [RelayCommand]
    private async Task CycleRepeatAsync()
    {
        var next = RepeatMode switch
        {
            SpotifyRepeatMode.Off => SpotifyRepeatMode.Context,
            SpotifyRepeatMode.Context => SpotifyRepeatMode.Track,
            _ => SpotifyRepeatMode.Off
        };

        RepeatMode = next;
        switch (next)
        {
            case SpotifyRepeatMode.Context:
                await _soloist.SetRepeatTrackAsync(false);
                await _soloist.SetRepeatContextAsync(true);
                break;
            case SpotifyRepeatMode.Track:
                await _soloist.SetRepeatContextAsync(false);
                await _soloist.SetRepeatTrackAsync(true);
                break;
            default:
                await _soloist.SetRepeatTrackAsync(false);
                await _soloist.SetRepeatContextAsync(false);
                break;
        }
    }

    [RelayCommand]
    private async Task ToggleLikeCurrentAsync()
    {
        var trackUri = _lastTrackUri;
        if (string.IsNullOrWhiteSpace(trackUri) || !_webApi.IsConnected)
        {
            return;
        }

        var desired = !IsCurrentTrackLiked;
        IsCurrentTrackLiked = desired;

        var ok = await _webApi.SetTrackLikedAsync(trackUri, desired, CancellationToken.None);
        if (!ok)
        {
            IsCurrentTrackLiked = !desired;
        }
        else
        {
            SyncLikedStateAcrossCollections(trackUri, desired);
            ShowToast(desired ? "Saved to Liked Songs" : "Removed from Liked Songs");
            _actionLog.Log("Spotify", desired ? $"Liked '{CurrentTrackTitle}'" : $"Unliked '{CurrentTrackTitle}'");
        }
    }

    [RelayCommand]
    private void ToggleLyricsFullscreen()
    {
        IsLyricsFullscreen = !IsLyricsFullscreen;
        if (IsLyricsFullscreen && !_lastTrackHasLoadedLyrics && _lastTrackMeta is not null)
        {
            _ = LoadLyricsAsync(_lastTrackMeta);
        }
    }

    [RelayCommand]
    private void CloseLyricsFullscreen() => IsLyricsFullscreen = false;

    [RelayCommand]
    private Task RefreshQueueAsync() => _soloist.RequestQueueAsync(25);

    [RelayCommand]
    private void ToggleLyricsDrawer()
    {
        if (IsDrawerOpen && IsLyricsDrawerActive)
        {
            IsDrawerOpen = false;
        }
        else
        {
            IsLyricsDrawerActive = true;
            IsDrawerOpen = true;
            if (!_lastTrackHasLoadedLyrics && _lastTrackMeta is not null)
            {
                _ = LoadLyricsAsync(_lastTrackMeta);
            }
        }
    }

    [RelayCommand]
    private async Task ToggleQueueDrawerAsync()
    {
        if (IsDrawerOpen && IsQueueDrawerActive)
        {
            IsDrawerOpen = false;
            return;
        }

        IsLyricsDrawerActive = false;
        IsDrawerOpen = true;
        await _soloist.RequestQueueAsync(25);
    }

    [RelayCommand]
    private void SelectLyricsTab()
    {
        IsLyricsDrawerActive = true;
        IsDrawerOpen = true;
        if (!_lastTrackHasLoadedLyrics && _lastTrackMeta is not null)
        {
            _ = LoadLyricsAsync(_lastTrackMeta);
        }
    }

    [RelayCommand]
    private async Task SelectQueueTabAsync()
    {
        IsLyricsDrawerActive = false;
        IsDrawerOpen = true;
        await _soloist.RequestQueueAsync(25);
    }

    [RelayCommand]
    private void NudgeLyricsOffset(string? deltaMsText)
    {
        if (!int.TryParse(deltaMsText, out var deltaMs) || string.IsNullOrWhiteSpace(_lastTrackUri))
        {
            return;
        }

        LyricsOffsetMs = Math.Clamp(LyricsOffsetMs + deltaMs, -30000, 30000);
        _database.SaveSpotifyLyricsOffset(_lastTrackUri, LyricsOffsetMs);
        SyncActiveLyricIndex((long)GetLivePlaybackPositionMs());
    }

    [RelayCommand]
    private void ResetLyricsOffset()
    {
        if (string.IsNullOrWhiteSpace(_lastTrackUri))
        {
            LyricsOffsetMs = 0;
            return;
        }

        LyricsOffsetMs = 0;
        _database.SaveSpotifyLyricsOffset(_lastTrackUri, 0);
        SyncActiveLyricIndex((long)GetLivePlaybackPositionMs());
    }

    [RelayCommand]
    private void ToggleSyncPickMode()
    {
        if (!IsSyncedLyrics || LyricLines.Count == 0)
        {
            IsSyncPickMode = false;
            return;
        }

        IsSyncPickMode = !IsSyncPickMode;
    }

    [RelayCommand]
    private async Task RetryLyricsAsync()
    {
        if (_lastTrackMeta is not null)
        {
            await LoadLyricsAsync(_lastTrackMeta, forceRefresh: true);
        }
    }

    [RelayCommand]
    private void ToggleLyricsSearch()
    {
        IsLyricsSearchOpen = !IsLyricsSearchOpen;
        if (IsLyricsSearchOpen && string.IsNullOrWhiteSpace(LyricsSearchQuery) && _lastTrackMeta is not null)
        {
            var cleanTitle = LrclibService.SanitizeTrackTitle(_lastTrackMeta.Name);
            var artist = LrclibService.SanitizePrimaryArtist(_lastTrackMeta.ArtistsDisplay);
            LyricsSearchQuery = string.IsNullOrWhiteSpace(artist) ? cleanTitle : $"{artist} {cleanTitle}";
        }
    }

    [RelayCommand]
    private async Task SearchLyricsManualAsync()
    {
        var query = LyricsSearchQuery.Trim();
        if (string.IsNullOrWhiteSpace(query) || _disposed)
        {
            return;
        }

        var targetDur = _lastTrackMeta is not null ? (int)Math.Round(_lastTrackMeta.DurationMs / 1000.0) : 0;
        var hits = await _lrclib.SearchCandidatesAsync(query, targetDur, CancellationToken.None);
        if (_disposed)
        {
            return;
        }

        LyricsSearchResults.Clear();
        foreach (var hit in hits)
        {
            LyricsSearchResults.Add(new SpotifyLyricCandidateRowViewModel(hit, ApplySelectedLyricCandidateAsync));
        }

        if (hits.Count == 0)
        {
            LyricsStatusText = $"No LRCLIB matches found for \"{query}\".";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _positionTimer.Stop();

        _module.StateChanged -= OnModuleStateChanged;
        _module.StatusSummaryChanged -= OnModuleStatusSummaryChanged;
        _soloist.StatusChanged -= OnSoloistStatusChanged;
        _soloist.AuthStateChanged -= OnSoloistAuthChanged;
        _soloist.QueueUpdated -= OnSoloistQueueUpdated;
        _soloist.PlaybackError -= OnSoloistPlaybackError;
        _soloist.ConnectionStateChanged -= OnSoloistConnectionChanged;
        RequestCloseToDashboard = null;

        CancelAndDispose(ref _workCts);
        CancelAndDispose(ref _lyricsCts);
        CancelAndDispose(ref _artCts);
        CancelAndDispose(ref _rowCoverCts);
        CancelAndDispose(ref _playlistCoverCts);
        CancelAndDispose(ref _detailCoverCts);
        CancelAndDispose(ref _mixCoverCts);
        CancelAndDispose(ref _toastCts);

        ClearTrackCollection(FypTracks);
        ClearTrackCollection(HomeRecentTracks);
        ClearArtistCards(HomeTopArtists);
        ClearAlbumCards(HomeAlbums);
        ClearTrackCollection(SearchTracks);
        ClearAllCollectionRows();
        ClearTrackCollection(DetailTracks);
        ClearAllSharedCovers();
        ClearPlaylistCollection();
        DisposeExtendedAssets();
        foreach (var card in RemMixCards) card.Dispose();
        RemMixCards.Clear();
        LyricsColorSwatches.Clear();
        _navHistory.Clear();
        LyricLines.Clear();
        LyricsSearchResults.Clear();
        QueueEntries.Clear();

        RemCompanionBitmap = null;
        CurrentAlbumArt?.Dispose();
        CurrentAlbumArt = null;
        ActiveMixCoverBitmap?.Dispose();
        ActiveMixCoverBitmap = null;
        AmbientGlowBrush = DefaultAmbientGlow;
    }

    private async Task InitializeAsync()
    {
        if (_disposed)
        {
            return;
        }

        NeedsSoloistApiKey = !_soloist.HasApiKey;
        if (NeedsSoloistApiKey)
        {
            EngineStatusText = "Soloist API key required";
            NeedsConnectPairing = false;
        }
        else
        {
            EngineStatusText = "Starting Spotify Soloist...";
            var (started, error) = await _soloist.EnsureDaemonRunningAsync(CancellationToken.None);
            if (_disposed)
            {
                return;
            }

            if (!started)
            {
                EngineStatusText = error ?? "soloist.exe unavailable";
                NeedsConnectPairing = false;
            }
            else
            {
                NeedsConnectPairing = !_soloist.IsAuthenticated && _soloist.CurrentStatus is null;
                if (_soloist.CurrentStatus is { } current)
                {
                    ApplyStatus(current);
                }
            }
        }

        await Task.WhenAll(
            LoadPlaylistsAsync(),
            LoadFypAsync(),
            (ShowPlaylistIcons && IsHomeSection)
                ? LoadRemMixCardCoversAsync(IsHomeShelvesLayout ? _homeCoverDecodeWidth : 160)
                : Task.CompletedTask);
    }

    private async Task LoadPlaylistsAsync()
    {
        if (_disposed || !_webApi.IsConnected)
        {
            return;
        }

        try
        {
            var playlists = await _webApi.GetUserPlaylistsAsync(CancellationToken.None);
            if (_disposed)
            {
                return;
            }

            var pinnedIds = _database.GetPinnedSpotifyPlaylistIds();
            var sorted = playlists
                .OrderByDescending(p => pinnedIds.Contains(p.Id))
                .ToList();

            ClearPlaylistCollection();
            foreach (var p in sorted)
            {
                Playlists.Add(new SpotifyPlaylistNavViewModel(
                    p,
                    pinnedIds.Contains(p.Id),
                    ShowPlaylistIcons,
                    SelectPlaylistAsync,
                    TogglePinPlaylist));
            }

            if (ShowPlaylistIcons && Playlists.Count > 0)
            {
                _ = LoadPlaylistCoversAsync(Playlists.ToList());
            }
        }
        catch (Exception ex)
        {
            _actionLog.Log("Warning", $"Spotify playlists load failed: {ex.Message}");
        }
    }

    private async Task SelectPlaylistAsync(SpotifyPlaylistNavViewModel tab)
    {
        if (!_isRestoringNav)
        {
            PushCurrentToNavHistory();
        }

        ReleaseHomeSectionAssetsIfOnHome();
        ResetToStandardSection();
        ActiveSection = SpotifyNavSection.Playlist;
        _activePlaylistItem = tab.Item;
        OnPropertyChanged(nameof(IsActivePlaylistEditable));
        _activeCollectionContextUri = tab.Item.Uri;
        foreach (var p in Playlists)
        {
            p.IsSelected = ReferenceEquals(p, tab);
        }

        CollectionTitle = tab.Name;
        CollectionSubtitle = $"{tab.Item.OwnerName} · {tab.TrackCountText}";
        FeedMessage = string.Empty;
        CollectionFilterQuery = string.Empty;
        CollectionPaginationStatus = string.Empty;
        ClearAllCollectionRows();

        var ct = ResetWorkToken();
        IsBusy = true;
        ListFadeOpacity = 0.4;
        try
        {
            if (!_webApi.IsConnected || string.IsNullOrWhiteSpace(tab.Item.Id))
            {
                FeedMessage = "Connect Spotify Client ID in Settings → Spotify & APIs to browse playlist tracks.";
                return;
            }

            const int pageSize = 50;
            var firstPage = await _webApi.GetPlaylistItemsPageAsync(tab.Item.Id, tab.Item.Uri, offset: 0, limit: pageSize, ct);
            if (_disposed || ct.IsCancellationRequested) return;

            if (firstPage.Tracks.Count == 0)
            {
                FeedMessage = firstPage.AccessRestricted
                    ? "Spotify restricts reading tracks for this private/restricted playlist directly, but you can still click 'Play All' above to play it through Soloist!"
                    : "This playlist appears empty or could not be loaded.";
                return;
            }

            AppendCollectionPageRows(firstPage.Tracks, isLikedList: false);
            ListFadeOpacity = 1.0;

            var firstRows = CollectionTracks.ToList();
            _ = SyncRowLikedStatesAsync(firstRows);
            if (ShowTrackCovers)
            {
                _ = LoadRowCoversAsync(firstRows);
            }

            if (firstPage.HasMore)
            {
                CollectionPaginationStatus = $"Loaded {_allCollectionRows.Count} / {(firstPage.Total > 0 ? firstPage.Total.ToString() : "?")} tracks...";
                _ = LoadRemainingPlaylistPagesAsync(tab.Item.Id, tab.Item.Uri, firstPage.Tracks.Count, firstPage.Total, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FeedMessage = ex.Message;
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
                ListFadeOpacity = 1.0;
            }
        }
    }

    private async Task LoadRemainingPlaylistPagesAsync(
        string playlistId,
        string? playlistUri,
        int startOffset,
        int expectedTotal,
        CancellationToken ct)
    {
        try
        {
            const int pageSize = 50;
            const int maxTracksCap = 2000;
            var offset = startOffset;

            while (!_disposed && !ct.IsCancellationRequested && offset < maxTracksCap && !_webApi.IsRateLimited)
            {
                await Task.Delay(120, ct);
                var page = await _webApi.GetPlaylistItemsPageAsync(playlistId, playlistUri, offset, pageSize, ct);
                if (_disposed || ct.IsCancellationRequested || page.Tracks.Count == 0)
                {
                    break;
                }

                var newRows = AppendCollectionPageRows(page.Tracks, isLikedList: false);
                _ = SyncRowLikedStatesAsync(newRows);
                if (ShowTrackCovers)
                {
                    _ = LoadRowCoversAsync(newRows, resetExisting: false);
                }

                offset += page.Tracks.Count;
                var totalDisplay = page.Total > 0 ? page.Total : (expectedTotal > 0 ? expectedTotal : offset);
                CollectionPaginationStatus = page.HasMore && offset < maxTracksCap
                    ? $"Loaded {_allCollectionRows.Count} / {totalDisplay} tracks..."
                    : string.Empty;

                if (!page.HasMore)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
        finally
        {
            if (!_disposed && !ct.IsCancellationRequested)
            {
                CollectionPaginationStatus = string.Empty;
            }
        }
    }

    private async Task LoadLikedSongsAsync()
    {
        _activeCollectionContextUri = "spotify:collection:tracks";
        CollectionTitle = "Liked Songs";
        CollectionSubtitle = "Your saved Spotify tracks";
        FeedMessage = string.Empty;
        CollectionFilterQuery = string.Empty;
        CollectionPaginationStatus = string.Empty;
        ClearAllCollectionRows();

        var ct = ResetWorkToken();
        IsBusy = true;
        ListFadeOpacity = 0.4;
        try
        {
            if (!_webApi.IsConnected)
            {
                FeedMessage = "Connect Spotify Web API in Settings → Spotify & APIs to load your Liked Songs.";
                return;
            }

            const int pageSize = 50;
            var firstPage = await _webApi.GetLikedSongsPageAsync(offset: 0, limit: pageSize, ct);
            if (_disposed || ct.IsCancellationRequested) return;

            if (firstPage.Tracks.Count == 0)
            {
                FeedMessage = "No liked songs found.";
                return;
            }

            AppendCollectionPageRows(firstPage.Tracks, isLikedList: true);
            if (firstPage.Total > 0)
            {
                CollectionSubtitle = $"Your saved Spotify tracks · {firstPage.Total} songs";
            }

            ListFadeOpacity = 1.0;
            if (ShowTrackCovers)
            {
                _ = LoadRowCoversAsync(CollectionTracks.ToList());
            }

            if (firstPage.HasMore)
            {
                CollectionPaginationStatus = $"Loaded {_allCollectionRows.Count} / {(firstPage.Total > 0 ? firstPage.Total.ToString() : "?")} liked songs...";
                _ = LoadRemainingLikedPagesAsync(firstPage.Tracks.Count, firstPage.Total, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FeedMessage = ex.Message;
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
                ListFadeOpacity = 1.0;
            }
        }
    }

    private async Task LoadRemainingLikedPagesAsync(int startOffset, int expectedTotal, CancellationToken ct)
    {
        try
        {
            const int pageSize = 50;
            const int maxTracksCap = 2000;
            var offset = startOffset;

            while (!_disposed && !ct.IsCancellationRequested && offset < maxTracksCap && !_webApi.IsRateLimited)
            {
                await Task.Delay(120, ct);
                var page = await _webApi.GetLikedSongsPageAsync(offset, pageSize, ct);
                if (_disposed || ct.IsCancellationRequested || page.Tracks.Count == 0)
                {
                    break;
                }

                var newRows = AppendCollectionPageRows(page.Tracks, isLikedList: true);
                if (ShowTrackCovers)
                {
                    _ = LoadRowCoversAsync(newRows, resetExisting: false);
                }

                offset += page.Tracks.Count;
                var totalDisplay = page.Total > 0 ? page.Total : (expectedTotal > 0 ? expectedTotal : offset);
                CollectionPaginationStatus = page.HasMore && offset < maxTracksCap
                    ? $"Loaded {_allCollectionRows.Count} / {totalDisplay} liked songs..."
                    : string.Empty;

                if (!page.HasMore)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
        finally
        {
            if (!_disposed && !ct.IsCancellationRequested)
            {
                CollectionPaginationStatus = string.Empty;
            }
        }
    }

    private List<SpotifyTrackRowViewModel> AppendCollectionPageRows(IReadOnlyList<SpotifyTrackItem> tracks, bool isLikedList)
    {
        _soloist.RememberTrackMetadata(tracks);
        var added = new List<SpotifyTrackRowViewModel>(tracks.Count);
        var query = CollectionFilterQuery.Trim();
        var hasQuery = !string.IsNullOrWhiteSpace(query);

        foreach (var tr in tracks)
        {
            var idx = _allCollectionRows.Count + 1;
            var row = new SpotifyTrackRowViewModel(idx, tr, ShowTrackCovers, PlayTrackItemAsync, ToggleRowLikeAsync);
            if (isLikedList)
            {
                row.IsLiked = true;
            }

            _allCollectionRows.Add(row);
            added.Add(row);
            if (!hasQuery || RowMatchesQuery(row, query))
            {
                CollectionTracks.Add(row);
            }
        }

        OnPropertyChanged(nameof(CollectionFilterSummary));
        return added;
    }

    private void ClearAllCollectionRows()
    {
        CancelAndDispose(ref _rowCoverCts);
        foreach (var row in _allCollectionRows)
        {
            row.Dispose();
        }

        _allCollectionRows.Clear();
        CollectionTracks.Clear();
        ClearUnusedSharedCovers(CollectionTracks);
        OnPropertyChanged(nameof(CollectionFilterSummary));
    }

    private void ApplyCollectionFilter()
    {
        var query = CollectionFilterQuery.Trim();
        CollectionTracks.Clear();
        if (string.IsNullOrWhiteSpace(query))
        {
            foreach (var row in _allCollectionRows)
            {
                CollectionTracks.Add(row);
            }

            return;
        }

        foreach (var row in _allCollectionRows)
        {
            if (RowMatchesQuery(row, query))
            {
                CollectionTracks.Add(row);
            }
        }
    }

    private static bool RowMatchesQuery(SpotifyTrackRowViewModel row, string query) =>
        row.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        row.Artists.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        row.AlbumName.Contains(query, StringComparison.OrdinalIgnoreCase);

    private async Task LoadFypAsync(bool forceRefresh = false)
    {
        if (_disposed)
        {
            return;
        }

        RefreshConnectionBadges();
        if (!_webApi.IsConnected)
        {
            FypSubtitle = "Connect Spotify Client ID in Settings → Spotify & APIs to unlock personalized For You feeds";
            ClearTrackCollection(FypTracks);
            ClearTrackCollection(HomeRecentTracks);
            ClearArtistCards(HomeTopArtists);
            ClearAlbumCards(HomeAlbums);
            HasFypTracks = false;
            HasHomeRecentTracks = false;
            HasHomeTopArtists = false;
            HasHomeAlbums = false;
            return;
        }

        var ct = ResetWorkToken();
        IsBusy = true;
        ListFadeOpacity = 0.4;
        FeedMessage = string.Empty;
        try
        {
            var fypCacheKey = $"rem-fyp-{DateTime.Now:yyyy-MM-dd}";
            if (!forceRefresh)
            {
                var cached = await _database.GetSpotifyCachedMixAsync(fypCacheKey, ct);
                if (cached is not null && cached.Tracks.Count > 0 && (DateTime.UtcNow - cached.GeneratedAtUtc).TotalHours < 6)
                {
                    FypSubtitle = cached.Subtitle;
                    _soloist.RememberTrackMetadata(cached.Tracks);
                    ClearTrackCollection(FypTracks);
                    for (var i = 0; i < cached.Tracks.Count; i++)
                    {
                        FypTracks.Add(new SpotifyTrackRowViewModel(i + 1, cached.Tracks[i], ShowTrackCovers, PlayTrackItemAsync, ToggleRowLikeAsync));
                    }

                    HasFypTracks = FypTracks.Count > 0;
                    if (IsHomeClassicLayout)
                    {
                        ClearHomeShelvesOnly();
                        var cachedRows = FypTracks.ToList();
                        if (cachedRows.Count > 0)
                        {
                            _ = SyncRowLikedStatesAsync(cachedRows);
                            if (ShowTrackCovers)
                            {
                                _ = LoadRowCoversAsync(cachedRows, decodeWidth: 44);
                            }
                        }
                    }
                    else
                    {
                        await PopulateHomeShelvesAsync([], cached.Tracks, ct);
                    }
                    return;
                }
            }

            var recentTask = _webApi.GetRecentlyPlayedAsync(12, ct);
            var topTask = _webApi.GetTopTracksAsync(12, ct, "medium_term");
            await Task.WhenAll(recentTask, topTask);

            var recent = await recentTask;
            var top = await topTask;
            var seeds = top.Concat(recent).GroupBy(t => t.Uri, StringComparer.Ordinal).Select(g => g.First()).ToList();

            var finalTracks = new List<SpotifyTrackItem>();
            if (_lastFm.HasApiKey && seeds.Count > 0)
            {
                FypSubtitle = "For You · Built from your Spotify taste + Last.fm similar tracks";
                var recs = await _lastFm.GetFypRecommendationsAsync(seeds, 14, ct);
                var seenUris = new HashSet<string>(StringComparer.Ordinal);
                var networkCalls = 0;

                foreach (var rec in recs)
                {
                    if (ct.IsCancellationRequested || finalTracks.Count >= 12)
                    {
                        break;
                    }

                    var key = $"{rec.Artist.Trim().ToLowerInvariant()}::{rec.Title.Trim().ToLowerInvariant()}";
                    var hit = await _database.GetResolvedSpotifyTrackAsync(key, ct);
                    if (hit is null)
                    {
                        if (_webApi.IsRateLimited) break;
                        if (networkCalls > 0) await Task.Delay(240, ct);
                        networkCalls++;
                        var matches = await _webApi.SearchTracksAsync($"{rec.Artist} {rec.Title}", 1, ct);
                        hit = matches.FirstOrDefault();
                        if (hit is not null)
                        {
                            await _database.SaveResolvedSpotifyTrackAsync(key, hit, ct);
                        }
                    }

                    if (hit is not null && seenUris.Add(hit.Uri))
                    {
                        finalTracks.Add(hit);
                    }
                }
            }
            else
            {
                FypSubtitle = seeds.Count > 0
                    ? "Your Top & Recent Rotation · Add a free Last.fm API key in Settings for discovery mixes"
                    : "Start playing tracks to seed your For You feed";
            }

            if (finalTracks.Count == 0)
            {
                finalTracks = seeds.Take(16).ToList();
            }

            if (finalTracks.Count == 0 && !string.IsNullOrWhiteSpace(_webApi.LastApiError))
            {
                FeedMessage = _webApi.LastApiError;
            }

            _soloist.RememberTrackMetadata(finalTracks);
            ClearTrackCollection(FypTracks);
            for (var i = 0; i < finalTracks.Count; i++)
            {
                FypTracks.Add(new SpotifyTrackRowViewModel(i + 1, finalTracks[i], ShowTrackCovers, PlayTrackItemAsync, ToggleRowLikeAsync));
            }

            HasFypTracks = FypTracks.Count > 0;
            if (finalTracks.Count > 0)
            {
                await _database.SaveSpotifyCachedMixAsync(
                    new RemMixDescriptor(fypCacheKey, "For You", FypSubtitle, "FYP", "#1DB954", DateTime.UtcNow, finalTracks),
                    ct);
            }

            if (IsHomeClassicLayout)
            {
                ClearHomeShelvesOnly();
                var freshRows = FypTracks.ToList();
                if (freshRows.Count > 0)
                {
                    _ = SyncRowLikedStatesAsync(freshRows);
                    if (ShowTrackCovers)
                    {
                        _ = LoadRowCoversAsync(freshRows, decodeWidth: 44);
                    }
                }
            }
            else
            {
                await PopulateHomeShelvesAsync(recent, finalTracks, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _actionLog.Log("Warning", $"Spotify FYP build error: {ex.Message}");
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
                ListFadeOpacity = 1.0;
            }
        }
    }

    private async Task PopulateHomeShelvesAsync(
        IReadOnlyList<SpotifyTrackItem> recentSeed,
        IReadOnlyList<SpotifyTrackItem> recommendedSeed,
        CancellationToken ct)
    {
        var recentTask = recentSeed.Count > 0
            ? Task.FromResult(recentSeed.ToList())
            : _webApi.GetRecentlyPlayedAsync(12, ct);
        var topArtistsTask = _webApi.GetTopArtistsAsync(10, "medium_term", ct);
        var savedAlbumsTask = _webApi.GetSavedAlbumsAsync(10, ct);
        await Task.WhenAll(recentTask, topArtistsTask, savedAlbumsTask);
        if (_disposed || ct.IsCancellationRequested)
        {
            return;
        }

        var recentList = (await recentTask)
            .GroupBy(t => t.Uri, StringComparer.Ordinal)
            .Select(g => g.First())
            .Take(12)
            .ToList();
        var artistsList = await topArtistsTask;
        var savedAlbums = await savedAlbumsTask;

        _soloist.RememberTrackMetadata(recentList);
        ClearTrackCollection(HomeRecentTracks);
        for (var i = 0; i < recentList.Count; i++)
        {
            HomeRecentTracks.Add(new SpotifyTrackRowViewModel(i + 1, recentList[i], ShowTrackCovers, PlayTrackItemAsync, ToggleRowLikeAsync));
        }

        HasHomeRecentTracks = HomeRecentTracks.Count > 0;

        ClearArtistCards(HomeTopArtists);
        foreach (var artist in artistsList)
        {
            HomeTopArtists.Add(new SpotifyArtistCardViewModel(artist, OpenArtistFromCardAsync));
        }

        HasHomeTopArtists = HomeTopArtists.Count > 0;

        var albumPool = new List<SpotifyAlbumItem>(savedAlbums);
        var seenAlbumIds = new HashSet<string>(
            albumPool.Where(a => !string.IsNullOrWhiteSpace(a.Id)).Select(a => a.Id),
            StringComparer.Ordinal);
        foreach (var tr in recommendedSeed.Concat(recentList))
        {
            if (albumPool.Count >= 12) break;
            if (!string.IsNullOrWhiteSpace(tr.AlbumId) &&
                !string.IsNullOrWhiteSpace(tr.AlbumName) &&
                seenAlbumIds.Add(tr.AlbumId))
            {
                albumPool.Add(new SpotifyAlbumItem(
                    tr.AlbumId,
                    $"spotify:album:{tr.AlbumId}",
                    tr.AlbumName,
                    tr.Artists,
                    string.Empty,
                    0,
                    tr.AlbumCoverUrl));
            }
        }

        ClearAlbumCards(HomeAlbums);
        foreach (var album in albumPool)
        {
            HomeAlbums.Add(new SpotifyAlbumCardViewModel(album, OpenAlbumFromCardAsync, PlayAlbumCardAsync));
        }

        HasHomeAlbums = HomeAlbums.Count > 0;
        _homeShelvesLoaded = true;

        var combinedTrackRows = FypTracks.Concat(HomeRecentTracks).ToList();
        if (combinedTrackRows.Count > 0)
        {
            _ = SyncRowLikedStatesAsync(combinedTrackRows);
            if (ShowTrackCovers)
            {
                _ = LoadRowCoversAsync(combinedTrackRows, decodeWidth: _homeCoverDecodeWidth);
            }
        }

        if (ShowTrackCovers || ShowPlaylistIcons)
        {
            if (HomeTopArtists.Count > 0)
            {
                _ = LoadArtistCardPhotosAsync(HomeTopArtists.ToList(), Math.Min(_homeCoverDecodeWidth, 240));
            }

            if (HomeAlbums.Count > 0)
            {
                _ = LoadAlbumCardCoversAsync(HomeAlbums.ToList(), ct, _homeCoverDecodeWidth);
            }
        }
    }

    private async Task PlayTrackItemAsync(SpotifyTrackItem item)
    {
        if (!_soloist.HasApiKey)
        {
            await _dialogService.ShowWarningAsync(
                "Soloist API Key Needed",
                "Paste your Spotify Soloist API key from developer.spotify.com into Spotify Settings → Spotify & APIs first.");
            return;
        }

        if (!_soloist.IsDaemonRunning)
        {
            var (started, err) = await _soloist.EnsureDaemonRunningAsync(CancellationToken.None);
            if (!started)
            {
                await _dialogService.ShowErrorAsync("Spotify Soloist Error", "Could not start Spotify Soloist.", err);
                return;
            }

            await Task.Delay(500);
        }

        var activeList = ActiveDetailPage != SpotifyDetailPage.None
            ? DetailTracks.Select(r => r.Item).ToList()
            : ActiveSection switch
            {
                SpotifyNavSection.HomeFyp => HomeRecentTracks.Any(r => string.Equals(r.Item.Uri, item.Uri, StringComparison.Ordinal)) &&
                                             !FypTracks.Any(r => string.Equals(r.Item.Uri, item.Uri, StringComparison.Ordinal))
                    ? HomeRecentTracks.Select(r => r.Item).ToList()
                    : FypTracks.Select(r => r.Item).ToList(),
                SpotifyNavSection.Search => SearchTracks.Select(r => r.Item).ToList(),
                _ => CollectionTracks.Select(r => r.Item).ToList()
            };

        var idx = activeList.FindIndex(t => string.Equals(t.Uri, item.Uri, StringComparison.Ordinal));
        var ok = idx >= 0
            ? await _soloist.PlayTrackListAsync(activeList, idx)
            : await _soloist.PlayTrackListAsync([item], 0);

        if (!ok)
        {
            await _dialogService.ShowWarningAsync(
                "Pair REM via Spotify Connect",
                "Open Spotify on your phone or browser and select the 'REM' Connect device once to authenticate Soloist, then click any song here.");
        }
        else
        {
            _actionLog.Log("Spotify", $"Playing via Soloist: '{item.Name}' — {item.Artists}");
        }
    }

    private async Task AddTrackToQueueAsync(SpotifyTrackItem item)
    {
        var ok = await _soloist.AddToQueueAsync(item);
        if (ok)
        {
            ShowToast($"Queued: {item.Name}");
            _actionLog.Log("Spotify", $"Added to Soloist queue: {item.Name}");
            if (_soloist.IsWebSocketConnected)
            {
                await _soloist.RequestQueueAsync(25);
            }
        }
        else
        {
            ShowToast("Play a track or pair REM first to add to queue");
        }
    }

    public Task PlayTrackFromMenuAsync(SpotifyTrackItem item) => PlayTrackItemAsync(item);
    public Task AddTrackToQueueFromMenuAsync(SpotifyTrackItem item) => AddTrackToQueueAsync(item);
    public Task ToggleLikeRowFromMenuAsync(SpotifyTrackRowViewModel row) => ToggleRowLikeAsync(row);
    public void TogglePinPlaylistFromMenu(SpotifyPlaylistNavViewModel tab) => TogglePinPlaylist(tab);

    public async Task PlayPlaylistFromMenuAsync(SpotifyPlaylistItem playlist)
    {
        if (!_soloist.HasApiKey)
        {
            ShowToast("Add your Soloist API key in Spotify Settings first");
            return;
        }

        if (!_soloist.IsDaemonRunning)
        {
            var (started, _) = await _soloist.EnsureDaemonRunningAsync(CancellationToken.None);
            if (!started)
            {
                ShowToast("Could not start Spotify Soloist");
                return;
            }

            await Task.Delay(500);
        }

        if (!string.IsNullOrWhiteSpace(playlist.Id) && _webApi.IsConnected)
        {
            var page = await _webApi.GetPlaylistItemsPageAsync(playlist.Id, playlist.Uri, offset: 0, limit: 50, CancellationToken.None);
            if (page.Tracks.Count > 0)
            {
                await _soloist.PlayTrackListAsync(page.Tracks, 0);
                ShowToast($"Playing {playlist.Name}");
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(playlist.Uri))
        {
            await _soloist.PlayAsync(playlist.Uri);
            ShowToast($"Playing {playlist.Name}");
        }
    }

    public async Task AddTrackToPlaylistFromMenuAsync(SpotifyPlaylistItem playlist, SpotifyTrackItem track)
    {
        var (ok, needsReauth, err) = await _webApi.AddTrackToPlaylistAsync(playlist.Id, track.Uri, CancellationToken.None);
        if (ok)
        {
            ShowToast($"Added to {playlist.Name}");
            _actionLog.Log("Spotify", $"Added '{track.Name}' to playlist '{playlist.Name}'.");
            return;
        }

        if (needsReauth)
        {
            await _dialogService.ShowWarningAsync(
                "Spotify Permission Needed",
                err ?? "Reconnect Spotify (PKCE) in Settings → Spotify & APIs to grant playlist edit permissions.");
        }
        else
        {
            await _dialogService.ShowErrorAsync("Add to Playlist Failed", $"Could not add '{track.Name}' to '{playlist.Name}'.", err);
        }
    }

    public void NotifyLinkCopied(string label) => ShowToast($"Copied link for {label}");

    private async Task PlayQueueEntryAsync(SpotifyQueueEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Uri))
        {
            await _soloist.PlayFromQueueAsync(entry);
        }
    }

    private void OnModuleStateChanged(object? sender, ModuleState state) => RefreshModuleHeaderStatus();
    private void OnModuleStatusSummaryChanged(object? sender, EventArgs e) => RefreshModuleHeaderStatus();

    private void OnSoloistConnectionChanged(object? sender, bool connected)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (!_soloist.HasApiKey)
            {
                EngineStatusText = "Soloist API key required";
                return;
            }

            EngineStatusText = connected
                ? (_soloist.IsAuthenticated ? $"Soloist · {_soloist.AuthenticatedUser}" : "Soloist · Select 'REM' in Spotify Connect")
                : (_soloist.IsDaemonRunning ? "Connecting to Soloist WS..." : "Soloist stopped");
        });
    }

    private void OnSoloistAuthChanged(object? sender, (bool IsAuthenticated, string? Username) args)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            NeedsConnectPairing = !args.IsAuthenticated && _soloist.IsDaemonRunning;
            if (args.IsAuthenticated)
            {
                EngineStatusText = string.IsNullOrWhiteSpace(args.Username)
                    ? "Soloist · Connected"
                    : $"Soloist · {args.Username}";
            }
        });
    }

    private void OnSoloistStatusChanged(object? sender, GoLibrespotStatus? status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && status is not null)
            {
                ApplyStatus(status);
            }
        });
    }

    private void OnSoloistQueueUpdated(object? sender, List<SpotifyQueueEntry> entries)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            QueueEntries.Clear();
            foreach (var entry in entries)
            {
                QueueEntries.Add(new SpotifyQueueRowViewModel(entry, PlayQueueEntryAsync));
            }

            HasQueueEntries = QueueEntries.Count > 0;
            QueueStatusText = HasQueueEntries
                ? "Up Next"
                : "Up Next is empty (play a playlist or right-click a track to Add to Queue)";
        });
    }

    private void OnSoloistPlaybackError(object? sender, string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                _ = _dialogService.ShowErrorAsync("Spotify Soloist Error", "Spotify Soloist reported an issue.", message);
            }
        });
    }

    private void ApplyStatus(GoLibrespotStatus status)
    {
        NeedsSoloistApiKey = false;
        NeedsConnectPairing = !_soloist.IsAuthenticated && status.Track is null;
        EngineStatusText = string.IsNullOrWhiteSpace(status.Username)
            ? $"Soloist · {status.DeviceName}"
            : $"{status.DeviceName} · {status.Username}";

        IsShuffled = status.ShuffleContext;
        RepeatMode = status.RepeatTrack
            ? SpotifyRepeatMode.Track
            : status.RepeatContext ? SpotifyRepeatMode.Context : SpotifyRepeatMode.Off;

        _isSyncingVolume = true;
        Volume = Math.Clamp(status.Volume, 0, 100);
        _isSyncingVolume = false;

        var track = status.Track;
        if (track is null || status.Stopped)
        {
            HasCurrentTrack = false;
            IsPlaying = false;
            CurrentTrackTitle = "Nothing playing";
            CurrentTrackArtist = NeedsConnectPairing
                ? "Select 'REM' in Spotify Connect once to pair"
                : "Select any track or playlist to play";
            CurrentTrackAlbum = string.Empty;
            CurrentAudioSpec = string.Empty;
            SeekMaxSeconds = 1;
            if (!_isUserScrubbing)
            {
                SeekPositionSeconds = 0;
                PositionDisplay = "0:00";
            }
            DurationDisplay = "0:00";
            return;
        }

        var wasPlaying = IsPlaying;
        var nowPlaying = !status.Paused && !status.Stopped;

        NeedsConnectPairing = false;
        HasCurrentTrack = true;
        IsPlaying = nowPlaying;
        CurrentTrackTitle = track.Name;
        CurrentTrackArtist = track.ArtistsDisplay;
        CurrentTrackAlbum = track.AlbumName;
        CurrentAudioSpec = track.Format ?? "Spotify Soloist";

        var durSec = Math.Max(1, track.DurationMs / 1000.0);
        SeekMaxSeconds = durSec;
        DurationDisplay = FormatSeconds(durSec);

        var trackChanged = !string.Equals(_lastTrackUri, track.Uri, StringComparison.Ordinal);
        if (trackChanged)
        {
            _anchorPositionMs = track.PositionMs;
            _anchorTimestamp = DateTimeOffset.UtcNow;
            _seekGraceUntil = DateTimeOffset.MinValue;
        }
        else if (status.HasAuthoritativePosition &&
                 !_isUserScrubbing &&
                 DateTimeOffset.UtcNow >= _seekGraceUntil)
        {
            _anchorPositionMs = track.PositionMs;
            _anchorTimestamp = DateTimeOffset.UtcNow;
        }
        else if (wasPlaying && !nowPlaying)
        {
            // Freeze exact live timestamp on pause instead of resetting to 0.
            var elapsed = (long)Math.Max(0, (DateTimeOffset.UtcNow - _anchorTimestamp).TotalMilliseconds);
            _anchorPositionMs = Math.Clamp(_anchorPositionMs + elapsed, 0, (long)(durSec * 1000));
            _anchorTimestamp = DateTimeOffset.UtcNow;
        }
        else if (!wasPlaying && nowPlaying)
        {
            // Resume timer cleanly from the frozen pause position.
            _anchorTimestamp = DateTimeOffset.UtcNow;
        }

        var livePosMs = (long)Math.Clamp(GetLivePlaybackPositionMs(), 0, durSec * 1000);
        if (!_isUserScrubbing)
        {
            var posSec = Math.Clamp(livePosMs / 1000.0, 0, durSec);
            SeekPositionSeconds = posSec;
            PositionDisplay = FormatSeconds(posSec);
        }

        SyncActiveLyricIndex(livePosMs);

        _lastTrackMeta = track;
        if (trackChanged)
        {
            _lastTrackUri = track.Uri;
            _lastTrackLyricsHadArtist = track.ArtistNames.Count > 0;
            _lastTrackHasLoadedLyrics = false;
            IsSyncPickMode = false;
            IsLyricsSearchOpen = false;
            LyricsSearchResults.Clear();
            LyricsSearchQuery = string.Empty;

            _ = LoadTrackExtrasAsync(track);
            _ = RecordPlayInHistoryAsync(track);
            if (IsDrawerOpen && IsQueueDrawerActive)
            {
                _ = _soloist.RequestQueueAsync(25);
            }
        }
        else
        {
            if (((IsDrawerOpen && IsLyricsDrawerActive) || IsLyricsFullscreen) &&
                !_lastTrackHasLoadedLyrics &&
                !_lastTrackLyricsHadArtist &&
                track.ArtistNames.Count > 0)
            {
                _lastTrackLyricsHadArtist = true;
                _ = LoadLyricsAsync(track);
            }

            if (!string.Equals(_lastAlbumArtUrl, track.AlbumCoverUrl, StringComparison.Ordinal))
            {
                _ = LoadAlbumArtAndGlowAsync(track.AlbumCoverUrl);
            }
        }
    }

    private async Task LoadTrackExtrasAsync(GoLibrespotTrack track)
    {
        var shouldFetchLyrics = (IsDrawerOpen && IsLyricsDrawerActive) || IsLyricsFullscreen;
        if (!shouldFetchLyrics)
        {
            _activeLyricIndex = -1;
            IsSyncedLyrics = false;
            PlainLyrics = string.Empty;
            LyricLines.Clear();
            if (FullscreenLyricLines.Count > 0) FullscreenLyricLines.Clear();
            LyricsStatusText = "Open Lyrics to load synced lyrics.";
        }

        await Task.WhenAll(
            LoadAlbumArtAndGlowAsync(track.AlbumCoverUrl),
            shouldFetchLyrics ? LoadLyricsAsync(track) : Task.CompletedTask,
            RefreshCurrentLikedStateAsync(track.Uri));
    }

    private async Task LoadAlbumArtAndGlowAsync(string? coverUrl)
    {
        _lastAlbumArtUrl = coverUrl;
        CancelAndDispose(ref _artCts);
        if (_disposed)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _artCts = cts;
        try
        {
            if (string.IsNullOrWhiteSpace(coverUrl) &&
                _webApi.IsConnected &&
                TryExtractSpotifyTrackId(_lastTrackUri, out var trackId))
            {
                var apiTrack = await _webApi.GetTrackByIdAsync(trackId, cts.Token);
                if (apiTrack is not null && !string.IsNullOrWhiteSpace(apiTrack.AlbumCoverUrl))
                {
                    coverUrl = apiTrack.AlbumCoverUrl;
                    _lastAlbumArtUrl = coverUrl;
                }
            }

            if (string.IsNullOrWhiteSpace(coverUrl) || cts.IsCancellationRequested || _disposed)
            {
                if (!cts.IsCancellationRequested && !_disposed)
                {
                    CurrentAlbumArt?.Dispose();
                    CurrentAlbumArt = null;
                    AmbientGlowBrush = DefaultAmbientGlow;
                }
                return;
            }

            var bytes = await _webApi.DownloadImageBytesAsync(coverUrl, cts.Token);
            if (bytes is null || bytes.Length == 0 || cts.IsCancellationRequested || _disposed)
            {
                return;
            }

            Bitmap? coverBmp = null;
            Color? dominantColor = null;

            await Task.Run(() =>
            {
                using var ms = new MemoryStream(bytes, writable: false);
                coverBmp = Bitmap.DecodeToWidth(ms, 180);
                dominantColor = DominantColorExtractor.ExtractDominantColor(bytes);
            }, cts.Token);

            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || cts.IsCancellationRequested)
                {
                    coverBmp?.Dispose();
                    return;
                }

                var prev = CurrentAlbumArt;
                CurrentAlbumArt = coverBmp;
                prev?.Dispose();
                if (dominantColor is { } dom)
                {
                    AmbientGlowBrush = DominantColorExtractor.CreateAmbientGlowBrush(dom);
                    UpdateAlbumTintFromColor(dom);
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private async Task LoadLyricsAsync(GoLibrespotTrack track, bool forceRefresh = false)
    {
        CancelAndDispose(ref _lyricsCts);
        if (_disposed)
        {
            return;
        }

        var reqId = Interlocked.Increment(ref _lyricsRequestId);
        var cts = new CancellationTokenSource();
        _lyricsCts = cts;

        LyricsStatusText = $"Fetching lyrics for {track.Name}...";
        _activeLyricIndex = -1;
        IsSyncedLyrics = false;
        PlainLyrics = string.Empty;
        LyricLines.Clear();
        if (FullscreenLyricLines.Count > 0) FullscreenLyricLines.Clear();
        LyricsOffsetMs = 0;
        IsSyncPickMode = false;

        try
        {
            var cached = !string.IsNullOrWhiteSpace(track.Uri)
                ? _database.GetCachedSpotifyLyrics(track.Uri)
                : null;

            if (cached is not null)
            {
                LyricsOffsetMs = cached.OffsetMs;
                if (!forceRefresh)
                {
                    if (cached.IsSynced && !string.IsNullOrWhiteSpace(cached.SyncedLrc))
                    {
                        var cachedLines = LrclibService.ParseLrcLines(cached.SyncedLrc);
                        if (cachedLines.Count > 0)
                        {
                            ApplyLyricsToView(
                                new LyricsResult(true, cachedLines, cached.PlainLyrics, cached.SyncedLrc),
                                cached.SourceLabel ?? "Synced · Cached (0 KB/s)");
                            return;
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(cached.PlainLyrics))
                    {
                        ApplyLyricsToView(
                            new LyricsResult(false, [], cached.PlainLyrics),
                            cached.SourceLabel ?? "Plain lyrics · Cached");
                        return;
                    }
                }
            }

            var artistQuery = track.ArtistsDisplay;
            var trackQuery = track.Name;
            var durSec = (int)Math.Round(track.DurationMs / 1000.0);

            if ((string.IsNullOrWhiteSpace(artistQuery) ||
                 string.Equals(artistQuery, "Unknown Artist", StringComparison.OrdinalIgnoreCase) ||
                 durSec <= 0) &&
                _webApi.IsConnected &&
                TryExtractSpotifyTrackId(track.Uri, out var trackId))
            {
                var apiTrack = await _webApi.GetTrackByIdAsync(trackId, cts.Token);
                if (apiTrack is not null)
                {
                    if (string.IsNullOrWhiteSpace(artistQuery) ||
                        string.Equals(artistQuery, "Unknown Artist", StringComparison.OrdinalIgnoreCase))
                    {
                        artistQuery = apiTrack.Artists;
                    }

                    if (string.IsNullOrWhiteSpace(trackQuery) || trackQuery == "Unknown Track")
                    {
                        trackQuery = apiTrack.Name;
                    }

                    if (durSec <= 0 && apiTrack.DurationMs > 0)
                    {
                        durSec = (int)Math.Round(apiTrack.DurationMs / 1000.0);
                    }

                    _lastTrackLyricsHadArtist = !string.IsNullOrWhiteSpace(artistQuery) &&
                        !string.Equals(artistQuery, "Unknown Artist", StringComparison.OrdinalIgnoreCase);
                }
            }

            var result = await _lrclib.GetLyricsAsync(artistQuery, trackQuery, durSec, cts.Token);
            if (_disposed || cts.IsCancellationRequested || reqId != _lyricsRequestId)
            {
                return;
            }

            if (result is null)
            {
                LyricsStatusText = "No lyrics found on LRCLIB — try Search to pick a match.";
                return;
            }

            var statusLabel = result.IsSynced ? "Synced · LRCLIB" : "Plain lyrics · LRCLIB";
            ApplyLyricsToView(result, statusLabel);

            if (!string.IsNullOrWhiteSpace(track.Uri))
            {
                _database.SaveCachedSpotifyLyrics(
                    track.Uri,
                    result.IsSynced,
                    result.RawSyncedLrc,
                    result.PlainLyrics,
                    LyricsOffsetMs,
                    statusLabel);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_disposed && reqId == _lyricsRequestId && !IsSyncedLyrics && !HasPlainLyrics)
            {
                LyricsStatusText = "Lyrics request cancelled — click Retry to fetch again.";
            }
        }
        catch (Exception ex)
        {
            _actionLog.Log("Warning", $"Lyrics load error: {ex.Message}");
            if (!_disposed && reqId == _lyricsRequestId)
            {
                LyricsStatusText = "Could not fetch lyrics right now — click Retry.";
            }
        }
    }

    private void ApplyLyricsToView(LyricsResult result, string statusLabel)
    {
        _activeLyricIndex = -1;
        LyricLines.Clear();
        PlainLyrics = string.Empty;
        IsSyncedLyrics = false;

        if (result.IsSynced && result.SyncedLines.Count > 0)
        {
            foreach (var line in result.SyncedLines)
            {
                LyricLines.Add(new SpotifyLyricLineViewModel(line, OnLyricLineClickedAsync));
            }

            IsSyncedLyrics = true;
            LyricsStatusText = statusLabel;
            _lastTrackHasLoadedLyrics = true;
            if (IsLyricsFullscreen)
            {
                OnPropertyChanged(nameof(FullscreenLyricLines));
            }
            SyncLyricLinesVisualOptions();
            SyncActiveLyricIndex((long)GetLivePlaybackPositionMs());
        }
        else if (!string.IsNullOrWhiteSpace(result.PlainLyrics))
        {
            IsSyncedLyrics = false;
            PlainLyrics = result.PlainLyrics;
            LyricsStatusText = statusLabel;
            _lastTrackHasLoadedLyrics = true;
            if (IsLyricsFullscreen)
            {
                OnPropertyChanged(nameof(FullscreenLyricLines));
            }
        }
        else
        {
            LyricsStatusText = "No lyrics available.";
            if (IsLyricsFullscreen)
            {
                OnPropertyChanged(nameof(FullscreenLyricLines));
            }
        }
    }

    private async Task ApplySelectedLyricCandidateAsync(LrclibSearchCandidate candidate)
    {
        IsLyricsSearchOpen = false;
        LyricsSearchResults.Clear();

        var label = candidate.IsSynced
            ? $"Synced · {candidate.Title}"
            : $"Plain · {candidate.Title}";
        ApplyLyricsToView(candidate.Lyrics, label);

        if (!string.IsNullOrWhiteSpace(_lastTrackUri))
        {
            _database.SaveCachedSpotifyLyrics(
                _lastTrackUri,
                candidate.Lyrics.IsSynced,
                candidate.Lyrics.RawSyncedLrc,
                candidate.Lyrics.PlainLyrics,
                LyricsOffsetMs,
                label);
        }

        await Task.CompletedTask;
    }

    private async Task OnLyricLineClickedAsync(SpotifyLyricLineViewModel line)
    {
        if (_disposed || !HasCurrentTrack)
        {
            return;
        }

        if (IsSyncPickMode)
        {
            var currentMs = GetLivePlaybackPositionMs();
            LyricsOffsetMs = Math.Clamp((int)Math.Round(line.Timestamp.TotalMilliseconds - currentMs), -30000, 30000);
            if (!string.IsNullOrWhiteSpace(_lastTrackUri))
            {
                _database.SaveSpotifyLyricsOffset(_lastTrackUri, LyricsOffsetMs);
            }

            IsSyncPickMode = false;
            SyncActiveLyricIndex((long)currentMs);
            _actionLog.Log("Spotify", $"Synced lyrics offset to {LyricsOffsetDisplay} at '{line.Text}'.");
            return;
        }

        var targetMs = Math.Max(0, (long)Math.Round(line.Timestamp.TotalMilliseconds) - LyricsOffsetMs);
        var maxMs = (long)Math.Max(1000, SeekMaxSeconds * 1000.0);
        targetMs = Math.Min(targetMs, maxMs);

        _anchorPositionMs = targetMs;
        _anchorTimestamp = DateTimeOffset.UtcNow;
        _seekGraceUntil = DateTimeOffset.UtcNow.AddMilliseconds(900);
        if (!_isUserScrubbing)
        {
            var sec = targetMs / 1000.0;
            SeekPositionSeconds = sec;
            PositionDisplay = FormatSeconds(sec);
        }

        SyncActiveLyricIndex(targetMs);
        await _soloist.SeekAsync(targetMs);
    }

    private void ApplyDisplaySettings(bool triggerReload)
    {
        var showIcons = ReadBoolSetting(DatabaseService.KeySpotifyShowPlaylistIcons, true);
        var showCovers = ReadBoolSetting(DatabaseService.KeySpotifyShowTrackCovers, true);
        var showPlayBarCover = ReadBoolSetting(DatabaseService.KeySpotifyShowPlayBarCover, true);
        var showGlow = ReadBoolSetting(DatabaseService.KeySpotifyShowAmbientGlow, true);
        var showRem = ReadBoolSetting(DatabaseService.KeySpotifyShowRemArt, true);
        var homeLayoutRaw = _database.GetSetting(DatabaseService.KeySpotifyHomeLayout);
        var newShelvesLayout = !string.Equals(homeLayoutRaw, "classic", StringComparison.OrdinalIgnoreCase);
        var rawQuality = _database.GetSetting(DatabaseService.KeySpotifyHomeCoverQuality);
        var newHomeWidth = int.TryParse(rawQuality, out var parsedWidth) && parsedWidth is 96 or 160 or 240 or 300
            ? parsedWidth
            : 160;
        var remFile = _database.GetSetting(DatabaseService.KeySpotifyRemArtFile);
        if (string.IsNullOrWhiteSpace(remFile))
        {
            remFile = "rem-welcome.png";
        }

        var layoutChanged = IsHomeShelvesLayout != newShelvesLayout;
        var qualityChanged = _homeCoverDecodeWidth != newHomeWidth;

        ShowPlaylistIcons = showIcons;
        ShowTrackCovers = showCovers;
        ShowPlayBarCover = showPlayBarCover;
        ShowAmbientGlow = showGlow;
        ShowRemArt = showRem;
        IsHomeShelvesLayout = newShelvesLayout;
        _homeCoverDecodeWidth = newHomeWidth;
        RemCompanionBitmap = showRem
            ? (_imageAssets.GetBrandImage(remFile, 220) ?? _imageAssets.GetBrandImage("rem-corner.png", 220))
            : null;

        if (!triggerReload)
        {
            return;
        }

        LoadLyricsCustomizationFromSettings();

        foreach (var row in FypTracks.Concat(HomeRecentTracks).Concat(SearchTracks).Concat(_allCollectionRows).Concat(DetailTracks))
        {
            row.ShowCover = showCovers;
            if (!showCovers)
            {
                row.SetCover(null);
            }
        }

        if (!showCovers)
        {
            ClearUnusedSharedCovers(null);
        }

        if (!showCovers && !showIcons)
        {
            foreach (var a in HomeTopArtists) a.SetPhoto(null);
            foreach (var al in HomeAlbums) al.SetCover(null);
        }

        if (layoutChanged)
        {
            if (IsHomeClassicLayout)
            {
                ClearHomeShelvesOnly();
                foreach (var r in FypTracks) r.SetCover(null);
                ClearUnusedSharedCovers(null);
                MemoryTrimService.ScheduleTrim();
            }
            else
            {
                foreach (var r in FypTracks) r.SetCover(null);
                ClearUnusedSharedCovers(null);
                if (IsHomeSection && FypTracks.Count > 0)
                {
                    var items = FypTracks.Select(r => r.Item).ToList();
                    var ct = ResetWorkToken();
                    _ = PopulateHomeShelvesAsync([], items, ct);
                }
            }
        }
        else if (qualityChanged && IsHomeShelvesLayout)
        {
            CancelAndDispose(ref _mixCoverCts);
            CancelAndDispose(ref _detailCoverCts);
            foreach (var r in FypTracks.Concat(HomeRecentTracks)) r.SetCover(null);
            foreach (var a in HomeTopArtists) a.SetPhoto(null);
            foreach (var al in HomeAlbums) al.SetCover(null);
            foreach (var c in RemMixCards) c.SetCover(null);
            ClearUnusedSharedCovers(null);
            MemoryTrimService.ScheduleTrim();
        }

        foreach (var pl in Playlists)
        {
            pl.ShowIcon = showIcons;
            if (!showIcons)
            {
                pl.SetCover(null);
            }
        }

        foreach (var card in RemMixCards)
        {
            card.ShowCoverArt = showIcons;
            if (!showIcons)
            {
                card.SetCover(null);
            }
        }

        if (!showIcons)
        {
            CancelAndDispose(ref _mixCoverCts);
            var oldMixCov = ActiveMixCoverBitmap;
            ActiveMixCoverBitmap = null;
            oldMixCov?.Dispose();
        }
        else
        {
            if (Playlists.Any(p => p.CoverBitmap is null && !string.IsNullOrWhiteSpace(p.Item.ImageUrl)))
            {
                _ = LoadPlaylistCoversAsync(Playlists.ToList());
            }

            if (IsHomeSection && RemMixCards.Any(c => c.CoverBitmap is null))
            {
                _ = LoadRemMixCardCoversAsync(IsHomeShelvesLayout ? _homeCoverDecodeWidth : 160);
            }

            if (ActiveDetailPage == SpotifyDetailPage.RemMix && _activeMix is not null && ActiveMixCoverBitmap is null)
            {
                _ = LoadActiveMixCoverAsync(_activeMix, _workCts?.Token ?? CancellationToken.None);
            }
        }

        if (showCovers)
        {
            var activeRows = ActiveDetailPage != SpotifyDetailPage.None
                ? DetailTracks.ToList()
                : ActiveSection switch
                {
                    SpotifyNavSection.HomeFyp => (IsHomeShelvesLayout ? FypTracks.Concat(HomeRecentTracks) : FypTracks).ToList(),
                    SpotifyNavSection.Search => SearchTracks.ToList(),
                    _ => CollectionTracks.ToList()
                };
            if (activeRows.Any(r => r.CoverBitmap is null && !string.IsNullOrWhiteSpace(r.Item.AlbumCoverUrl)))
            {
                var targetWidth = (IsHomeSection && IsHomeShelvesLayout) ? _homeCoverDecodeWidth : 44;
                _ = LoadRowCoversAsync(activeRows, decodeWidth: targetWidth);
            }
        }

        if ((showCovers || showIcons) && IsHomeSection && IsHomeShelvesLayout)
        {
            if (HomeTopArtists.Any(a => a.PhotoBitmap is null && !string.IsNullOrWhiteSpace(a.Item.ImageUrl)))
            {
                _ = LoadArtistCardPhotosAsync(HomeTopArtists.ToList(), Math.Min(_homeCoverDecodeWidth, 240));
            }

            if (HomeAlbums.Any(a => a.CoverBitmap is null && !string.IsNullOrWhiteSpace(a.Item.ImageUrl)))
            {
                _ = LoadAlbumCardCoversAsync(HomeAlbums.ToList(), _workCts?.Token ?? CancellationToken.None, _homeCoverDecodeWidth);
            }
        }
    }

    private bool ReadBoolSetting(string key, bool defaultValue)
    {
        var raw = _database.GetSetting(key);
        return bool.TryParse(raw, out var val) ? val : defaultValue;
    }

    private async Task ToggleRowLikeAsync(SpotifyTrackRowViewModel row)
    {
        if (!_webApi.IsConnected)
        {
            ShowToast("Connect Spotify (PKCE) in Settings to save liked songs");
            return;
        }

        var uri = row.Item.Uri;
        if (string.IsNullOrWhiteSpace(uri))
        {
            return;
        }

        var desired = !row.IsLiked;
        SyncLikedStateAcrossCollections(uri, desired);

        var ok = await _webApi.SetTrackLikedAsync(uri, desired, CancellationToken.None);
        if (!ok)
        {
            SyncLikedStateAcrossCollections(uri, !desired);
            ShowToast("Could not update Liked Songs");
            return;
        }

        ShowToast(desired ? $"Saved '{row.Title}'" : $"Removed '{row.Title}'");
        _actionLog.Log("Spotify", desired ? $"Liked '{row.Title}'" : $"Unliked '{row.Title}'");
    }

    private void SyncLikedStateAcrossCollections(string trackUri, bool liked)
    {
        if (string.Equals(_lastTrackUri, trackUri, StringComparison.Ordinal))
        {
            IsCurrentTrackLiked = liked;
        }

        foreach (var row in FypTracks.Concat(HomeRecentTracks).Concat(SearchTracks).Concat(_allCollectionRows).Concat(DetailTracks))
        {
            if (string.Equals(row.Item.Uri, trackUri, StringComparison.Ordinal))
            {
                row.IsLiked = liked;
            }
        }
    }

    private async Task SyncRowLikedStatesAsync(List<SpotifyTrackRowViewModel> rows)
    {
        if (_disposed || !_webApi.IsConnected || rows.Count == 0)
        {
            return;
        }

        try
        {
            var map = await _webApi.CheckTracksLikedBatchAsync(rows.Select(r => r.Item.Uri), CancellationToken.None);
            if (_disposed || map.Count == 0)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed)
                {
                    return;
                }

                foreach (var row in rows)
                {
                    if (map.TryGetValue(row.Item.Uri, out var liked))
                    {
                        row.IsLiked = liked;
                    }
                }
            });
        }
        catch
        {
            // Ignore transient liked-state sync error.
        }
    }

    private void TogglePinPlaylist(SpotifyPlaylistNavViewModel tab)
    {
        var nextPinned = !tab.IsPinned;
        tab.IsPinned = nextPinned;
        _database.SetSpotifyPlaylistPinned(tab.Item.Id, nextPinned);

        var ordered = Playlists
            .OrderByDescending(p => p.IsPinned)
            .ToList();
        Playlists.Clear();
        foreach (var p in ordered)
        {
            Playlists.Add(p);
        }

        ShowToast(nextPinned ? $"Pinned '{tab.Name}'" : $"Unpinned '{tab.Name}'");
    }

    private async Task LoadPlaylistCoversAsync(List<SpotifyPlaylistNavViewModel> tabs)
    {
        CancelAndDispose(ref _playlistCoverCts);
        if (_disposed || tabs.Count == 0 || !ShowPlaylistIcons)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _playlistCoverCts = cts;

        using var gate = new SemaphoreSlim(4, 4);
        var tasks = tabs.Select(async tab =>
        {
            if (cts.IsCancellationRequested || _disposed || !ShowPlaylistIcons || string.IsNullOrWhiteSpace(tab.Item.ImageUrl))
            {
                return;
            }

            await gate.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                if (cts.IsCancellationRequested || _disposed || !ShowPlaylistIcons)
                {
                    return;
                }

                var bmp = await _webApi.LoadSmallBitmapAsync(tab.Item.ImageUrl, 48, cts.Token).ConfigureAwait(false);
                if (bmp is not null)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_disposed || cts.IsCancellationRequested || !ShowPlaylistIcons)
                        {
                            bmp.Dispose();
                            return;
                        }

                        tab.SetCover(bmp);
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ClearPlaylistCollection()
    {
        CancelAndDispose(ref _playlistCoverCts);
        foreach (var p in Playlists)
        {
            p.Dispose();
        }

        Playlists.Clear();
    }

    private void ShowToast(string message)
    {
        if (_disposed)
        {
            return;
        }

        CancelAndDispose(ref _toastCts);
        ToastMessage = message;

        var cts = new CancellationTokenSource();
        _toastCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2600, cts.Token).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed && !cts.IsCancellationRequested && string.Equals(ToastMessage, message, StringComparison.Ordinal))
                    {
                        ToastMessage = string.Empty;
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private double GetLivePlaybackPositionMs()
    {
        if (!IsPlaying)
        {
            return _anchorPositionMs;
        }

        var elapsed = (DateTimeOffset.UtcNow - _anchorTimestamp).TotalMilliseconds;
        return Math.Clamp(_anchorPositionMs + elapsed, 0, Math.Max(1, SeekMaxSeconds) * 1000.0);
    }

    private static bool TryExtractSpotifyTrackId(string? uri, out string trackId)
    {
        trackId = string.Empty;
        if (string.IsNullOrWhiteSpace(uri))
        {
            return false;
        }

        const string prefix = "spotify:track:";
        if (uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && uri.Length > prefix.Length)
        {
            trackId = uri[prefix.Length..];
            return true;
        }

        return false;
    }

    private async Task RefreshCurrentLikedStateAsync(string trackUri)
    {
        if (!_webApi.IsConnected || string.IsNullOrWhiteSpace(trackUri))
        {
            return;
        }

        try
        {
            var liked = await _webApi.IsTrackLikedAsync(trackUri, CancellationToken.None);
            if (!_disposed && string.Equals(_lastTrackUri, trackUri, StringComparison.Ordinal) && liked is { } val)
            {
                IsCurrentTrackLiked = val;
            }
        }
        catch
        {
        }
    }

    private async Task LoadRowCoversAsync(List<SpotifyTrackRowViewModel> rows, bool resetExisting = true, int decodeWidth = 44)
    {
        CancellationTokenSource cts;
        if (resetExisting)
        {
            CancelAndDispose(ref _rowCoverCts);
            if (_disposed || rows.Count == 0)
            {
                return;
            }

            cts = new CancellationTokenSource();
            _rowCoverCts = cts;
        }
        else
        {
            if (_disposed || rows.Count == 0)
            {
                return;
            }

            cts = _rowCoverCts is { IsCancellationRequested: false } existing
                ? existing
                : (_rowCoverCts = new CancellationTokenSource());
        }

        var toDownload = new List<(SpotifyTrackRowViewModel Row, string Url, string CacheKey)>();
        foreach (var row in rows)
        {
            if (cts.IsCancellationRequested || _disposed)
            {
                return;
            }

            var url = row.Item.AlbumCoverUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var cacheKey = $"{decodeWidth}|{url}";
            if (_sharedRowCovers.TryGetValue(cacheKey, out var existingBmp))
            {
                row.SetCover(existingBmp, ownsCover: false, cacheKey: cacheKey);
            }
            else
            {
                toDownload.Add((row, url, cacheKey));
            }
        }

        if (toDownload.Count == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(4, 4);
        var tasks = toDownload.Select(async entry =>
        {
            if (cts.IsCancellationRequested || _disposed)
            {
                return;
            }

            await gate.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                if (cts.IsCancellationRequested || _disposed)
                {
                    return;
                }

                var bmp = await _webApi.LoadSmallBitmapAsync(entry.Url, decodeWidth, cts.Token).ConfigureAwait(false);
                if (bmp is not null)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_disposed || cts.IsCancellationRequested)
                        {
                            bmp.Dispose();
                            return;
                        }

                        if (_sharedRowCovers.TryGetValue(entry.CacheKey, out var cached))
                        {
                            bmp.Dispose();
                            entry.Row.SetCover(cached, ownsCover: false, cacheKey: entry.CacheKey);
                        }
                        else
                        {
                            _sharedRowCovers[entry.CacheKey] = bmp;
                            entry.Row.SetCover(bmp, ownsCover: false, cacheKey: entry.CacheKey);
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnPositionTimerTick()
    {
        if (_disposed || !HasCurrentTrack || !IsPlaying || _isUserScrubbing)
        {
            return;
        }

        var elapsed = (DateTimeOffset.UtcNow - _anchorTimestamp).TotalMilliseconds;
        var currentMs = Math.Clamp(_anchorPositionMs + elapsed, 0, SeekMaxSeconds * 1000.0);
        var currentSec = currentMs / 1000.0;

        if (Math.Abs(currentSec - SeekPositionSeconds) >= 0.5)
        {
            SeekPositionSeconds = currentSec;
        }

        var wholeSec = (int)currentSec;
        if (wholeSec != _lastDisplayedSecond)
        {
            _lastDisplayedSecond = wholeSec;
            PositionDisplay = FormatSeconds(currentSec);
        }

        SyncActiveLyricIndex((long)currentMs);
    }

    private void SyncActiveLyricIndex(long positionMs)
    {
        if (!IsSyncedLyrics || LyricLines.Count == 0)
        {
            return;
        }

        var targetTs = TimeSpan.FromMilliseconds(Math.Max(0, positionMs + LyricsOffsetMs) + 150);
        var activeIdx = -1;
        for (var i = 0; i < LyricLines.Count; i++)
        {
            if (LyricLines[i].Timestamp <= targetTs)
            {
                activeIdx = i;
            }
            else
            {
                break;
            }
        }

        var prevIdx = _activeLyricIndex;
        if (prevIdx == activeIdx)
        {
            return;
        }

        _activeLyricIndex = activeIdx;

        // Only update the prior active line and the range between prevIdx and activeIdx.
        if (prevIdx >= 0 && prevIdx < LyricLines.Count)
        {
            LyricLines[prevIdx].IsActive = false;
        }

        if (activeIdx > prevIdx)
        {
            var start = Math.Max(0, prevIdx);
            for (var i = start; i < activeIdx && i < LyricLines.Count; i++)
            {
                if (!LyricLines[i].IsPast)
                {
                    LyricLines[i].IsPast = true;
                }
            }
        }
        else
        {
            var start = Math.Max(0, activeIdx);
            var end = prevIdx >= 0 ? Math.Min(prevIdx, LyricLines.Count - 1) : LyricLines.Count - 1;
            for (var i = start; i <= end; i++)
            {
                if (LyricLines[i].IsPast)
                {
                    LyricLines[i].IsPast = false;
                }
            }
        }

        if (activeIdx >= 0 && activeIdx < LyricLines.Count)
        {
            LyricLines[activeIdx].IsPast = false;
            LyricLines[activeIdx].IsActive = true;
            ActiveLyricChanged?.Invoke(this, activeIdx);
        }
    }

    private void RefreshConnectionBadges()
    {
        HasWebApiAuth = _webApi.IsConnected;
    }

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

    private void DeselectPlaylists()
    {
        foreach (var p in Playlists)
        {
            p.IsSelected = false;
        }
    }

    private void ClearTrackCollection(ObservableCollection<SpotifyTrackRowViewModel> items)
    {
        CancelAndDispose(ref _rowCoverCts);
        foreach (var item in items)
        {
            item.Dispose();
        }

        items.Clear();
        ClearUnusedSharedCovers(items);
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

    private static void CancelAndDispose(ref CancellationTokenSource? cts)
    {
        var current = Interlocked.Exchange(ref cts, null);
        if (current is null)
        {
            return;
        }

        try { current.Cancel(); } catch (ObjectDisposedException) { }
        finally { current.Dispose(); }
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

    private static IBrush CreateDefaultGlow()
    {
        return new RadialGradientBrush
        {
            Center = new RelativePoint(0.28, 0.25, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.28, 0.25, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.85, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.85, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(46, 29, 185, 84), 0.0),
                new GradientStop(Color.FromArgb(14, 29, 185, 84), 0.45),
                new GradientStop(Color.FromArgb(0, 29, 185, 84), 1.0)
            }
        };
    }
}
