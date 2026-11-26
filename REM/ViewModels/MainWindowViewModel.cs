using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using REM.Models;
using REM.Modules;
using REM.Services;

namespace REM.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private enum NavKind
    {
        Profiles,
        DashboardHub,
        DashboardModule
    }

    private sealed record NavEntry(NavKind Kind, UserProfile? Profile, string? ModuleId);

    private readonly DatabaseService _database;
    private readonly ThemeService _themeService;
    private readonly AudioService _audioService;
    private readonly ModuleManager _moduleManager;
    private readonly ImageAssetService _imageAssets;
    private readonly ActionLogService _actionLog;
    private readonly StartupService _startupService;
    private readonly UpdateService _updateService;
    private readonly YtDlpService _ytDlp;
    private readonly TwitchHelixService _twitch;
    private readonly SpotifySoloistService _soloist;
    private readonly SpotifyWebApiService _spotifyWebApi;
    private readonly LastFmService _lastFm;

    private readonly List<NavEntry> _history = [];
    private int _navIndex = -1;
    private bool _isRestoringHistory;
    private CancellationTokenSource? _updateInstallCts;
    private UpdateCheckResult? _availableUpdate;

    public MainWindowViewModel(
        DatabaseService database,
        ThemeService themeService,
        AudioService audioService,
        ModuleManager moduleManager,
        ImageAssetService imageAssets,
        ActionLogService actionLog,
        DialogService dialogService,
        StartupService startupService,
        UpdateService updateService,
        YtDlpService ytDlp,
        TwitchHelixService twitch,
        SpotifySoloistService soloist,
        SpotifyWebApiService spotifyWebApi,
        LastFmService lastFm)
    {
        _database = database;
        _themeService = themeService;
        _audioService = audioService;
        _moduleManager = moduleManager;
        _imageAssets = imageAssets;
        _actionLog = actionLog;
        _startupService = startupService;
        _updateService = updateService;
        _ytDlp = ytDlp;
        _twitch = twitch;
        _soloist = soloist;
        _spotifyWebApi = spotifyWebApi;
        _lastFm = lastFm;
        DialogService = dialogService;

        TitleBarIconBitmap = _imageAssets.GetBrandImage("rem-icon.png", 32);
        _moduleManager.ModuleStateChanged += OnModuleStateChanged;
        _updateService.UpdateCheckCompleted += OnUpdateCheckCompleted;
    }

    public DialogService DialogService { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTitleBarIcon))]
    private Bitmap? _titleBarIconBitmap;

    public bool HasTitleBarIcon => TitleBarIconBitmap is not null;

    [ObservableProperty]
    private ObservableObject? _currentScreen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsOpen))]
    private SettingsViewModel? _activeSettings;

    public bool IsSettingsOpen => ActiveSettings is not null;

    [ObservableProperty]
    private bool _hasAvailableUpdate;

    [ObservableProperty]
    private string _updatePillText = string.Empty;

    [ObservableProperty]
    private bool _isInstallingUpdate;

    [ObservableProperty]
    private double _updateProgressPercent;

    public Func<Task<string?>>? PickAudioFileAsync { get; set; }
    public Func<Task<string?>>? PickImageFileAsync { get; set; }
    public Action? RefreshShellIcons { get; set; }
    public Action? RequestAppShutdown { get; set; }

    public bool ShouldMinimizeToTray() => _database.LoadSettings().MinimizeToTray;

    public void LogMinimizedToTray() =>
        _actionLog.Log("System", "Window hidden to system tray (background apps remain active).");

    public void OnWindowVisibilityChanged(bool isVisibleAndNotMinimized)
    {
        if (CurrentScreen is DashboardViewModel dashboardVm)
        {
            dashboardVm.SetWindowActive(isVisibleAndNotMinimized);
        }
    }

    public async Task StartAsync()
    {
        var (hasNotice, isSuccess, noticeMsg) = _updateService.ConsumePostUpdateNotice();
        if (hasNotice)
        {
            if (isSuccess)
            {
                _ = DialogService.ShowAsync(new DialogRequest(
                    DialogKind.Info,
                    "REM Updated",
                    noticeMsg,
                    null,
                    [new DialogButtonOption("ok", "OK", IsPrimary: true)]));
            }
            else
            {
                _ = DialogService.ShowWarningAsync(
                    "Update Rolled Back",
                    "REM automatically restored your previous version after the update failed to swap files.",
                    noticeMsg);
            }
        }

        var settings = _database.LoadSettings();
        if (settings.AutoCheckUpdates)
        {
            _ = CheckForUpdatesOnStartupAsync();
        }

        if (settings.SkipIntro)
        {
            _actionLog.Log("Navigation", "Skipping welcome intro (SkipIntro enabled).");
            ShowProfiles();
            return;
        }

        var welcome = new WelcomeViewModel(
            _audioService,
            _imageAssets,
            _actionLog,
            settings.WelcomeAudioPath,
            ShowProfiles);
        SwapCurrentScreen(welcome);
        await welcome.BeginAsync();
    }

    public void ShowProfiles()
    {
        _moduleManager.KillAll();
        var profiles = new ProfilesViewModel(
            _database,
            _imageAssets,
            _actionLog,
            DialogService,
            () => PickImageFileAsync is not null ? PickImageFileAsync() : Task.FromResult<string?>(null),
            profile => OpenProfileDashboard(profile),
            OpenSettings);
        SwapCurrentScreen(profiles);
        PushHistory(new NavEntry(NavKind.Profiles, null, null));
        _actionLog.Log("Navigation", "Opened Profiles screen.");
    }

    public void OpenProfileDashboard(UserProfile profile, string? initialModuleId = null)
    {
        var dashboard = new DashboardViewModel(
            profile,
            _database,
            _moduleManager,
            _imageAssets,
            _actionLog,
            DialogService,
            _ytDlp,
            _twitch,
            _soloist,
            _spotifyWebApi,
            _lastFm,
            onSwitchProfile: ShowProfiles,
            onOpenSettings: OpenSettings,
            onNavigatedToHub: () => PushHistory(new NavEntry(NavKind.DashboardHub, profile, null)),
            onNavigatedToModule: moduleId => PushHistory(new NavEntry(NavKind.DashboardModule, profile, moduleId)));

        SwapCurrentScreen(dashboard);

        if (!string.IsNullOrWhiteSpace(initialModuleId))
        {
            _isRestoringHistory = true;
            try
            {
                dashboard.FocusOrOpenModule(initialModuleId);
            }
            finally
            {
                _isRestoringHistory = false;
            }

            PushHistory(new NavEntry(NavKind.DashboardModule, profile, initialModuleId));
        }
        else
        {
            PushHistory(new NavEntry(NavKind.DashboardHub, profile, null));
            _actionLog.Log("Profile", $"Opened workspace for profile '{profile.Name}'.");
        }
    }

    public void OpenModuleFromTray(string moduleId)
    {
        if (CurrentScreen is DashboardViewModel dashboardVm)
        {
            dashboardVm.FocusOrOpenModule(moduleId);
        }
    }

    public bool HandleMediaKey(Avalonia.Input.Key key)
    {
        var spActive = _soloist.IsDaemonRunning && _soloist.CurrentStatus is { Stopped: false, Track: not null };
        var spPlaying = spActive && _soloist.CurrentStatus is { Paused: false };
        var auEngine = _moduleManager.AudioEngine;
        var auActive = auEngine.CurrentTrack is not null;
        var auPlaying = auActive && auEngine.IsPlaying;

        var vlcEngine = _moduleManager.VideoPlayerModule.Engine;
        var swEngine = (_moduleManager.GetModule("stream-watcher") as StreamWatcherModule)?.Engine;
        var vidEngine = swEngine is { IsPlaying: true } ? swEngine
            : vlcEngine is { IsPlaying: true } ? vlcEngine
            : swEngine is { IsPaused: true } ? swEngine
            : vlcEngine is { IsPaused: true } ? vlcEngine
            : null;

        // Pick whichever player is actively playing first, otherwise any loaded audio/video session.
        if (spPlaying)
        {
            return DispatchSpotifyMediaKey(key);
        }

        if (auPlaying)
        {
            return DispatchAudioEngineMediaKey(auEngine, key);
        }

        if (vidEngine is { IsPlaying: true })
        {
            return DispatchVideoEngineMediaKey(vidEngine, key);
        }

        if (spActive)
        {
            return DispatchSpotifyMediaKey(key);
        }

        if (auActive)
        {
            return DispatchAudioEngineMediaKey(auEngine, key);
        }

        if (vidEngine is not null)
        {
            return DispatchVideoEngineMediaKey(vidEngine, key);
        }

        return false;
    }

    private double _preMuteSpotifyVolume = 80;
    private int _preMuteAudioVolume = 85;

    public bool TryExitActiveModuleSubOverlay()
    {
        if (CurrentScreen is DashboardViewModel { ActiveModuleId: "spotify" } &&
            (_moduleManager.GetModule("spotify") as SpotifyModule)?.ActiveViewModel is { IsLyricsFullscreen: true } spVm)
        {
            spVm.CloseLyricsFullscreenCommand.Execute(null);
            return true;
        }

        return false;
    }

    public bool HandleTransportShortcut(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers)
    {
        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt) ||
            CurrentScreen is not DashboardViewModel dashboard)
        {
            return false;
        }

        var shift = modifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift);
        var ctrl = modifiers.HasFlag(Avalonia.Input.KeyModifiers.Control);
        var openId = dashboard.ActiveModuleId;

        if (string.Equals(openId, "spotify", StringComparison.Ordinal))
        {
            if ((_moduleManager.GetModule("spotify") as SpotifyModule)?.ActiveViewModel is { } spVm)
            {
                return DispatchOpenSpotifyShortcut(spVm, key, ctrl, shift);
            }

            return DispatchHeadlessSpotifyShortcut(key, ctrl, shift, allowMute: true);
        }

        if (string.Equals(openId, "audio-streamer", StringComparison.Ordinal))
        {
            return DispatchAudioShortcut(_moduleManager.AudioEngine, key, ctrl, shift, allowMute: true);
        }

        if (openId is not null)
        {
            // VideoPlayer and StreamWatcher handle their own window-level shortcuts with OSD.
            return false;
        }

        // Dashboard Hub: route transport keys (except bare 'M') to whichever headless session is playing or loaded.
        var spActive = _soloist.IsDaemonRunning && _soloist.CurrentStatus is { Stopped: false, Track: not null };
        var spPlaying = spActive && _soloist.CurrentStatus is { Paused: false };
        var au = _moduleManager.AudioEngine;
        var auActive = au.CurrentTrack is not null;
        var auPlaying = auActive && au.IsPlaying;
        var vlc = _moduleManager.VideoPlayerModule.Engine;
        var vlcActive = vlc is { IsPlaying: true } or { IsPaused: true };
        var vlcPlaying = vlc is { IsPlaying: true };

        if (spPlaying)
        {
            return DispatchHeadlessSpotifyShortcut(key, ctrl, shift, allowMute: false);
        }

        if (auPlaying)
        {
            return DispatchAudioShortcut(au, key, ctrl, shift, allowMute: false);
        }

        if (vlcPlaying && key == Avalonia.Input.Key.Space)
        {
            _ = vlc!.TogglePauseAsync();
            return true;
        }

        if (spActive)
        {
            return DispatchHeadlessSpotifyShortcut(key, ctrl, shift, allowMute: false);
        }

        if (auActive)
        {
            return DispatchAudioShortcut(au, key, ctrl, shift, allowMute: false);
        }

        if (vlcActive && key == Avalonia.Input.Key.Space)
        {
            _ = vlc!.TogglePauseAsync();
            return true;
        }

        return false;
    }

    private bool DispatchOpenSpotifyShortcut(SpotifyViewModel spVm, Avalonia.Input.Key key, bool ctrl, bool shift)
    {
        switch (key)
        {
            case Avalonia.Input.Key.Space:
                spVm.TogglePlayPauseCommand.Execute(null);
                return true;
            case Avalonia.Input.Key.Left when ctrl:
                spVm.PrevTrackCommand.Execute(null);
                return true;
            case Avalonia.Input.Key.Right when ctrl:
                spVm.NextTrackCommand.Execute(null);
                return true;
            case Avalonia.Input.Key.Left:
                _ = spVm.CommitScrubAsync(spVm.SeekPositionSeconds - (shift ? 15 : 5));
                return true;
            case Avalonia.Input.Key.Right:
                _ = spVm.CommitScrubAsync(spVm.SeekPositionSeconds + (shift ? 15 : 5));
                return true;
            case Avalonia.Input.Key.Up:
                spVm.Volume = Math.Clamp(spVm.Volume + 5, 0, 100);
                return true;
            case Avalonia.Input.Key.Down:
                spVm.Volume = Math.Clamp(spVm.Volume - 5, 0, 100);
                return true;
            case Avalonia.Input.Key.M:
                if (spVm.Volume > 0)
                {
                    _preMuteSpotifyVolume = spVm.Volume;
                    spVm.Volume = 0;
                }
                else
                {
                    spVm.Volume = _preMuteSpotifyVolume > 0 ? _preMuteSpotifyVolume : 80;
                }

                return true;
            default:
                return false;
        }
    }

    private bool DispatchHeadlessSpotifyShortcut(Avalonia.Input.Key key, bool ctrl, bool shift, bool allowMute)
    {
        switch (key)
        {
            case Avalonia.Input.Key.Space:
                _ = _soloist.PlayPauseAsync();
                return true;
            case Avalonia.Input.Key.Left when ctrl:
                _ = _soloist.PrevAsync();
                return true;
            case Avalonia.Input.Key.Right when ctrl:
                _ = _soloist.NextAsync();
                return true;
            case Avalonia.Input.Key.Left:
                _ = _soloist.SeekAsync(Math.Max(0, _soloist.GetEstimatedPositionMs() - (shift ? 15_000 : 5_000)));
                return true;
            case Avalonia.Input.Key.Right:
                _ = _soloist.SeekAsync(Math.Max(0, _soloist.GetEstimatedPositionMs() + (shift ? 15_000 : 5_000)));
                return true;
            case Avalonia.Input.Key.Up:
            case Avalonia.Input.Key.Down:
            {
                var current = int.TryParse(_database.GetSetting(DatabaseService.KeySpotifyVolume), out var v)
                    ? Math.Clamp(v, 0, 100)
                    : 80;
                var next = Math.Clamp(current + (key == Avalonia.Input.Key.Up ? 5 : -5), 0, 100);
                _database.SaveSetting(DatabaseService.KeySpotifyVolume, next.ToString());
                _ = _soloist.SetVolumeAsync(next);
                return true;
            }
            case Avalonia.Input.Key.M when allowMute:
            {
                var current = int.TryParse(_database.GetSetting(DatabaseService.KeySpotifyVolume), out var v)
                    ? Math.Clamp(v, 0, 100)
                    : 80;
                int next;
                if (current > 0)
                {
                    _preMuteSpotifyVolume = current;
                    next = 0;
                }
                else
                {
                    next = _preMuteSpotifyVolume > 0 ? (int)Math.Round(_preMuteSpotifyVolume) : 80;
                }

                _database.SaveSetting(DatabaseService.KeySpotifyVolume, next.ToString());
                _ = _soloist.SetVolumeAsync(next);
                return true;
            }
            default:
                return false;
        }
    }

    private bool DispatchAudioShortcut(AudioPlaybackEngine au, Avalonia.Input.Key key, bool ctrl, bool shift, bool allowMute)
    {
        switch (key)
        {
            case Avalonia.Input.Key.Space:
                _ = au.TogglePauseAsync();
                return true;
            case Avalonia.Input.Key.Left when !ctrl:
                au.SkipRelativeSeconds(shift ? -15 : -5);
                return true;
            case Avalonia.Input.Key.Right when !ctrl:
                au.SkipRelativeSeconds(shift ? 15 : 5);
                return true;
            case Avalonia.Input.Key.Up:
                au.Volume = Math.Clamp(au.Volume + 5, 0, 100);
                _database.SaveSetting(DatabaseService.KeyAudioVolume, au.Volume.ToString());
                return true;
            case Avalonia.Input.Key.Down:
                au.Volume = Math.Clamp(au.Volume - 5, 0, 100);
                _database.SaveSetting(DatabaseService.KeyAudioVolume, au.Volume.ToString());
                return true;
            case Avalonia.Input.Key.M when allowMute:
                if (au.Volume > 0)
                {
                    _preMuteAudioVolume = au.Volume;
                    au.Volume = 0;
                }
                else
                {
                    au.Volume = _preMuteAudioVolume > 0 ? _preMuteAudioVolume : 85;
                }

                _database.SaveSetting(DatabaseService.KeyAudioVolume, au.Volume.ToString());
                return true;
            default:
                return false;
        }
    }

    private bool DispatchSpotifyMediaKey(Avalonia.Input.Key key)
    {
        switch (key)
        {
            case Avalonia.Input.Key.MediaPlayPause:
                _ = _soloist.PlayPauseAsync();
                return true;
            case Avalonia.Input.Key.MediaNextTrack:
                _ = _soloist.NextAsync();
                return true;
            case Avalonia.Input.Key.MediaPreviousTrack:
                _ = _soloist.PrevAsync();
                return true;
            case Avalonia.Input.Key.MediaStop:
                _ = _soloist.PauseAsync();
                return true;
            default:
                return false;
        }
    }

    private static bool DispatchAudioEngineMediaKey(AudioPlaybackEngine au, Avalonia.Input.Key key)
    {
        switch (key)
        {
            case Avalonia.Input.Key.MediaPlayPause:
                _ = au.TogglePauseAsync();
                return true;
            case Avalonia.Input.Key.MediaPreviousTrack:
                if (!au.IsLive && au.DurationSeconds > 0) au.SeekSeconds(0);
                return true;
            case Avalonia.Input.Key.MediaStop:
                _ = au.PauseIfPlayingAsync();
                return true;
            default:
                return false;
        }
    }

    private static bool DispatchVideoEngineMediaKey(VideoPlayerService vid, Avalonia.Input.Key key)
    {
        switch (key)
        {
            case Avalonia.Input.Key.MediaPlayPause:
                _ = vid.TogglePauseAsync();
                return true;
            case Avalonia.Input.Key.MediaStop:
                _ = vid.PauseAsync();
                return true;
            default:
                return false;
        }
    }

    public void OpenSettings()
    {
        if (ActiveSettings is not null)
        {
            return;
        }

        ActiveSettings = new SettingsViewModel(
            _database,
            _themeService,
            _audioService,
            _imageAssets,
            _actionLog,
            DialogService,
            _startupService,
            _updateService,
            () => PickAudioFileAsync is not null ? PickAudioFileAsync() : Task.FromResult<string?>(null),
            () => PickImageFileAsync is not null ? PickImageFileAsync() : Task.FromResult<string?>(null),
            InstallUpdateAsync,
            HandleProfilesChanged,
            ApplyHeaderBarPreference,
            ReloadAssets,
            RefreshWallpaperSettings,
            CloseSettings);
        _actionLog.Log("Navigation", "Opened Settings.");
    }

    private void RefreshWallpaperSettings()
    {
        if (CurrentScreen is DashboardViewModel dashboardVm)
        {
            dashboardVm.RefreshWallpaper();
        }
    }

    public void CloseSettings()
    {
        if (ActiveSettings is null)
        {
            return;
        }

        ActiveSettings.Dispose();
        ActiveSettings = null;
    }

    public void ReloadAssets()
    {
        _imageAssets.ReloadBrandImages();
        TitleBarIconBitmap = _imageAssets.GetBrandImage("rem-icon.png", 32);

        if (CurrentScreen is ProfilesViewModel profilesVm)
        {
            profilesVm.RefreshBrandAssets();
        }
        else if (CurrentScreen is DashboardViewModel dashboardVm)
        {
            dashboardVm.RefreshBrandAssets();
        }

        RefreshShellIcons?.Invoke();
        _actionLog.Log("Assets", "Reloaded custom Rem PNG images.");
    }

    private void ApplyHeaderBarPreference(bool showHeader)
    {
        if (CurrentScreen is DashboardViewModel dashboardVm)
        {
            dashboardVm.ApplyHeaderBarPreference(showHeader);
        }
    }

    public bool NavigateBack()
    {
        if (DialogService.TryDismissActive())
        {
            return true;
        }

        if (IsSettingsOpen)
        {
            CloseSettings();
            return true;
        }

        if (CurrentScreen is WelcomeViewModel welcome)
        {
            welcome.Skip();
            return true;
        }

        if (CurrentScreen is ProfilesViewModel profilesVm && profilesVm.TryCloseModal())
        {
            return true;
        }

        if (CurrentScreen is DashboardViewModel dashboardVm && dashboardVm.TryCloseModal())
        {
            return true;
        }

        if (_navIndex <= 0)
        {
            return false;
        }

        _navIndex--;
        RestoreHistoryEntry(_history[_navIndex]);
        return true;
    }

    public bool NavigateForward()
    {
        if (DialogService.IsDialogOpen || IsSettingsOpen)
        {
            return false;
        }

        if (CurrentScreen is ProfilesViewModel profilesVm && profilesVm.HasOpenModal)
        {
            return false;
        }

        if (CurrentScreen is DashboardViewModel dashboardVm && dashboardVm.HasOpenModal)
        {
            return false;
        }

        if (_navIndex < 0 || _navIndex + 1 >= _history.Count)
        {
            return false;
        }

        _navIndex++;
        RestoreHistoryEntry(_history[_navIndex]);
        return true;
    }

    public void Shutdown()
    {
        _moduleManager.ModuleStateChanged -= OnModuleStateChanged;
        _updateService.UpdateCheckCompleted -= OnUpdateCheckCompleted;
        _updateInstallCts?.Cancel();
        _updateInstallCts?.Dispose();
        _updateInstallCts = null;

        DialogService.TryDismissActive();
        CloseSettings();
        if (CurrentScreen is IDisposable disposableScreen)
        {
            disposableScreen.Dispose();
        }

        CurrentScreen = null;
        _audioService.Dispose();
        _moduleManager.Dispose();
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            await Task.Delay(4000).ConfigureAwait(false);
            var result = await _updateService.CheckForUpdatesAsync().ConfigureAwait(false);
            if (result.Status == UpdateCheckStatus.UpdateAvailable)
            {
                _actionLog.Log("Update", $"Found REM {result.LatestVersion} on GitHub (current {result.CurrentVersion}).");
            }
        }
        catch
        {
        }
    }

    private void OnUpdateCheckCompleted(object? sender, UpdateCheckResult result)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (result.Status == UpdateCheckStatus.UpdateAvailable)
            {
                var skipped = _database.GetSetting(DatabaseService.KeySkippedUpdateVersion);
                var isSkipped = !string.IsNullOrWhiteSpace(skipped) &&
                    (string.Equals(skipped, result.LatestVersion, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(skipped, result.LatestTag, StringComparison.OrdinalIgnoreCase));

                _availableUpdate = result;
                if (!isSkipped)
                {
                    UpdatePillText = $"Update {result.LatestVersion}";
                    HasAvailableUpdate = true;
                }
            }
            else if (result.Status == UpdateCheckStatus.UpToDate)
            {
                _availableUpdate = null;
                HasAvailableUpdate = false;
                UpdatePillText = string.Empty;
            }
        });
    }

    [RelayCommand]
    private async Task PromptOrInstallUpdateAsync()
    {
        var update = _availableUpdate ?? _updateService.LastCheckResult;
        if (update is null || update.Status != UpdateCheckStatus.UpdateAvailable)
        {
            return;
        }

        if (IsInstallingUpdate)
        {
            return;
        }

        var summary = BuildUpdateDialogSummary(update);
        var notes = string.IsNullOrWhiteSpace(update.ReleaseNotes)
            ? null
            : (update.ReleaseNotes.Length > 750 ? update.ReleaseNotes[..750] + "..." : update.ReleaseNotes);

        var choice = await DialogService.ShowAsync(new DialogRequest(
            DialogKind.Info,
            $"REM {update.LatestVersion} Available",
            summary,
            notes,
            [
                new DialogButtonOption("update", "Update Now", IsPrimary: true),
                new DialogButtonOption("skip", "Skip Version"),
                new DialogButtonOption("later", "Later")
            ]));

        if (choice == "skip")
        {
            _database.SaveSetting(DatabaseService.KeySkippedUpdateVersion, update.LatestVersion ?? update.LatestTag ?? string.Empty);
            HasAvailableUpdate = false;
            return;
        }

        if (choice == "update")
        {
            await InstallUpdateAsync(update);
        }
    }

    public async Task<bool> InstallUpdateAsync(UpdateCheckResult update)
    {
        if (IsInstallingUpdate || update.Status != UpdateCheckStatus.UpdateAvailable)
        {
            return false;
        }

        if (update.IsDevelopmentBuild)
        {
            var confirmDev = await DialogService.ShowConfirmAsync(
                "Development Build Detected",
                $"This instance appears to be running from a local build output folder ({_updateService.InstallDirectory}). Overwrite files here with GitHub release {update.LatestVersion} anyway?",
                "Overwrite Build",
                isDanger: true);
            if (!confirmDev)
            {
                return false;
            }
        }

        IsInstallingUpdate = true;
        UpdateProgressPercent = 0;
        var previousPill = UpdatePillText;
        UpdatePillText = "Updating...";

        _updateInstallCts?.Cancel();
        _updateInstallCts?.Dispose();
        _updateInstallCts = new CancellationTokenSource();
        var ct = _updateInstallCts.Token;

        try
        {
            var progress = new Progress<UpdateProgress>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateProgressPercent = p.Percent;
                    UpdatePillText = p.TotalFiles > 0 && p.FilesCompleted < p.TotalFiles
                        ? $"Updating {p.Percent:F0}%"
                        : p.Phase;
                    ActiveSettings?.ReportDownloadProgress(p);
                });
            });

            var (ok, stagingDir, dlError) = await _updateService.DownloadAndStageUpdateAsync(update, progress, ct);
            if (!ok || string.IsNullOrWhiteSpace(stagingDir))
            {
                UpdatePillText = string.IsNullOrEmpty(previousPill) ? $"Update {update.LatestVersion}" : previousPill;
                await DialogService.ShowErrorAsync(
                    "Update Download Failed",
                    "Could not download or verify the update files from GitHub. Your existing REM installation was not modified.",
                    dlError);
                return false;
            }

            if (!_updateService.LaunchUpdateHelperAndPrepareExit(update, stagingDir, out var launchErr))
            {
                UpdatePillText = string.IsNullOrEmpty(previousPill) ? $"Update {update.LatestVersion}" : previousPill;
                await DialogService.ShowErrorAsync(
                    "Could Not Apply Update",
                    "Files were verified, but REM could not launch the update swap helper.",
                    launchErr);
                return false;
            }

            RequestAppShutdown?.Invoke();
            return true;
        }
        catch (OperationCanceledException)
        {
            UpdatePillText = string.IsNullOrEmpty(previousPill) ? $"Update {update.LatestVersion}" : previousPill;
            return false;
        }
        catch (Exception ex)
        {
            UpdatePillText = string.IsNullOrEmpty(previousPill) ? $"Update {update.LatestVersion}" : previousPill;
            await DialogService.ShowErrorAsync("Update Error", "An unexpected error occurred during update.", ex.Message);
            return false;
        }
        finally
        {
            IsInstallingUpdate = false;
        }
    }

    private static string BuildUpdateDialogSummary(UpdateCheckResult update)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"You are running {update.CurrentVersion}. Version {update.LatestVersion} is ready on GitHub.");
        if (update.HasManifestDelta)
        {
            sb.AppendLine($"Delta update: {update.FilesToDownload.Count} changed file(s) (~{UpdateService.FormatBytes(update.DeltaCompressedBytes)} instead of {UpdateService.FormatBytes(update.ZipTotalBytes)} full download).");
            if (update.FilesToRemove.Count > 0)
            {
                sb.AppendLine($"{update.FilesToRemove.Count} obsolete file(s) will be cleaned up.");
            }
        }
        else if (update.ZipTotalBytes > 0)
        {
            sb.AppendLine($"Archive size: {UpdateService.FormatBytes(update.ZipTotalBytes)}.");
        }

        sb.Append("Your profiles, saved API keys, themes, and %LocalAppData%\\REM data will not be modified.");
        return sb.ToString();
    }

    private void OnModuleStateChanged(object? sender, (string ModuleId, ModuleState State) args)
    {
        if (args.State == ModuleState.Stopped)
        {
            PruneModuleFromHistory(args.ModuleId);
        }
    }

    private void PruneModuleFromHistory(string moduleId)
    {
        if (_history.Count == 0)
        {
            return;
        }

        var currentIndex = _navIndex;
        var pruned = new List<NavEntry>(_history.Count);
        var nextIndex = -1;

        for (var i = 0; i < _history.Count; i++)
        {
            var entry = _history[i];
            var shouldDrop = entry.Kind == NavKind.DashboardModule &&
                             string.Equals(entry.ModuleId, moduleId, StringComparison.Ordinal);
            if (!shouldDrop)
            {
                if (pruned.Count == 0 || !IsSameEntry(pruned[^1], entry))
                {
                    pruned.Add(entry);
                }
            }

            if (i <= currentIndex && pruned.Count > 0)
            {
                nextIndex = pruned.Count - 1;
            }
        }

        _history.Clear();
        _history.AddRange(pruned);
        _navIndex = _history.Count == 0 ? -1 : Math.Clamp(nextIndex, 0, _history.Count - 1);
    }

    private void HandleProfilesChanged(long? affectedProfileId, bool wasDeleted)
    {
        if (CurrentScreen is ProfilesViewModel profilesVm)
        {
            profilesVm.RefreshProfiles();
            return;
        }

        if (CurrentScreen is DashboardViewModel dashboardVm && affectedProfileId == dashboardVm.ProfileId)
        {
            if (wasDeleted)
            {
                CloseSettings();
                ShowProfiles();
                return;
            }

            var updated = _database.GetProfileById(dashboardVm.ProfileId);
            if (updated is not null)
            {
                dashboardVm.RefreshProfileMetadata(updated);
            }
        }
    }

    private void PushHistory(NavEntry entry)
    {
        if (_isRestoringHistory)
        {
            return;
        }

        if (_navIndex >= 0 && _navIndex < _history.Count && IsSameEntry(_history[_navIndex], entry))
        {
            return;
        }

        if (_navIndex + 1 < _history.Count)
        {
            _history.RemoveRange(_navIndex + 1, _history.Count - (_navIndex + 1));
        }

        _history.Add(entry);
        _navIndex = _history.Count - 1;
    }

    private void RestoreHistoryEntry(NavEntry entry)
    {
        _isRestoringHistory = true;
        try
        {
            switch (entry.Kind)
            {
                case NavKind.Profiles:
                    _moduleManager.KillAll();
                    var profiles = new ProfilesViewModel(
                        _database,
                        _imageAssets,
                        _actionLog,
                        DialogService,
                        () => PickImageFileAsync is not null ? PickImageFileAsync() : Task.FromResult<string?>(null),
                        profile => OpenProfileDashboard(profile),
                        OpenSettings);
                    SwapCurrentScreen(profiles);
                    break;

                case NavKind.DashboardHub when entry.Profile is not null:
                    if (CurrentScreen is DashboardViewModel hubDash && hubDash.ProfileId == entry.Profile.Id)
                    {
                        hubDash.ShowHub();
                    }
                    else
                    {
                        OpenProfileDashboard(entry.Profile);
                    }
                    break;

                case NavKind.DashboardModule when entry.Profile is not null && !string.IsNullOrWhiteSpace(entry.ModuleId):
                    var module = _moduleManager.GetModule(entry.ModuleId);
                    if (module is null || module.State == ModuleState.Stopped)
                    {
                        if (CurrentScreen is DashboardViewModel activeDash && activeDash.ProfileId == entry.Profile.Id)
                        {
                            activeDash.ShowHub();
                        }
                        else
                        {
                            OpenProfileDashboard(entry.Profile);
                        }
                        break;
                    }

                    if (CurrentScreen is DashboardViewModel modDash && modDash.ProfileId == entry.Profile.Id)
                    {
                        modDash.FocusOrOpenModule(entry.ModuleId);
                    }
                    else
                    {
                        OpenProfileDashboard(entry.Profile, entry.ModuleId);
                    }
                    break;
            }
        }
        finally
        {
            _isRestoringHistory = false;
        }
    }

    private static bool IsSameEntry(NavEntry a, NavEntry b) =>
        a.Kind == b.Kind &&
        a.Profile?.Id == b.Profile?.Id &&
        string.Equals(a.ModuleId, b.ModuleId, StringComparison.Ordinal);

    private void SwapCurrentScreen(ObservableObject nextScreen)
    {
        var previous = CurrentScreen;
        CurrentScreen = nextScreen;
        if (previous is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
