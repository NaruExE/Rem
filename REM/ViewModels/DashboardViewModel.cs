using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using REM.Models;
using REM.Modules;
using REM.Services;

namespace REM.ViewModels;

public sealed partial class AddAppTileModel : ObservableObject
{
    private readonly Action _onOpenPicker;

    public AddAppTileModel(Action onOpenPicker)
    {
        _onOpenPicker = onOpenPicker;
    }

    [RelayCommand]
    private void OpenPicker() => _onOpenPicker();
}

public sealed partial class AppPickerItemViewModel : ObservableObject
{
    private readonly Action<AppPickerItemViewModel> _onToggle;

    public AppPickerItemViewModel(IAppModule module, Action<AppPickerItemViewModel> onToggle)
    {
        Module = module;
        _onToggle = onToggle;
        AccentBrush = new SolidColorBrush(Color.Parse(module.AccentHex));
    }

    public IAppModule Module { get; }
    public string Id => Module.Id;
    public string Name => Module.Name;
    public string Description => Module.Description;
    public Geometry IconGeometry => Module.IconGeometry;
    public IBrush AccentBrush { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectionOrder))]
    private int _selectionOrder;

    public bool HasSelectionOrder => SelectionOrder > 0;

    [RelayCommand]
    private void Toggle() => _onToggle(this);
}

public sealed partial class SidebarTabViewModel : ObservableObject, IDisposable
{
    private readonly IAppModule _module;
    private readonly Action<IAppModule> _onOpenUi;
    private readonly Action<IAppModule> _onRunHeadless;
    private readonly Action<IAppModule> _onPause;
    private readonly Action<IAppModule> _onKill;
    private bool _disposed;

    public SidebarTabViewModel(
        IAppModule module,
        bool isActiveTab,
        Action<IAppModule> onOpenUi,
        Action<IAppModule> onRunHeadless,
        Action<IAppModule> onPause,
        Action<IAppModule> onKill)
    {
        _module = module;
        _isActiveTab = isActiveTab;
        _onOpenUi = onOpenUi;
        _onRunHeadless = onRunHeadless;
        _onPause = onPause;
        _onKill = onKill;

        _module.StateChanged += OnModuleStateChanged;
        SyncState();
    }

    public string ModuleId => _module.Id;
    public string Name => _module.Name;
    public Geometry IconGeometry => _module.IconGeometry;

    [ObservableProperty]
    private bool _isActiveTab;

    [ObservableProperty]
    private bool _isCompact;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunningUi))]
    [NotifyPropertyChangedFor(nameof(IsRunningHeadless))]
    [NotifyPropertyChangedFor(nameof(IsPaused))]
    [NotifyPropertyChangedFor(nameof(StateBadgeText))]
    private ModuleState _state;

    public bool IsRunningUi => State == ModuleState.RunningUI;
    public bool IsRunningHeadless => State == ModuleState.RunningHeadless;
    public bool IsPaused => State == ModuleState.Paused;

    public string StateBadgeText => State switch
    {
        ModuleState.RunningUI => "UI",
        ModuleState.RunningHeadless => "BG",
        ModuleState.Paused => "Paused",
        _ => string.Empty
    };

    [RelayCommand]
    private void OpenUi() => _onOpenUi(_module);

    [RelayCommand]
    private void RunHeadless() => _onRunHeadless(_module);

    [RelayCommand]
    private void Pause() => _onPause(_module);

    [RelayCommand]
    private void Kill() => _onKill(_module);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _module.StateChanged -= OnModuleStateChanged;
    }

    private void OnModuleStateChanged(object? sender, ModuleState state) => SyncState();

    private void SyncState() => State = _module.State;
}

public sealed partial class ModuleTileViewModel : ObservableObject, IDisposable
{
    private readonly IAppModule _module;
    private readonly Action<IAppModule> _onOpenActionChooser;
    private readonly Action<IAppModule> _onOpenSettings;
    private bool _disposed;

    public ModuleTileViewModel(
        IAppModule module,
        Action<IAppModule> onOpenActionChooser,
        Action<IAppModule> onOpenSettings)
    {
        _module = module;
        _onOpenActionChooser = onOpenActionChooser;
        _onOpenSettings = onOpenSettings;
        AccentBrush = new SolidColorBrush(Color.Parse(module.AccentHex));

        _module.StateChanged += OnModuleStateChanged;
        _module.StatusSummaryChanged += OnStatusSummaryChanged;
        SyncFromModule();
    }

