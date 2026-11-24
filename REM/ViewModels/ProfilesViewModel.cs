using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using REM.Models;
using REM.Services;

namespace REM.ViewModels;

public sealed partial class ProfileCardViewModel : ObservableObject, IDisposable
{
    private readonly ImageAssetService _imageAssets;
    private readonly Action<UserProfile> _onSelect;
    private IBrush? _avatarBrush;
    private bool _disposed;

    public ProfileCardViewModel(
        UserProfile profile,
        ImageAssetService imageAssets,
        Action<UserProfile> onSelect)
    {
        Profile = profile;
        _imageAssets = imageAssets;
        _onSelect = onSelect;

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
        AvatarBitmap = _imageAssets.LoadScaledBitmap(Profile.AvatarPath, 140);
        previous?.Dispose();
    }

    [RelayCommand]
    private void Select() => _onSelect(Profile);

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

public sealed partial class ProfilesViewModel : ObservableObject, IDisposable
{
    private static readonly string[] CozyPalette =
    [
        "#4E6FB8",
        "#6D59A8",
        "#3E8562",
        "#A6644C",
        "#487CA8",
        "#8D558B"
    ];

    private readonly DatabaseService _database;
    private readonly ImageAssetService _imageAssets;
    private readonly ActionLogService _actionLog;
    private readonly DialogService _dialogService;
    private readonly Func<Task<string?>> _pickImageFileAsync;
    private readonly Action<UserProfile> _onProfileUnlocked;
    private readonly Action _onOpenSettings;

    private UserProfile? _pendingUnlockProfile;
    private string? _newProfileAvatarSourcePath;
    private bool _disposed;

    public ProfilesViewModel(
        DatabaseService database,
        ImageAssetService imageAssets,
        ActionLogService actionLog,
        DialogService dialogService,
        Func<Task<string?>> pickImageFileAsync,
        Action<UserProfile> onProfileUnlocked,
        Action onOpenSettings)
    {
        _database = database;
        _imageAssets = imageAssets;
        _actionLog = actionLog;
        _dialogService = dialogService;
        _pickImageFileAsync = pickImageFileAsync;
        _onProfileUnlocked = onProfileUnlocked;
        _onOpenSettings = onOpenSettings;

        BrandIconBitmap = _imageAssets.GetBrandImage("rem-icon.png", 64);
        PopupArtBitmap = _imageAssets.GetPopupImage();
        LoadProfiles();
    }

