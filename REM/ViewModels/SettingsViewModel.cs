using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using REM.Models;
using REM.Services;

namespace REM.ViewModels;

public enum SettingsPage
{
    General,
    Appearance,
    Profiles,
    Assets
}

public sealed partial class SettingsProfileRowViewModel : ObservableObject, IDisposable
{
    private readonly ImageAssetService _imageAssets;
    private readonly Action<SettingsProfileRowViewModel> _onEdit;
    private IBrush? _avatarBrush;
    private bool _disposed;

    public SettingsProfileRowViewModel(
        UserProfile profile,
        ImageAssetService imageAssets,
        Action<SettingsProfileRowViewModel> onEdit)
    {
        Profile = profile;
        _imageAssets = imageAssets;
        _onEdit = onEdit;
        ReloadAvatar();
    }

    public UserProfile Profile { get; }
    public string Name => Profile.Name;
    public string Initials => Profile.Initials;
    public IBrush AvatarBrush => _avatarBrush ??= new SolidColorBrush(Color.Parse(Profile.AvatarColor));
    public bool HasPassword => Profile.HasPassword;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvatar))]
    private Bitmap? _avatarBitmap;

    public bool HasAvatar => AvatarBitmap is not null;

    public void ReloadAvatar()
    {
        var previous = AvatarBitmap;
        AvatarBitmap = _imageAssets.LoadScaledBitmap(Profile.AvatarPath, 96);
        previous?.Dispose();
    }

    [RelayCommand]
    private void Edit() => _onEdit(this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AvatarBitmap?.Dispose();
        AvatarBitmap = null;
    }
}

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly DatabaseService _database;
    private readonly ThemeService _themeService;
    private readonly AudioService _audioService;
    private readonly ImageAssetService _imageAssets;
    private readonly ActionLogService _actionLog;
    private readonly DialogService _dialogService;
    private readonly StartupService _startupService;
    private readonly Func<Task<string?>> _pickAudioFileAsync;
    private readonly Func<Task<string?>> _pickImageFileAsync;
    private readonly Action<long?, bool> _onProfilesChanged;
    private readonly Action<bool> _onHeaderBarPreferenceChanged;
    private readonly Action _onReloadAssets;
    private readonly Action _onWallpaperChanged;
    private readonly Action _onClose;

    private CancellationTokenSource? _previewCts;
    private UserProfile? _editingProfile;
    private bool _suppressSave;
    private bool _disposed;

    public SettingsViewModel(
        DatabaseService database,
        ThemeService themeService,
        AudioService audioService,
        ImageAssetService imageAssets,
        ActionLogService actionLog,
        DialogService dialogService,
        StartupService startupService,
        Func<Task<string?>> pickAudioFileAsync,
        Func<Task<string?>> pickImageFileAsync,
        Action<long?, bool> onProfilesChanged,
        Action<bool> onHeaderBarPreferenceChanged,
        Action onReloadAssets,
        Action onWallpaperChanged,
        Action onClose)
    {
        _database = database;
        _themeService = themeService;
        _audioService = audioService;
        _imageAssets = imageAssets;
        _actionLog = actionLog;
        _dialogService = dialogService;
        _startupService = startupService;
        _pickAudioFileAsync = pickAudioFileAsync;
        _pickImageFileAsync = pickImageFileAsync;
        _onProfilesChanged = onProfilesChanged;
        _onHeaderBarPreferenceChanged = onHeaderBarPreferenceChanged;
        _onReloadAssets = onReloadAssets;
        _onWallpaperChanged = onWallpaperChanged;
        _onClose = onClose;

        var settings = _database.LoadSettings();
        _suppressSave = true;
        SkipIntro = settings.SkipIntro;
        ShowAppHeaderBar = settings.ShowAppHeaderBar;
        MinimizeToTray = settings.MinimizeToTray;
        StartWithWindows = _startupService.IsEnabled();
        WelcomeAudioPath = settings.WelcomeAudioPath;
        WallpaperPath = settings.WallpaperPath;
        HasWallpaper = !string.IsNullOrWhiteSpace(settings.WallpaperPath) && File.Exists(settings.WallpaperPath);
        WallpaperDisplayName = HasWallpaper
            ? Path.GetFileName(settings.WallpaperPath)
            : (string.IsNullOrWhiteSpace(settings.WallpaperPath) ? "None (Solid theme background)" : $"Missing: {Path.GetFileName(settings.WallpaperPath)}");
        WallpaperDimPercent = Math.Round(settings.WallpaperDim * 100.0);
        WallpaperSoftBlur = settings.WallpaperSoftBlur;
        ModuleOpacityPercent = Math.Round(settings.ModuleOpacity * 100.0);
        ReloadThemesCore(settings.ThemeName);
        _suppressSave = false;

        RefreshAudioDisplay();
        ReloadProfilesList();
    }

    [ObservableProperty]
    private string _wallpaperPath = string.Empty;

    [ObservableProperty]
    private string _wallpaperDisplayName = "None (Solid theme background)";

    [ObservableProperty]
    private bool _hasWallpaper;

    [ObservableProperty]
    private double _wallpaperDimPercent = 35;

    [ObservableProperty]
    private bool _wallpaperSoftBlur;

    [ObservableProperty]
    private double _moduleOpacityPercent = 82;

    public ObservableCollection<ThemeDescriptor> AvailableThemes { get; } = [];
    public ObservableCollection<SettingsProfileRowViewModel> ProfilesList { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneralPage))]
    [NotifyPropertyChangedFor(nameof(IsAppearancePage))]
    [NotifyPropertyChangedFor(nameof(IsProfilesPage))]
    [NotifyPropertyChangedFor(nameof(IsAssetsPage))]
    private SettingsPage _currentPage = SettingsPage.General;

    public bool IsGeneralPage => CurrentPage == SettingsPage.General;
    public bool IsAppearancePage => CurrentPage == SettingsPage.Appearance;
    public bool IsProfilesPage => CurrentPage == SettingsPage.Profiles;
    public bool IsAssetsPage => CurrentPage == SettingsPage.Assets;

    [ObservableProperty]
    private bool _skipIntro;

    [ObservableProperty]
    private bool _showAppHeaderBar;

    [ObservableProperty]
    private bool _minimizeToTray = true;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private ThemeDescriptor? _selectedTheme;

    [ObservableProperty]
    private string _welcomeAudioPath = string.Empty;

    [ObservableProperty]
    private string _welcomeAudioDisplay = string.Empty;

    [ObservableProperty]
    private bool _hasCustomAudio;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewButtonText))]
    private bool _isPreviewPlaying;

    [ObservableProperty]
    private bool _hasAnyProfiles;

    [ObservableProperty]
    private bool _isEditingProfile;

    [ObservableProperty]
    private string _editingProfileName = string.Empty;

    [ObservableProperty]
    private string _editingProfileInitials = string.Empty;

    [ObservableProperty]
    private IBrush? _editingProfileAvatarBrush;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditingProfileAvatar))]
    private Bitmap? _editingProfileAvatarBitmap;

    public bool HasEditingProfileAvatar => EditingProfileAvatarBitmap is not null;

    [ObservableProperty]
    private bool _editingProfileHasPassword;

    [ObservableProperty]
    private string _editingProfileCurrentPin = string.Empty;

    [ObservableProperty]
    private string _editingProfileNewPin = string.Empty;

    [ObservableProperty]
    private bool _isDeleteConfirming;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfileFeedback))]
    private string _profileFeedbackMessage = string.Empty;

    [ObservableProperty]
    private bool _isProfileFeedbackError;

    public bool HasProfileFeedback => !string.IsNullOrEmpty(ProfileFeedbackMessage);

    public string PreviewButtonText => IsPreviewPlaying ? "Stop Preview" : "Preview Audio";
    public string ExternalThemesDirectory => _themeService.ExternalThemesDirectory;
    public string CustomImagesDirectory => _imageAssets.ActiveImagesDirectory;
    public string LogFilePath => _actionLog.LogFilePath;

    partial void OnSkipIntroChanged(bool value)
    {
        if (_suppressSave)
        {
            return;
        }

        _database.SaveSetting(DatabaseService.KeySkipIntro, value.ToString());
        _actionLog.Log("Settings", $"SkipIntro set to {value}.");
    }

    partial void OnShowAppHeaderBarChanged(bool value)
    {
        if (_suppressSave)
        {
            return;
        }

        _database.SaveSetting(DatabaseService.KeyShowAppHeaderBar, value.ToString());
        _onHeaderBarPreferenceChanged(value);
        _actionLog.Log("Settings", $"ShowAppHeaderBar set to {value}.");
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        if (_suppressSave)
        {
            return;
        }

        _database.SaveSetting(DatabaseService.KeyMinimizeToTray, value.ToString());
        _actionLog.Log("Settings", $"MinimizeToTray set to {value}.");
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_suppressSave)
        {
            return;
        }

        if (_startupService.SetEnabled(value, out var errorMessage))
        {
            _actionLog.Log("Settings", $"StartWithWindows set to {value}.");
            return;
        }

        _suppressSave = true;
        StartWithWindows = !value;
        _suppressSave = false;

        _ = _dialogService.ShowErrorAsync(
            "Startup Setting Failed",
            "Could not update the Windows startup registry entry.",
            errorMessage);
    }

    partial void OnSelectedThemeChanged(ThemeDescriptor? value)
    {
        if (_suppressSave || value is null)
        {
            return;
        }

        if (_themeService.ApplyTheme(value.Name, out var errorMessage))
        {
            _database.SaveSetting(DatabaseService.KeyThemeName, value.Name);
            _actionLog.Log("Theme", $"Switched theme to '{value.DisplayName}'.");
        }
        else
        {
            _ = _dialogService.ShowWarningAsync(
                "Theme Mod Error",
                $"Could not apply '{value.DisplayName}'. Fell back to a working theme.",
                errorMessage);
        }
    }

    [RelayCommand]
    private void SelectGeneralPage() => CurrentPage = SettingsPage.General;

    [RelayCommand]
    private void SelectAppearancePage() => CurrentPage = SettingsPage.Appearance;

    [RelayCommand]
    private void SelectProfilesPage()
    {
        CurrentPage = SettingsPage.Profiles;
        if (!IsEditingProfile)
        {
            ReloadProfilesList();
        }
    }

    [RelayCommand]
    private void SelectAssetsPage() => CurrentPage = SettingsPage.Assets;

    [RelayCommand]
    private void ReloadThemes()
    {
        var currentName = SelectedTheme?.Name ?? _themeService.CurrentThemeName;
        _suppressSave = true;
        ReloadThemesCore(currentName);
        _suppressSave = false;
        _actionLog.Log("Theme", "Reloaded theme list.");
    }

    [RelayCommand]
    private Task OpenThemesFolderAsync() => OpenFolderInExplorerAsync(_themeService.ExternalThemesDirectory);

    partial void OnWallpaperDimPercentChanged(double value)
    {
        if (_suppressSave || _disposed) return;
        var norm = Math.Clamp(value, 0, 85) / 100.0;
        _database.SaveSetting(DatabaseService.KeyWallpaperDim, norm.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        _onWallpaperChanged();
    }

    partial void OnWallpaperSoftBlurChanged(bool value)
    {
        if (_suppressSave || _disposed) return;
        _database.SaveSetting(DatabaseService.KeyWallpaperSoftBlur, value.ToString());
        _onWallpaperChanged();
    }

    partial void OnModuleOpacityPercentChanged(double value)
    {
        if (_suppressSave || _disposed) return;
        var norm = Math.Clamp(value, 25, 100) / 100.0;
        _database.SaveSetting(DatabaseService.KeyModuleOpacity, norm.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        _themeService.ApplyModuleTransparency(norm, HasWallpaper);
    }

    [RelayCommand]
    private async Task PickWallpaperAsync()
    {
        var path = await _pickImageFileAsync();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || _disposed)
        {
            return;
        }

        WallpaperPath = path;
        HasWallpaper = true;
        WallpaperDisplayName = Path.GetFileName(path);
        _database.SaveSetting(DatabaseService.KeyWallpaperPath, path);
        _themeService.ApplyModuleTransparency(Math.Clamp(ModuleOpacityPercent, 25, 100) / 100.0, hasWallpaper: true);
        _onWallpaperChanged();
        _actionLog.Log("Theme", $"Set dashboard wallpaper to '{WallpaperDisplayName}'.");
    }

    [RelayCommand]
    private void ClearWallpaper()
    {
        WallpaperPath = string.Empty;
        HasWallpaper = false;
        WallpaperDisplayName = "None (Solid theme background)";
        _database.SaveSetting(DatabaseService.KeyWallpaperPath, string.Empty);
        _themeService.ApplyModuleTransparency(1.0, hasWallpaper: false);
        _onWallpaperChanged();
        _actionLog.Log("Theme", "Cleared dashboard wallpaper.");
    }

    [RelayCommand]
    private Task OpenImagesFolderAsync() => OpenFolderInExplorerAsync(_imageAssets.ActiveImagesDirectory);

    [RelayCommand]
    private void ReloadImages() => _onReloadAssets();

    private async Task OpenFolderInExplorerAsync(string folderPath)
    {
        try
        {
            Directory.CreateDirectory(folderPath);
            Process.Start(new ProcessStartInfo(folderPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync(
                "Could Not Open Folder",
                "Failed to open the target directory in Windows Explorer.",
                ex.Message);
        }
    }

    [RelayCommand]
    private async Task OpenLogFileAsync()
    {
        if (!_actionLog.TryOpenLogFile(out var err))
        {
            await _dialogService.ShowErrorAsync(
                "Could Not Open Log File",
                "Failed to open the action log in your default text editor.",
                err);
        }
    }

    [RelayCommand]
    private async Task ClearLogFileAsync()
    {
        var confirmed = await _dialogService.ShowConfirmAsync(
            "Clear Action Log?",
            "This will erase all saved log lines in actions.log.",
            "Clear Log",
            isDanger: true);

        if (confirmed)
        {
            _actionLog.Clear();
        }
    }

    [RelayCommand]
    private async Task ChooseAudioAsync()
    {
        var path = await _pickAudioFileAsync();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!File.Exists(path))
        {
            await _dialogService.ShowFileNotFoundAsync("Welcome Audio", path, allowPickNew: false, allowReset: false);
            return;
        }

        WelcomeAudioPath = path;
        _database.SaveSetting(DatabaseService.KeyWelcomeAudioPath, path);
        _actionLog.Log("Audio", $"Set welcome audio to '{Path.GetFileName(path)}'.");
        RefreshAudioDisplay();
    }

    [RelayCommand]
    private void ResetAudio()
    {
        StopPreview();
        WelcomeAudioPath = string.Empty;
        _database.SaveSetting(DatabaseService.KeyWelcomeAudioPath, string.Empty);
        _actionLog.Log("Audio", "Reset welcome audio to default.");
        RefreshAudioDisplay();
    }

    [RelayCommand]
    private async Task TogglePreviewAudioAsync()
    {
        if (IsPreviewPlaying)
        {
            StopPreview();
            return;
        }

        var pathToPlay = WelcomeAudioPath;
        if (!string.IsNullOrWhiteSpace(pathToPlay) && !File.Exists(pathToPlay))
        {
            var choice = await _dialogService.ShowFileNotFoundAsync("Welcome Audio", pathToPlay);
            if (choice == "pick")
            {
                await ChooseAudioAsync();
            }
            else if (choice == "reset")
            {
                ResetAudio();
            }

            return;
        }

        var resolved = _audioService.ResolveWelcomeAudioPath(pathToPlay);
        if (resolved is null)
        {
            await _dialogService.ShowWarningAsync(
                "Welcome Audio Not Found",
                "No welcome.wav or welcome.mp3 was found in Assets\\Audio, and no custom file is selected.",
                Path.Combine(AppContext.BaseDirectory, "Assets", "Audio"));
            return;
        }

        StopPreview();
        _previewCts = new CancellationTokenSource();
        IsPreviewPlaying = true;
        try
        {
            var result = await _audioService.PlayDetailedAsync(resolved, _previewCts.Token);
            if (result.Status == AudioPlayStatus.Failed)
            {
                await _dialogService.ShowErrorAsync(
                    "Audio Playback Error",
                    "Could not play the selected audio file.",
                    result.ErrorMessage);
            }
        }
        finally
        {
            IsPreviewPlaying = false;
        }
    }

    [RelayCommand]
    private void BackToProfilesList()
    {
        ClearEditingState();
        ReloadProfilesList();
    }

    [RelayCommand]
    private async Task ChangeEditingPfpAsync()
    {
        if (_editingProfile is null)
        {
            return;
        }

        var picked = await _pickImageFileAsync();
        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        if (!File.Exists(picked))
        {
            await _dialogService.ShowFileNotFoundAsync("Profile Picture", picked, allowPickNew: false, allowReset: false);
            return;
        }

        using var testDecode = _imageAssets.LoadScaledBitmap(picked, 140);
        if (testDecode is null)
        {
            await _dialogService.ShowErrorAsync(
                "Invalid Image",
                "Could not decode the selected profile picture. Please pick a valid PNG, JPG, or WEBP file.",
                picked);
            return;
        }

        var oldPath = _editingProfile.AvatarPath;
        var newPath = _imageAssets.CopyProfileAvatar(picked);
        _database.UpdateProfileAvatar(_editingProfile.Id, newPath);
        _editingProfile.AvatarPath = newPath;
        RefreshEditingPreview();
        _imageAssets.TryDeleteProfileAvatar(oldPath);

        SetFeedback("Profile picture updated.", isError: false);
        _actionLog.Log("Profile", $"Updated PFP for profile '{_editingProfile.Name}'.");
        _onProfilesChanged(_editingProfile.Id, false);
    }

    [RelayCommand]
    private void RemoveEditingPfp()
    {
        if (_editingProfile is null || string.IsNullOrEmpty(_editingProfile.AvatarPath))
        {
            return;
        }

        var oldPath = _editingProfile.AvatarPath;
        _database.UpdateProfileAvatar(_editingProfile.Id, null);
        _editingProfile.AvatarPath = null;
        RefreshEditingPreview();
        _imageAssets.TryDeleteProfileAvatar(oldPath);

        SetFeedback("Profile picture removed.", isError: false);
        _actionLog.Log("Profile", $"Removed PFP for profile '{_editingProfile.Name}'.");
        _onProfilesChanged(_editingProfile.Id, false);
    }

    [RelayCommand]
    private void SaveEditingName()
    {
        if (_editingProfile is null)
        {
            return;
        }

        var trimmed = EditingProfileName.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            SetFeedback("Profile name cannot be empty.", isError: true);
            return;
        }

        try
        {
            var oldName = _editingProfile.Name;
            _database.UpdateProfileName(_editingProfile.Id, trimmed);
            _editingProfile.Name = trimmed;
            EditingProfileName = trimmed;
            EditingProfileInitials = _editingProfile.Initials;
            SetFeedback("Profile name saved.", isError: false);
            _actionLog.Log("Profile", $"Renamed profile '{oldName}' -> '{trimmed}'.");
            _onProfilesChanged(_editingProfile.Id, false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            SetFeedback("Another profile already uses that name.", isError: true);
        }
        catch (Exception ex)
        {
            SetFeedback($"Failed to rename profile: {ex.Message}", isError: true);
            _actionLog.Log("Error", $"Failed to rename profile: {ex.Message}");
        }
    }

    [RelayCommand]
    private void UpdateEditingPin()
    {
        if (_editingProfile is null)
        {
            return;
        }

        if (_editingProfile.HasPassword &&
            !_database.VerifyProfilePassword(_editingProfile.Id, EditingProfileCurrentPin))
        {
            SetFeedback("Current PIN / password is incorrect.", isError: true);
            _actionLog.Log("Warning", $"Incorrect current PIN entered while editing '{_editingProfile.Name}'.");
            EditingProfileCurrentPin = string.Empty;
            return;
        }

        if (!_editingProfile.HasPassword && string.IsNullOrEmpty(EditingProfileNewPin))
        {
            SetFeedback("Enter a PIN or password to set.", isError: true);
            return;
        }

        var nextPin = string.IsNullOrEmpty(EditingProfileNewPin) ? null : EditingProfileNewPin;
        _database.UpdateProfilePassword(_editingProfile.Id, nextPin);

        var refreshed = _database.GetProfileById(_editingProfile.Id);
        if (refreshed is not null)
        {
            _editingProfile = refreshed;
            EditingProfileHasPassword = refreshed.HasPassword;
        }

        EditingProfileCurrentPin = string.Empty;
        EditingProfileNewPin = string.Empty;
        SetFeedback(
            nextPin is null ? "PIN / password removed." : "PIN / password updated.",
            isError: false);
        _actionLog.Log("Security", nextPin is null
            ? $"Removed PIN for profile '{_editingProfile.Name}'."
            : $"Updated PIN for profile '{_editingProfile.Name}'.");
        _onProfilesChanged(_editingProfile.Id, false);
    }

    [RelayCommand]
    private void RequestDeleteProfile()
    {
        if (_editingProfile is null)
        {
            return;
        }

        if (_editingProfile.HasPassword &&
            !_database.VerifyProfilePassword(_editingProfile.Id, EditingProfileCurrentPin))
        {
            SetFeedback("Enter your current PIN / password before deleting this profile.", isError: true);
            return;
        }

        IsDeleteConfirming = true;
        ProfileFeedbackMessage = string.Empty;
    }

    [RelayCommand]
    private void CancelDeleteProfile()
    {
        IsDeleteConfirming = false;
    }

    [RelayCommand]
    private void ConfirmDeleteProfile()
    {
        if (_editingProfile is null)
        {
            return;
        }

        if (_editingProfile.HasPassword &&
            !_database.VerifyProfilePassword(_editingProfile.Id, EditingProfileCurrentPin))
        {
            IsDeleteConfirming = false;
            SetFeedback("Current PIN / password is incorrect.", isError: true);
            return;
        }

        var deletedId = _editingProfile.Id;
        var deletedName = _editingProfile.Name;
        var avatarPath = _editingProfile.AvatarPath;
        _database.DeleteProfile(deletedId);
        _imageAssets.TryDeleteProfileAvatar(avatarPath);
        _actionLog.Log("Profile", $"Deleted profile '{deletedName}'.");

        ClearEditingState();
        ReloadProfilesList();
        _onProfilesChanged(deletedId, true);
    }

    [RelayCommand]
    private void Close()
    {
        StopPreview();
        _onClose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopPreview();
        ClearEditingState();
        DisposeProfilesList();
    }

    private void OpenProfileEditor(SettingsProfileRowViewModel row)
    {
        var latest = _database.GetProfileById(row.Profile.Id) ?? row.Profile;
        _editingProfile = latest;
        EditingProfileName = latest.Name;
        EditingProfileInitials = latest.Initials;
        EditingProfileAvatarBrush = new SolidColorBrush(Color.Parse(latest.AvatarColor));
        EditingProfileHasPassword = latest.HasPassword;
        EditingProfileCurrentPin = string.Empty;
        EditingProfileNewPin = string.Empty;
        IsDeleteConfirming = false;
        ProfileFeedbackMessage = string.Empty;
        IsProfileFeedbackError = false;
        RefreshEditingPreview();
        IsEditingProfile = true;

        if (!string.IsNullOrWhiteSpace(latest.AvatarPath) && !File.Exists(latest.AvatarPath))
        {
            _ = HandleMissingProfileAvatarAsync(latest.AvatarPath);
        }
    }

    private async Task HandleMissingProfileAvatarAsync(string missingPath)
    {
        var choice = await _dialogService.ShowFileNotFoundAsync("Profile Picture", missingPath);
        if (choice == "pick")
        {
            await ChangeEditingPfpAsync();
        }
        else if (choice == "reset")
        {
            RemoveEditingPfp();
        }
    }

    private void RefreshEditingPreview()
    {
        var previous = EditingProfileAvatarBitmap;
        EditingProfileAvatarBitmap = _editingProfile is not null
            ? _imageAssets.LoadScaledBitmap(_editingProfile.AvatarPath, 140)
            : null;
        previous?.Dispose();
    }

    private void ClearEditingState()
    {
        _editingProfile = null;
        IsEditingProfile = false;
        IsDeleteConfirming = false;
        EditingProfileCurrentPin = string.Empty;
        EditingProfileNewPin = string.Empty;
        ProfileFeedbackMessage = string.Empty;
        var prev = EditingProfileAvatarBitmap;
        EditingProfileAvatarBitmap = null;
        prev?.Dispose();
    }

    private void SetFeedback(string message, bool isError)
    {
        ProfileFeedbackMessage = message;
        IsProfileFeedbackError = isError;
    }

    private void ReloadProfilesList()
    {
        DisposeProfilesList();
        foreach (var profile in _database.GetProfiles())
        {
            ProfilesList.Add(new SettingsProfileRowViewModel(profile, _imageAssets, OpenProfileEditor));
        }

        HasAnyProfiles = ProfilesList.Count > 0;
    }

    private void DisposeProfilesList()
    {
        foreach (var row in ProfilesList)
        {
            row.Dispose();
        }

        ProfilesList.Clear();
    }

    private void ReloadThemesCore(string preferredThemeName)
    {
        AvailableThemes.Clear();
        foreach (var theme in _themeService.GetAvailableThemes())
        {
            AvailableThemes.Add(theme);
        }

        SelectedTheme = AvailableThemes.FirstOrDefault(t =>
                string.Equals(t.Name, preferredThemeName, StringComparison.OrdinalIgnoreCase))
            ?? AvailableThemes.FirstOrDefault();
    }

    private void RefreshAudioDisplay()
    {
        HasCustomAudio = !string.IsNullOrWhiteSpace(WelcomeAudioPath);
        var resolved = _audioService.ResolveWelcomeAudioPath(WelcomeAudioPath);
        if (HasCustomAudio)
        {
            WelcomeAudioDisplay = File.Exists(WelcomeAudioPath)
                ? $"Custom: {Path.GetFileName(WelcomeAudioPath)}"
                : $"Missing custom file: {Path.GetFileName(WelcomeAudioPath)}";
        }
        else if (resolved is not null)
        {
            WelcomeAudioDisplay = $"Default bundled: {Path.GetFileName(resolved)}";
        }
        else
        {
            WelcomeAudioDisplay = "No welcome audio configured (silent intro)";
        }
    }

    private void StopPreview()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
        _audioService.Stop();
        IsPreviewPlaying = false;
    }
}
