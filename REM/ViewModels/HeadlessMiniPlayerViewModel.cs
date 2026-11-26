using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using REM.Models;
using REM.Modules;
using REM.Services;

namespace REM.ViewModels;

public sealed partial class HeadlessMiniPlayerViewModel : ObservableObject, IDisposable
{
    private static readonly IBrush SpotifyAccentBrush = new SolidColorBrush(Color.Parse("#1DB954"));
    private static readonly IBrush AudioAccentBrush = new SolidColorBrush(Color.Parse("#9146FF"));
    private static readonly IBrush VideoAccentBrush = new SolidColorBrush(Color.Parse("#FF8800"));

    private readonly ModuleManager _moduleManager;
    private readonly SpotifySoloistService _soloist;
    private readonly SpotifyWebApiService _spotifyWebApi;
    private readonly AudioPlaybackEngine _audioEngine;
    private readonly VideoPlayerService _videoPlayer;
    private readonly Action<string> _onOpenModuleUi;

    private static readonly string[] TrackedPlayerIds = ["spotify", "audio-streamer", "video-player"];

    private readonly List<string> _headlessPlayerIds = new(3);
    private int _selectedIndex;
    private string? _activeOpenModuleId;
    private string? _lastCheckedLikedTrackUri;
    private string _lastStateFingerprint = string.Empty;
    private bool _disposed;

    public HeadlessMiniPlayerViewModel(
        ModuleManager moduleManager,
        SpotifySoloistService soloist,
        SpotifyWebApiService spotifyWebApi,
        AudioPlaybackEngine audioEngine,
        VideoPlayerService videoPlayer,
        Action<string> onOpenModuleUi)
    {
        _moduleManager = moduleManager;
        _soloist = soloist;
        _spotifyWebApi = spotifyWebApi;
        _audioEngine = audioEngine;
        _videoPlayer = videoPlayer;
        _onOpenModuleUi = onOpenModuleUi;

        _moduleManager.ModuleStateChanged += OnAnyModuleStateChanged;
        _soloist.StatusChanged += OnSoloistStatusChanged;
        _audioEngine.PlaybackStateChanged += OnEnginePlaybackChanged;
        _videoPlayer.PlaybackStateChanged += OnVideoPlaybackChanged;

        Refresh();
    }

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private bool _hasMultiplePlayers;

    [ObservableProperty]
    private string _playerCounterText = string.Empty;

    [ObservableProperty]
    private string _appLabel = string.Empty;

    [ObservableProperty]
    private Geometry _appIconGeometry = IconCatalog.Spotify;

    [ObservableProperty]
    private IBrush _appAccentBrush = SpotifyAccentBrush;

    [ObservableProperty]
    private string _trackTitle = string.Empty;

    [ObservableProperty]
    private string _trackSubtitle = string.Empty;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isLoopActive;

    [ObservableProperty]
    private string _loopBadgeText = string.Empty;

    [ObservableProperty]
    private string _loopTooltip = "Toggle Loop / Repeat";

    [ObservableProperty]
    private bool _showLikeButton;

    [ObservableProperty]
    private bool _isTrackLiked;

    public void SetActiveOpenModuleId(string? moduleId)
    {
        if (string.Equals(_activeOpenModuleId, moduleId, StringComparison.Ordinal))
        {
            return;
        }

        _activeOpenModuleId = moduleId;
        Refresh();
    }

    [RelayCommand]
    private void PreviousPlayer()
    {
        if (_headlessPlayerIds.Count <= 1) return;
        _selectedIndex = (_selectedIndex - 1 + _headlessPlayerIds.Count) % _headlessPlayerIds.Count;
        Refresh();
    }

    [RelayCommand]
    private void NextPlayer()
    {
        if (_headlessPlayerIds.Count <= 1) return;
        _selectedIndex = (_selectedIndex + 1) % _headlessPlayerIds.Count;
        Refresh();
    }

    [RelayCommand]
    private void OpenActiveApp()
    {
        var id = GetCurrentModuleId();
        if (!string.IsNullOrWhiteSpace(id))
        {
            _onOpenModuleUi(id);
        }
    }

    [RelayCommand]
    private async Task TogglePlayPauseAsync()
    {
        switch (GetCurrentModuleId())
        {
            case "spotify":
                await _soloist.PlayPauseAsync();
                break;
            case "audio-streamer":
                await _audioEngine.TogglePauseAsync();
                break;
            case "video-player":
                await _videoPlayer.TogglePauseAsync();
                break;
        }
    }

    [RelayCommand]
    private async Task PreviousTrackAsync()
    {
        switch (GetCurrentModuleId())
        {
            case "spotify":
                await _soloist.PrevAsync();
                break;
            case "audio-streamer":
                _audioEngine.SkipRelativeSeconds(-15);
                break;
            case "video-player":
                await _moduleManager.VideoPlayerModule.PlayPreviousInPlaylistAsync();
                break;
        }
    }

