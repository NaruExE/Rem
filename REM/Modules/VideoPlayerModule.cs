using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using REM.Models;
using REM.Services;
using REM.ViewModels;
using REM.Views;

namespace REM.Modules;

/// <summary>
/// Manages the Video Player (VLC) module lifecycle, SQLite playlists, and auto-advance playback.
/// </summary>
public sealed class VideoPlayerModule : IAppModule, IHeaderConfigurableModule, ICloseableModule, IFullscreenModule
{
    private readonly DatabaseService _database;
    private readonly VideoMediaResolverService _resolver;
    private readonly VideoPlayerService _engine;
    private readonly DialogService _dialogService;
    private readonly ActionLogService _actionLog;

    private Control? _activeView;
    private VideoPlayerViewModel? _activeViewModel;
    private Action<bool>? _onFullscreenChanged;
    private ModuleState _state = ModuleState.Stopped;
    private long _activeProfileId;
    private long? _activePlaylistId;
    private bool _disposed;

    public VideoPlayerModule(
        DatabaseService database,
        VideoMediaResolverService resolver,
        VideoPlayerService engine,
        DialogService dialogService,
        ActionLogService actionLog)
    {
        _database = database;
        _resolver = resolver;
        _engine = engine;
        _dialogService = dialogService;
        _actionLog = actionLog;

        _engine.PlaybackStateChanged += OnEnginePlaybackStateChanged;
        _engine.EndReached += OnEngineEndReached;
        StatusSummary = "Stopped · LibVLC Video unloaded";
    }

    public string Id => "video-player";
    public string Name => "Video Player (VLC)";
    public string Description => "Hardware-decoded VLC video & stream player with playlists";
    public string IconGlyph => "V";
    public Geometry IconGeometry => IconCatalog.VideoPlayer;
    public string AccentHex => "#FF8800";

    public ModuleState State => _state;
    public bool HasActiveView => _activeView is not null;
    public string StatusSummary { get; private set; }

    public VideoPlayerService Engine => _engine;
    public VideoMediaResolverService Resolver => _resolver;
    public long ActiveProfileId => _activeProfileId;
    public long? ActivePlaylistId => _activePlaylistId;

    public event EventHandler<ModuleState>? StateChanged;
    public event EventHandler? StatusSummaryChanged;

    public void SetActiveProfile(long profileId)
    {
        if (_activeProfileId != profileId)
        {
            _activeProfileId = profileId;
            _activePlaylistId = null;
            _activeViewModel?.SetProfile(profileId);
        }
    }

    public async Task PlayPlaylistAsync(long playlistId, long? startItemId = null, CancellationToken cancellationToken = default)
    {
        var items = await _database.GetVideoPlaylistItemsAsync(playlistId, cancellationToken).ConfigureAwait(false);
        if (items.Count == 0)
        {
            throw new InvalidOperationException("This video playlist has no items.");
        }

        _activePlaylistId = playlistId;
        var target = startItemId.HasValue
            ? items.FirstOrDefault(i => i.Id == startItemId.Value) ?? items[0]
            : items[0];

        await _engine.PlayPlaylistItemAsync(target, cancellationToken).ConfigureAwait(false);
    }

