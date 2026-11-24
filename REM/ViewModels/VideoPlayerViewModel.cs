using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using REM.Models;
using REM.Modules;
using REM.Services;

namespace REM.ViewModels;

public sealed record VideoSpeedOption(float Rate, string Label)
{
    public override string ToString() => Label;
}

public sealed record VideoQualityOption(string Key, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class VideoPlaylistItemRowViewModel : ObservableObject
{
    private readonly Action<VideoPlaylistItemRowViewModel> _onPlay;
    private readonly Action<VideoPlaylistItemRowViewModel> _onMoveUp;
    private readonly Action<VideoPlaylistItemRowViewModel> _onMoveDown;
    private readonly Action<VideoPlaylistItemRowViewModel> _onRemove;

    public VideoPlaylistItemRowViewModel(
        VideoPlaylistItem item,
        int displayIndex,
        bool isPlaying,
        bool canMoveUp,
        bool canMoveDown,
        Action<VideoPlaylistItemRowViewModel> onPlay,
        Action<VideoPlaylistItemRowViewModel> onMoveUp,
        Action<VideoPlaylistItemRowViewModel> onMoveDown,
        Action<VideoPlaylistItemRowViewModel> onRemove)
    {
        Item = item;
        DisplayIndex = displayIndex;
        _isPlaying = isPlaying;
        _canMoveUp = canMoveUp;
        _canMoveDown = canMoveDown;
        _onPlay = onPlay;
        _onMoveUp = onMoveUp;
        _onMoveDown = onMoveDown;
        _onRemove = onRemove;
    }

    public VideoPlaylistItem Item { get; }
    public long Id => Item.Id;
    public int DisplayIndex { get; }
    public string Title => Item.Title;
    public string SourceUrl => Item.SourceUrl;
    public bool HasReferer => !string.IsNullOrWhiteSpace(Item.Referer);
    public bool HasProgress => Item.LastPositionMs >= 2000;
    public double ProgressPercent =>
        Item.DurationMs > 0
            ? Math.Clamp(Item.LastPositionMs * 100.0 / Item.DurationMs, 0, 100)
            : 0;

    public string ProgressText =>
        !HasProgress ? string.Empty :
        Item.DurationMs > 0
            ? $"{VideoPlayerViewModel.FormatDuration(Item.LastPositionMs)} / {VideoPlayerViewModel.FormatDuration(Item.DurationMs)}"
            : $"Resume {VideoPlayerViewModel.FormatDuration(Item.LastPositionMs)}";

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _canMoveUp;

    [ObservableProperty]
    private bool _canMoveDown;

    [RelayCommand]
    private void Play() => _onPlay(this);

    [RelayCommand]
    private void MoveUp() => _onMoveUp(this);

    [RelayCommand]
    private void MoveDown() => _onMoveDown(this);

    [RelayCommand]
    private void Remove() => _onRemove(this);
}

public sealed partial class VideoPlayerViewModel : ObservableObject, IDisposable
{
    private readonly VideoPlayerModule _module;
    private readonly VideoPlayerService _engine;
    private readonly DatabaseService _database;
    private readonly DialogService _dialogService;
    private readonly ActionLogService _actionLog;

    private long _profileId;
    private TaskCompletionSource<bool> _surfaceAttachedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _toastCts;
    private bool _isScrubbing;
    private bool _syncingTracks;
    private bool _syncingVolume;
    private bool _syncingSpeed;
    private bool _syncingQuality;
    private bool _syncingPlaylists;
    private int _trackRefreshPending;
    private bool _disposed;

    public VideoPlayerViewModel(
        VideoPlayerModule module,
        long profileId,
        DatabaseService database,
        DialogService dialogService,
        ActionLogService actionLog)
    {
        _module = module;
        _engine = module.Engine;
        _profileId = profileId;
        _database = database;
        _dialogService = dialogService;
        _actionLog = actionLog;

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

        _engine.PlaybackStateChanged += OnEnginePlaybackStateChanged;
        _engine.PositionChanged += OnEnginePositionChanged;
        _engine.DurationChanged += OnEngineDurationChanged;
        _engine.BufferingChanged += OnEngineBufferingChanged;
        _engine.TracksChanged += OnEngineTracksChanged;
        _engine.PlaybackError += OnEnginePlaybackError;
        _module.StateChanged += OnModuleStateChanged;
        _module.StatusSummaryChanged += OnModuleStatusSummaryChanged;

        SyncFromModule();
        SyncFromSnapshot(_engine.GetSnapshot());
        _ = LoadPlaylistsAsync();
        _ = RefreshTracksAsync();
    }

    public string Name => _module.Name;
    public IBrush AccentBrush { get; }
    public Geometry IconGeometry => _module.IconGeometry;
    public IReadOnlyList<VideoSpeedOption> SpeedOptions { get; }
    public IReadOnlyList<VideoQualityOption> QualityOptions { get; }

    public Action? RequestCloseToDashboard { get; set; }
    public event EventHandler<bool>? FullscreenChanged;
    public Func<Task<string?>>? PickVideoFileRequested { get; set; }
    public Func<Task<string?>>? PickSubtitleFileRequested { get; set; }

    public ObservableCollection<VideoPlaylist> Playlists { get; } = [];
    public ObservableCollection<VideoPlaylistItemRowViewModel> PlaylistItems { get; } = [];
    public ObservableCollection<VideoTrackOption> AudioTracks { get; } = [];
    public ObservableCollection<VideoTrackOption> SubtitleTracks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowModuleHeader))]
    private bool _isHeaderVisible = true;

    [ObservableProperty]
    private string _stateLabel = "Running (UI)";

    [ObservableProperty]
    private string _statusSummary = string.Empty;

    [ObservableProperty]
    private string _mediaInputText = string.Empty;

    [ObservableProperty]
    private string _refererInputText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRefererOrVisible))]
    private bool _isRefererVisible;

    public bool HasRefererOrVisible => IsRefererVisible || !string.IsNullOrWhiteSpace(RefererInputText);

    partial void OnRefererInputTextChanged(string value) =>
        OnPropertyChanged(nameof(HasRefererOrVisible));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlaylistSidebar))]
    private bool _isPlaylistSidebarOpen = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowModuleHeader))]
    [NotifyPropertyChangedFor(nameof(ShowPlaylistSidebar))]
    private bool _isFullscreen;

    public bool ShowModuleHeader => IsHeaderVisible && !IsFullscreen;
    public bool ShowPlaylistSidebar => IsPlaylistSidebarOpen && !IsFullscreen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlaylists))]
    private VideoPlaylist? _selectedPlaylist;

    public bool HasPlaylists => Playlists.Count > 0;
    public bool HasPlaylistItems => PlaylistItems.Count > 0;

    partial void OnSelectedPlaylistChanged(VideoPlaylist? value)
    {
        if (_syncingPlaylists)
        {
            return;
        }

        _ = LoadPlaylistItemsAsync(value?.Id);
    }

    [ObservableProperty]
    private bool _isCreatingPlaylist;

    [ObservableProperty]
    private string _newPlaylistName = string.Empty;

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
    private bool _isPlaying;

    partial void OnIsPlayingChanged(bool value)
    {
        if (!value)
        {
            IsControlsVisible = true;
        }
    }

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _isBuffering;

    [ObservableProperty]
    private bool _hasMedia;

    partial void OnHasMediaChanged(bool value)
    {
        if (!value)
        {
            IsControlsVisible = true;
        }
    }

    [ObservableProperty]
    private bool _isControlsVisible = true;

    [ObservableProperty]
    private string _currentTitle = "No video loaded";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusBanner))]
    private string _statusBannerText = string.Empty;

    public bool HasStatusBanner => !string.IsNullOrWhiteSpace(StatusBannerText);

    [ObservableProperty]
    private double _seekPositionSeconds;

    [ObservableProperty]
    private double _seekMaxSeconds = 1.0;

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
    private VideoTrackOption? _selectedAudioTrack;

    partial void OnSelectedAudioTrackChanged(VideoTrackOption? value)
    {
        if (_syncingTracks || value is null || _disposed)
        {
            return;
        }

        _ = _engine.SetAudioTrackAsync(value.Id);
    }

    public bool HasAudioTracks => AudioTracks.Count > 1;

    [ObservableProperty]
    private VideoTrackOption? _selectedSubtitleTrack;

    partial void OnSelectedSubtitleTrackChanged(VideoTrackOption? value)
    {
        if (_syncingTracks || value is null || _disposed)
        {
            return;
        }

        _ = _engine.SetSubtitleTrackAsync(value.Id);
    }

    public bool HasSubtitleTracks => SubtitleTracks.Count > 0;

    [ObservableProperty]
    private bool _isSubtitleUrlOpen;

    partial void OnIsSubtitleUrlOpenChanged(bool value)
    {
        if (value)
        {
            IsControlsVisible = true;
        }
    }

    [ObservableProperty]
    private string _subtitleUrlInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleDelayDisplay))]
    private long _subtitleDelayMs;

    public string SubtitleDelayDisplay => SubtitleDelayMs switch
    {
        0 => "0 ms",
        > 0 => $"+{SubtitleDelayMs} ms",
        _ => $"{SubtitleDelayMs} ms"
    };

    [ObservableProperty]
    private bool _canGoPrev;

    [ObservableProperty]
    private bool _canGoNext;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoopActive))]
    [NotifyPropertyChangedFor(nameof(IsLoopOne))]
    [NotifyPropertyChangedFor(nameof(LoopBadgeText))]
    [NotifyPropertyChangedFor(nameof(LoopTooltip))]
    private VideoLoopMode _loopMode = VideoLoopMode.Off;

    public bool IsLoopActive => LoopMode != VideoLoopMode.Off;
    public bool IsLoopOne => LoopMode == VideoLoopMode.One;
    public string LoopBadgeText => LoopMode switch
    {
        VideoLoopMode.One => "1",
        VideoLoopMode.All => "ALL",
        _ => string.Empty
    };
    public string LoopTooltip => LoopMode switch
    {
        VideoLoopMode.One => "Loop: Current Video (L)",
        VideoLoopMode.All => "Loop: Playlist (L)",
        _ => "Loop: Off (L)"
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShuffleTooltip))]
    private bool _isShuffled;

    public string ShuffleTooltip => IsShuffled ? "Shuffle: On" : "Shuffle: Off";

    [ObservableProperty]
    private string _abLoopButtonText = "A-B";

    [ObservableProperty]
    private bool _isAbLoopActive;

    [ObservableProperty]
    private string _abLoopTooltip = "A-B Loop (click to set A, then B, then clear)";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomAspectRatio))]
    private string _aspectRatioLabel = "Default";

    public bool IsCustomAspectRatio => !string.Equals(AspectRatioLabel, "Default", StringComparison.Ordinal);

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
        if (!_disposed && HasMedia && IsPlaying && !IsSubtitleUrlOpen && !IsBusy && !_isScrubbing)
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
        _ = LoadPlaylistsAsync();
    }

    public void BeginScrub()
    {
        _isScrubbing = true;
    }

    public void UpdateScrubPreview(double seconds)
    {
        if (!_isScrubbing)
        {
            return;
        }

        PositionDisplay = FormatDuration((long)(Math.Max(0, seconds) * 1000.0));
    }

    public async Task CommitScrubAsync(double seconds)
    {
        _isScrubbing = false;
        var targetMs = (long)Math.Round(Math.Max(0, seconds) * 1000.0);
        await _engine.SeekAsync(targetMs);
    }

    public async Task SeekRelativeAsync(long deltaMs)
    {
        if (_disposed || !HasMedia)
        {
            return;
        }

        var nextMs = _engine.PositionMs + deltaMs;
        await _engine.SeekAsync(nextMs);
    }

    public void AdjustVolume(int delta)
    {
        if (_disposed)
        {
            return;
        }

        Volume = Math.Clamp(Volume + delta, 0, 100);
    }

    public void StepSpeed(int direction)
    {
        if (_disposed || SpeedOptions.Count == 0)
        {
            return;
        }

        var currentIdx = -1;
        for (var i = 0; i < SpeedOptions.Count; i++)
        {
            if (ReferenceEquals(SpeedOptions[i], SelectedSpeedOption))
            {
                currentIdx = i;
                break;
            }
        }

        if (currentIdx < 0)
        {
            currentIdx = 1;
        }

        var nextIdx = Math.Clamp(currentIdx + direction, 0, SpeedOptions.Count - 1);
        SelectedSpeedOption = SpeedOptions[nextIdx];
        ShowTransientBanner($"Speed: {SelectedSpeedOption.Label}");
    }

    [RelayCommand]
    private void CycleLoopMode()
    {
        if (_disposed)
        {
            return;
        }

        var next = _engine.CycleLoopMode();
        ShowTransientBanner(next switch
        {
            VideoLoopMode.One => "Loop: Current Video",
            VideoLoopMode.All => "Loop: Playlist",
            _ => "Loop: Off"
        });
    }

    [RelayCommand]
    private void ToggleShuffle()
    {
        if (_disposed)
        {
            return;
        }

        var enabled = _engine.ToggleShuffle();
        ShowTransientBanner(enabled ? "Shuffle: On" : "Shuffle: Off");
    }

    [RelayCommand]
    private void CycleAbLoop()
    {
        if (_disposed)
        {
            return;
        }

        var msg = _engine.CycleAbLoop();
        ShowTransientBanner(msg);
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
            ShowTransientBanner("Play a video before taking a snapshot.");
            return;
        }

        var path = await _engine.TakeSnapshotAsync();
        ShowTransientBanner(path is not null
            ? $"Snapshot saved: {Path.GetFileName(path)}"
            : "Could not save snapshot (not available in audio-only headless mode).");
    }

    [RelayCommand]
    private Task MinimizeToHeadlessAsync() => _module.PromptAndRunHeadlessAsync();

    [RelayCommand]
    private void PauseModule() => _module.Pause();

    [RelayCommand]
    private void Kill() => _module.Kill();

    [RelayCommand]
    private void BackToDashboard() => RequestCloseToDashboard?.Invoke();

    [RelayCommand]
    private void ToggleHeader() => IsHeaderVisible = !IsHeaderVisible;

    [RelayCommand]
    private void ToggleReferer() => IsRefererVisible = !IsRefererVisible;

    [RelayCommand]
    private void TogglePlaylistSidebar() => IsPlaylistSidebarOpen = !IsPlaylistSidebarOpen;

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
    private async Task OpenLocalFileAsync()
    {
        if (PickVideoFileRequested is null || _disposed)
        {
            return;
        }

        var path = await PickVideoFileRequested();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        MediaInputText = path;
        RefererInputText = string.Empty;
        await PlayInputAsync();
    }

    [RelayCommand]
    private async Task PlayInputAsync()
    {
        if (_disposed || IsBusy)
        {
            return;
        }

        var input = MediaInputText.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            ShowTransientBanner("Paste a video URL or choose a local file first.");
            return;
        }

        var referer = string.IsNullOrWhiteSpace(RefererInputText) ? null : RefererInputText.Trim();
        IsBusy = true;
        StatusBannerText = "Resolving stream...";

        try
        {
            await WaitForSurfaceAttachedAsync();
            var resolved = await _engine.PlaySourceAsync(input, referer);
            ShowTransientBanner(resolved.UsedYtDlp
                ? $"Resolved: {resolved.Title}"
                : $"Playing: {resolved.Title}");
        }
        catch (Exception ex)
        {
            ShowTransientBanner($"Play failed: {ex.Message}");
            _actionLog.Log("Warning", $"Video play error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AddInputToPlaylistAsync()
    {
        if (_disposed || IsBusy)
        {
            return;
        }

        var input = MediaInputText.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            ShowTransientBanner("Enter a video URL or choose a file to add.");
            return;
        }

        var referer = string.IsNullOrWhiteSpace(RefererInputText) ? null : RefererInputText.Trim();
        IsBusy = true;
        StatusBannerText = "Adding to playlist...";

        try
        {
            var targetPlaylist = SelectedPlaylist;
            if (targetPlaylist is null)
            {
                targetPlaylist = await _database.CreateVideoPlaylistAsync(_profileId, "Watchlist");
                _syncingPlaylists = true;
                try
                {
                    Playlists.Add(targetPlaylist);
                    SelectedPlaylist = targetPlaylist;
                    OnPropertyChanged(nameof(HasPlaylists));
                }
                finally
                {
                    _syncingPlaylists = false;
                }
            }

            string title;
            try
            {
                var resolved = await _module.Resolver.ResolveAsync(input, referer);
                title = resolved.Title;
            }
            catch
            {
                title = File.Exists(input)
                    ? Path.GetFileNameWithoutExtension(input)
                    : input;
            }

            await _database.AddVideoPlaylistItemAsync(
                targetPlaylist.Id,
                title,
                input,
                referer);

            await LoadPlaylistItemsAsync(targetPlaylist.Id);
            ShowTransientBanner($"Added '{title}' to '{targetPlaylist.Name}'");
        }
        catch (Exception ex)
        {
            ShowTransientBanner($"Add failed: {ex.Message}");
            _actionLog.Log("Warning", $"Add video playlist item failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ToggleCreatePlaylist()
    {
        IsCreatingPlaylist = !IsCreatingPlaylist;
        if (!IsCreatingPlaylist)
        {
            NewPlaylistName = string.Empty;
        }
    }

    [RelayCommand]
    private async Task CreatePlaylistAsync()
    {
        var name = NewPlaylistName.Trim();
        if (string.IsNullOrWhiteSpace(name) || _profileId <= 0)
        {
            return;
        }

        try
        {
            var created = await _database.CreateVideoPlaylistAsync(_profileId, name);
            NewPlaylistName = string.Empty;
            IsCreatingPlaylist = false;

            _syncingPlaylists = true;
            try
            {
                Playlists.Add(created);
                SelectedPlaylist = created;
                OnPropertyChanged(nameof(HasPlaylists));
            }
            finally
            {
                _syncingPlaylists = false;
            }

            await LoadPlaylistItemsAsync(created.Id);
            ShowTransientBanner($"Created playlist '{created.Name}'");
        }
        catch (Exception ex)
        {
            ShowTransientBanner($"Could not create playlist: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedPlaylistAsync()
    {
        var pl = SelectedPlaylist;
        if (pl is null)
        {
            return;
        }

        var confirmed = await _dialogService.ShowConfirmAsync(
            "Delete Playlist?",
            $"Delete '{pl.Name}' and all of its saved video resume points?",
            "Delete",
            isDanger: true);

        if (!confirmed)
        {
            return;
        }

        await _database.DeleteVideoPlaylistAsync(pl.Id);
        await LoadPlaylistsAsync();
        ShowTransientBanner($"Deleted playlist '{pl.Name}'");
    }

    [RelayCommand]
    private async Task TogglePlayPauseAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (!HasMedia && PlaylistItems.Count > 0 && SelectedPlaylist is not null)
        {
            await WaitForSurfaceAttachedAsync();
            await _module.PlayPlaylistAsync(SelectedPlaylist.Id, PlaylistItems[0].Id);
            return;
        }

        await _engine.TogglePauseAsync();
    }

    [RelayCommand]
    private async Task PrevItemAsync()
    {
        if (_disposed)
        {
            return;
        }

        await WaitForSurfaceAttachedAsync();
        await _module.PlayPreviousInPlaylistAsync();
    }

    [RelayCommand]
    private async Task NextItemAsync()
    {
        if (_disposed)
        {
            return;
        }

        await WaitForSurfaceAttachedAsync();
        await _module.PlayNextInPlaylistAsync();
    }

    [RelayCommand]
    private Task ToggleMuteAsync() => _engine.ToggleMuteAsync();

    [RelayCommand]
    private async Task LoadSubtitleFileAsync()
    {
        if (PickSubtitleFileRequested is null || _disposed)
        {
            return;
        }

        if (!HasMedia)
        {
            ShowTransientBanner("Play a video before loading external subtitles.");
            return;
        }

        var path = await PickSubtitleFileRequested();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var ok = await _engine.LoadExternalSubtitleAsync(path);
            ShowTransientBanner(ok
                ? $"Loaded subtitles: {Path.GetFileName(path)}"
                : "Could not attach subtitle file.");
        }
        catch (Exception ex)
        {
            ShowTransientBanner($"Subtitle error: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ToggleSubtitleUrl() => IsSubtitleUrlOpen = !IsSubtitleUrlOpen;

    [RelayCommand]
    private async Task LoadSubtitleUrlAsync()
    {
        var url = SubtitleUrlInput.Trim();
        if (string.IsNullOrWhiteSpace(url) || _disposed)
        {
            return;
        }

        if (!HasMedia)
        {
            ShowTransientBanner("Play a video before loading subtitle URL.");
            return;
        }

        try
        {
            var ok = await _engine.LoadExternalSubtitleAsync(url);
            if (ok)
            {
                IsSubtitleUrlOpen = false;
                ShowTransientBanner("Loaded subtitle URL.");
            }
            else
            {
                ShowTransientBanner("Could not attach subtitle URL.");
            }
        }
        catch (Exception ex)
        {
            ShowTransientBanner($"Subtitle error: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task NudgeSubtitleDelayAsync(string deltaText)
    {
        if (_disposed || !long.TryParse(deltaText, out var deltaMs))
        {
            return;
        }

        await _engine.NudgeSubtitleDelayMsAsync(deltaMs);
    }

    [RelayCommand]
    private async Task ResetSubtitleDelayAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _engine.SetSubtitleDelayMsAsync(0);
    }

    public static string FormatDuration(long ms)
    {
        if (ms <= 0)
        {
            return "0:00";
        }

        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1.0
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelToastTimer();

        _engine.PlaybackStateChanged -= OnEnginePlaybackStateChanged;
        _engine.PositionChanged -= OnEnginePositionChanged;
        _engine.DurationChanged -= OnEngineDurationChanged;
        _engine.BufferingChanged -= OnEngineBufferingChanged;
        _engine.TracksChanged -= OnEngineTracksChanged;
        _engine.PlaybackError -= OnEnginePlaybackError;
        _module.StateChanged -= OnModuleStateChanged;
        _module.StatusSummaryChanged -= OnModuleStatusSummaryChanged;
    }

    private async Task WaitForSurfaceAttachedAsync()
    {
        var timeout = Task.Delay(1500);
        await Task.WhenAny(_surfaceAttachedTcs.Task, timeout);
    }

    private async Task LoadPlaylistsAsync()
    {
        if (_profileId <= 0 || _disposed)
        {
            return;
        }

        var loaded = await _database.GetVideoPlaylistsAsync(_profileId);
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (_disposed)
            {
                return;
            }

            var prevId = SelectedPlaylist?.Id ?? _module.ActivePlaylistId;
            _syncingPlaylists = true;
            try
            {
                Playlists.Clear();
                foreach (var pl in loaded)
                {
                    Playlists.Add(pl);
                }

                SelectedPlaylist = prevId.HasValue
                    ? Playlists.FirstOrDefault(p => p.Id == prevId.Value) ?? Playlists.FirstOrDefault()
                    : Playlists.FirstOrDefault();
                OnPropertyChanged(nameof(HasPlaylists));
            }
            finally
            {
                _syncingPlaylists = false;
            }

            await LoadPlaylistItemsAsync(SelectedPlaylist?.Id);
        });
    }

    private async Task LoadPlaylistItemsAsync(long? playlistId)
    {
        if (_disposed)
        {
            return;
        }

        if (!playlistId.HasValue)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PlaylistItems.Clear();
                OnPropertyChanged(nameof(HasPlaylistItems));
                UpdatePrevNextFlags();
            });
            return;
        }

        var items = await _database.GetVideoPlaylistItemsAsync(playlistId.Value);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed || SelectedPlaylist?.Id != playlistId.Value)
            {
                return;
            }

            var currentItemId = _engine.CurrentPlaylistItemId;
            PlaylistItems.Clear();
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                PlaylistItems.Add(new VideoPlaylistItemRowViewModel(
                    item: item,
                    displayIndex: i + 1,
                    isPlaying: currentItemId.HasValue && item.Id == currentItemId.Value,
                    canMoveUp: i > 0,
                    canMoveDown: i + 1 < items.Count,
                    onPlay: row => _ = PlayPlaylistRowAsync(row),
                    onMoveUp: row => _ = MovePlaylistRowAsync(row, -1),
                    onMoveDown: row => _ = MovePlaylistRowAsync(row, 1),
                    onRemove: row => _ = RemovePlaylistRowAsync(row)));
            }

            OnPropertyChanged(nameof(HasPlaylistItems));
            UpdatePrevNextFlags();
        });
    }

    private async Task PlayPlaylistRowAsync(VideoPlaylistItemRowViewModel row)
    {
        var pl = SelectedPlaylist;
        if (_disposed || pl is null)
        {
            return;
        }

        IsBusy = true;
        StatusBannerText = $"Loading {row.Title}...";
        try
        {
            await WaitForSurfaceAttachedAsync();
            await _module.PlayPlaylistAsync(pl.Id, row.Id);
            ShowTransientBanner($"Playing: {row.Title}");
        }
        catch (Exception ex)
        {
            ShowTransientBanner($"Play failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task MovePlaylistRowAsync(VideoPlaylistItemRowViewModel row, int offset)
    {
        var pl = SelectedPlaylist;
        if (_disposed || pl is null)
        {
            return;
        }

        var ids = PlaylistItems.Select(x => x.Id).ToList();
        var idx = ids.IndexOf(row.Id);
        var nextIdx = idx + offset;
        if (idx < 0 || nextIdx < 0 || nextIdx >= ids.Count)
        {
            return;
        }

        (ids[idx], ids[nextIdx]) = (ids[nextIdx], ids[idx]);
        await _database.ReorderVideoPlaylistItemsAsync(pl.Id, ids);
        await LoadPlaylistItemsAsync(pl.Id);
    }

    private async Task RemovePlaylistRowAsync(VideoPlaylistItemRowViewModel row)
    {
        var pl = SelectedPlaylist;
        if (_disposed || pl is null)
        {
            return;
        }

        await _database.RemoveVideoPlaylistItemAsync(row.Id);
        await LoadPlaylistItemsAsync(pl.Id);
    }

    private async Task RefreshTracksAsync()
    {
        if (_disposed)
        {
            return;
        }

        var audio = await _engine.GetAudioTracksAsync();
        var subs = await _engine.GetSubtitleTracksAsync();
        var snap = _engine.GetSnapshot();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            _syncingTracks = true;
            try
            {
                AudioTracks.Clear();
                foreach (var a in audio)
                {
                    AudioTracks.Add(a);
                }
                SelectedAudioTrack = AudioTracks.FirstOrDefault(t => t.Id == snap.ActiveAudioTrackId)
                    ?? AudioTracks.FirstOrDefault();
                OnPropertyChanged(nameof(HasAudioTracks));

                SubtitleTracks.Clear();
                foreach (var s in subs)
                {
                    SubtitleTracks.Add(s);
                }
                SelectedSubtitleTrack = SubtitleTracks.FirstOrDefault(t => t.Id == snap.ActiveSubtitleTrackId)
                    ?? SubtitleTracks.FirstOrDefault();
                OnPropertyChanged(nameof(HasSubtitleTracks));
            }
            finally
            {
                _syncingTracks = false;
            }
        });
    }

    private void SyncFromSnapshot(VideoPlaybackSnapshot snap)
    {
        IsPlaying = snap.IsPlaying;
        IsPaused = snap.IsPaused;
        IsBuffering = snap.IsBuffering;
        HasMedia = !string.IsNullOrWhiteSpace(snap.CurrentSourceUrl);
        CurrentTitle = string.IsNullOrWhiteSpace(snap.CurrentTitle) ? "No video loaded" : snap.CurrentTitle;
        SubtitleDelayMs = snap.SubtitleDelayMs;

        var durSec = Math.Max(1.0, snap.DurationMs / 1000.0);
        SeekMaxSeconds = durSec;
        DurationDisplay = FormatDuration(snap.DurationMs);

        if (!_isScrubbing)
        {
            SeekPositionSeconds = Math.Clamp(snap.PositionMs / 1000.0, 0, durSec);
            PositionDisplay = FormatDuration(snap.PositionMs);
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

        foreach (var row in PlaylistItems)
        {
            row.IsPlaying = snap.CurrentPlaylistItemId.HasValue && row.Id == snap.CurrentPlaylistItemId.Value;
        }

        LoopMode = snap.LoopMode;
        IsShuffled = snap.IsShuffled;
        AspectRatioLabel = snap.AspectRatioLabel;

        if (snap.AbLoopStartMs is { } abStart && snap.AbLoopEndMs is { } abEnd)
        {
            AbLoopButtonText = "A↔B";
            IsAbLoopActive = true;
            AbLoopTooltip = $"A-B Loop: {FormatDuration(abStart)} → {FormatDuration(abEnd)} (click to clear)";
        }
        else if (snap.AbLoopStartMs is { } onlyA)
        {
            AbLoopButtonText = "A→?";
            IsAbLoopActive = true;
            AbLoopTooltip = $"A-B Loop: A={FormatDuration(onlyA)} (click to set B)";
        }
        else
        {
            AbLoopButtonText = "A-B";
            IsAbLoopActive = false;
            AbLoopTooltip = "A-B Loop (click to set A, then B, then clear)";
        }

        UpdatePrevNextFlags();
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
                : $"Quality set to {option.Label} (applies to streams)");
        }
        catch (Exception ex)
        {
            ShowTransientBanner($"Quality switch failed: {ex.Message}");
            _actionLog.Log("Warning", $"Video quality switch failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdatePrevNextFlags()
    {
        var currentItemId = _engine.CurrentPlaylistItemId;
        if (!currentItemId.HasValue || PlaylistItems.Count == 0)
        {
            CanGoPrev = false;
            CanGoNext = false;
            return;
        }

        var idx = -1;
        for (var i = 0; i < PlaylistItems.Count; i++)
        {
            if (PlaylistItems[i].Id == currentItemId.Value)
            {
                idx = i;
                break;
            }
        }

        var cyclic = _engine.LoopMode == VideoLoopMode.All || _engine.IsShuffled;
        CanGoPrev = idx > 0 || (cyclic && PlaylistItems.Count > 1);
        CanGoNext = (idx >= 0 && idx + 1 < PlaylistItems.Count) || (cyclic && PlaylistItems.Count > 1);
    }

    private void SyncFromModule()
    {
        StateLabel = _module.State switch
        {
            ModuleState.RunningUI => "Running (UI)",
            ModuleState.RunningHeadless => "Headless Audio",
            ModuleState.Paused => "Paused",
            _ => "Stopped"
        };
        StatusSummary = _module.StatusSummary;
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

    private void OnEnginePlaybackStateChanged(object? sender, VideoPlaybackSnapshot snap)
    {
        if (_disposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                SyncFromSnapshot(snap);
            }
        });
    }

    private void OnEnginePositionChanged(object? sender, long posMs)
    {
        if (_disposed || _isScrubbing)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _isScrubbing)
            {
                return;
            }

            SeekPositionSeconds = Math.Clamp(posMs / 1000.0, 0, SeekMaxSeconds);
            PositionDisplay = FormatDuration(posMs);
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
            if (_disposed)
            {
                return;
            }

            SeekMaxSeconds = Math.Max(1.0, durMs / 1000.0);
            DurationDisplay = FormatDuration(durMs);
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
            if (_disposed)
            {
                return;
            }

            IsBuffering = percent < 99.5f;
            if (IsBuffering && !IsBusy)
            {
                StatusBannerText = $"Buffering {percent:0}%";
            }
            else if (!IsBusy && StatusBannerText.StartsWith("Buffering ", StringComparison.Ordinal))
            {
                StatusBannerText = string.Empty;
            }
        });
    }

    private void OnEngineTracksChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _trackRefreshPending, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(120, CancellationToken.None).ConfigureAwait(false);
                if (!_disposed)
                {
                    await RefreshTracksAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _trackRefreshPending, 0);
            }
        });
    }

    private void OnEnginePlaybackError(object? sender, string error)
    {
        if (_disposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                ShowTransientBanner(error);
            }
        });
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