    [RelayCommand]
    private async Task NextTrackAsync()
    {
        switch (GetCurrentModuleId())
        {
            case "spotify":
                await _soloist.NextAsync();
                break;
            case "audio-streamer":
                _audioEngine.SkipRelativeSeconds(15);
                break;
            case "video-player":
                await _moduleManager.VideoPlayerModule.PlayNextInPlaylistAsync();
                break;
        }
    }

    [RelayCommand]
    private async Task CycleLoopAsync()
    {
        switch (GetCurrentModuleId())
        {
            case "spotify":
                var st = _soloist.CurrentStatus;
                if (st is null) return;
                if (!st.RepeatContext && !st.RepeatTrack)
                {
                    await _soloist.SetRepeatTrackAsync(false);
                    await _soloist.SetRepeatContextAsync(true);
                }
                else if (st.RepeatContext && !st.RepeatTrack)
                {
                    await _soloist.SetRepeatContextAsync(false);
                    await _soloist.SetRepeatTrackAsync(true);
                }
                else
                {
                    await _soloist.SetRepeatTrackAsync(false);
                    await _soloist.SetRepeatContextAsync(false);
                }
                break;

            case "audio-streamer":
                _audioEngine.ToggleLoop();
                break;

            case "video-player":
                _videoPlayer.CycleLoopMode();
                Refresh();
                break;
        }
    }

    [RelayCommand]
    private async Task ToggleLikeAsync()
    {
        if (GetCurrentModuleId() != "spotify" || !_spotifyWebApi.IsConnected)
        {
            return;
        }

        var trackUri = _soloist.CurrentStatus?.Track?.Uri;
        if (string.IsNullOrWhiteSpace(trackUri))
        {
            return;
        }

        var desired = !IsTrackLiked;
        IsTrackLiked = desired;
        var ok = await _spotifyWebApi.SetTrackLikedAsync(trackUri, desired, CancellationToken.None);
        if (!ok && !_disposed)
        {
            IsTrackLiked = !desired;
        }
    }

    private string? GetCurrentModuleId()
    {
        if (_headlessPlayerIds.Count == 0) return null;
        if (_selectedIndex < 0 || _selectedIndex >= _headlessPlayerIds.Count) _selectedIndex = 0;
        return _headlessPlayerIds[_selectedIndex];
    }

    private void OnAnyModuleStateChanged(object? sender, (string ModuleId, ModuleState State) args) => PostRefresh();
    private void OnSoloistStatusChanged(object? sender, GoLibrespotStatus? status) => PostRefresh();
    private void OnEnginePlaybackChanged(object? sender, EventArgs e) => PostRefresh();
    private void OnVideoPlaybackChanged(object? sender, VideoPlaybackSnapshot snapshot) => PostRefresh();