    public async Task PlayFileInUiAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        if (_activeViewModel is { } vm)
        {
            await vm.PlayDirectFileAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _engine.PlaySourceAsync(filePath, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool> PlayNextInPlaylistAsync(CancellationToken cancellationToken = default)
    {
        var playlistId = _activePlaylistId;
        var currentItemId = _engine.CurrentPlaylistItemId;
        if (!playlistId.HasValue || !currentItemId.HasValue)
        {
            return false;
        }

        var items = await _database.GetVideoPlaylistItemsAsync(playlistId.Value, cancellationToken).ConfigureAwait(false);
        if (items.Count == 0)
        {
            return false;
        }

        var idx = items.FindIndex(i => i.Id == currentItemId.Value);
        int targetIdx;
        if (_engine.IsShuffled && items.Count > 1)
        {
            do
            {
                targetIdx = Random.Shared.Next(items.Count);
            } while (targetIdx == idx);
        }
        else if (idx >= 0 && idx + 1 < items.Count)
        {
            targetIdx = idx + 1;
        }
        else if (_engine.LoopMode == VideoLoopMode.All && items.Count > 0)
        {
            targetIdx = 0;
        }
        else
        {
            return false;
        }

        await _engine.PlayPlaylistItemAsync(items[targetIdx], cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> PlayPreviousInPlaylistAsync(CancellationToken cancellationToken = default)
    {
        var playlistId = _activePlaylistId;
        var currentItemId = _engine.CurrentPlaylistItemId;
        if (!playlistId.HasValue || !currentItemId.HasValue)
        {
            return false;
        }

        var items = await _database.GetVideoPlaylistItemsAsync(playlistId.Value, cancellationToken).ConfigureAwait(false);
        if (items.Count == 0)
        {
            return false;
        }

        var idx = items.FindIndex(i => i.Id == currentItemId.Value);
        int targetIdx;
        if (idx > 0)
        {
            targetIdx = idx - 1;
        }
        else if (_engine.LoopMode == VideoLoopMode.All && items.Count > 1)
        {
            targetIdx = items.Count - 1;
        }
        else
        {
            return false;
        }

        await _engine.PlayPlaylistItemAsync(items[targetIdx], cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Control OpenUI()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_activeView is null || _activeViewModel is null)
        {
            _activeViewModel = new VideoPlayerViewModel(
                this,
                _activeProfileId,
                _database,
                _dialogService,
                _actionLog);

            _activeViewModel.IsHeaderVisible = _database.LoadSettings().ShowAppHeaderBar;
            _activeViewModel.FullscreenChanged += OnViewModelFullscreenChanged;

            _activeView = new VideoPlayerView
            {
                DataContext = _activeViewModel
            };
        }

        SetState(ModuleState.RunningUI);
        UpdateStatusSummary();
        return _activeView;
    }

    public void SetHeaderVisibility(bool visible)
    {
        if (_activeViewModel is not null)
        {
            _activeViewModel.IsHeaderVisible = visible;
        }
    }

    public void AttachCloseCallback(Action closeToDashboard)
    {
        if (_activeViewModel is not null)
        {
            _activeViewModel.RequestCloseToDashboard = closeToDashboard;
        }
    }

    public void AttachFullscreenCallback(Action<bool> onFullscreenChanged)
    {
        _onFullscreenChanged = onFullscreenChanged;
    }

    private void OnViewModelFullscreenChanged(object? sender, bool isFullscreen) =>
        _onFullscreenChanged?.Invoke(isFullscreen);

    public void RunHeadless() => RunHeadless(VideoHeadlessMode.KeepVideoStream);

    public void RunHeadless(VideoHeadlessMode mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        DisposeView();
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
            "Run Video Player Headless",
            "Choose how to handle streaming in the background:\n\n• Audio Only (Save Data): Switches to an audio-only stream to save bandwidth (~1s rebuffer).\n• Keep Video Stream: Instant gapless audio, but continues downloading video data.",
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

        DisposeView();
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

        DisposeView();
        _activePlaylistId = null;

        try
        {
            _engine.StopAndDisposeEngineAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore teardown race.
        }

        SetState(ModuleState.Stopped);
        StatusSummary = "Stopped · LibVLC Video disposed";
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
        _engine.EndReached -= OnEngineEndReached;
        DisposeView();

        try
        {
            _engine.StopAndDisposeEngineAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        GC.SuppressFinalize(this);
    }

    private void OnEnginePlaybackStateChanged(object? sender, VideoPlaybackSnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(UpdateStatusSummary);
    }

    private void OnEngineEndReached(object? sender, long? finishedItemId)
    {
        if (_disposed || !_activePlaylistId.HasValue || !finishedItemId.HasValue)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var playlistId = _activePlaylistId;
                if (!playlistId.HasValue)
                {
                    return;
                }

                var items = await _database.GetVideoPlaylistItemsAsync(playlistId.Value, CancellationToken.None)
                    .ConfigureAwait(false);
                if (items.Count == 0)
                {
                    return;
                }

                var idx = items.FindIndex(i => i.Id == finishedItemId.Value);
                int targetIdx;
                if (_engine.IsShuffled && items.Count > 1)
                {
                    do
                    {
                        targetIdx = Random.Shared.Next(items.Count);
                    } while (targetIdx == idx);
                }
                else if (idx >= 0 && idx + 1 < items.Count)
                {
                    targetIdx = idx + 1;
                }
                else if (_engine.LoopMode == VideoLoopMode.All)
                {
                    targetIdx = 0;
                }
                else
                {
                    return;
                }

                await _engine.PlayPlaylistItemAsync(items[targetIdx], CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _actionLog.Log("Warning", $"Video auto-advance failed: {ex.Message}");
            }
        });
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
                ModuleState.RunningUI => "Ready · Video backend loaded",
                ModuleState.RunningHeadless => "Headless · Video audio idle",
                ModuleState.Paused => "Paused",
                _ => "Stopped · LibVLC Video disposed"
            };
        }
        else
        {
            var mode = _state == ModuleState.RunningHeadless
                ? (_engine.HeadlessMode == VideoHeadlessMode.AudioOnlyStream ? "Headless · Audio Only" : "Headless · Video Stream")
                : _state == ModuleState.Paused || _engine.IsPaused ? "Paused"
                : _engine.IsPlaying ? "Playing" : "Ready";
            StatusSummary = $"{mode} · {title}";
        }

        StatusSummaryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DisposeView()
    {
        if (_activeViewModel is not null)
        {
            _activeViewModel.FullscreenChanged -= OnViewModelFullscreenChanged;
        }

        _onFullscreenChanged?.Invoke(false);

        if (_activeView is VideoPlayerView videoView)
        {
            videoView.DetachSurface();
        }

        if (_activeView is not null)
        {
            _activeView.DataContext = null;
            _activeView = null;
        }

        _activeViewModel?.Dispose();
        _activeViewModel = null;
    }
}