    public ObservableCollection<ProfileCardViewModel> Profiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBrandIcon))]
    private Bitmap? _brandIconBitmap;

    public bool HasBrandIcon => BrandIconBitmap is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPopupArt))]
    private Bitmap? _popupArtBitmap;

    public bool HasPopupArt => PopupArtBitmap is not null;

    public void RefreshBrandAssets()
    {
        BrandIconBitmap = _imageAssets.GetBrandImage("rem-icon.png", 64);
        PopupArtBitmap = _imageAssets.GetPopupImage();
    }

    [ObservableProperty]
    private bool _hasProfiles;

    [ObservableProperty]
    private bool _isAddProfileOpen;

    [ObservableProperty]
    private string _newProfileName = string.Empty;

    [ObservableProperty]
    private string _newProfilePassword = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewProfileAvatar))]
    private Bitmap? _newProfileAvatarPreview;

    public bool HasNewProfileAvatar => NewProfileAvatarPreview is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddProfileError))]
    private string _addProfileError = string.Empty;

    public bool HasAddProfileError => !string.IsNullOrEmpty(AddProfileError);

    [ObservableProperty]
    private bool _isUnlockOpen;

    [ObservableProperty]
    private string _unlockTargetProfileName = string.Empty;

    [ObservableProperty]
    private string _unlockPassword = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnlockError))]
    private string _unlockError = string.Empty;

    public bool HasUnlockError => !string.IsNullOrEmpty(UnlockError);
    public bool HasOpenModal => IsAddProfileOpen || IsUnlockOpen;

    public void RefreshProfiles() => LoadProfiles();

    public bool TryCloseModal()
    {
        if (IsAddProfileOpen)
        {
            CancelAddProfile();
            return true;
        }

        if (IsUnlockOpen)
        {
            CancelUnlock();
            return true;
        }

        return false;
    }

    [RelayCommand]
    private void OpenSettings() => _onOpenSettings();

    [RelayCommand]
    private void OpenAddProfile()
    {
        NewProfileName = string.Empty;
        NewProfilePassword = string.Empty;
        AddProfileError = string.Empty;
        ClearNewProfilePfp();
        IsAddProfileOpen = true;
    }

    [RelayCommand]
    private async Task ChooseNewProfilePfpAsync()
    {
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

        var decoded = _imageAssets.LoadScaledBitmap(picked, 140);
        if (decoded is null)
        {
            await _dialogService.ShowErrorAsync(
                "Invalid Image",
                "Could not load the selected image file. Please choose a valid PNG, JPG, or WEBP image.",
                picked);
            return;
        }

        _newProfileAvatarSourcePath = picked;
        var prev = NewProfileAvatarPreview;
        NewProfileAvatarPreview = decoded;
        prev?.Dispose();
    }

    [RelayCommand]
    private void ClearNewProfilePfp()
    {
        _newProfileAvatarSourcePath = null;
        var prev = NewProfileAvatarPreview;
        NewProfileAvatarPreview = null;
        prev?.Dispose();
    }

    [RelayCommand]
    private void CancelAddProfile()
    {
        IsAddProfileOpen = false;
        AddProfileError = string.Empty;
        ClearNewProfilePfp();
    }

    [RelayCommand]
    private void ConfirmAddProfile()
    {
        var trimmed = NewProfileName.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            AddProfileError = "Please enter a profile name.";
            return;
        }

        var color = CozyPalette[Profiles.Count % CozyPalette.Length];
        string? savedAvatarPath = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(_newProfileAvatarSourcePath) &&
                File.Exists(_newProfileAvatarSourcePath))
            {
                savedAvatarPath = _imageAssets.CopyProfileAvatar(_newProfileAvatarSourcePath);
            }

            var created = _database.CreateProfile(
                trimmed,
                string.IsNullOrEmpty(NewProfilePassword) ? null : NewProfilePassword,
                color,
                savedAvatarPath);

            _actionLog.Log("Profile", $"Created profile '{created.Name}' (protected={created.HasPassword}).");
            IsAddProfileOpen = false;
            AddProfileError = string.Empty;
            NewProfilePassword = string.Empty;
            ClearNewProfilePfp();
            LoadProfiles();
            _onProfileUnlocked(created);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            _imageAssets.TryDeleteProfileAvatar(savedAvatarPath);
            AddProfileError = "A profile with that name already exists.";
            _actionLog.Log("Warning", $"Duplicate profile creation rejected for '{trimmed}'.");
        }
        catch (Exception ex)
        {
            _imageAssets.TryDeleteProfileAvatar(savedAvatarPath);
            AddProfileError = $"Could not create profile: {ex.Message}";
            _actionLog.Log("Error", $"Failed to create profile '{trimmed}': {ex.Message}");
        }
    }

    [RelayCommand]
    private void CancelUnlock()
    {
        _pendingUnlockProfile = null;
        UnlockPassword = string.Empty;
        UnlockError = string.Empty;
        IsUnlockOpen = false;
    }

    [RelayCommand]
    private void ConfirmUnlock()
    {
        if (_pendingUnlockProfile is null)
        {
            CancelUnlock();
            return;
        }

        if (_database.VerifyProfilePassword(_pendingUnlockProfile.Id, UnlockPassword))
        {
            var unlocked = _pendingUnlockProfile;
            _actionLog.Log("Security", $"Unlocked profile '{unlocked.Name}'.");
            CancelUnlock();
            _onProfileUnlocked(unlocked);
            return;
        }

        _actionLog.Log("Warning", $"Failed unlock attempt for profile '{_pendingUnlockProfile.Name}'.");
        UnlockError = "Incorrect password or PIN. Please try again.";
        UnlockPassword = string.Empty;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearNewProfilePfp();
        DisposeProfileCards();
    }

    private void LoadProfiles()
    {
        DisposeProfileCards();
        foreach (var profile in _database.GetProfiles())
        {
            Profiles.Add(new ProfileCardViewModel(
                profile,
                _imageAssets,
                HandleProfileSelect));
        }

        HasProfiles = Profiles.Count > 0;
    }

    private void DisposeProfileCards()
    {
        foreach (var card in Profiles)
        {
            card.Dispose();
        }

        Profiles.Clear();
    }

    private void HandleProfileSelect(UserProfile profile)
    {
        if (!profile.HasPassword)
        {
            _onProfileUnlocked(profile);
            return;
        }

        _pendingUnlockProfile = profile;
        UnlockTargetProfileName = profile.Name;
        UnlockPassword = string.Empty;
        UnlockError = string.Empty;
        IsUnlockOpen = true;
    }
}