    private void PostRefresh()
    {
        if (_disposed)
        {
            return;
        }

        var fp = BuildStateFingerprint();
        if (string.Equals(fp, _lastStateFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        _lastStateFingerprint = fp;
        Dispatcher.UIThread.Post(Refresh);
    }

    private string BuildStateFingerprint()
    {
        var anyHeadless = false;
        var sp = _moduleManager.GetModule("spotify")?.State;
        var au = _moduleManager.GetModule("audio-streamer")?.State;
        var vp = _moduleManager.GetModule("video-player")?.State;
        if (sp == ModuleState.RunningHeadless || au == ModuleState.RunningHeadless || vp == ModuleState.RunningHeadless)
        {
            anyHeadless = true;
        }

        if (!anyHeadless)
        {
            return $"none|{_activeOpenModuleId}";
        }

        var st = _soloist.CurrentStatus;
        var auTr = _audioEngine.CurrentTrack;
        return string.Concat(
            _activeOpenModuleId ?? string.Empty, "|",
            ((int)(sp ?? 0)).ToString(), "|", ((int)(au ?? 0)).ToString(), "|", ((int)(vp ?? 0)).ToString(), "|",
            st?.Track?.Uri ?? string.Empty, "|", st?.Paused == true ? "P" : "R", "|", st?.Stopped == true ? "S" : "A", "|",
            st?.RepeatContext == true ? "RC" : string.Empty, st?.RepeatTrack == true ? "RT" : string.Empty, "|",
            auTr?.Title ?? string.Empty, "|", _audioEngine.IsPlaying ? "1" : "0", "|", _audioEngine.IsLooping ? "L" : string.Empty, "|",
            _videoPlayer.CurrentTitle ?? string.Empty, "|", _videoPlayer.IsPlaying ? "1" : "0", "|", ((int)_videoPlayer.LoopMode).ToString());
    }

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var previousId = GetCurrentModuleId();
        _headlessPlayerIds.Clear();

        foreach (var id in TrackedPlayerIds)
        {
            if (string.Equals(_activeOpenModuleId, id, StringComparison.Ordinal))
            {
                continue;
            }

            var mod = _moduleManager.GetModule(id);
            if (mod?.State == ModuleState.RunningHeadless)
            {
                _headlessPlayerIds.Add(id);
            }
        }

        if (_headlessPlayerIds.Count == 0)
        {
            IsVisible = false;
            HasMultiplePlayers = false;
            ShowLikeButton = false;
            return;
        }

        if (!string.IsNullOrWhiteSpace(previousId))
        {
            var foundIdx = _headlessPlayerIds.IndexOf(previousId);
            if (foundIdx >= 0)
            {
                _selectedIndex = foundIdx;
            }
        }

        if (_selectedIndex >= _headlessPlayerIds.Count)
        {
            _selectedIndex = 0;
        }

        var currentId = _headlessPlayerIds[_selectedIndex];
        HasMultiplePlayers = _headlessPlayerIds.Count > 1;
        PlayerCounterText = HasMultiplePlayers ? $"{_selectedIndex + 1}/{_headlessPlayerIds.Count}" : string.Empty;

        switch (currentId)
        {
            case "spotify":
            {
                var st = _soloist.CurrentStatus;
                var tr = st?.Track;
                AppLabel = "Spotify · Headless";
                AppIconGeometry = IconCatalog.Spotify;
                AppAccentBrush = SpotifyAccentBrush;
                TrackTitle = tr?.Name ?? "Spotify Headless";
                TrackSubtitle = tr is not null ? tr.ArtistsDisplay : "Ready on Spotify Connect";
                IsPlaying = st is { Stopped: false, Paused: false, Track: not null };
                IsLoopActive = st is { RepeatContext: true } or { RepeatTrack: true };
                LoopBadgeText = st is { RepeatTrack: true } ? "1" : string.Empty;
                LoopTooltip = st is { RepeatTrack: true }
                    ? "Repeat: Track"
                    : st is { RepeatContext: true } ? "Repeat: Playlist" : "Repeat: Off";
                ShowLikeButton = tr is not null && _spotifyWebApi.IsConnected;

                var uri = tr?.Uri;
                if (!string.Equals(uri, _lastCheckedLikedTrackUri, StringComparison.Ordinal))
                {
                    _lastCheckedLikedTrackUri = uri;
                    IsTrackLiked = false;
                    if (!string.IsNullOrWhiteSpace(uri) && _spotifyWebApi.IsConnected)
                    {
                        _ = RefreshSpotifyLikedStateAsync(uri);
                    }
                }
                break;
            }
            case "audio-streamer":
            {
                var tr = _audioEngine.CurrentTrack;
                AppLabel = "Audio Streamer · Headless";
                AppIconGeometry = IconCatalog.AudioStreamer;
                AppAccentBrush = AudioAccentBrush;
                TrackTitle = tr?.Title ?? "Audio Streamer Headless";
                TrackSubtitle = tr is not null ? tr.Channel : "Idle";
                IsPlaying = _audioEngine.IsPlaying;
                IsLoopActive = _audioEngine.IsLooping;
                LoopBadgeText = _audioEngine.IsLooping ? "1" : string.Empty;
                LoopTooltip = _audioEngine.IsLooping ? "Loop Track: On" : "Loop Track: Off";
                ShowLikeButton = false;
                break;
            }
            case "video-player":
            {
                AppLabel = "VLC Player · Headless";
                AppIconGeometry = IconCatalog.VideoPlayer;
                AppAccentBrush = VideoAccentBrush;
                TrackTitle = string.IsNullOrWhiteSpace(_videoPlayer.CurrentTitle) ? "VLC Headless" : _videoPlayer.CurrentTitle!;
                TrackSubtitle = _videoPlayer.IsHeadlessAudioOnly ? "Audio-only background stream" : "Background stream";
                IsPlaying = _videoPlayer.IsPlaying;
                IsLoopActive = _videoPlayer.LoopMode != VideoLoopMode.Off;
                LoopBadgeText = _videoPlayer.LoopMode == VideoLoopMode.One ? "1" : string.Empty;
                LoopTooltip = $"Loop: {_videoPlayer.LoopMode}";
                ShowLikeButton = false;
                break;
            }
        }

        IsVisible = true;
    }

    private async Task RefreshSpotifyLikedStateAsync(string trackUri)
    {
        try
        {
            var liked = await _spotifyWebApi.IsTrackLikedAsync(trackUri, CancellationToken.None).ConfigureAwait(false);
            if (!_disposed && liked is { } val && string.Equals(trackUri, _lastCheckedLikedTrackUri, StringComparison.Ordinal))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed && string.Equals(trackUri, _lastCheckedLikedTrackUri, StringComparison.Ordinal))
                    {
                        IsTrackLiked = val;
                    }
                });
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _moduleManager.ModuleStateChanged -= OnAnyModuleStateChanged;
        _soloist.StatusChanged -= OnSoloistStatusChanged;
        _audioEngine.PlaybackStateChanged -= OnEnginePlaybackChanged;
        _videoPlayer.PlaybackStateChanged -= OnVideoPlaybackChanged;
    }
}
