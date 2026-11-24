using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
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
    private readonly YtDlpService _ytDlp;
    private readonly TwitchHelixService _twitch;
    private readonly SpotifySoloistService _soloist;
    private readonly SpotifyWebApiService _spotifyWebApi;
    private readonly LastFmService _lastFm;

    private readonly List<NavEntry> _history = [];
    private int _navIndex = -1;
    private bool _isRestoringHistory;

    public MainWindowViewModel(
        DatabaseService database,
        ThemeService themeService,
        AudioService audioService,
        ModuleManager moduleManager,
        ImageAssetService imageAssets,
        ActionLogService actionLog,
        DialogService dialogService,
        StartupService startupService,
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
        _ytDlp = ytDlp;
        _twitch = twitch;
        _soloist = soloist;
        _spotifyWebApi = spotifyWebApi;
        _lastFm = lastFm;
        DialogService = dialogService;

        TitleBarIconBitmap = _imageAssets.GetBrandImage("rem-icon.png", 32);
        _moduleManager.ModuleStateChanged += OnModuleStateChanged;
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

    public Func<Task<string?>>? PickAudioFileAsync { get; set; }
    public Func<Task<string?>>? PickImageFileAsync { get; set; }
    public Action? RefreshShellIcons { get; set; }

    public bool ShouldMinimizeToTray() => _database.LoadSettings().MinimizeToTray;

    public void LogMinimizedToTray() =>
        _actionLog.Log("System", "Window hidden to system tray (background apps remain active).");

    public async Task StartAsync()
    {
        var settings = _database.LoadSettings();
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
            () => PickAudioFileAsync is not null ? PickAudioFileAsync() : Task.FromResult<string?>(null),
            () => PickImageFileAsync is not null ? PickImageFileAsync() : Task.FromResult<string?>(null),
            HandleProfilesChanged,
            ApplyHeaderBarPreference,
            ReloadAssets,
            CloseSettings);
        _actionLog.Log("Navigation", "Opened Settings.");
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