    public string ModuleId => _module.Id;
    public string Name => _module.Name;
    public string Description => _module.Description;
    public Geometry IconGeometry => _module.IconGeometry;
    public IBrush AccentBrush { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    [NotifyPropertyChangedFor(nameof(IsStopped))]
    [NotifyPropertyChangedFor(nameof(IsRunningUi))]
    [NotifyPropertyChangedFor(nameof(IsRunningHeadless))]
    [NotifyPropertyChangedFor(nameof(IsPaused))]
    private ModuleState _state;

    [ObservableProperty]
    private string _statusSummary = string.Empty;

    public string StateLabel => State switch
    {
        ModuleState.RunningUI => "Running (UI)",
        ModuleState.RunningHeadless => "Headless",
        ModuleState.Paused => "Paused",
        _ => "Stopped"
    };

    public bool IsStopped => State == ModuleState.Stopped;
    public bool IsRunningUi => State == ModuleState.RunningUI;
    public bool IsRunningHeadless => State == ModuleState.RunningHeadless;
    public bool IsPaused => State == ModuleState.Paused;

    [RelayCommand]
    private void OpenActionChooser() => _onOpenActionChooser(_module);

    [RelayCommand]
    private void OpenSettings() => _onOpenSettings(_module);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _module.StateChanged -= OnModuleStateChanged;
        _module.StatusSummaryChanged -= OnStatusSummaryChanged;
    }

    private void OnModuleStateChanged(object? sender, ModuleState state) => SyncFromModule();

    private void OnStatusSummaryChanged(object? sender, EventArgs e) => SyncFromModule();

    private void SyncFromModule()
    {
        State = _module.State;
        StatusSummary = _module.StatusSummary;
    }
}

public sealed partial class DashboardViewModel : ObservableObject, IDisposable
{
    private UserProfile _profile;
    private readonly DatabaseService _database;
    private readonly ModuleManager _moduleManager;
    private readonly ImageAssetService _imageAssets;
    private readonly ActionLogService _actionLog;
    private readonly DialogService _dialogService;
    private readonly YtDlpService _ytDlp;
    private readonly TwitchHelixService _twitch;
    private readonly SpotifySoloistService _soloist;
    private readonly SpotifyWebApiService _spotifyWebApi;
    private readonly LastFmService _lastFm;
    private readonly Action _onSwitchProfile;
    private readonly Action _onOpenSettings;
    private readonly Action _onNavigatedToHub;
    private readonly Action<string> _onNavigatedToModule;
    private readonly DispatcherTimer _ramTimer;
    private readonly Process _currentProcess;
    private readonly List<ModuleTileViewModel> _activeTiles = [];
    private readonly List<string> _pickerOrder = [];

    private IAppModule? _actionTargetModule;
    private string? _activeModuleId;
    private long _lastDownloadedBytes;
    private long _lastBandwidthTimestamp;
    private int _statsTickCount;
    private bool _lastBandwidthWasZero;
    private long _lastTooltipTotalBytes = -1;
    private string _lastWorkingSetText = string.Empty;
    private int _lastManagedTenths = -1;
    private double _lastTrimBaselineMb;
    private DateTimeOffset _lastAutoTrimUtc = DateTimeOffset.UtcNow;
    private bool _isWindowActive = true;
    private bool _disposed;

    public DashboardViewModel(
        UserProfile profile,
        DatabaseService database,
        ModuleManager moduleManager,
        ImageAssetService imageAssets,
        ActionLogService actionLog,
        DialogService dialogService,
        YtDlpService ytDlp,
        TwitchHelixService twitch,
        SpotifySoloistService soloist,
        SpotifyWebApiService spotifyWebApi,
        LastFmService lastFm,
        Action onSwitchProfile,
        Action onOpenSettings,
        Action onNavigatedToHub,
        Action<string> onNavigatedToModule)
    {
        _profile = profile;
        _database = database;
        _moduleManager = moduleManager;
        _imageAssets = imageAssets;
        _actionLog = actionLog;
        _dialogService = dialogService;
        _ytDlp = ytDlp;
        _twitch = twitch;
        _soloist = soloist;
        _spotifyWebApi = spotifyWebApi;
        _lastFm = lastFm;
        _onSwitchProfile = onSwitchProfile;
        _onOpenSettings = onOpenSettings;
        _onNavigatedToHub = onNavigatedToHub;
        _onNavigatedToModule = onNavigatedToModule;

        _currentProcess = Process.GetCurrentProcess();
        MiniPlayer = new HeadlessMiniPlayerViewModel(
            _moduleManager,
            _soloist,
            _spotifyWebApi,
            _moduleManager.AudioEngine,
            _moduleManager.VideoPlayerModule.Engine,
            moduleId =>
            {
                var target = _moduleManager.GetModule(moduleId);
                if (target is not null)
                {
                    OpenModuleUi(target);
                }
            });

        ProfileAvatarBrush = new SolidColorBrush(Color.Parse(profile.AvatarColor));
        BrandIconBitmap = _imageAssets.GetBrandImage("rem-icon.png", 64);
        CornerArtBitmap = _imageAssets.GetBrandImage("rem-corner.png", 280);
        PopupArtBitmap = _imageAssets.GetPopupImage();
        ProfileAvatarBitmap = _imageAssets.LoadScaledBitmap(profile.AvatarPath, 64);
        RefreshWallpaper();
        ProfileName = profile.Name;
        ProfileInitials = profile.Initials;

        _moduleManager.SetActiveProfile(profile.Id);
        _moduleManager.OpenModuleSettingsRequested = OpenModuleSettingsById;
        _moduleManager.ModuleStateChanged += OnAnyModuleStateChanged;

        _lastDownloadedBytes = BandwidthMonitor.GetTotalDownloadedBytes();
        _lastBandwidthTimestamp = Stopwatch.GetTimestamp();

        _ramTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _ramTimer.Tick += (_, _) =>
        {
            UpdateBandwidthStats();
            if (++_statsTickCount >= 2)
            {
                _statsTickCount = 0;
                UpdateRamStats();
            }
        };
        _ramTimer.Start();

        UpdateBandwidthStats();
        UpdateRamStats();
        LoadAssignedApps(promptIfEmpty: true);
        SyncSidebarTabs();
    }

