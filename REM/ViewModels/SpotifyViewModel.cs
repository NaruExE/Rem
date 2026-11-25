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

    public void SetCover(Bitmap? bmp, bool ownsCover = true)
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
    private CancellationTokenSource? _toastCts;

    private string? _lastTrackUri;
    private GoLibrespotTrack? _lastTrackMeta;
    private string? _lastAlbumArtUrl;
    private string? _activeCollectionContextUri;
    private long _anchorPositionMs;
    private DateTimeOffset _anchorTimestamp = DateTimeOffset.UtcNow;
    private DateTimeOffset _seekGraceUntil = DateTimeOffset.MinValue;
    private int _lyricsRequestId;
    private bool _lastTrackLyricsHadArtist;
    private bool _lastTrackHasLoadedLyrics;
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
    public ObservableCollection<SpotifyTrackRowViewModel> SearchTracks { get; } = [];
    public ObservableCollection<SpotifyTrackRowViewModel> CollectionTracks { get; } = [];
    public ObservableCollection<SpotifyPlaylistNavViewModel> Playlists { get; } = [];
    public ObservableCollection<SpotifyLyricLineViewModel> LyricLines { get; } = [];
    public ObservableCollection<SpotifyLyricCandidateRowViewModel> LyricsSearchResults { get; } = [];
    public ObservableCollection<SpotifyQueueRowViewModel> QueueEntries { get; } = [];

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
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeSection))]
    [NotifyPropertyChangedFor(nameof(IsSearchSection))]
    [NotifyPropertyChangedFor(nameof(IsCollectionSection))]
    private SpotifyNavSection _activeSection = SpotifyNavSection.HomeFyp;

    public bool IsHomeSection => ActiveSection == SpotifyNavSection.HomeFyp && ActiveDetailPage == SpotifyDetailPage.None;
    public bool IsSearchSection => ActiveSection == SpotifyNavSection.Search && ActiveDetailPage == SpotifyDetailPage.None;
    public bool IsCollectionSection => ActiveSection is SpotifyNavSection.LikedSongs or SpotifyNavSection.Playlist && ActiveDetailPage == SpotifyDetailPage.None;

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

    public bool HasCurrentAlbumArt => CurrentAlbumArt is not null;

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

    [RelayCommand]
    private async Task SelectHomeAsync()
    {
        ResetToStandardSection();
        ActiveSection = SpotifyNavSection.HomeFyp;
        DeselectPlaylists();
        if (FypTracks.Count == 0)
        {
            await LoadFypAsync();
        }
    }

    [RelayCommand]
    private void SelectSearch()
    {
        ResetToStandardSection();
        ActiveSection = SpotifyNavSection.Search;
        DeselectPlaylists();
    }

    [RelayCommand]
    private async Task SelectLikedSongsAsync()
    {
        ResetToStandardSection();
        ActiveSection = SpotifyNavSection.LikedSongs;
        DeselectPlaylists();
        await LoadLikedSongsAsync();
    }

    [RelayCommand]
    private async Task RefreshFypAsync() => await LoadFypAsync();

    [RelayCommand]
    private async Task SearchAsync()
    {
        var query = SearchQuery.Trim();
        if (string.IsNullOrWhiteSpace(query) || _disposed)
        {
            return;
        }

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
        if (!string.IsNullOrWhiteSpace(_activeCollectionContextUri))
        {
            await _soloist.PlayAsync(_activeCollectionContextUri);
            return;
        }

        var first = ActiveDetailPage != SpotifyDetailPage.None
            ? CollectionTracks.FirstOrDefault()?.Item
            : ActiveSection switch
            {
                SpotifyNavSection.HomeFyp => FypTracks.FirstOrDefault()?.Item,
                SpotifyNavSection.Search => SearchTracks.FirstOrDefault()?.Item,
                _ => CollectionTracks.FirstOrDefault()?.Item
            };

        if (first is not null)
        {
            await PlayTrackItemAsync(first);
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
        CancelAndDispose(ref _toastCts);

        ClearTrackCollection(FypTracks);
        ClearTrackCollection(SearchTracks);
        ClearTrackCollection(CollectionTracks);
        ClearAllSharedCovers();
        ClearPlaylistCollection();
        DisposeExtendedAssets();
        RemMixCards.Clear();
        LyricsColorSwatches.Clear();
        _navHistory.Clear();
        LyricLines.Clear();
        LyricsSearchResults.Clear();
        QueueEntries.Clear();

        RemCompanionBitmap = null;
        CurrentAlbumArt?.Dispose();
        CurrentAlbumArt = null;
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

        await LoadPlaylistsAsync();
        await LoadFypAsync();
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

        var ct = ResetWorkToken();
        IsBusy = true;
        try
        {
            if (!_webApi.IsConnected || string.IsNullOrWhiteSpace(tab.Item.Id))
            {
                ClearTrackCollection(CollectionTracks);
                FeedMessage = "Connect Spotify Client ID in Settings → Spotify & APIs to browse playlist tracks.";
                return;
            }

            var res = await _webApi.GetPlaylistItemsAsync(tab.Item.Id, tab.Item.Uri, ct);
            ClearTrackCollection(CollectionTracks);
            for (var i = 0; i < res.Tracks.Count; i++)
            {
                CollectionTracks.Add(new SpotifyTrackRowViewModel(i + 1, res.Tracks[i], ShowTrackCovers, PlayTrackItemAsync, ToggleRowLikeAsync));
            }

            if (res.Tracks.Count == 0)
            {
                FeedMessage = res.AccessRestricted
                    ? "Spotify restricts reading tracks for this private/restricted playlist directly, but you can still click 'Play All' above to play it through Soloist!"
                    : "This playlist appears empty or could not be loaded.";
            }
            else
            {
                var rows = CollectionTracks.ToList();
                _ = SyncRowLikedStatesAsync(rows);
                if (ShowTrackCovers)
                {
                    _ = LoadRowCoversAsync(rows);
                }
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
            }
        }
    }

    private async Task LoadLikedSongsAsync()
    {
        _activeCollectionContextUri = "spotify:collection:tracks";
        CollectionTitle = "Liked Songs";
        CollectionSubtitle = "Your saved Spotify tracks";
        FeedMessage = string.Empty;

        var ct = ResetWorkToken();
        IsBusy = true;
        try
        {
            if (!_webApi.IsConnected)
            {
                ClearTrackCollection(CollectionTracks);
                FeedMessage = "Connect Spotify Web API in Settings → Spotify & APIs to load your Liked Songs.";
                return;
            }

            var tracks = await _webApi.GetLikedSongsAsync(ct);
            ClearTrackCollection(CollectionTracks);
            for (var i = 0; i < tracks.Count; i++)
            {
                CollectionTracks.Add(new SpotifyTrackRowViewModel(i + 1, tracks[i], ShowTrackCovers, PlayTrackItemAsync, ToggleRowLikeAsync)
                {
                    IsLiked = true
                });
            }

            if (tracks.Count == 0)
            {
                FeedMessage = "No liked songs found.";
            }
            else if (ShowTrackCovers)
            {
                _ = LoadRowCoversAsync(CollectionTracks.ToList());
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
            }
        }
    }

    private async Task LoadFypAsync()
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
            return;
        }

        var ct = ResetWorkToken();
        IsBusy = true;
        FeedMessage = string.Empty;
        try
        {
            var recentTask = _webApi.GetRecentlyPlayedAsync(12, ct);
            var topTask = _webApi.GetTopTracksAsync(12, ct);
            await Task.WhenAll(recentTask, topTask);

            var recent = await recentTask;
            var top = await topTask;
            var seeds = recent.Concat(top).GroupBy(t => t.Uri, StringComparer.Ordinal).Select(g => g.First()).ToList();

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

            ClearTrackCollection(FypTracks);
            for (var i = 0; i < finalTracks.Count; i++)
            {
                FypTracks.Add(new SpotifyTrackRowViewModel(i + 1, finalTracks[i], ShowTrackCovers, PlayTrackItemAsync, ToggleRowLikeAsync));
            }

            if (finalTracks.Count > 0)
            {
                var rows = FypTracks.ToList();
                _ = SyncRowLikedStatesAsync(rows);
                if (ShowTrackCovers)
                {
                    _ = LoadRowCoversAsync(rows);
                }
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

        var ok = await _soloist.PlayAsync(item.Uri);
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
        var ok = await _soloist.AddToQueueAsync(item.Uri);
        if (ok)
        {
            ShowToast($"Queued: {item.Name}");
            _actionLog.Log("Spotify", $"Added to Soloist queue: {item.Name}");
            await _soloist.RequestQueueAsync(25);
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
            await _soloist.PlayAsync(entry.Uri);
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
            if (!_lastTrackHasLoadedLyrics && !_lastTrackLyricsHadArtist && track.ArtistNames.Count > 0)
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
        await Task.WhenAll(
            LoadAlbumArtAndGlowAsync(track.AlbumCoverUrl),
            LoadLyricsAsync(track),
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

        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            CurrentAlbumArt?.Dispose();
            CurrentAlbumArt = null;
            AmbientGlowBrush = DefaultAmbientGlow;
            return;
        }

        var cts = new CancellationTokenSource();
        _artCts = cts;
        try
        {
            var bytes = await _webApi.DownloadImageBytesAsync(coverUrl, cts.Token);
            if (bytes is null || bytes.Length == 0 || cts.IsCancellationRequested || _disposed)
            {
                return;
            }

            Bitmap? coverBmp = null;
            IBrush? glowBrush = null;

            await Task.Run(() =>
            {
                using var ms = new MemoryStream(bytes, writable: false);
                coverBmp = Bitmap.DecodeToWidth(ms, 180);
                glowBrush = DominantColorExtractor.CreateAmbientGlowBrush(bytes);
                UpdateAlbumTintFromBytes(bytes);
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
                if (glowBrush is not null)
                {
                    AmbientGlowBrush = glowBrush;
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
        IsSyncedLyrics = false;
        PlainLyrics = string.Empty;
        LyricLines.Clear();
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
            SyncLyricLinesVisualOptions();
            SyncActiveLyricIndex((long)GetLivePlaybackPositionMs());
        }
        else if (!string.IsNullOrWhiteSpace(result.PlainLyrics))
        {
            IsSyncedLyrics = false;
            PlainLyrics = result.PlainLyrics;
            LyricsStatusText = statusLabel;
            _lastTrackHasLoadedLyrics = true;
        }
        else
        {
            LyricsStatusText = "No lyrics available.";
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
        var showGlow = ReadBoolSetting(DatabaseService.KeySpotifyShowAmbientGlow, true);
        var showRem = ReadBoolSetting(DatabaseService.KeySpotifyShowRemArt, true);
        var remFile = _database.GetSetting(DatabaseService.KeySpotifyRemArtFile);
        if (string.IsNullOrWhiteSpace(remFile))
        {
            remFile = "rem-welcome.png";
        }

        ShowPlaylistIcons = showIcons;
        ShowTrackCovers = showCovers;
        ShowAmbientGlow = showGlow;
        ShowRemArt = showRem;
        RemCompanionBitmap = showRem
            ? (_imageAssets.GetBrandImage(remFile, 220) ?? _imageAssets.GetBrandImage("rem-corner.png", 220))
            : null;

        if (!triggerReload)
        {
            return;
        }

        LoadLyricsCustomizationFromSettings();

        foreach (var row in FypTracks.Concat(SearchTracks).Concat(CollectionTracks))
        {
            row.ShowCover = showCovers;
            if (!showCovers)
            {
                row.SetCover(null);
            }
        }

        foreach (var pl in Playlists)
        {
            pl.ShowIcon = showIcons;
            if (!showIcons)
            {
                pl.SetCover(null);
            }
        }

        if (showIcons && Playlists.Any(p => p.CoverBitmap is null && !string.IsNullOrWhiteSpace(p.Item.ImageUrl)))
        {
            _ = LoadPlaylistCoversAsync(Playlists.ToList());
        }

        if (showCovers)
        {
            var activeRows = ActiveSection switch
            {
                SpotifyNavSection.HomeFyp => FypTracks.ToList(),
                SpotifyNavSection.Search => SearchTracks.ToList(),
                _ => CollectionTracks.ToList()
            };
            if (activeRows.Any(r => r.CoverBitmap is null && !string.IsNullOrWhiteSpace(r.Item.AlbumCoverUrl)))
            {
                _ = LoadRowCoversAsync(activeRows);
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

        foreach (var row in FypTracks.Concat(SearchTracks).Concat(CollectionTracks))
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

        foreach (var tab in tabs)
        {
            if (cts.IsCancellationRequested || _disposed || !ShowPlaylistIcons)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(tab.Item.ImageUrl))
            {
                continue;
            }

            var bmp = await _webApi.LoadSmallBitmapAsync(tab.Item.ImageUrl, 48, cts.Token);
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

    private async Task LoadRowCoversAsync(List<SpotifyTrackRowViewModel> rows)
    {
        CancelAndDispose(ref _rowCoverCts);
        if (_disposed || rows.Count == 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _rowCoverCts = cts;

        foreach (var row in rows)
        {
            if (cts.IsCancellationRequested || _disposed)
            {
                break;
            }

            var url = row.Item.AlbumCoverUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (_sharedRowCovers.TryGetValue(url, out var existingBmp))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed && !cts.IsCancellationRequested)
                    {
                        row.SetCover(existingBmp, ownsCover: false);
                    }
                });
                continue;
            }

            var bmp = await _webApi.LoadSmallBitmapAsync(url, 44, cts.Token);
            if (bmp is not null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed || cts.IsCancellationRequested)
                    {
                        bmp.Dispose();
                        return;
                    }

                    if (_sharedRowCovers.TryGetValue(url, out var cached))
                    {
                        bmp.Dispose();
                        row.SetCover(cached, ownsCover: false);
                    }
                    else
                    {
                        _sharedRowCovers[url] = bmp;
                        row.SetCover(bmp, ownsCover: false);
                    }
                });
            }
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

        var changedTo = -1;
        for (var i = 0; i < LyricLines.Count; i++)
        {
            var shouldBeActive = i == activeIdx;
            var shouldBePast = activeIdx >= 0 && i < activeIdx;
            if (LyricLines[i].IsPast != shouldBePast)
            {
                LyricLines[i].IsPast = shouldBePast;
            }

            if (LyricLines[i].IsActive != shouldBeActive)
            {
                LyricLines[i].IsActive = shouldBeActive;
                if (shouldBeActive)
                {
                    changedTo = i;
                }
            }
        }

        if (changedTo >= 0)
        {
            ActiveLyricChanged?.Invoke(this, changedTo);
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
