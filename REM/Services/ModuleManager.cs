using Avalonia.Controls;
using REM.Modules;

namespace REM.Services;

public sealed class ModuleManager : IDisposable, IDownloadDispatcher
{
    private readonly List<IAppModule> _modules;
    private readonly Dictionary<string, IAppModule> _modulesById;
    private readonly SpotifyModule _spotifyModule;
    private readonly AudioStreamerModule _audioStreamer;
    private readonly VideoPlayerModule _videoPlayer;
    private readonly StreamWatcherModule _streamWatcher;
    private readonly MoviesTvModule _moviesTvModule;
    private readonly MediaGrabberModule _mediaGrabber;
    private readonly MediaStudioModule _mediaStudio;
    private readonly WatchPartyModule _watchParty;
    private Action<string>? _openModuleSettingsRequested;
    private bool _disposed;

    public ModuleManager(
        DatabaseService database,
        ActionLogService actionLog,
        DialogService dialogService,
        ImageAssetService imageAssets,
        YtDlpService ytDlp,
        YouTubeAudioService youtube,
        TwitchHelixService twitch,
        SpotifySoloistService soloist,
        SpotifyWebApiService spotifyWebApi,
        LastFmService lastFm,
        LrclibService lrclib,
        VideoMediaResolverService videoResolver,
        VideoPlayerService videoPlayer,
        TmdbService tmdb)
    {
        _spotifyModule = new SpotifyModule(
            database,
            actionLog,
            dialogService,
            imageAssets,
            soloist,
            spotifyWebApi,
            lastFm,
            lrclib,
            this);

        _audioStreamer = new AudioStreamerModule(
            database,
            actionLog,
            dialogService,
            ytDlp,
            youtube,
            twitch,
            this);

        _videoPlayer = new VideoPlayerModule(
            database,
            videoResolver,
            videoPlayer,
            dialogService,
            actionLog);

        _streamWatcher = new StreamWatcherModule(
            database,
            actionLog,
            dialogService,
            ytDlp,
            youtube,
            twitch,
            videoResolver,
            this);

        _moviesTvModule = new MoviesTvModule(
            database,
            tmdb,
            dialogService,
            actionLog);

        _mediaGrabber = new MediaGrabberModule(
            database,
            ytDlp,
            dialogService,
            actionLog);

        _mediaStudio = new MediaStudioModule(
            database,
            ytDlp,
            actionLog);

        _watchParty = new WatchPartyModule(
            database,
            _videoPlayer,
            actionLog);

        _modules =
        [
            _spotifyModule,
            _audioStreamer,
            _videoPlayer,
            _streamWatcher,
            _moviesTvModule,
            _mediaGrabber,
            _mediaStudio,
            _watchParty
        ];

        _modulesById = _modules.ToDictionary(m => m.Id, StringComparer.Ordinal);
        foreach (var module in _modules)
        {
            module.StateChanged += OnModuleStateChanged;
        }
    }

    public IReadOnlyList<IAppModule> AllModules => _modules;
    internal AudioPlaybackEngine AudioEngine => _audioStreamer.AudioEngine;
    internal VideoPlayerModule VideoPlayerModule => _videoPlayer;
    internal MoviesTvModule MoviesTvModule => _moviesTvModule;
    internal MediaGrabberModule MediaGrabberModule => _mediaGrabber;
    internal MediaStudioModule MediaStudioModule => _mediaStudio;
    internal WatchPartyModule WatchPartyModule => _watchParty;

    public Task EnqueueAsync(string url, string? title, string formatPreset, CancellationToken cancellationToken = default) =>
        _mediaGrabber.EnqueueFromDispatcherAsync(url, title, formatPreset, cancellationToken);

    public event EventHandler<(string ModuleId, ModuleState State)>? ModuleStateChanged;

    public Action<string>? OpenModuleSettingsRequested
    {
        get => _openModuleSettingsRequested;
        set
        {
            _openModuleSettingsRequested = value;
            _spotifyModule.OpenModuleSettingsRequested = value;
            _audioStreamer.OpenModuleSettingsRequested = value;
            _streamWatcher.OpenModuleSettingsRequested = value;
        }
    }

    public void SetActiveProfile(long profileId)
    {
        _audioStreamer.SetActiveProfile(profileId);
        _videoPlayer.SetActiveProfile(profileId);
        _streamWatcher.SetActiveProfile(profileId);
        _moviesTvModule.SetActiveProfile(profileId);
        _mediaGrabber.SetActiveProfile(profileId);
    }

    public void NotifyModuleSettingsChanged(string moduleId)
    {
        if (string.Equals(moduleId, _audioStreamer.Id, StringComparison.Ordinal) ||
            string.Equals(moduleId, _streamWatcher.Id, StringComparison.Ordinal))
        {
            _audioStreamer.RefreshSettings();
            _streamWatcher.RefreshSettings();
        }
        else if (string.Equals(moduleId, _spotifyModule.Id, StringComparison.Ordinal))
        {
            _spotifyModule.RefreshSettings();
        }
    }

    public void NotifyModuleViewLeft(string? moduleId)
    {
        if (string.Equals(moduleId, _audioStreamer.Id, StringComparison.Ordinal))
        {
            _audioStreamer.OnViewLeft();
        }
        else if (string.Equals(moduleId, _streamWatcher.Id, StringComparison.Ordinal))
        {
            _streamWatcher.OnViewLeft();
        }
        else if (string.Equals(moduleId, _spotifyModule.Id, StringComparison.Ordinal))
        {
            _spotifyModule.OnViewLeft();
        }
    }

    public IAppModule? GetModule(string moduleId) =>
        _modulesById.GetValueOrDefault(moduleId);

    public Control OpenModuleUI(string moduleId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var module = RequireModule(moduleId);
        return module.OpenUI();
    }

    public void StartModule(string moduleId) => OpenModuleUI(moduleId);

    public void RunModuleHeadless(string moduleId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireModule(moduleId).RunHeadless();
    }

    public void PauseModule(string moduleId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireModule(moduleId).Pause();
    }

    public void KillModule(string moduleId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireModule(moduleId).Kill();
    }

    public void KillAll()
    {
        foreach (var module in _modules)
        {
            module.Kill();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var module in _modules)
        {
            module.StateChanged -= OnModuleStateChanged;
            module.Dispose();
        }
    }

    private IAppModule RequireModule(string moduleId)
    {
        if (_modulesById.TryGetValue(moduleId, out var module))
        {
            return module;
        }

        throw new KeyNotFoundException($"Module '{moduleId}' is not registered.");
    }

    private void OnModuleStateChanged(object? sender, ModuleState state)
    {
        if (sender is IAppModule module)
        {
            ModuleStateChanged?.Invoke(this, (module.Id, state));
        }
    }
}