    public long ProfileId => _profile.Id;
    internal string? ActiveModuleId => _activeModuleId;
    public IBrush ProfileAvatarBrush { get; }
    public HeadlessMiniPlayerViewModel MiniPlayer { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWallpaper))]
    private Bitmap? _wallpaperBitmap;

    public bool HasWallpaper => WallpaperBitmap is not null;

    [ObservableProperty]
    private double _wallpaperDimOpacity = 0.35;

    public void RefreshWallpaper()
    {
        if (_disposed)
        {
            return;
        }

        var settings = _database.LoadSettings();
        WallpaperDimOpacity = Math.Clamp(settings.WallpaperDim, 0.0, 0.85);
        var nextBmp = _isWindowActive
            ? _imageAssets.LoadWallpaperBitmap(settings.WallpaperPath, settings.WallpaperSoftBlur)
            : null;
        var prev = WallpaperBitmap;
        WallpaperBitmap = nextBmp;
        if (!ReferenceEquals(prev, nextBmp))
        {
            prev?.Dispose();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HubTitle))]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private string _profileInitials = string.Empty;

    public string HubTitle => $"{ProfileName}'s Hub";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBrandIcon))]
    private Bitmap? _brandIconBitmap;

    public bool HasBrandIcon => BrandIconBitmap is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCornerArt))]
    [NotifyPropertyChangedFor(nameof(ShowCornerArt))]
    private Bitmap? _cornerArtBitmap;

    public bool HasCornerArt => CornerArtBitmap is not null;
    public bool ShowCornerArt => HasCornerArt && !IsSidebarCollapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPopupArt))]
    private Bitmap? _popupArtBitmap;

    public bool HasPopupArt => PopupArtBitmap is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfileAvatar))]
    private Bitmap? _profileAvatarBitmap;

    public bool HasProfileAvatar => ProfileAvatarBitmap is not null;

    public ObservableCollection<object> DashboardCards { get; } = [];
    public ObservableCollection<SidebarTabViewModel> SidebarTabs { get; } = [];
    public ObservableCollection<AppPickerItemViewModel> AvailablePickerApps { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarWidth))]
    [NotifyPropertyChangedFor(nameof(ShowCornerArt))]
    private bool _isSidebarCollapsed;

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        foreach (var tab in SidebarTabs)
        {
            tab.IsCompact = value;
        }
    }

    public double SidebarWidth => IsSidebarCollapsed ? 64 : 236;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private bool _isAppPickerOpen;

    [ObservableProperty]
    private bool _hasAnyActiveModule;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModuleViewOpen))]
    [NotifyPropertyChangedFor(nameof(IsHubActive))]
    private Control? _activeModuleView;

    [ObservableProperty]
    private bool _isImmersiveFullscreen;

    [ObservableProperty]
    private string _workingSetText = "-- MB";

    [ObservableProperty]
    private string _workingSetTooltip = "Active RAM (Task Manager Private Working Set)";

    [ObservableProperty]
    private string _bandwidthText = "↓ 0 KB/s";

    [ObservableProperty]
    private string _bandwidthCompactText = "0K";

    [ObservableProperty]
    private string _bandwidthTooltip = "Download speed: 0 KB/s · Session: 0 B";

    [ObservableProperty]
    private bool _isActionSheetOpen;

    [ObservableProperty]
    private string _actionModuleName = string.Empty;

    [ObservableProperty]
    private string _actionModuleDescription = string.Empty;

    [ObservableProperty]
    private Geometry? _actionModuleIcon;

    [ObservableProperty]
    private IBrush? _actionModuleAccentBrush;

    [ObservableProperty]
    private string _actionModuleStateLabel = string.Empty;

    [ObservableProperty]
    private string _actionModuleSummary = string.Empty;

    [ObservableProperty]
    private bool _actionCanStart;

    [ObservableProperty]
    private bool _actionCanRunHeadless;

    [ObservableProperty]
    private bool _actionCanPause;

    [ObservableProperty]
    private bool _actionCanKill;

    [ObservableProperty]
    private string _actionStartLabel = "Start (UI)";

    [ObservableProperty]
    private bool _actionIsStopped;

    [ObservableProperty]
    private bool _actionIsRunningUi;

    [ObservableProperty]
    private bool _actionIsRunningHeadless;

    [ObservableProperty]
    private bool _actionIsPaused;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModuleSettingsOpen))]
    private ModuleSettingsViewModel? _activeModuleSettings;

    public bool IsModuleSettingsOpen => ActiveModuleSettings is not null;
    public bool IsModuleViewOpen => ActiveModuleView is not null;
    public bool IsHubActive => ActiveModuleView is null;
    public bool HasOpenModal => IsModuleSettingsOpen || IsActionSheetOpen || IsAppPickerOpen;

    public void RefreshProfileMetadata(UserProfile updatedProfile)
    {
        _profile = updatedProfile;
        ProfileName = updatedProfile.Name;
        ProfileInitials = updatedProfile.Initials;

        var oldAvatar = ProfileAvatarBitmap;
        ProfileAvatarBitmap = _imageAssets.LoadScaledBitmap(updatedProfile.AvatarPath, 64);
        oldAvatar?.Dispose();
    }

    public void RefreshBrandAssets()
    {
        BrandIconBitmap = _imageAssets.GetBrandImage("rem-icon.png", 64);
        CornerArtBitmap = _imageAssets.GetBrandImage("rem-corner.png", 280);
        PopupArtBitmap = _imageAssets.GetPopupImage();
    }

    public void ApplyHeaderBarPreference(bool showHeader)
    {
        if (ActiveModuleView?.DataContext is ModulePlaceholderViewModel placeholderVm)
        {
            placeholderVm.IsHeaderVisible = showHeader;
        }

        foreach (var module in _moduleManager.AllModules)
        {
            if (module is IHeaderConfigurableModule configurable)
            {
                configurable.SetHeaderVisibility(showHeader);
            }
        }
    }

    public bool TryCloseModal()
    {
        if (IsModuleSettingsOpen)
        {
            CloseModuleSettings();
            return true;
        }

        if (IsActionSheetOpen)
        {
            CloseActionSheet();
            return true;
        }

        if (IsAppPickerOpen)
        {
            CancelAppPicker();
            return true;
        }

        return false;
    }

    private void OpenModuleSettingsById(string moduleId)
    {
        var module = _moduleManager.GetModule(moduleId);
        if (module is not null)
        {
            OpenModuleSettings(module);
        }
    }

    private void OpenModuleSettings(IAppModule module)
    {
        CloseModuleSettings();
        ActiveModuleSettings = new ModuleSettingsViewModel(
            module,
            _profile.Id,
            _database,
            _actionLog,
            _dialogService,
            _ytDlp,
            _twitch,
            _soloist,
            _spotifyWebApi,
            _lastFm,
            id => _moduleManager.NotifyModuleSettingsChanged(id),
            async target =>
            {
                var removed = await RemoveModuleFromProfileAsync(target);
                if (removed)
                {
                    CloseModuleSettings();
                }
            },
            CloseModuleSettings);
    }

    private void CloseModuleSettings()
    {
        if (ActiveModuleSettings is null)
        {
            return;
        }

        ActiveModuleSettings.Dispose();
        ActiveModuleSettings = null;
        ScheduleTrimAndRefreshStats();
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    [RelayCommand]
    public void ShowHub()
    {
        if (ActiveModuleView is null && _activeModuleId is null)
        {
            return;
        }

        _moduleManager.NotifyModuleViewLeft(_activeModuleId);
        IsImmersiveFullscreen = false;
        ActiveModuleView = null;
        _activeModuleId = null;
        UpdateSidebarActiveTab();
        _onNavigatedToHub();
    }

    public void FocusOrOpenModule(string moduleId)
    {
        var module = _moduleManager.GetModule(moduleId);
        if (module is not null)
        {
            OpenModuleUi(module);
        }
    }

    [RelayCommand]
    private void OpenSettings() => _onOpenSettings();

    [RelayCommand]
    private void OpenAppPicker()
    {
        _pickerOrder.Clear();
        _pickerOrder.AddRange(_database.GetProfileModuleIds(_profile.Id));

        AvailablePickerApps.Clear();
        foreach (var module in _moduleManager.AllModules)
        {
            AvailablePickerApps.Add(new AppPickerItemViewModel(module, HandlePickerToggle));
        }

        SyncPickerBadges();
        IsAppPickerOpen = true;
    }

    [RelayCommand]
    private void CancelAppPicker()
    {
        IsAppPickerOpen = false;
    }

    [RelayCommand]
    private void ConfirmAppPicker()
    {
        var selectedIds = _pickerOrder.ToList();

        var previousIds = _database.GetProfileModuleIds(_profile.Id);
        foreach (var removedId in previousIds.Except(selectedIds, StringComparer.Ordinal))
        {
            CloseActiveViewIfFor(removedId);
            _moduleManager.KillModule(removedId);
        }

        _database.SetProfileModuleIds(_profile.Id, selectedIds);
        _actionLog.Log("Profile", $"Updated apps for '{_profile.Name}': [{string.Join(", ", selectedIds)}].");
        IsAppPickerOpen = false;
        LoadAssignedApps(promptIfEmpty: false);
        SyncSidebarTabs();
    }

    private void HandlePickerToggle(AppPickerItemViewModel item)
    {
        var existingIndex = _pickerOrder.FindIndex(id => string.Equals(id, item.Id, StringComparison.Ordinal));
        if (existingIndex >= 0)
        {
            _pickerOrder.RemoveAt(existingIndex);
        }
        else
        {
            _pickerOrder.Add(item.Id);
        }

        SyncPickerBadges();
    }

    private void SyncPickerBadges()
    {
        foreach (var item in AvailablePickerApps)
        {
            var idx = _pickerOrder.FindIndex(id => string.Equals(id, item.Id, StringComparison.Ordinal));
            item.IsSelected = idx >= 0;
            item.SelectionOrder = idx >= 0 ? idx + 1 : 0;
        }
    }

    [RelayCommand]
    private void CloseActionSheet()
    {
        IsActionSheetOpen = false;
        _actionTargetModule = null;
    }

    [RelayCommand]
    private void ActionStartUi()
    {
        var target = _actionTargetModule;
        CloseActionSheet();
        if (target is not null)
        {
            OpenModuleUi(target);
        }
    }

    [RelayCommand]
    private void ActionRunHeadless()
    {
        var target = _actionTargetModule;
        CloseActionSheet();
        if (target is not null)
        {
            RunModuleHeadless(target);
        }
    }

    [RelayCommand]
    private void ActionPause()
    {
        var target = _actionTargetModule;
        CloseActionSheet();
        if (target is not null)
        {
            PauseModule(target);
        }
    }

    [RelayCommand]
    private void ActionKill()
    {
        var target = _actionTargetModule;
        CloseActionSheet();
        if (target is not null)
        {
            KillModule(target);
        }
    }

    [RelayCommand]
    private void SwitchProfile()
    {
        ActiveModuleView = null;
        _activeModuleId = null;
        _moduleManager.KillAll();
        _actionLog.Log("Profile", $"Switched away from profile '{_profile.Name}'.");
        _onSwitchProfile();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ramTimer.Stop();
        _moduleManager.OpenModuleSettingsRequested = null;
        _moduleManager.ModuleStateChanged -= OnAnyModuleStateChanged;
        CloseModuleSettings();
        ActiveModuleView = null;
        _activeModuleId = null;
        MiniPlayer.Dispose();
        DisposeTiles();
        DisposeSidebarTabs();

        WallpaperBitmap?.Dispose();
        WallpaperBitmap = null;
        ProfileAvatarBitmap?.Dispose();
        ProfileAvatarBitmap = null;
        _currentProcess.Dispose();
    }

    partial void OnActiveModuleViewChanged(Control? value)
    {
        MiniPlayer?.SetActiveOpenModuleId(value is null ? null : _activeModuleId);
    }

    private void LoadAssignedApps(bool promptIfEmpty)
    {
        DisposeTiles();
        DashboardCards.Clear();

        var assignedIds = _database.GetProfileModuleIds(_profile.Id);
        foreach (var moduleId in assignedIds)
        {
            var module = _moduleManager.GetModule(moduleId);
            if (module is null)
            {
                continue;
            }

            var tile = new ModuleTileViewModel(
                module,
                OpenActionChooserFor,
                OpenModuleSettings);

            _activeTiles.Add(tile);
            DashboardCards.Add(tile);
        }

        IsEmpty = _activeTiles.Count == 0;
        DashboardCards.Add(new AddAppTileModel(OpenAppPicker));
        RefreshActiveModuleFlag();

        if (IsEmpty && promptIfEmpty)
        {
            OpenAppPicker();
        }
    }

    private void OpenActionChooserFor(IAppModule module)
    {
        _actionTargetModule = module;
        ActionModuleName = module.Name;
        ActionModuleDescription = module.Description;
        ActionModuleIcon = module.IconGeometry;
        ActionModuleAccentBrush = new SolidColorBrush(Color.Parse(module.AccentHex));
        ActionModuleSummary = module.StatusSummary;

        var state = module.State;
        ActionIsStopped = state == ModuleState.Stopped;
        ActionIsRunningUi = state == ModuleState.RunningUI;
        ActionIsRunningHeadless = state == ModuleState.RunningHeadless;
        ActionIsPaused = state == ModuleState.Paused;

        ActionModuleStateLabel = state switch
        {
            ModuleState.RunningUI => "Running (UI)",
            ModuleState.RunningHeadless => "Running (Headless)",
            ModuleState.Paused => "Paused",
            _ => "Stopped"
        };

        ActionCanStart = true;
        ActionStartLabel = state switch
        {
            ModuleState.RunningUI => "Focus UI",
            ModuleState.RunningHeadless => "Open UI",
            ModuleState.Paused => "Resume UI",
            _ => "Start (UI)"
        };
        ActionCanRunHeadless = state != ModuleState.RunningHeadless;
        ActionCanPause = state == ModuleState.RunningUI || state == ModuleState.RunningHeadless;
        ActionCanKill = state != ModuleState.Stopped;

        IsActionSheetOpen = true;
    }

    private void OpenModuleUi(IAppModule module)
    {
        try
        {
            if (_activeModuleId is not null && !string.Equals(_activeModuleId, module.Id, StringComparison.Ordinal))
            {
                _moduleManager.NotifyModuleViewLeft(_activeModuleId);
            }

            var view = _moduleManager.OpenModuleUI(module.Id);
            var showHeader = _database.LoadSettings().ShowAppHeaderBar;

            if (view.DataContext is ModulePlaceholderViewModel placeholderVm)
            {
                placeholderVm.RequestCloseToDashboard = ShowHub;
                placeholderVm.IsHeaderVisible = showHeader;
            }

            if (module is ICloseableModule closeable)
            {
                closeable.AttachCloseCallback(ShowHub);
            }

            if (module is IFullscreenModule fullscreenable)
            {
                fullscreenable.AttachFullscreenCallback(isFullscreen => IsImmersiveFullscreen = isFullscreen);
            }

            if (module is IHeaderConfigurableModule headerConfigurable)
            {
                headerConfigurable.SetHeaderVisibility(showHeader);
            }

            if (module is IVlcPlayMediaModule vlcPlayRequester)
            {
                vlcPlayRequester.PlayCompletedMediaInVlcRequested = filePath =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var vlcModule = _moduleManager.VideoPlayerModule;
                        OpenModuleUi(vlcModule);
                        _ = vlcModule.PlayFileInUiAsync(filePath);
                    });
                };
            }

            if (module is IVlcOpenUiModule vlcOpenRequester)
            {
                vlcOpenRequester.OpenVlcUiRequested = () =>
                {
                    Dispatcher.UIThread.Post(() => OpenModuleUi(_moduleManager.VideoPlayerModule));
                };
            }

            _activeModuleId = module.Id;
            ActiveModuleView = view;
            SyncSidebarTabs();
            RefreshActiveModuleFlag();
            UpdateRamStats();
            _actionLog.Log("Module", $"'{module.Name}' -> Running (UI).");
            _onNavigatedToModule(module.Id);
        }
        catch (Exception ex)
        {
            _actionLog.Log("Error", $"Failed to open '{module.Name}': {ex.Message}");
            _ = _dialogService.ShowErrorAsync("Module Error", $"Could not open '{module.Name}'.", ex.Message);
        }
    }

    private void RunModuleHeadless(IAppModule module)
    {
        try
        {
            if (module is VideoPlayerModule videoModule &&
                !string.IsNullOrWhiteSpace(videoModule.Engine.CurrentSourceUrl))
            {
                _ = RunVideoHeadlessWithPromptAsync(videoModule);
                return;
            }

            if (module is StreamWatcherModule streamWatcherModule &&
                !string.IsNullOrWhiteSpace(streamWatcherModule.Engine.CurrentSourceUrl))
            {
                _ = RunStreamWatcherHeadlessWithPromptAsync(streamWatcherModule);
                return;
            }

            var wasOpen = string.Equals(_activeModuleId, module.Id, StringComparison.Ordinal);
            CloseActiveViewIfFor(module.Id);
            _moduleManager.RunModuleHeadless(module.Id);
            SyncSidebarTabs();
            RefreshActiveModuleFlag();
            UpdateRamStats();
            ScheduleTrimAndRefreshStats();
            _actionLog.Log("Module", $"'{module.Name}' -> Running (Headless).");
            if (wasOpen)
            {
                _onNavigatedToHub();
            }
        }
        catch (Exception ex)
        {
            _actionLog.Log("Error", $"Failed to run '{module.Name}' headless: {ex.Message}");
            _ = _dialogService.ShowErrorAsync("Module Error", $"Could not switch '{module.Name}' to Headless mode.", ex.Message);
        }
    }

    private async Task RunVideoHeadlessWithPromptAsync(VideoPlayerModule videoModule)
    {
        var wasOpen = string.Equals(_activeModuleId, videoModule.Id, StringComparison.Ordinal);
        var confirmed = await videoModule.PromptAndRunHeadlessAsync();
        if (!confirmed || _disposed)
        {
            return;
        }

        CloseActiveViewIfFor(videoModule.Id);
        SyncSidebarTabs();
        RefreshActiveModuleFlag();
        UpdateRamStats();
        ScheduleTrimAndRefreshStats();
        _actionLog.Log("Module", $"'{videoModule.Name}' -> Running (Headless).");
        if (wasOpen)
        {
            _onNavigatedToHub();
        }
    }

    private async Task RunStreamWatcherHeadlessWithPromptAsync(StreamWatcherModule streamWatcherModule)
    {
        var wasOpen = string.Equals(_activeModuleId, streamWatcherModule.Id, StringComparison.Ordinal);
        var confirmed = await streamWatcherModule.PromptAndRunHeadlessAsync();
        if (!confirmed || _disposed)
        {
            return;
        }

        CloseActiveViewIfFor(streamWatcherModule.Id);
        SyncSidebarTabs();
        RefreshActiveModuleFlag();
        UpdateRamStats();
        ScheduleTrimAndRefreshStats();
        _actionLog.Log("Module", $"'{streamWatcherModule.Name}' -> Running (Headless).");
        if (wasOpen)
        {
            _onNavigatedToHub();
        }
    }

    private void PauseModule(IAppModule module)
    {
        try
        {
            var wasOpen = string.Equals(_activeModuleId, module.Id, StringComparison.Ordinal);
            CloseActiveViewIfFor(module.Id);
            _moduleManager.PauseModule(module.Id);
            SyncSidebarTabs();
            RefreshActiveModuleFlag();
            UpdateRamStats();
            ScheduleTrimAndRefreshStats();
            _actionLog.Log("Module", $"'{module.Name}' -> Paused.");
            if (wasOpen)
            {
                _onNavigatedToHub();
            }
        }
        catch (Exception ex)
        {
            _actionLog.Log("Error", $"Failed to pause '{module.Name}': {ex.Message}");
        }
    }

    private void KillModule(IAppModule module)
    {
        try
        {
            var wasOpen = string.Equals(_activeModuleId, module.Id, StringComparison.Ordinal);
            CloseActiveViewIfFor(module.Id);
            _moduleManager.KillModule(module.Id);
            SyncSidebarTabs();
            RefreshActiveModuleFlag();
            UpdateRamStats();
            ScheduleTrimAndRefreshStats();
            _actionLog.Log("Module", $"'{module.Name}' -> Killed (Stopped).");
            if (wasOpen)
            {
                _onNavigatedToHub();
            }
        }
        catch (Exception ex)
        {
            _actionLog.Log("Error", $"Failed to kill '{module.Name}': {ex.Message}");
        }
    }

    private async Task<bool> RemoveModuleFromProfileAsync(IAppModule module)
    {
        if (module.State != ModuleState.Stopped)
        {
            var confirmed = await _dialogService.ShowConfirmAsync(
                "Remove Active App?",
                $"{module.Name} is currently active. Stop it and remove it from '{_profile.Name}'?",
                "Stop & Remove",
                isDanger: true);
            if (!confirmed)
            {
                return false;
            }
        }

        var wasOpen = string.Equals(_activeModuleId, module.Id, StringComparison.Ordinal);
        CloseActiveViewIfFor(module.Id);
        _moduleManager.KillModule(module.Id);
        _database.RemoveProfileModule(_profile.Id, module.Id);
        _actionLog.Log("Profile", $"Removed '{module.Name}' from '{_profile.Name}'.");
        LoadAssignedApps(promptIfEmpty: false);
        SyncSidebarTabs();
        if (wasOpen)
        {
            _onNavigatedToHub();
        }

        return true;
    }

    private void CloseActiveViewIfFor(string moduleId)
    {
        if (string.Equals(_activeModuleId, moduleId, StringComparison.Ordinal))
        {
            _moduleManager.NotifyModuleViewLeft(moduleId);
            IsImmersiveFullscreen = false;
            ActiveModuleView = null;
            _activeModuleId = null;
        }
    }

    private void OnAnyModuleStateChanged(object? sender, (string ModuleId, ModuleState State) args)
    {
        RefreshActiveModuleFlag();
        if (args.State != ModuleState.RunningUI)
        {
            CloseActiveViewIfFor(args.ModuleId);
            ScheduleTrimAndRefreshStats();
        }

        SyncSidebarTabs();
    }

    private void SyncSidebarTabs()
    {
        for (var i = SidebarTabs.Count - 1; i >= 0; i--)
        {
            var tab = SidebarTabs[i];
            var mod = _moduleManager.GetModule(tab.ModuleId);
            if (mod is null || mod.State == ModuleState.Stopped)
            {
                SidebarTabs.RemoveAt(i);
                tab.Dispose();
            }
        }

        foreach (var module in _moduleManager.AllModules)
        {
            if (module.State == ModuleState.Stopped)
            {
                continue;
            }

            var existing = SidebarTabs.FirstOrDefault(t => string.Equals(t.ModuleId, module.Id, StringComparison.Ordinal));
            var isActive = string.Equals(_activeModuleId, module.Id, StringComparison.Ordinal);
            if (existing is not null)
            {
                existing.IsActiveTab = isActive;
                existing.IsCompact = IsSidebarCollapsed;
            }
            else
            {
                SidebarTabs.Add(new SidebarTabViewModel(
                    module,
                    isActive,
                    OpenModuleUi,
                    RunModuleHeadless,
                    PauseModule,
                    KillModule)
                {
                    IsCompact = IsSidebarCollapsed
                });
            }
        }
    }

    private void UpdateSidebarActiveTab()
    {
        foreach (var tab in SidebarTabs)
        {
            tab.IsActiveTab = string.Equals(_activeModuleId, tab.ModuleId, StringComparison.Ordinal);
        }
    }

    private void RefreshActiveModuleFlag()
    {
        HasAnyActiveModule = _moduleManager.AllModules.Any(m => m.State != ModuleState.Stopped);
    }

    public void SetWindowActive(bool isVisibleAndNotMinimized)
    {
        if (_disposed || _isWindowActive == isVisibleAndNotMinimized)
        {
            return;
        }

        _isWindowActive = isVisibleAndNotMinimized;
        if (!isVisibleAndNotMinimized)
        {
            _ramTimer.Stop();
            var prevWp = WallpaperBitmap;
            WallpaperBitmap = null;
            prevWp?.Dispose();
            ScheduleTrimAndRefreshStats();
        }
        else
        {
            RefreshWallpaper();
            UpdateBandwidthStats();
            UpdateRamStats();
            if (!_ramTimer.IsEnabled)
            {
                _ramTimer.Start();
            }
        }
    }

    private void ScheduleTrimAndRefreshStats()
    {
        MemoryTrimService.ScheduleTrim(() => Dispatcher.UIThread.Post(() =>
        {
            _lastTrimBaselineMb = 0;
            UpdateRamStats();
        }));
    }

    private void UpdateBandwidthStats()
    {
        if (_disposed)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var totalBytes = BandwidthMonitor.GetTotalDownloadedBytes();
        var elapsedSeconds = (_lastBandwidthTimestamp > 0)
            ? (double)(now - _lastBandwidthTimestamp) / Stopwatch.Frequency
            : 1.0;

        var deltaBytes = Math.Max(0, totalBytes - _lastDownloadedBytes);
        var rate = elapsedSeconds > 0.05 ? deltaBytes / elapsedSeconds : 0.0;

        _lastDownloadedBytes = totalBytes;
        _lastBandwidthTimestamp = now;

        if (deltaBytes == 0 && _lastBandwidthWasZero && totalBytes == _lastTooltipTotalBytes)
        {
            return;
        }

        _lastBandwidthWasZero = rate < 1;
        _lastTooltipTotalBytes = totalBytes;

        BandwidthText = $"↓ {BandwidthMonitor.FormatRate(rate)}";
        BandwidthCompactText = BandwidthMonitor.FormatCompactRate(rate);
        BandwidthTooltip = $"Download speed: {BandwidthMonitor.FormatRate(rate)} · {BandwidthMonitor.FormatTotal(totalBytes)}";
    }

    private void UpdateRamStats()
    {
        if (_disposed)
        {
            return;
        }

        var (remActiveMb, remCommitMb, managedMb) = MemoryTrimService.ReadMemoryMetrics(_currentProcess);
        var (childActiveMb, childCommitMb, childBreakdown) = MemoryTrimService.ReadChildProcessMetrics();
        var totalActiveMb = remActiveMb + childActiveMb;
        var totalCommitMb = remCommitMb + childCommitMb;

        var activeText = $"{totalActiveMb:F1} MB";
        var managedTenths = (int)Math.Round(managedMb * 10);

        if (!string.Equals(_lastWorkingSetText, activeText, StringComparison.Ordinal) ||
            managedTenths != _lastManagedTenths)
        {
            _lastWorkingSetText = activeText;
            _lastManagedTenths = managedTenths;
            WorkingSetText = activeText;
            WorkingSetTooltip = string.IsNullOrWhiteSpace(childBreakdown)
                ? $"Total Active RAM: {totalActiveMb:F1} MB (REM only) · Managed Heap: {managedMb:F1} MB · Private Commit: {totalCommitMb:F1} MB"
                : $"Total Active RAM: {totalActiveMb:F1} MB (REM: {remActiveMb:F1} MB · {childBreakdown}) · Managed Heap: {managedMb:F1} MB · Total Commit: {totalCommitMb:F1} MB";
        }

        if (_lastTrimBaselineMb <= 0 || remActiveMb < _lastTrimBaselineMb)
        {
            _lastTrimBaselineMb = remActiveMb;
        }
        else if (ActiveModuleView is null &&
                 remActiveMb >= _lastTrimBaselineMb + 14.0 &&
                 (DateTimeOffset.UtcNow - _lastAutoTrimUtc) >= TimeSpan.FromSeconds(90))
        {
            _lastAutoTrimUtc = DateTimeOffset.UtcNow;
            MemoryTrimService.ScheduleTrim(() => Dispatcher.UIThread.Post(() =>
            {
                _lastTrimBaselineMb = 0;
                UpdateRamStats();
            }), trimOsWorkingSet: false);
        }
    }

    private void DisposeTiles()
    {
        foreach (var tile in _activeTiles)
        {
            tile.Dispose();
        }

        _activeTiles.Clear();
    }

    private void DisposeSidebarTabs()
    {
        foreach (var tab in SidebarTabs)
        {
            tab.Dispose();
        }

        SidebarTabs.Clear();
    }
}
