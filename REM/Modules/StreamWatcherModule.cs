using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using REM.Models;
using REM.Services;
using REM.ViewModels;
using REM.Views;

namespace REM.Modules;

/// <summary>
/// Standalone "Stream Watcher (YT/Twitch)" module combining YouTube &amp; Twitch discovery feeds
/// with a dedicated LibVLC hardware-decoded video player and toggleable live Twitch/7TV chat.
/// </summary>
public sealed class StreamWatcherModule : IAppModule
{
    private readonly DatabaseService _database;
    private readonly ActionLogService _actionLog;
    private readonly DialogService _dialogService;
    private readonly YtDlpService _ytDlp;
    private readonly YouTubeAudioService _youtube;
    private readonly TwitchHelixService _twitch;
    private readonly VideoPlayerService _engine;
    private readonly TwitchChatService _chatService;
    private readonly TwitchEmoteCacheService _emoteCache;

    private Control? _activeView;
    private StreamWatcherViewModel? _activeViewModel;
    private Action<bool>? _onFullscreenChanged;
    private ModuleState _state = ModuleState.Stopped;
    private long _activeProfileId;
    private bool _disposed;

    public StreamWatcherModule(
        DatabaseService database,
        ActionLogService actionLog,
        DialogService dialogService,
        YtDlpService ytDlp,
        YouTubeAudioService youtube,
        TwitchHelixService twitch,
        VideoMediaResolverService videoResolver)
    {
        _database = database;
        _actionLog = actionLog;
        _dialogService = dialogService;
        _ytDlp = ytDlp;
        _youtube = youtube;
        _twitch = twitch;

        _engine = new VideoPlayerService(
            database,
            videoResolver,
            actionLog,
            bandwidthProviderKey: "libvlc-stream-watch",
            volumeSettingKey: DatabaseService.KeyWatchVolume,
            qualitySettingKey: DatabaseService.KeyWatchQuality);

        _chatService = new TwitchChatService(actionLog);
        _emoteCache = new TwitchEmoteCacheService(actionLog);

        _engine.PlaybackStateChanged += OnEnginePlaybackStateChanged;
        StatusSummary = "Stopped · Video & Chat unloaded";
    }

    public string Id => "stream-watcher";
    public string Name => "Stream Watcher (YT/Twitch)";
    public string Description => "Watch YouTube & Twitch via LibVLC with quality control and toggleable 7TV chat";
    public string IconGlyph => "W";
    public Geometry IconGeometry => IconCatalog.StreamWatch;
    public string AccentHex => "#FF3B5C";

    public ModuleState State => _state;
    public bool HasActiveView => _activeView is not null;
    public string StatusSummary { get; private set; }
    public VideoPlayerService Engine => _engine;
    public Action<string>? OpenModuleSettingsRequested { get; set; }

    public event EventHandler<ModuleState>? StateChanged;
    public event EventHandler? StatusSummaryChanged;

    public void SetActiveProfile(long profileId)
    {
        _activeProfileId = profileId;
        _activeViewModel?.SetProfile(profileId);
    }

    public void RefreshSettings()
    {
        _activeViewModel?.RefreshFromSettings();
    }

