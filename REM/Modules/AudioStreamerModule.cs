using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using REM.Services;
using REM.ViewModels;
using REM.Views;

namespace REM.Modules;

public sealed class AudioStreamerModule : IAppModule
{
    private readonly DatabaseService _database;
    private readonly ActionLogService _actionLog;
    private readonly DialogService _dialogService;
    private readonly YtDlpService _ytDlp;
    private readonly YouTubeAudioService _youtube;
    private readonly TwitchHelixService _twitch;
    private readonly AudioPlaybackEngine _engine;

    private Control? _activeView;
    private AudioStreamerViewModel? _activeViewModel;
    private ModuleState _state = ModuleState.Stopped;
    private long _activeProfileId;
    private bool _disposed;

    public AudioStreamerModule(
        DatabaseService database,
        ActionLogService actionLog,
        DialogService dialogService,
        YtDlpService ytDlp,
        YouTubeAudioService youtube,
        TwitchHelixService twitch)
    {
        _database = database;
        _actionLog = actionLog;
        _dialogService = dialogService;
        _ytDlp = ytDlp;
        _youtube = youtube;
        _twitch = twitch;

        _engine = new AudioPlaybackEngine(_actionLog);
        if (int.TryParse(_database.GetSetting(DatabaseService.KeyAudioVolume), out var vol))
        {
            _engine.Volume = vol;
        }

        _engine.PlaybackStateChanged += OnEnginePlaybackStateChanged;
        StatusSummary = "Stopped · LibVLC unloaded";
    }

    public string Id => "audio-streamer";
    public string Name => "Audio Streamer (YT/Twitch)";
    public string Description => "Audio-only YouTube & Twitch REM";
    public string IconGlyph => "A";
    public Geometry IconGeometry => IconCatalog.AudioStreamer;
    public string AccentHex => "#9146FF";

    public ModuleState State => _state;
    public bool HasActiveView => _activeView is not null;
    public string StatusSummary { get; private set; }
    internal AudioPlaybackEngine AudioEngine => _engine;

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
            _activeViewModel = new AudioStreamerViewModel(
                this,
                _activeProfileId,
                _database,
                _actionLog,
                _dialogService,
                _ytDlp,
                _youtube,
                _twitch,
                _engine,
                () => OpenModuleSettingsRequested?.Invoke(Id));

            _activeViewModel.IsHeaderVisible = _database.LoadSettings().ShowAppHeaderBar;

            _activeView = new AudioStreamerView
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
        _youtube.ReleaseIdleClient();
        _ = _engine.ResumeIfPausedAsync();
        SetState(ModuleState.RunningHeadless);
        UpdateStatusSummary();
    }

    public void Pause()
    {
        if (_disposed || _state == ModuleState.Stopped)
        {
            return;
        }

        DisposeView();
        _youtube.ReleaseIdleClient();
        _ = _engine.PauseIfPlayingAsync();
        SetState(ModuleState.Paused);
        UpdateStatusSummary();
    }

    public void Kill()
    {
        if (_disposed)
        {
            return;
        }

        DisposeView();
        _youtube.ReleaseIdleClient();
        _engine.StopAndDisposeEngine();
        SetState(ModuleState.Stopped);
        StatusSummary = "Stopped · LibVLC disposed";
        StatusSummaryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine.PlaybackStateChanged -= OnEnginePlaybackStateChanged;
        DisposeView();
        _engine.Dispose();
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

    internal void OnViewLeft()
    {
        _activeViewModel?.ResetExpandedFeedsToDefault();
    }

    private void OnEnginePlaybackStateChanged(object? sender, EventArgs e)
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
        var track = _engine.CurrentTrack;
        if (track is null)
        {
            StatusSummary = _state switch
            {
                ModuleState.RunningUI => "Ready · Search YouTube or browse Twitch",
                ModuleState.RunningHeadless => "Headless · Idle",
                ModuleState.Paused => "Paused",
                _ => "Stopped · LibVLC disposed"
            };
        }
        else
        {
            var mode = _state == ModuleState.RunningHeadless ? "Headless" :
                       _state == ModuleState.Paused ? "Paused" :
                       _engine.IsPlaying ? "Playing" : "Ready";
            StatusSummary = $"{mode} · {track.Title} ({track.Channel})";
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
