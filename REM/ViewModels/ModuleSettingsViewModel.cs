using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using REM.Models;
using REM.Modules;
using REM.Services;

namespace REM.ViewModels;

public enum ModuleSettingsPage
{
    General,
    YouTube,
    TwitchApi,
    Categories,
    SpotifyApi
}

public sealed record BrowserCookieOption(string Key, string Label)
{
    public override string ToString() => Label;
}

public sealed record AudioQualityOption(string Key, string Label)
{
    public override string ToString() => Label;
}

public sealed record SpotifyCacheSizeOption(int SizeMb, string Label)
{
    public override string ToString() => Label;
}

public sealed record RemArtPoseOption(string FileName, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class PinnedYouTubeTopicRowViewModel : ObservableObject
{
    private readonly Action<PinnedYouTubeTopicRowViewModel> _onRemove;

    public PinnedYouTubeTopicRowViewModel(string topic, Action<PinnedYouTubeTopicRowViewModel> onRemove)
    {
        Topic = topic;
        _onRemove = onRemove;
    }

    public string Topic { get; }

    [RelayCommand]
    private void Remove() => _onRemove(this);
}

public sealed partial class PinnedCategoryRowViewModel : ObservableObject
{
    private readonly Action<PinnedCategoryRowViewModel> _onRemove;

    public PinnedCategoryRowViewModel(TwitchCategoryItem item, Action<PinnedCategoryRowViewModel> onRemove)
    {
        Item = item;
        _onRemove = onRemove;
    }

    public TwitchCategoryItem Item { get; }
    public string Name => Item.Name;
    public string GameIdDisplay => string.IsNullOrWhiteSpace(Item.GameId) ? "Auto-resolved" : $"ID: {Item.GameId}";

    [RelayCommand]
    private void Remove() => _onRemove(this);
}

public sealed partial class ModuleSettingsViewModel : ObservableObject, IDisposable
{
    private static readonly BrowserCookieOption[] AllBrowserOptions =
    [
        new("zen", "Zen Browser"),
        new("firefox", "Firefox"),
        new("chrome", "Chrome"),
        new("edge", "Microsoft Edge"),
        new("brave", "Brave"),
        new("none", "Disabled (Topics & Search only)")
    ];

    private static readonly AudioQualityOption[] AllAudioQualityOptions =
    [
        new("max", "Highest Available (Default · ASMR/Music)"),
        new("160", "High (Up to 160 kbps)"),
        new("128", "Balanced (Up to 128 kbps)"),
        new("64", "Data Saver (Up to 64 kbps)")
    ];

    private static readonly SpotifyCacheSizeOption[] AllSpotifyCacheOptions =
    [
        new(250, "250 MB Shared"),
        new(500, "500 MB Shared"),
        new(1024, "1 GB Shared (Recommended)"),
        new(2048, "2 GB Shared"),
        new(5120, "5 GB Shared"),
        new(0, "Unlimited (0)")
    ];

    private static readonly RemArtPoseOption[] AllRemPoseOptions =
    [
        new("rem-welcome.png", "Welcome Pose (Default)"),
        new("rem-corner.png", "Corner Pose"),
        new("rem-popup-info.png", "Info Pose"),
        new("rem-popup-confirm.png", "Confirm Pose"),
        new("rem-popup-warning.png", "Warning Pose"),
        new("rem-popup-error.png", "Error Pose")
    ];

    private readonly IAppModule _module;
    private readonly long _profileId;
    private readonly DatabaseService _database;
    private readonly ActionLogService _actionLog;
    private readonly DialogService _dialogService;
    private readonly YtDlpService _ytDlp;
    private readonly TwitchHelixService _twitch;
    private readonly SpotifySoloistService _soloist;
    private readonly SpotifyWebApiService _spotifyWebApi;
    private readonly LastFmService _lastFm;
    private readonly Action<string> _onSettingsApplied;
    private readonly Func<IAppModule, Task> _onRemoveFromProfile;
    private readonly Action _onClose;

    private CancellationTokenSource? _oauthCts;
    private bool _suppressSave;
    private bool _disposed;

    public ModuleSettingsViewModel(
        IAppModule module,
        long profileId,
        DatabaseService database,
        ActionLogService actionLog,
        DialogService dialogService,
        YtDlpService ytDlp,
        TwitchHelixService twitch,
        SpotifySoloistService soloist,
        SpotifyWebApiService spotifyWebApi,
        LastFmService lastFm,
        Action<string> onSettingsApplied,
        Func<IAppModule, Task> onRemoveFromProfile,
        Action onClose)
    {
        _module = module;
        _profileId = profileId;
        _database = database;
        _actionLog = actionLog;
        _dialogService = dialogService;
        _ytDlp = ytDlp;
        _twitch = twitch;
        _soloist = soloist;
        _spotifyWebApi = spotifyWebApi;
        _lastFm = lastFm;
        _onSettingsApplied = onSettingsApplied;
        _onRemoveFromProfile = onRemoveFromProfile;
        _onClose = onClose;

        AccentBrush = new SolidColorBrush(Color.Parse(module.AccentHex));
        IsAudioStreamer = string.Equals(module.Id, "audio-streamer", StringComparison.Ordinal) ||
                          string.Equals(module.Id, "stream-watcher", StringComparison.Ordinal);
        IsSpotify = string.Equals(module.Id, "spotify", StringComparison.Ordinal);

        if (IsAudioStreamer)
        {
            _suppressSave = true;
            ShowThumbnails = ReadBool(DatabaseService.KeyAudioShowThumbnails, false);
            ShowYouTubeThumbnails = ReadBool(DatabaseService.KeyAudioShowYouTubeThumbnails, true);
            UseYtDlpFallback = ReadBool(DatabaseService.KeyAudioYtDlpFallback, true);
            TwitchClientId = _database.GetSetting(DatabaseService.KeyTwitchClientId) ?? string.Empty;
            TwitchClientSecret = _database.GetSetting(DatabaseService.KeyTwitchClientSecret) ?? string.Empty;

            var savedBrowser = _database.GetSetting(DatabaseService.KeyAudioYouTubeCookieBrowser);
            if (string.IsNullOrWhiteSpace(savedBrowser))
            {
                savedBrowser = _ytDlp.DetectZenProfileDirectory() is not null ? "zen" : "none";
            }

            SelectedBrowserOption = AllBrowserOptions.FirstOrDefault(o =>
                    string.Equals(o.Key, savedBrowser, StringComparison.OrdinalIgnoreCase))
                ?? AllBrowserOptions[^1];
            YouTubeProfileOverride = _database.GetSetting(DatabaseService.KeyAudioYouTubeCookieProfile) ?? string.Empty;

            var savedQuality = _database.GetSetting(DatabaseService.KeyAudioQualityPreset) ?? "max";
            SelectedAudioQualityOption = AllAudioQualityOptions.FirstOrDefault(o =>
                    string.Equals(o.Key, savedQuality, StringComparison.OrdinalIgnoreCase))
                ?? AllAudioQualityOptions[0];
            _suppressSave = false;

            var ytDlpPath = _ytDlp.ResolveExecutablePath();
            YtDlpStatusText = ytDlpPath is not null
                ? $"Found: {ytDlpPath}"
                : "Not downloaded yet (auto-downloads when needed)";

            UpdateYouTubeCookieStatus();
            RefreshTwitchUserStatus();
            LoadPinnedYouTubeTopics();
            LoadPinnedCategories();
        }
        else if (IsSpotify)
        {
            _suppressSave = true;
            SpotifyClientId = _database.GetSetting(DatabaseService.KeySpotifyClientId) ?? string.Empty;
            SpotifySoloistApiKey = _database.GetSetting(DatabaseService.KeySpotifySoloistApiKey) ?? string.Empty;
            SpotifyLastFmApiKey = _database.GetSetting(DatabaseService.KeySpotifyLastFmApiKey) ?? string.Empty;

            var savedCacheMb = _soloist.ConfiguredCacheSizeMb;
            SelectedSpotifyCacheSizeOption = AllSpotifyCacheOptions.FirstOrDefault(o => o.SizeMb == savedCacheMb)
                ?? AllSpotifyCacheOptions[2];

            ShowSpotifyPlaylistIcons = ReadBool(DatabaseService.KeySpotifyShowPlaylistIcons, true);
            ShowSpotifyTrackCovers = ReadBool(DatabaseService.KeySpotifyShowTrackCovers, true);
            ShowSpotifyAmbientGlow = ReadBool(DatabaseService.KeySpotifyShowAmbientGlow, true);
            ShowSpotifyRemArt = ReadBool(DatabaseService.KeySpotifyShowRemArt, true);
            var savedPose = _database.GetSetting(DatabaseService.KeySpotifyRemArtFile);
            if (string.IsNullOrWhiteSpace(savedPose))
            {
                savedPose = "rem-welcome.png";
            }

            SelectedSpotifyRemPose = AllRemPoseOptions.FirstOrDefault(o =>
                    string.Equals(o.FileName, savedPose, StringComparison.OrdinalIgnoreCase))
                ?? AllRemPoseOptions[0];
            _suppressSave = false;

            var soloistPath = _soloist.ResolveExecutablePath();
            SoloistPathText = soloistPath is not null
                ? $"Found: {soloistPath}"
                : "Not installed yet (auto-downloads on start, or click Download / Update)";

            RefreshSpotifyStatus();
        }
    }

    public string ModuleId => _module.Id;
    public string ModuleName => _module.Name;
    public string ModuleDescription => _module.Description;
    public Geometry ModuleIcon => _module.IconGeometry;
    public IBrush AccentBrush { get; }
    public bool IsAudioStreamer { get; }
    public bool IsSpotify { get; }
    public IReadOnlyList<BrowserCookieOption> BrowserOptions => AllBrowserOptions;
    public IReadOnlyList<AudioQualityOption> AudioQualityOptions => AllAudioQualityOptions;
    public IReadOnlyList<SpotifyCacheSizeOption> SpotifyCacheOptions => AllSpotifyCacheOptions;
    public IReadOnlyList<RemArtPoseOption> SpotifyRemPoseOptions => AllRemPoseOptions;

    public string CurrentStateLabel => _module.State switch
    {
        ModuleState.RunningUI => "Running (UI)",
        ModuleState.RunningHeadless => "Running (Headless)",
        ModuleState.Paused => "Paused",
        _ => "Stopped"
    };

    public ObservableCollection<PinnedYouTubeTopicRowViewModel> PinnedYouTubeTopics { get; } = [];
    public ObservableCollection<PinnedCategoryRowViewModel> PinnedCategories { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneralPage))]
    [NotifyPropertyChangedFor(nameof(IsYouTubePage))]
    [NotifyPropertyChangedFor(nameof(IsTwitchApiPage))]
    [NotifyPropertyChangedFor(nameof(IsCategoriesPage))]
    [NotifyPropertyChangedFor(nameof(IsSpotifyApiPage))]
    private ModuleSettingsPage _currentPage = ModuleSettingsPage.General;

    public bool IsGeneralPage => CurrentPage == ModuleSettingsPage.General;
    public bool IsYouTubePage => CurrentPage == ModuleSettingsPage.YouTube;
    public bool IsTwitchApiPage => CurrentPage == ModuleSettingsPage.TwitchApi;
    public bool IsCategoriesPage => CurrentPage == ModuleSettingsPage.Categories;
    public bool IsSpotifyApiPage => CurrentPage == ModuleSettingsPage.SpotifyApi;

    [ObservableProperty]
    private bool _showThumbnails;

    [ObservableProperty]
    private bool _showYouTubeThumbnails = true;

    [ObservableProperty]
    private bool _useYtDlpFallback = true;

    [ObservableProperty]
    private string _ytDlpStatusText = string.Empty;

    [ObservableProperty]
    private BrowserCookieOption? _selectedBrowserOption;

    [ObservableProperty]
    private AudioQualityOption? _selectedAudioQualityOption;

    [ObservableProperty]
    private string _youTubeProfileOverride = string.Empty;

    [ObservableProperty]
    private string _youTubeCookieStatusText = string.Empty;

    [ObservableProperty]
    private string _twitchClientId = string.Empty;

    [ObservableProperty]
    private string _twitchClientSecret = string.Empty;

    [ObservableProperty]
    private bool _isTwitchUserConnected;

    [ObservableProperty]
    private string _twitchUserStatus = "Not connected";

    [ObservableProperty]
    private bool _isOAuthBusy;

    [ObservableProperty]
    private string _spotifyClientId = string.Empty;

    [ObservableProperty]
    private string _spotifySoloistApiKey = string.Empty;

    [ObservableProperty]
    private string _spotifyLastFmApiKey = string.Empty;

    [ObservableProperty]
    private bool _isSpotifyApiConnected;

    [ObservableProperty]
    private string _spotifyApiStatusText = "Not connected";

    [ObservableProperty]
    private string _spotifyEngineStatusText = string.Empty;

    [ObservableProperty]
    private string _soloistPathText = string.Empty;

    [ObservableProperty]
    private bool _isSpotifyOAuthBusy;

    [ObservableProperty]
    private SpotifyCacheSizeOption? _selectedSpotifyCacheSizeOption;

    [ObservableProperty]
    private string _spotifyCacheUsageText = string.Empty;

    [ObservableProperty]
    private bool _isClearingSpotifyCache;

    [ObservableProperty]
    private bool _isUpdatingSoloist;

    [ObservableProperty]
    private bool _showSpotifyPlaylistIcons = true;

    [ObservableProperty]
    private bool _showSpotifyTrackCovers = true;

    [ObservableProperty]
    private bool _showSpotifyAmbientGlow = true;

    [ObservableProperty]
    private bool _showSpotifyRemArt = true;

    [ObservableProperty]
    private RemArtPoseOption? _selectedSpotifyRemPose;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeedback))]
    private string _feedbackMessage = string.Empty;

    public bool HasFeedback => !string.IsNullOrEmpty(FeedbackMessage);

    partial void OnShowThumbnailsChanged(bool value)
    {
        if (_suppressSave)
        {
            return;
        }

        _database.SaveSetting(DatabaseService.KeyAudioShowThumbnails, value.ToString());
        _actionLog.Log("Settings", $"Twitch stream thumbnails set to {value}.");
        _onSettingsApplied(ModuleId);
    }

    partial void OnShowYouTubeThumbnailsChanged(bool value)
    {
        if (_suppressSave)
        {
            return;
        }

        _database.SaveSetting(DatabaseService.KeyAudioShowYouTubeThumbnails, value.ToString());
        _actionLog.Log("Settings", $"YouTube thumbnails set to {value}.");
        _onSettingsApplied(ModuleId);
    }

    partial void OnUseYtDlpFallbackChanged(bool value)
    {
        if (_suppressSave)
        {
            return;
        }

        _database.SaveSetting(DatabaseService.KeyAudioYtDlpFallback, value.ToString());
        _actionLog.Log("Settings", $"YouTube 403 yt-dlp fallback set to {value}.");
    }

    partial void OnSelectedBrowserOptionChanged(BrowserCookieOption? value)
    {
        if (_suppressSave || value is null)
        {
            return;
        }

        _database.SaveSetting(DatabaseService.KeyAudioYouTubeCookieBrowser, value.Key);
        UpdateYouTubeCookieStatus();
        FeedbackMessage = $"YouTube FYP browser set to {value.Label}.";
        _actionLog.Log("YouTube", $"YouTube FYP cookie source set to {value.Key}.");
        _onSettingsApplied(ModuleId);
    }

    partial void OnSelectedAudioQualityOptionChanged(AudioQualityOption? value)
    {
        if (_suppressSave || value is null)
        {
            return;
        }

        _database.SaveSetting(DatabaseService.KeyAudioQualityPreset, value.Key);
        FeedbackMessage = $"Audio quality preset set to {value.Label}.";
        _actionLog.Log("Audio", $"Audio bitrate preset set to {value.Key}.");
        _onSettingsApplied(ModuleId);
    }

    partial void OnSelectedSpotifyCacheSizeOptionChanged(SpotifyCacheSizeOption? value)
    {
        if (_suppressSave || value is null)
        {
            return;
        }

        _database.SaveSetting(DatabaseService.KeySpotifyCacheSizeMb, value.SizeMb.ToString());
        RefreshSpotifyStatus();
        FeedbackMessage = $"Spotify disk audio cache limit set to {value.Label} (click Restart Spotify Soloist if currently playing).";
        _actionLog.Log("Spotify", $"Spotify disk cache limit set to {value.SizeMb} MB.");
        _onSettingsApplied(ModuleId);
    }

    partial void OnShowSpotifyPlaylistIconsChanged(bool value)
    {
        if (_suppressSave) return;
        _database.SaveSetting(DatabaseService.KeySpotifyShowPlaylistIcons, value.ToString());
        _actionLog.Log("Spotify", $"Spotify playlist cover icons set to {value}.");
        _onSettingsApplied(ModuleId);
    }

    partial void OnShowSpotifyTrackCoversChanged(bool value)
    {
        if (_suppressSave) return;
        _database.SaveSetting(DatabaseService.KeySpotifyShowTrackCovers, value.ToString());
        _actionLog.Log("Spotify", $"Spotify track row covers set to {value}.");
        _onSettingsApplied(ModuleId);
    }

    partial void OnShowSpotifyAmbientGlowChanged(bool value)
    {
        if (_suppressSave) return;
        _database.SaveSetting(DatabaseService.KeySpotifyShowAmbientGlow, value.ToString());
        _actionLog.Log("Spotify", $"Spotify dynamic ambient glow set to {value}.");
        _onSettingsApplied(ModuleId);
    }

    partial void OnShowSpotifyRemArtChanged(bool value)
    {
        if (_suppressSave) return;
        _database.SaveSetting(DatabaseService.KeySpotifyShowRemArt, value.ToString());
        _actionLog.Log("Spotify", $"Spotify Rem companion art set to {value}.");
        _onSettingsApplied(ModuleId);
    }

    partial void OnSelectedSpotifyRemPoseChanged(RemArtPoseOption? value)
    {
        if (_suppressSave || value is null) return;
        _database.SaveSetting(DatabaseService.KeySpotifyRemArtFile, value.FileName);
        FeedbackMessage = $"Spotify Rem companion pose set to {value.Label}.";
        _actionLog.Log("Spotify", $"Spotify Rem pose set to {value.FileName}.");
        _onSettingsApplied(ModuleId);
    }

    [RelayCommand]
    private void SelectGeneralPage() => CurrentPage = ModuleSettingsPage.General;

    [RelayCommand]
    private void SelectYouTubePage()
    {
        LoadPinnedYouTubeTopics();
        UpdateYouTubeCookieStatus();
        CurrentPage = ModuleSettingsPage.YouTube;
    }

    [RelayCommand]
    private void SelectTwitchApiPage() => CurrentPage = ModuleSettingsPage.TwitchApi;

    [RelayCommand]
    private void SelectCategoriesPage()
    {
        LoadPinnedCategories();
        CurrentPage = ModuleSettingsPage.Categories;
    }

    [RelayCommand]
    private void SelectSpotifyApiPage()
    {
        RefreshSpotifyStatus();
        CurrentPage = ModuleSettingsPage.SpotifyApi;
    }

    [RelayCommand]
    private void SaveYouTubeProfileOverride()
    {
        _database.SaveSetting(DatabaseService.KeyAudioYouTubeCookieProfile, YouTubeProfileOverride.Trim());
        UpdateYouTubeCookieStatus();
        FeedbackMessage = "Saved YouTube browser profile override.";
        _actionLog.Log("YouTube", "Updated custom browser profile override.");
        _onSettingsApplied(ModuleId);
    }

    [RelayCommand]
    private void SaveTwitchCredentials()
    {
        _database.SaveSetting(DatabaseService.KeyTwitchClientId, TwitchClientId.Trim());
        _database.SaveSetting(DatabaseService.KeyTwitchClientSecret, TwitchClientSecret.Trim());
        _twitch.InvalidateAppToken();
        FeedbackMessage = "Twitch Client credentials saved.";
        _actionLog.Log("Twitch", "Updated Twitch API Client credentials.");
        _onSettingsApplied(ModuleId);
    }

    [RelayCommand]
    private async Task ConnectTwitchAccountAsync()
    {
        SaveTwitchCredentials();
        _oauthCts?.Cancel();
        _oauthCts?.Dispose();
        _oauthCts = new CancellationTokenSource();

        IsOAuthBusy = true;
        FeedbackMessage = "Waiting for Twitch login in your browser...";
        try
        {
            var (success, login, error) = await _twitch.AuthenticateUserAsync(_oauthCts.Token);
            RefreshTwitchUserStatus();
            if (success)
            {
                FeedbackMessage = $"Connected to Twitch as {login}.";
                _onSettingsApplied(ModuleId);
            }
            else
            {
                FeedbackMessage = string.Empty;
                await _dialogService.ShowErrorAsync("Twitch OAuth Failed", "Could not connect your Twitch account.", error);
            }
        }
        finally
        {
            IsOAuthBusy = false;
        }
    }

    [RelayCommand]
    private void DisconnectTwitchAccount()
    {
        _twitch.DisconnectUser();
        RefreshTwitchUserStatus();
        FeedbackMessage = "Disconnected Twitch user account.";
        _onSettingsApplied(ModuleId);
    }

    [RelayCommand]
    private async Task SaveSpotifySettingsAsync()
    {
        _database.SaveSetting(DatabaseService.KeySpotifyClientId, SpotifyClientId.Trim());
        _database.SaveSetting(DatabaseService.KeySpotifySoloistApiKey, SpotifySoloistApiKey.Trim());
        _database.SaveSetting(DatabaseService.KeySpotifyLastFmApiKey, SpotifyLastFmApiKey.Trim());

        if (_soloist.HasApiKey && !_soloist.IsDaemonRunning)
        {
            await _soloist.EnsureDaemonRunningAsync(CancellationToken.None);
        }

        RefreshSpotifyStatus();
        FeedbackMessage = "Spotify Soloist, Web API & Last.fm settings saved.";
        _actionLog.Log("Spotify", "Updated Spotify Soloist API Key / Client ID / Last.fm Key.");
        _onSettingsApplied(ModuleId);
    }

    [RelayCommand]
    private async Task ConnectSpotifyApiAsync()
    {
        _database.SaveSetting(DatabaseService.KeySpotifyClientId, SpotifyClientId.Trim());
        _database.SaveSetting(DatabaseService.KeySpotifySoloistApiKey, SpotifySoloistApiKey.Trim());
        _database.SaveSetting(DatabaseService.KeySpotifyLastFmApiKey, SpotifyLastFmApiKey.Trim());

        _oauthCts?.Cancel();
        _oauthCts?.Dispose();
        _oauthCts = new CancellationTokenSource();

        IsSpotifyOAuthBusy = true;
        FeedbackMessage = "Waiting for Spotify PKCE authorization in your browser...";
        try
        {
            var (success, displayName, error) = await _spotifyWebApi.AuthenticatePkceAsync(_oauthCts.Token);
            RefreshSpotifyStatus();
            if (success)
            {
                FeedbackMessage = $"Connected Spotify Web API as {displayName}.";
                _onSettingsApplied(ModuleId);
            }
            else
            {
                FeedbackMessage = string.Empty;
                await _dialogService.ShowErrorAsync("Spotify PKCE Failed", "Could not authorize Spotify Web API.", error);
            }
        }
        finally
        {
            IsSpotifyOAuthBusy = false;
        }
    }

    [RelayCommand]
    private void DisconnectSpotifyApi()
    {
        _spotifyWebApi.Disconnect();
        RefreshSpotifyStatus();
        FeedbackMessage = "Disconnected Spotify Web API session.";
        _onSettingsApplied(ModuleId);
    }

    [RelayCommand]
    private async Task DownloadOrUpdateSoloistAsync()
    {
        if (IsUpdatingSoloist)
        {
            return;
        }

        IsUpdatingSoloist = true;
        FeedbackMessage = "Downloading latest Spotify Soloist build...";
        try
        {
            var (path, err) = await _soloist.EnsureExecutableAsync(
                forceRedownload: true,
                status => FeedbackMessage = status,
                CancellationToken.None);

            if (!string.IsNullOrWhiteSpace(path))
            {
                SoloistPathText = $"Found: {path}";
                FeedbackMessage = "Downloaded latest soloist.exe cleanly.";
                if (_soloist.HasApiKey)
                {
                    await _soloist.EnsureDaemonRunningAsync(CancellationToken.None);
                }

                RefreshSpotifyStatus();
                _onSettingsApplied(ModuleId);
            }
            else
            {
                FeedbackMessage = string.Empty;
                await _dialogService.ShowErrorAsync("Soloist Download Failed", "Could not download soloist.exe.", err);
            }
        }
        finally
        {
            IsUpdatingSoloist = false;
        }
    }

    [RelayCommand]
    private async Task RestartSoloistAsync()
    {
        _database.SaveSetting(DatabaseService.KeySpotifySoloistApiKey, SpotifySoloistApiKey.Trim());
        FeedbackMessage = "Restarting Spotify Soloist engine...";
        var (ok, err) = await _soloist.RestartDaemonAsync(CancellationToken.None);
        RefreshSpotifyStatus();
        if (ok)
        {
            FeedbackMessage = "Spotify Soloist restarted cleanly.";
            _onSettingsApplied(ModuleId);
        }
        else
        {
            FeedbackMessage = string.Empty;
            await _dialogService.ShowErrorAsync("Spotify Soloist Error", "Could not restart soloist.exe.", err);
        }
    }

    [RelayCommand]
    private async Task ClearSpotifyCacheAsync()
    {
        if (IsClearingSpotifyCache)
        {
            return;
        }

        IsClearingSpotifyCache = true;
        FeedbackMessage = "Clearing Spotify audio & art disk cache...";
        try
        {
            _spotifyWebApi.ClearArtCache();
            var (cleared, err) = await _soloist.ClearDiskCacheAsync(CancellationToken.None);
            RefreshSpotifyStatus();
            if (cleared)
            {
                FeedbackMessage = "Cleared Spotify audio & album art disk cache.";
            }
            else
            {
                FeedbackMessage = string.Empty;
                await _dialogService.ShowWarningAsync("Cache Partially Cleared", "Some cached files could not be removed.", err);
            }
        }
        finally
        {
            IsClearingSpotifyCache = false;
        }
    }

    [RelayCommand]
    private async Task RemoveModuleAsync()
    {
        await _onRemoveFromProfile(_module);
    }

    [RelayCommand]
    private void Close() => _onClose();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _oauthCts?.Cancel();
        _oauthCts?.Dispose();
        _oauthCts = null;
    }

    private void UpdateYouTubeCookieStatus()
    {
        var key = SelectedBrowserOption?.Key ?? "none";
        _ytDlp.ResolveCookieBrowserArgument(key, YouTubeProfileOverride, out var status);
        YouTubeCookieStatusText = status;
    }

    private void RefreshTwitchUserStatus()
    {
        IsTwitchUserConnected = _twitch.HasUserAuth;
        TwitchUserStatus = IsTwitchUserConnected
            ? $"Connected as {_twitch.ConnectedUserLogin}"
            : "Not connected (Following tab requires login)";
    }

    private void RefreshSpotifyStatus()
    {
        IsSpotifyApiConnected = _spotifyWebApi.IsConnected;
        SpotifyApiStatusText = IsSpotifyApiConnected
            ? $"Web API Connected as {_spotifyWebApi.UserDisplayName ?? "Spotify User"}"
            : "Not connected (Redirect URI: http://127.0.0.1:8888/callback)";

        var status = _soloist.CurrentStatus;
        SpotifyEngineStatusText = _soloist.IsDaemonRunning
            ? (_soloist.IsAuthenticated
                ? $"Running & Authenticated ({status?.Username ?? _soloist.AuthenticatedUser}) as 'REM'"
                : "Running (Select 'REM' in Spotify Connect once to link)")
            : (_soloist.HasApiKey
                ? "Stopped (Starts automatically when you open Spotify)"
                : "API Key not set");

        var audioBytes = _soloist.GetDiskCacheSizeBytes();
        var artBytes = _spotifyWebApi.GetArtCacheSizeBytes();
        var limitMb = _soloist.ConfiguredCacheSizeMb;
        var limitLabel = limitMb <= 0
            ? "Unlimited"
            : limitMb >= 1024 ? $"{limitMb / 1024.0:0.#} GB" : $"{limitMb} MB";
        SpotifyCacheUsageText = $"Cached on disk: {FormatBytes(audioBytes + artBytes)} (Audio {FormatBytes(audioBytes)} · Art {FormatBytes(artBytes)}) · Limit: {limitLabel}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.#} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.##} GB";
    }

    private void LoadPinnedYouTubeTopics()
    {
        PinnedYouTubeTopics.Clear();
        foreach (var topic in _database.GetProfileYouTubeTopics(_profileId))
        {
            PinnedYouTubeTopics.Add(new PinnedYouTubeTopicRowViewModel(topic, RemoveYouTubeTopic));
        }
    }

    private void RemoveYouTubeTopic(PinnedYouTubeTopicRowViewModel row)
    {
        _database.RemoveProfileYouTubeTopic(_profileId, row.Topic);
        PinnedYouTubeTopics.Remove(row);
        _actionLog.Log("YouTube", $"Removed pinned topic '{row.Topic}'.");
        _onSettingsApplied(ModuleId);
    }

    private void LoadPinnedCategories()
    {
        PinnedCategories.Clear();
        foreach (var cat in _database.GetProfileTwitchCategories(_profileId))
        {
            PinnedCategories.Add(new PinnedCategoryRowViewModel(cat, RemoveCategory));
        }
    }

    private void RemoveCategory(PinnedCategoryRowViewModel row)
    {
        _database.RemoveProfileTwitchCategory(_profileId, row.Name);
        PinnedCategories.Remove(row);
        _actionLog.Log("Twitch", $"Removed pinned category '{row.Name}'.");
        _onSettingsApplied(ModuleId);
    }

    private bool ReadBool(string key, bool defaultValue)
    {
        var raw = _database.GetSetting(key);
        return bool.TryParse(raw, out var val) ? val : defaultValue;
    }
}