    public Control OpenUI()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_activeView is null || _activeViewModel is null)
        {
            _activeViewModel = new StreamWatcherViewModel(
                this,
                _activeProfileId,
                _database,
                _actionLog,
                _dialogService,
                _ytDlp,
                _youtube,
                _twitch,
                _engine,
                _chatService,
                _emoteCache,
                () => OpenModuleSettingsRequested?.Invoke(Id));

            _activeViewModel.IsHeaderVisible = _database.LoadSettings().ShowAppHeaderBar;
            _activeViewModel.FullscreenChanged += OnViewModelFullscreenChanged;

            _activeView = new StreamWatcherView
            {
                DataContext = _activeViewModel
            };
        }

        SetState(ModuleState.RunningUI);
        UpdateStatusSummary();
        return _activeView;
    }

    public void RunHeadless() => RunHeadless(VideoHeadlessMode.KeepVideoStream);

    public void RunHeadless(VideoHeadlessMode mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        DisposeViewAndReleaseChat();
        _ = _engine.DetachVideoSurfaceAsync(mode);
        if (_engine.IsPaused)
        {
            _ = _engine.ResumeAsync();
        }

        SetState(ModuleState.RunningHeadless);
        UpdateStatusSummary();
        MemoryTrimService.ScheduleTrim();
    }

    public async Task<bool> PromptAndRunHeadlessAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(_engine.CurrentSourceUrl))
        {
            RunHeadless(VideoHeadlessMode.KeepVideoStream);
            return true;
        }

        var choice = await _dialogService.ShowAsync(new DialogRequest(
            DialogKind.Confirm,
            "Run Stream Watcher Headless",
            "Choose how to handle streaming in the background (Twitch chat disconnects automatically to save RAM):\n\n• Audio Only (Save Data): Switches to an audio-only stream to save bandwidth (~1s rebuffer).\n• Keep Video Stream: Instant gapless audio, but continues downloading video data.",
            _engine.CurrentTitle,
            [
                new DialogButtonOption("audio-only", "Audio Only (Save Data)", IsPrimary: true),
                new DialogButtonOption("keep-video", "Keep Video Stream"),
                new DialogButtonOption("cancel", "Cancel")
            ]));

        switch (choice)
        {
            case "audio-only":
                RunHeadless(VideoHeadlessMode.AudioOnlyStream);
                return true;
            case "keep-video":
                RunHeadless(VideoHeadlessMode.KeepVideoStream);
                return true;
            default:
                return false;
        }
    }

    public void Pause()
    {
        if (_disposed || _state == ModuleState.Stopped)
        {
            return;
        }

        DisposeViewAndReleaseChat();
        _ = _engine.PauseAsync();
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

        DisposeViewAndReleaseChat();

        try
        {
            _engine.StopAndDisposeEngineAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore teardown race.
        }

        GC.Collect(2, GCCollectionMode.Optimized);
        SetState(ModuleState.Stopped);
        StatusSummary = "Stopped · Video & Chat disposed";
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
        _engine.PlaybackStateChanged -= OnEnginePlaybackStateChanged;
        DisposeViewAndReleaseChat();

        try
        {
            _engine.Dispose();
        }
        catch
        {
        }

        _chatService.Dispose();
        _emoteCache.Dispose();
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

    internal void AttachFullscreenCallback(Action<bool> onFullscreenChanged) =>
        _onFullscreenChanged = onFullscreenChanged;

    internal void OnViewLeft() => _activeViewModel?.ResetExpandedFeedsToDefault();

    private void OnViewModelFullscreenChanged(object? sender, bool isFullscreen) =>
        _onFullscreenChanged?.Invoke(isFullscreen);

    private void OnEnginePlaybackStateChanged(object? sender, VideoPlaybackSnapshot snapshot)
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
        var title = _engine.CurrentTitle;
        if (string.IsNullOrWhiteSpace(title))
        {
            StatusSummary = _state switch
            {
                ModuleState.RunningUI => "Ready · Browse YouTube or Twitch",
                ModuleState.RunningHeadless => "Headless · Idle",
                ModuleState.Paused => "Paused",
                _ => "Stopped · Video & Chat unloaded"
            };
        }
        else
        {
            var mode = _state == ModuleState.RunningHeadless
                ? (_engine.HeadlessMode == VideoHeadlessMode.AudioOnlyStream ? "Headless · Audio Only" : "Headless · Video Stream")
                : _state == ModuleState.Paused || _engine.IsPaused ? "Paused"
                : _engine.IsPlaying ? "Watching" : "Ready";
            StatusSummary = $"{mode} · {title}";
        }

        StatusSummaryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DisposeViewAndReleaseChat()
    {
        if (_activeViewModel is not null)
        {
            _activeViewModel.FullscreenChanged -= OnViewModelFullscreenChanged;
        }

        _onFullscreenChanged?.Invoke(false);

        if (_activeView is StreamWatcherView watchView)
        {
            watchView.DetachSurface();
        }

        if (_activeView is not null)
        {
            _activeView.DataContext = null;
            _activeView = null;
        }

        _activeViewModel?.Dispose();
        _activeViewModel = null;

        try
        {
            _chatService.DisconnectAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        _emoteCache.ClearAll();
    }
}
