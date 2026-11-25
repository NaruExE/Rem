using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using REM.Models;
using REM.Services;

namespace REM.ViewModels;

public enum SpotifyDetailPage
{
    None,
    ArtistProfile,
    AlbumDetail,
    SavedAlbums,
    TopArtists,
    RemMix
}

public sealed partial class SpotifyArtistCardViewModel : ObservableObject, IDisposable
{
    private readonly Func<SpotifyArtistItem, Task> _onOpen;
    private bool _disposed;

    public SpotifyArtistCardViewModel(SpotifyArtistItem item, Func<SpotifyArtistItem, Task> onOpen)
    {
        Item = item;
        _onOpen = onOpen;
    }

    public SpotifyArtistItem Item { get; }
    public string Name => Item.Name;
    public string Subtitle => !string.IsNullOrWhiteSpace(Item.Subtitle)
        ? Item.Subtitle
        : Item.Followers > 0 ? $"{Item.Followers:N0} followers" : "Artist";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPhoto))]
    private Bitmap? _photoBitmap;

    public bool HasPhoto => PhotoBitmap is not null;

    public void SetPhoto(Bitmap? bmp)
    {
        if (_disposed)
        {
            bmp?.Dispose();
            return;
        }

        var prev = PhotoBitmap;
        PhotoBitmap = bmp;
        if (!ReferenceEquals(prev, bmp))
        {
            prev?.Dispose();
        }
    }

    [RelayCommand]
    private Task OpenAsync() => _onOpen(Item);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var prev = PhotoBitmap;
        PhotoBitmap = null;
        prev?.Dispose();
    }
}

public sealed partial class SpotifyAlbumCardViewModel : ObservableObject, IDisposable
{
    private readonly Func<SpotifyAlbumItem, Task> _onOpen;
    private readonly Func<SpotifyAlbumItem, Task> _onPlay;
    private bool _disposed;

    public SpotifyAlbumCardViewModel(
        SpotifyAlbumItem item,
        Func<SpotifyAlbumItem, Task> onOpen,
        Func<SpotifyAlbumItem, Task> onPlay)
    {
        Item = item;
        _onOpen = onOpen;
        _onPlay = onPlay;
    }

    public SpotifyAlbumItem Item { get; }
    public string Title => Item.Name;
    public string Subtitle
    {
        get
        {
            var year = Item.ReleaseDate.Length >= 4 ? Item.ReleaseDate[..4] : Item.ReleaseDate;
            return string.IsNullOrWhiteSpace(year)
                ? Item.Artists
                : $"{Item.Artists} · {year}";
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    private Bitmap? _coverBitmap;

    public bool HasCover => CoverBitmap is not null;

    public void SetCover(Bitmap? bmp)
    {
        if (_disposed)
        {
            bmp?.Dispose();
            return;
        }

        var prev = CoverBitmap;
        CoverBitmap = bmp;
        if (!ReferenceEquals(prev, bmp))
        {
            prev?.Dispose();
        }
    }

    [RelayCommand]
    private Task OpenAsync() => _onOpen(Item);

    [RelayCommand]
    private Task PlayAsync() => _onPlay(Item);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var prev = CoverBitmap;
        CoverBitmap = null;
        prev?.Dispose();
    }
}

public sealed partial class SpotifyPlaylistCardViewModel : ObservableObject, IDisposable
{
    private readonly Func<SpotifyPlaylistItem, Task> _onOpen;
    private readonly Func<SpotifyPlaylistItem, Task> _onPlay;
    private readonly Func<SpotifyPlaylistItem, Task> _onFollow;
    private bool _disposed;

    public SpotifyPlaylistCardViewModel(
        SpotifyPlaylistItem item,
        Func<SpotifyPlaylistItem, Task> onOpen,
        Func<SpotifyPlaylistItem, Task> onPlay,
        Func<SpotifyPlaylistItem, Task> onFollow)
    {
        Item = item;
        _onOpen = onOpen;
        _onPlay = onPlay;
        _onFollow = onFollow;
    }

    public SpotifyPlaylistItem Item { get; }
    public string Title => Item.Name;
    public string Subtitle => Item.TrackCount > 0
        ? $"{Item.OwnerName} · {Item.TrackCount} tracks"
        : Item.OwnerName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    private Bitmap? _coverBitmap;

    public bool HasCover => CoverBitmap is not null;

    public void SetCover(Bitmap? bmp)
    {
        if (_disposed)
        {
            bmp?.Dispose();
            return;
        }

        var prev = CoverBitmap;
        CoverBitmap = bmp;
        if (!ReferenceEquals(prev, bmp))
        {
            prev?.Dispose();
        }
    }

    [RelayCommand]
    private Task OpenAsync() => _onOpen(Item);

    [RelayCommand]
    private Task PlayAsync() => _onPlay(Item);

    [RelayCommand]
    private Task FollowAsync() => _onFollow(Item);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var prev = CoverBitmap;
        CoverBitmap = null;
        prev?.Dispose();
    }
}

public sealed partial class RemMixCardViewModel : ObservableObject
{
    private readonly Func<string, Task> _onOpen;

    public RemMixCardViewModel(string kind, string title, string subtitle, string badge, string accentHex, Func<string, Task> onOpen)
    {
        Kind = kind;
        Title = title;
        Subtitle = subtitle;
        Badge = badge;
        AccentBrush = new SolidColorBrush(Color.TryParse(accentHex, out var c) ? c : Color.Parse("#1DB954"));
        _onOpen = onOpen;
    }

    public string Kind { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string Badge { get; }
    public IBrush AccentBrush { get; }

    [RelayCommand]
    private Task OpenAsync() => _onOpen(Kind);
}

public sealed partial class LyricsColorSwatchViewModel : ObservableObject
{
    private readonly Action<string> _onSelect;

    public LyricsColorSwatchViewModel(string hex, string name, Action<string> onSelect)
    {
        Hex = hex;
        Name = name;
        ColorBrush = new SolidColorBrush(Color.Parse(hex));
        _onSelect = onSelect;
    }

    public string Hex { get; }
    public string Name { get; }
    public IBrush ColorBrush { get; }

    [ObservableProperty]
    private bool _isSelected;

    [RelayCommand]
    private void Select() => _onSelect(Hex);
}

public sealed partial class SpotifyLyricLineViewModel
{
    [ObservableProperty]
    private bool _isPast;

    [ObservableProperty]
    private bool _showTimestamp = true;

    [ObservableProperty]
    private bool _isLyricsAnimated = true;

    [ObservableProperty]
    private double _drawerFontSize = 14;

    [ObservableProperty]
    private double _fullscreenFontSize = 24;

    [ObservableProperty]
    private TextAlignment _lineTextAlignment = TextAlignment.Left;
}

public sealed partial class SpotifyViewModel
{
    private sealed record NavStateEntry(SpotifyNavSection Section, SpotifyDetailPage DetailPage, string? EntityId, string? FallbackTitle);

    private readonly Dictionary<string, Bitmap> _sharedRowCovers = new(StringComparer.Ordinal);
    private readonly Stack<NavStateEntry> _navHistory = new();
    private bool _isRestoringNav;
    private SpotifyPlaylistItem? _activePlaylistItem;
    private SpotifyArtistItem? _activeArtistItem;
    private SpotifyAlbumItem? _activeAlbumItem;
    private RemMixDescriptor? _activeMix;
    private bool _isEditingPlaylistModal;

    public ObservableCollection<SpotifyArtistCardViewModel> SearchArtists { get; } = [];
    public ObservableCollection<SpotifyAlbumCardViewModel> SearchAlbums { get; } = [];
    public ObservableCollection<SpotifyPlaylistCardViewModel> SearchPlaylists { get; } = [];
    public ObservableCollection<SpotifyAlbumCardViewModel> ArtistAlbums { get; } = [];
    public ObservableCollection<SpotifyArtistCardViewModel> SimilarArtists { get; } = [];
    public ObservableCollection<SpotifyAlbumCardViewModel> SavedAlbums { get; } = [];
    public ObservableCollection<SpotifyArtistCardViewModel> TopArtists { get; } = [];
    public ObservableCollection<RemMixCardViewModel> RemMixCards { get; } = [];
    public ObservableCollection<LyricsColorSwatchViewModel> LyricsColorSwatches { get; } = [];
    public ObservableCollection<string> ActiveArtistTags { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeSection))]
    [NotifyPropertyChangedFor(nameof(IsSearchSection))]
    [NotifyPropertyChangedFor(nameof(IsCollectionSection))]
    [NotifyPropertyChangedFor(nameof(IsArtistProfileSection))]
    [NotifyPropertyChangedFor(nameof(IsAlbumDetailSection))]
    [NotifyPropertyChangedFor(nameof(IsSavedAlbumsSection))]
    [NotifyPropertyChangedFor(nameof(IsTopArtistsSection))]
    [NotifyPropertyChangedFor(nameof(IsRemMixSection))]
    [NotifyPropertyChangedFor(nameof(IsActivePlaylistEditable))]
    private SpotifyDetailPage _activeDetailPage = SpotifyDetailPage.None;

    public bool IsArtistProfileSection => ActiveDetailPage == SpotifyDetailPage.ArtistProfile;
    public bool IsAlbumDetailSection => ActiveDetailPage == SpotifyDetailPage.AlbumDetail;
    public bool IsSavedAlbumsSection => ActiveDetailPage == SpotifyDetailPage.SavedAlbums;
    public bool IsTopArtistsSection => ActiveDetailPage == SpotifyDetailPage.TopArtists;
    public bool IsRemMixSection => ActiveDetailPage == SpotifyDetailPage.RemMix;
    public bool IsActivePlaylistEditable =>
        ActiveSection == SpotifyNavSection.Playlist &&
        ActiveDetailPage == SpotifyDetailPage.None &&
        _activePlaylistItem is not null;

    [ObservableProperty]
    private bool _canNavigateBack;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLyricsBgAmbient))]
    [NotifyPropertyChangedFor(nameof(IsLyricsBgSolid))]
    [NotifyPropertyChangedFor(nameof(IsLyricsBgAlbumTint))]
    [NotifyPropertyChangedFor(nameof(LyricsDrawerBackgroundBrush))]
    [NotifyPropertyChangedFor(nameof(LyricsFullscreenBackgroundBrush))]
    private string _lyricsBgMode = "Ambient";

    public bool IsLyricsBgAmbient => string.Equals(LyricsBgMode, "Ambient", StringComparison.OrdinalIgnoreCase);
    public bool IsLyricsBgSolid => string.Equals(LyricsBgMode, "Solid", StringComparison.OrdinalIgnoreCase);
    public bool IsLyricsBgAlbumTint => string.Equals(LyricsBgMode, "AlbumTint", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LyricsDrawerBackgroundBrush))]
    [NotifyPropertyChangedFor(nameof(LyricsFullscreenBackgroundBrush))]
    private string _lyricsSolidColorHex = "#0B0D14";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LyricsDrawerBackgroundBrush))]
    [NotifyPropertyChangedFor(nameof(LyricsFullscreenBackgroundBrush))]
    private IBrush _lyricsSolidBrush = new SolidColorBrush(Color.Parse("#0B0D14"));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LyricsDrawerBackgroundBrush))]
    [NotifyPropertyChangedFor(nameof(LyricsFullscreenBackgroundBrush))]
    private IBrush _albumTintLyricsBrush = new SolidColorBrush(Color.FromRgb(18, 28, 24));

    private static readonly IBrush DefaultAmbientDrawerBrush = new SolidColorBrush(Color.FromArgb(204, 16, 19, 28));
    private static readonly IBrush DefaultAmbientFullscreenBrush = new SolidColorBrush(Color.FromArgb(242, 11, 14, 23));

    public IBrush LyricsDrawerBackgroundBrush => LyricsBgMode switch
    {
        "Solid" => LyricsSolidBrush,
        "AlbumTint" => AlbumTintLyricsBrush,
        _ => DefaultAmbientDrawerBrush
    };

    public IBrush LyricsFullscreenBackgroundBrush => LyricsBgMode switch
    {
        "Solid" => LyricsSolidBrush,
        "AlbumTint" => AlbumTintLyricsBrush,
        _ => DefaultAmbientFullscreenBrush
    };

    [ObservableProperty]
    private bool _lyricsShowTimestamps = true;

    [ObservableProperty]
    private bool _lyricsAnimate = true;

    [ObservableProperty]
    private double _lyricsFontSize = 14;

    [ObservableProperty]
    private bool _lyricsCentered;

    [ObservableProperty]
    private bool _isLyricsStyleOpen;

    [ObservableProperty]
    private bool _preferDeepCuts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsSearchFilterTracks))]
    [NotifyPropertyChangedFor(nameof(IsSearchFilterArtists))]
    [NotifyPropertyChangedFor(nameof(IsSearchFilterAlbums))]
    [NotifyPropertyChangedFor(nameof(IsSearchFilterPlaylists))]
    [NotifyPropertyChangedFor(nameof(ShowSearchTracksSection))]
    [NotifyPropertyChangedFor(nameof(ShowSearchArtistsSection))]
    [NotifyPropertyChangedFor(nameof(ShowSearchAlbumsSection))]
    [NotifyPropertyChangedFor(nameof(ShowSearchPlaylistsSection))]
    private string _searchFilter = "All";

    public bool IsSearchFilterAll => SearchFilter == "All";
    public bool IsSearchFilterTracks => SearchFilter == "Tracks";
    public bool IsSearchFilterArtists => SearchFilter == "Artists";
    public bool IsSearchFilterAlbums => SearchFilter == "Albums";
    public bool IsSearchFilterPlaylists => SearchFilter == "Playlists";

    public bool ShowSearchTracksSection => (IsSearchFilterAll || IsSearchFilterTracks) && SearchTracks.Count > 0;
    public bool ShowSearchArtistsSection => (IsSearchFilterAll || IsSearchFilterArtists) && SearchArtists.Count > 0;
    public bool ShowSearchAlbumsSection => (IsSearchFilterAll || IsSearchFilterAlbums) && SearchAlbums.Count > 0;
    public bool ShowSearchPlaylistsSection => (IsSearchFilterAll || IsSearchFilterPlaylists) && SearchPlaylists.Count > 0;

    // Artist Profile Properties
    [ObservableProperty]
    private string _activeArtistName = string.Empty;

    [ObservableProperty]
    private string _activeArtistSubtitle = string.Empty;

    [ObservableProperty]
    private string _activeArtistBio = string.Empty;

    [ObservableProperty]
    private bool _hasActiveArtistBio;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArtistFollowLabel))]
    private bool _isActiveArtistFollowed;

    public string ArtistFollowLabel => IsActiveArtistFollowed ? "Following" : "Follow";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveArtistBitmap))]
    private Bitmap? _activeArtistBitmap;

    public bool HasActiveArtistBitmap => ActiveArtistBitmap is not null;

    // Album Detail Properties
    [ObservableProperty]
    private string _activeAlbumTitle = string.Empty;

    [ObservableProperty]
    private string _activeAlbumSubtitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlbumSaveLabel))]
    private bool _isActiveAlbumSaved;

    public string AlbumSaveLabel => IsActiveAlbumSaved ? "Saved" : "Save Album";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveAlbumBitmap))]
    private Bitmap? _activeAlbumBitmap;

    public bool HasActiveAlbumBitmap => ActiveAlbumBitmap is not null;

    // Top Artists Range State
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTopRangeShort))]
    [NotifyPropertyChangedFor(nameof(IsTopRangeMedium))]
    [NotifyPropertyChangedFor(nameof(IsTopRangeLong))]
    private string _topArtistsRange = "short_term";

    public bool IsTopRangeShort => TopArtistsRange == "short_term";
    public bool IsTopRangeMedium => TopArtistsRange == "medium_term";
    public bool IsTopRangeLong => TopArtistsRange == "long_term";

    // REM Mix Page Properties
    [ObservableProperty]
    private string _activeMixTitle = string.Empty;

    [ObservableProperty]
    private string _activeMixSubtitle = string.Empty;

    [ObservableProperty]
    private string _activeMixBadge = "MIX";

    // Inline Playlist Create / Edit Modal
    [ObservableProperty]
    private bool _isPlaylistModalOpen;

    [ObservableProperty]
    private string _playlistModalTitleText = "Create Playlist";

    [ObservableProperty]
    private string _playlistModalName = string.Empty;

    [ObservableProperty]
    private string _playlistModalDescription = string.Empty;

    [ObservableProperty]
    private bool _playlistModalIsPublic;

    private void InitializeLyricsCustomization()
    {
        var swatches = new (string Hex, string Name)[]
        {
            ("#0B0D14", "Obsidian"),
            ("#000000", "Pitch Black"),
            ("#111B2E", "Midnight"),
            ("#171126", "Velvet"),
            ("#0F211B", "Forest"),
            ("#241418", "Bordeaux"),
            ("#1C1B18", "Slate")
        };

        LyricsColorSwatches.Clear();
        foreach (var (hex, name) in swatches)
        {
            LyricsColorSwatches.Add(new LyricsColorSwatchViewModel(hex, name, SelectLyricsSwatch));
        }

        LoadLyricsCustomizationFromSettings();
    }

    private void LoadLyricsCustomizationFromSettings()
    {
        var mode = _database.GetSetting(DatabaseService.KeySpotifyLyricsBgMode);
        LyricsBgMode = mode is "Solid" or "AlbumTint" or "Ambient" ? mode : "Ambient";

        var hex = _database.GetSetting(DatabaseService.KeySpotifyLyricsSolidColor);
        if (string.IsNullOrWhiteSpace(hex) || !Color.TryParse(hex, out _))
        {
            hex = "#0B0D14";
        }

        LyricsSolidColorHex = hex;
        ApplySolidColorHex(hex, save: false);

        LyricsShowTimestamps = ReadBoolSetting(DatabaseService.KeySpotifyLyricsShowTimestamps, true);
        LyricsAnimate = ReadBoolSetting(DatabaseService.KeySpotifyLyricsAnimate, true);
        LyricsCentered = ReadBoolSetting(DatabaseService.KeySpotifyLyricsCentered, false);
        PreferDeepCuts = ReadBoolSetting(DatabaseService.KeySpotifyPreferDeepCuts, false);

        var fontRaw = _database.GetSetting(DatabaseService.KeySpotifyLyricsFontSize);
        if (double.TryParse(fontRaw, out var fs))
        {
            LyricsFontSize = Math.Clamp(fs, 12, 22);
        }

        SyncLyricLinesVisualOptions();
    }

    partial void OnLyricsShowTimestampsChanged(bool value)
    {
        if (_disposed) return;
        _database.SaveSetting(DatabaseService.KeySpotifyLyricsShowTimestamps, value.ToString());
        SyncLyricLinesVisualOptions();
    }

    partial void OnLyricsAnimateChanged(bool value)
    {
        if (_disposed) return;
        _database.SaveSetting(DatabaseService.KeySpotifyLyricsAnimate, value.ToString());
        SyncLyricLinesVisualOptions();
    }

    partial void OnLyricsFontSizeChanged(double value)
    {
        if (_disposed) return;
        _database.SaveSetting(DatabaseService.KeySpotifyLyricsFontSize, Math.Round(Math.Clamp(value, 12, 22)).ToString());
        SyncLyricLinesVisualOptions();
    }

    partial void OnLyricsCenteredChanged(bool value)
    {
        if (_disposed) return;
        _database.SaveSetting(DatabaseService.KeySpotifyLyricsCentered, value.ToString());
        SyncLyricLinesVisualOptions();
    }

    partial void OnPreferDeepCutsChanged(bool value)
    {
        if (_disposed) return;
        _database.SaveSetting(DatabaseService.KeySpotifyPreferDeepCuts, value.ToString());
    }

    private void SyncLyricLinesVisualOptions()
    {
        var drawerSize = Math.Clamp(LyricsFontSize, 12, 22);
        var fullSize = drawerSize + 10;
        var align = LyricsCentered ? TextAlignment.Center : TextAlignment.Left;

        foreach (var line in LyricLines)
        {
            line.ShowTimestamp = LyricsShowTimestamps;
            line.IsLyricsAnimated = LyricsAnimate;
            line.DrawerFontSize = drawerSize;
            line.FullscreenFontSize = fullSize;
            line.LineTextAlignment = align;
        }
    }

    private void UpdateAlbumTintFromBytes(byte[] imageBytes)
    {
        var dominant = DominantColorExtractor.ExtractDominantColor(imageBytes);
        var tinted = Color.FromRgb(
            (byte)Math.Clamp(12 + dominant.R * 0.24, 0, 96),
            (byte)Math.Clamp(14 + dominant.G * 0.24, 0, 96),
            (byte)Math.Clamp(20 + dominant.B * 0.26, 0, 108));

        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                AlbumTintLyricsBrush = new SolidColorBrush(tinted);
            }
        });
    }

    [RelayCommand]
    private void ToggleLyricsStyle() => IsLyricsStyleOpen = !IsLyricsStyleOpen;

    [RelayCommand]
    private void SetLyricsBgMode(string? mode)
    {
        if (mode is "Ambient" or "Solid" or "AlbumTint")
        {
            LyricsBgMode = mode;
            _database.SaveSetting(DatabaseService.KeySpotifyLyricsBgMode, mode);
        }
    }

    [RelayCommand]
    private void ApplyCustomLyricsColor()
    {
        var hex = LyricsSolidColorHex.Trim();
        if (!hex.StartsWith('#'))
        {
            hex = "#" + hex;
        }

        if (Color.TryParse(hex, out _))
        {
            ApplySolidColorHex(hex, save: true);
            LyricsBgMode = "Solid";
            _database.SaveSetting(DatabaseService.KeySpotifyLyricsBgMode, "Solid");
        }
        else
        {
            ShowToast("Enter a valid hex color like #111B2E");
        }
    }

    private void SelectLyricsSwatch(string hex)
    {
        LyricsSolidColorHex = hex;
        ApplySolidColorHex(hex, save: true);
        LyricsBgMode = "Solid";
        _database.SaveSetting(DatabaseService.KeySpotifyLyricsBgMode, "Solid");
    }

    private void ApplySolidColorHex(string hex, bool save)
    {
        if (!Color.TryParse(hex, out var parsed))
        {
            return;
        }

        var opaque = Color.FromRgb(parsed.R, parsed.G, parsed.B);
        LyricsSolidBrush = new SolidColorBrush(opaque);

        foreach (var sw in LyricsColorSwatches)
        {
            sw.IsSelected = string.Equals(sw.Hex, hex, StringComparison.OrdinalIgnoreCase);
        }

        if (save)
        {
            _database.SaveSetting(DatabaseService.KeySpotifyLyricsSolidColor, hex);
        }
    }

    private void SeedRemMixCards()
    {
        RemMixCards.Clear();
        RemMixCards.Add(new RemMixCardViewModel("discover", "Discover Weekly", "Fresh finds filtered against your library", "WEEKLY", "#1DB954", OpenRemMixCommandAsync));
        RemMixCards.Add(new RemMixCardViewModel("daylist", "Your Daylist", "Tunes tuned to this time of day", "NOW", "#F59E0B", OpenRemMixCommandAsync));
        RemMixCards.Add(new RemMixCardViewModel("daily1", "Daily Mix 1", "Favorite artists & close vibes", "MIX 1", "#38BDF8", OpenRemMixCommandAsync));
        RemMixCards.Add(new RemMixCardViewModel("daily2", "Daily Mix 2", "Second rotation mood & genre", "MIX 2", "#A78BFA", OpenRemMixCommandAsync));
        RemMixCards.Add(new RemMixCardViewModel("daily3", "Daily Mix 3", "Deep cuts & fresh moods", "MIX 3", "#F472B6", OpenRemMixCommandAsync));
        RemMixCards.Add(new RemMixCardViewModel("release", "Release Radar", "New drops from artists you follow", "NEW", "#FB7185", OpenRemMixCommandAsync));
    }

    private Task OpenRemMixCommandAsync(string kind) => OpenRemMixAsync(kind, forceRefresh: false);

    private void PushCurrentToNavHistory()
    {
        if (_isRestoringNav)
        {
            return;
        }

        var entityId = ActiveDetailPage switch
        {
            SpotifyDetailPage.ArtistProfile => _activeArtistItem?.Id,
            SpotifyDetailPage.AlbumDetail => _activeAlbumItem?.Id,
            SpotifyDetailPage.RemMix => _activeMix?.Key,
            SpotifyDetailPage.None when ActiveSection == SpotifyNavSection.Playlist => _activePlaylistItem?.Id,
            _ => null
        };

        var title = ActiveDetailPage switch
        {
            SpotifyDetailPage.ArtistProfile => ActiveArtistName,
            SpotifyDetailPage.AlbumDetail => ActiveAlbumTitle,
            SpotifyDetailPage.RemMix => ActiveMixTitle,
            _ => CollectionTitle
        };

        if (_navHistory.Count >= 12)
        {
            var trimmed = _navHistory.Reverse().Skip(1).ToList();
            _navHistory.Clear();
            foreach (var entry in trimmed)
            {
                _navHistory.Push(entry);
            }
        }

        _navHistory.Push(new NavStateEntry(ActiveSection, ActiveDetailPage, entityId, title));
        CanNavigateBack = _navHistory.Count > 0;
    }

    private void ResetToStandardSection()
    {
        _activePlaylistItem = null;
        if (ActiveDetailPage != SpotifyDetailPage.None)
        {
            ActiveDetailPage = SpotifyDetailPage.None;
            ClearDetailCollections();
        }
    }

    [RelayCommand]
    private async Task NavigateBackAsync()
    {
        if (_navHistory.Count == 0 || _disposed)
        {
            CanNavigateBack = false;
            return;
        }

        var prev = _navHistory.Pop();
        CanNavigateBack = _navHistory.Count > 0;
        _isRestoringNav = true;
        try
        {
            switch (prev.DetailPage)
            {
                case SpotifyDetailPage.ArtistProfile when !string.IsNullOrWhiteSpace(prev.EntityId):
                    await OpenArtistProfileAsync(prev.EntityId, prev.FallbackTitle);
                    break;
                case SpotifyDetailPage.AlbumDetail when !string.IsNullOrWhiteSpace(prev.EntityId):
                    await OpenAlbumDetailAsync(prev.EntityId, prev.FallbackTitle);
                    break;
                case SpotifyDetailPage.SavedAlbums:
                    await SelectSavedAlbumsAsync();
                    break;
                case SpotifyDetailPage.TopArtists:
                    await SelectTopArtistsAsync();
                    break;
                default:
                    ActiveDetailPage = SpotifyDetailPage.None;
                    ClearDetailCollections();
                    if (prev.Section == SpotifyNavSection.Search) SelectSearch();
                    else if (prev.Section == SpotifyNavSection.LikedSongs) await SelectLikedSongsAsync();
                    else if (prev.Section == SpotifyNavSection.Playlist && !string.IsNullOrWhiteSpace(prev.EntityId))
                    {
                        var nav = Playlists.FirstOrDefault(p => string.Equals(p.Item.Id, prev.EntityId, StringComparison.Ordinal));
                        if (nav is not null) await SelectPlaylistAsync(nav);
                        else await SelectHomeAsync();
                    }
                    else await SelectHomeAsync();
                    break;
            }
        }
        finally
        {
            _isRestoringNav = false;
        }
    }

    [RelayCommand]
    private void SetSearchFilter(string? filter)
    {
        if (filter is "All" or "Tracks" or "Artists" or "Albums" or "Playlists")
        {
            SearchFilter = filter;
            NotifySearchVisibility();
        }
    }

    private void NotifySearchVisibility()
    {
        OnPropertyChanged(nameof(ShowSearchTracksSection));
        OnPropertyChanged(nameof(ShowSearchArtistsSection));
        OnPropertyChanged(nameof(ShowSearchAlbumsSection));
        OnPropertyChanged(nameof(ShowSearchPlaylistsSection));
    }

    [RelayCommand]
    private async Task SelectSavedAlbumsAsync()
    {
        if (!_isRestoringNav && ActiveDetailPage != SpotifyDetailPage.SavedAlbums)
        {
            PushCurrentToNavHistory();
        }

        ActiveDetailPage = SpotifyDetailPage.SavedAlbums;
        DeselectPlaylists();
        FeedMessage = string.Empty;

        if (!_webApi.IsConnected)
        {
            ClearAlbumCards(SavedAlbums);
            FeedMessage = "Connect Spotify (PKCE) in Settings to view your Saved Albums.";
            return;
        }

        var ct = ResetWorkToken();
        IsBusy = true;
        try
        {
            var albums = await _webApi.GetSavedAlbumsAsync(30, ct);
            ClearAlbumCards(SavedAlbums);
            foreach (var al in albums)
            {
                SavedAlbums.Add(new SpotifyAlbumCardViewModel(al, OpenAlbumFromCardAsync, PlayAlbumCardAsync));
            }

            if (albums.Count == 0)
            {
                FeedMessage = !string.IsNullOrWhiteSpace(_webApi.LastApiError)
                    ? _webApi.LastApiError
                    : "No saved albums yet.";
            }
            else
            {
                _ = LoadAlbumCardCoversAsync(SavedAlbums.ToList());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FeedMessage = ex.Message; }
        finally { if (!_disposed) IsBusy = false; }
    }

    [RelayCommand]
    private Task SelectTopArtistsAsync() => LoadTopArtistsAsync(TopArtistsRange);

    [RelayCommand]
    private async Task SetTopArtistsRangeAsync(string? range)
    {
        if (range is "short_term" or "medium_term" or "long_term")
        {
            TopArtistsRange = range;
            await LoadTopArtistsAsync(range);
        }
    }

    private async Task LoadTopArtistsAsync(string range)
    {
        if (!_isRestoringNav && ActiveDetailPage != SpotifyDetailPage.TopArtists)
        {
            PushCurrentToNavHistory();
        }

        ActiveDetailPage = SpotifyDetailPage.TopArtists;
        DeselectPlaylists();
        FeedMessage = string.Empty;

        if (!_webApi.IsConnected)
        {
            ClearArtistCards(TopArtists);
            FeedMessage = "Connect Spotify (PKCE) in Settings to view your Top Artists.";
            return;
        }

        var ct = ResetWorkToken();
        IsBusy = true;
        try
        {
            var artists = await _webApi.GetTopArtistsAsync(18, range, ct);
            ClearArtistCards(TopArtists);
            foreach (var a in artists)
            {
                TopArtists.Add(new SpotifyArtistCardViewModel(a, OpenArtistFromCardAsync));
            }

            if (artists.Count == 0)
            {
                FeedMessage = !string.IsNullOrWhiteSpace(_webApi.LastApiError)
                    ? _webApi.LastApiError
                    : "Not enough Spotify listening history for Top Artists yet.";
            }
            else
            {
                _ = LoadArtistCardPhotosAsync(TopArtists.ToList());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FeedMessage = ex.Message; }
        finally { if (!_disposed) IsBusy = false; }
    }

    private Task OpenArtistFromCardAsync(SpotifyArtistItem artist) => OpenArtistProfileAsync(artist.Id, artist.Name);
    private Task OpenAlbumFromCardAsync(SpotifyAlbumItem album) => OpenAlbumDetailAsync(album.Id, album.Name);

    private async Task PlayAlbumCardAsync(SpotifyAlbumItem album)
    {
        if (!string.IsNullOrWhiteSpace(album.Uri))
        {
            await _soloist.PlayAsync(album.Uri);
            ShowToast($"Playing {album.Name}");
        }
    }

    private Task OpenSearchedPlaylistAsync(SpotifyPlaylistItem playlist) =>
        SelectExternalPlaylistAsync(playlist);

    private async Task FollowSearchedPlaylistAsync(SpotifyPlaylistItem playlist)
    {
        if (!_webApi.IsConnected || string.IsNullOrWhiteSpace(playlist.Id))
        {
            ShowToast("Connect Spotify in Settings to save playlists");
            return;
        }

        var ok = await _webApi.FollowPlaylistAsync(playlist.Id, CancellationToken.None);
        if (ok)
        {
            ShowToast($"Saved '{playlist.Name}' to your library");
            await LoadPlaylistsAsync();
        }
        else
        {
            ShowToast($"Could not save '{playlist.Name}' right now");
        }
    }

    [RelayCommand]
    private async Task OpenCurrentTrackArtistAsync()
    {
        if (!HasCurrentTrack || _lastTrackMeta is null)
        {
            return;
        }

        var primary = _lastTrackMeta.ArtistNames.FirstOrDefault() ?? CurrentTrackArtist;
        if (string.IsNullOrWhiteSpace(primary))
        {
            return;
        }

        await OpenArtistProfileByNameAsync(primary);
    }

    [RelayCommand]
    private async Task OpenCurrentTrackAlbumAsync()
    {
        if (!HasCurrentTrack || _lastTrackMeta is null || !_webApi.IsConnected)
        {
            return;
        }

        if (TryExtractSpotifyTrackId(_lastTrackMeta.Uri, out var trackId))
        {
            var tr = await _webApi.GetTrackByIdAsync(trackId, CancellationToken.None);
            if (tr is not null && !string.IsNullOrWhiteSpace(tr.AlbumId))
            {
                await OpenAlbumDetailAsync(tr.AlbumId, tr.AlbumName);
                return;
            }
        }

        ShowToast("Album details unavailable for this track");
    }

    public Task OpenArtistProfileByNameAsync(string artistName) => OpenArtistProfileAsync(string.Empty, artistName);

    public async Task OpenTrackArtistFromMenuAsync(SpotifyTrackItem track)
    {
        if (!string.IsNullOrWhiteSpace(track.PrimaryArtistId))
        {
            await OpenArtistProfileAsync(track.PrimaryArtistId, track.Artists);
        }
        else
        {
            var primary = track.Artists.Split(',')[0].Trim();
            await OpenArtistProfileByNameAsync(primary);
        }
    }

    public async Task OpenTrackAlbumFromMenuAsync(SpotifyTrackItem track)
    {
        if (!string.IsNullOrWhiteSpace(track.AlbumId))
        {
            await OpenAlbumDetailAsync(track.AlbumId, track.AlbumName);
            return;
        }

        if (!string.IsNullOrWhiteSpace(track.Id))
        {
            var full = await _webApi.GetTrackByIdAsync(track.Id, CancellationToken.None);
            if (full is not null && !string.IsNullOrWhiteSpace(full.AlbumId))
            {
                await OpenAlbumDetailAsync(full.AlbumId, full.AlbumName);
                return;
            }
        }

        ShowToast("Album details unavailable for this track");
    }

    public async Task OpenArtistProfileAsync(string artistId, string? fallbackName)
    {
        if (!_webApi.IsConnected)
        {
            ShowToast("Connect Spotify (PKCE) in Settings to view Artist Profiles");
            return;
        }

        if (!_isRestoringNav)
        {
            PushCurrentToNavHistory();
        }

        DeselectPlaylists();
        ActiveDetailPage = SpotifyDetailPage.ArtistProfile;
        FeedMessage = string.Empty;

        var ct = ResetWorkToken();
        IsBusy = true;
        try
        {
            SpotifyArtistItem? artist = null;
            if (!string.IsNullOrWhiteSpace(artistId))
            {
                artist = await _webApi.GetArtistAsync(artistId, ct);
            }

            if (artist is null && !string.IsNullOrWhiteSpace(fallbackName))
            {
                var first = fallbackName.Split(',')[0].Trim();
                artist = await _webApi.SearchArtistByNameAsync(first, ct);
            }

            if (artist is null)
            {
                FeedMessage = !string.IsNullOrWhiteSpace(_webApi.LastApiError)
                    ? _webApi.LastApiError
                    : $"Could not load profile for \"{fallbackName}\".";
                return;
            }

            _activeArtistItem = artist;
            _activeCollectionContextUri = artist.Uri;
            ActiveArtistName = artist.Name;
            ActiveArtistSubtitle = artist.Followers > 0
                ? $"{artist.Followers:N0} followers"
                : string.Join(" · ", (artist.Genres ?? []).Take(3));

            var albumsTask = _webApi.GetArtistAlbumsAsync(artist.Id, 10, ct);
            var followTask = _webApi.IsArtistFollowedAsync(artist.Id, ct);
            var lfmTask = _lastFm.GetArtistProfileAsync(artist.Name, ct);
            var lfmTopTracksTask = _lastFm.GetArtistTopTrackNamesAsync(artist.Name, 15, ct);
            await Task.WhenAll(albumsTask, followTask, lfmTask, lfmTopTracksTask);

            if (_disposed || ct.IsCancellationRequested) return;

            var albums = await albumsTask;
            var followed = await followTask;
            var lfmProfile = await lfmTask;
            var lfmTopNames = await lfmTopTracksTask;
            var topTracks = await _webApi.GetArtistTopTracksAsync(artist.Id, artist.Name, ct, albums, lfmTopNames);
            if (_disposed || ct.IsCancellationRequested) return;

            if (topTracks.Count == 0 && albums.Count == 0 && !string.IsNullOrWhiteSpace(_webApi.LastApiError))
            {
                FeedMessage = _webApi.LastApiError;
            }

            IsActiveArtistFollowed = followed ?? false;

            ActiveArtistTags.Clear();
            var tagSource = lfmProfile?.Tags is { Count: > 0 } ? lfmProfile.Tags : (artist.Genres ?? []);
            foreach (var tag in tagSource.Take(5))
            {
                ActiveArtistTags.Add(tag);
            }

            ActiveArtistBio = lfmProfile?.BioSummary ?? string.Empty;
            HasActiveArtistBio = !string.IsNullOrWhiteSpace(ActiveArtistBio);
            if (lfmProfile is { Listeners: > 0 } && artist.Followers == 0)
            {
                ActiveArtistSubtitle = $"{lfmProfile.Listeners:N0} Last.fm listeners";
            }

            ClearTrackCollection(CollectionTracks);
            for (var i = 0; i < topTracks.Count; i++)
            {
                CollectionTracks.Add(new SpotifyTrackRowViewModel(i + 1, topTracks[i], ShowTrackCovers, PlayTrackItemAsync, ToggleRowLikeAsync));
            }

            ClearAlbumCards(ArtistAlbums);
            foreach (var al in albums)
            {
                ArtistAlbums.Add(new SpotifyAlbumCardViewModel(al, OpenAlbumFromCardAsync, PlayAlbumCardAsync));
            }

            ClearArtistCards(SimilarArtists);

            if (ShowTrackCovers && CollectionTracks.Count > 0)
            {
                var rows = CollectionTracks.ToList();
                _ = SyncRowLikedStatesAsync(rows);
                _ = LoadRowCoversAsync(rows);
            }

            _ = LoadSingleArtistBannerBitmapAsync(artist.ImageUrl, ct);
            _ = LoadAlbumCardCoversAsync(ArtistAlbums.ToList());

            if (lfmProfile?.SimilarArtistNames is { Count: > 0 })
            {
                _ = LoadSimilarArtistsAsync(lfmProfile.SimilarArtistNames.Take(4).ToList(), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FeedMessage = ex.Message; }
        finally { if (!_disposed) IsBusy = false; }
    }

    private async Task LoadSimilarArtistsAsync(List<string> names, CancellationToken ct)
    {
        var resolved = new List<SpotifyArtistCardViewModel>();
        for (var i = 0; i < names.Count; i++)
        {
            if (ct.IsCancellationRequested || _disposed || _webApi.IsRateLimited) break;
            if (i > 0)
            {
                await Task.Delay(280, ct);
            }

            var a = await _webApi.SearchArtistByNameAsync(names[i], ct);
            if (a is not null)
            {
                resolved.Add(new SpotifyArtistCardViewModel(a, OpenArtistFromCardAsync));
            }
        }

        if (_disposed || ct.IsCancellationRequested || resolved.Count == 0)
        {
            foreach (var r in resolved) r.Dispose();
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || ct.IsCancellationRequested)
            {
                foreach (var r in resolved) r.Dispose();
                return;
            }

            ClearArtistCards(SimilarArtists);
            foreach (var r in resolved)
            {
                SimilarArtists.Add(r);
            }

            _ = LoadArtistCardPhotosAsync(SimilarArtists.ToList());
        });
    }

    [RelayCommand]
    private async Task ToggleFollowActiveArtistAsync()
    {
        if (_activeArtistItem is null || !_webApi.IsConnected) return;

        var desired = !IsActiveArtistFollowed;
        IsActiveArtistFollowed = desired;
        var ok = await _webApi.SetArtistFollowedAsync(_activeArtistItem.Id, desired, CancellationToken.None);
        if (!ok)
        {
            IsActiveArtistFollowed = !desired;
            ShowToast("Could not update follow status (reconnect Spotify to grant follow scope)");
        }
        else
        {
            ShowToast(desired ? $"Following {_activeArtistItem.Name}" : $"Unfollowed {_activeArtistItem.Name}");
        }
    }

    [RelayCommand]
    private async Task StartActiveArtistRadioAsync()
    {
        if (_activeArtistItem is null) return;
        await StartArtistRadioAsync(_activeArtistItem);
    }

    public async Task StartSongRadioAsync(SpotifyTrackItem track)
    {
        if (!_webApi.IsConnected)
        {
            ShowToast("Connect Spotify in Settings to build Song Radio");
            return;
        }

        if (!_isRestoringNav) PushCurrentToNavHistory();
        DeselectPlaylists();
        ActiveDetailPage = SpotifyDetailPage.RemMix;
        ActiveMixTitle = $"{track.Name} Radio";
        ActiveMixSubtitle = $"Building radio from {track.Artists}...";
        ActiveMixBadge = "SONG RADIO";
        _activeCollectionContextUri = null;
        FeedMessage = string.Empty;

        var ct = ResetWorkToken();
        IsBusy = true;
        try
        {
            var mix = await _mixGenerator.BuildSongRadioAsync(track, PreferDeepCuts, ct);
            ApplyMixResult(mix);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FeedMessage = ex.Message; }
        finally { if (!_disposed) IsBusy = false; }
    }

    public async Task StartArtistRadioAsync(SpotifyArtistItem artist)
    {
        if (!_webApi.IsConnected) return;

        if (!_isRestoringNav) PushCurrentToNavHistory();
        DeselectPlaylists();
        ActiveDetailPage = SpotifyDetailPage.RemMix;
        ActiveMixTitle = $"{artist.Name} Radio";
        ActiveMixSubtitle = "Building artist radio...";
        ActiveMixBadge = "ARTIST RADIO";
        _activeCollectionContextUri = null;
        FeedMessage = string.Empty;

        var ct = ResetWorkToken();
        IsBusy = true;
        try
        {
            var mix = await _mixGenerator.BuildArtistRadioAsync(artist, PreferDeepCuts, ct);
            ApplyMixResult(mix);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FeedMessage = ex.Message; }
        finally { if (!_disposed) IsBusy = false; }
    }

    private async Task OpenRemMixAsync(string kind, bool forceRefresh)
    {
        if (!_webApi.IsConnected)
        {
            ShowToast("Connect Spotify (PKCE) in Settings for personal REM mixes");
            return;
        }

        if (!_isRestoringNav && ActiveDetailPage != SpotifyDetailPage.RemMix)
        {
            PushCurrentToNavHistory();
        }

        DeselectPlaylists();
        ActiveDetailPage = SpotifyDetailPage.RemMix;
        _activeCollectionContextUri = null;
        FeedMessage = string.Empty;
        ActiveMixTitle = kind switch
        {
            "discover" => "REM Discover Weekly",
            "daylist" => "Your Daylist",
            "daily1" => "Daily Mix 1",
            "daily2" => "Daily Mix 2",
            "daily3" => "Daily Mix 3",
            _ => "Release Radar"
        };
        ActiveMixSubtitle = "Curating tracks from your listening taste...";
        ActiveMixBadge = "REM MIX";

        var ct = ResetWorkToken();
        IsBusy = true;
        try
        {
            var mix = kind switch
            {
                "discover" => await _mixGenerator.BuildDiscoverWeeklyAsync(forceRefresh, PreferDeepCuts, ct),
                "daylist" => await _mixGenerator.BuildDaylistAsync(forceRefresh, ct),
                "daily1" => await _mixGenerator.BuildDailyMixAsync(1, forceRefresh, ct),
                "daily2" => await _mixGenerator.BuildDailyMixAsync(2, forceRefresh, ct),
                "daily3" => await _mixGenerator.BuildDailyMixAsync(3, forceRefresh, ct),
                _ => await _mixGenerator.BuildReleaseRadarAsync(forceRefresh, ct)
            };

            ApplyMixResult(mix);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FeedMessage = ex.Message; }
        finally { if (!_disposed) IsBusy = false; }
    }

    [RelayCommand]
    private async Task RefreshActiveMixAsync()
    {
        if (_activeMix is null) return;
        var kind = _activeMix.Key switch
        {
            "rem-discover-weekly" => "discover",
            var k when k.StartsWith("rem-daylist", StringComparison.Ordinal) => "daylist",
            "rem-daily-mix-1" => "daily1",
            "rem-daily-mix-2" => "daily2",
            "rem-daily-mix-3" => "daily3",
            _ => "release"
        };
        await OpenRemMixAsync(kind, forceRefresh: true);
    }

    private void ApplyMixResult(RemMixDescriptor mix)
    {
        _activeMix = mix;
        ActiveMixTitle = mix.Title;
        ActiveMixSubtitle = mix.Subtitle;
        ActiveMixBadge = mix.Badge;

        ClearTrackCollection(CollectionTracks);
        for (var i = 0; i < mix.Tracks.Count; i++)
        {
            CollectionTracks.Add(new SpotifyTrackRowViewModel(i + 1, mix.Tracks[i], ShowTrackCovers, PlayTrackItemAsync, ToggleRowLikeAsync));
        }

        if (mix.Tracks.Count == 0)
        {
            FeedMessage = !string.IsNullOrWhiteSpace(_webApi.LastApiError)
                ? _webApi.LastApiError
                : "Could not gather tracks for this mix yet — play a few songs or add a Last.fm API key in Settings.";
        }
        else
        {
            var rows = CollectionTracks.ToList();
            _ = SyncRowLikedStatesAsync(rows);
            if (ShowTrackCovers) _ = LoadRowCoversAsync(rows);
        }
    }

    [RelayCommand]
    private async Task SaveActiveMixToSpotifyAsync()
    {
        if (!_webApi.IsConnected || CollectionTracks.Count == 0)
        {
            ShowToast("Play or open a mix with tracks first");
            return;
        }

        var name = string.IsNullOrWhiteSpace(ActiveMixTitle) ? "REM Mix" : ActiveMixTitle;
        var (created, needsReauth, err) = await _webApi.CreatePlaylistAsync(
            name,
            $"Generated by REM · {ActiveMixSubtitle}",
            isPublic: false,
            CancellationToken.None);

        if (created is null)
        {
            if (needsReauth)
            {
                await _dialogService.ShowWarningAsync("Spotify Permission Needed", err ?? "Reconnect Spotify in Settings.");
            }
            else
            {
                await _dialogService.ShowErrorAsync("Create Playlist Failed", "Could not create Spotify playlist.", err);
            }
            return;
        }

        var uris = CollectionTracks.Select(t => t.Item.Uri).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        var (added, addErr) = await _webApi.AddTracksToPlaylistAsync(created.Id, uris, CancellationToken.None);
        if (added)
        {
            ShowToast($"Saved '{name}' ({uris.Count} tracks) to Spotify");
            await LoadPlaylistsAsync();
        }
        else
        {
            ShowToast(addErr ?? "Created playlist, but some tracks failed to add");
        }
    }

    public async Task OpenAlbumDetailAsync(string albumId, string? fallbackTitle)
    {
        if (!_webApi.IsConnected || string.IsNullOrWhiteSpace(albumId))
        {
            ShowToast("Connect Spotify (PKCE) in Settings to view Albums");
            return;
        }

        if (!_isRestoringNav) PushCurrentToNavHistory();
        DeselectPlaylists();
        ActiveDetailPage = SpotifyDetailPage.AlbumDetail;
        ActiveAlbumTitle = fallbackTitle ?? "Album";
        ActiveAlbumSubtitle = "Loading album...";
        FeedMessage = string.Empty;

        var ct = ResetWorkToken();
        IsBusy = true;
        try
        {
            var albumTask = _webApi.GetAlbumAsync(albumId, ct);
            var savedTask = _webApi.IsAlbumSavedAsync(albumId, ct);
            await Task.WhenAll(albumTask, savedTask);
            if (_disposed || ct.IsCancellationRequested) return;

            var (album, tracks) = await albumTask;
            IsActiveAlbumSaved = await savedTask ?? false;
            _activeAlbumItem = album;
            _activeCollectionContextUri = album?.Uri;

            if (album is not null)
            {
                ActiveAlbumTitle = album.Name;
                ActiveAlbumSubtitle = $"{album.Artists} · {album.ReleaseDate} · {tracks.Count} tracks";
                _ = LoadSingleAlbumCoverBitmapAsync(album.ImageUrl, ct);
            }

            ClearTrackCollection(CollectionTracks);
            for (var i = 0; i < tracks.Count; i++)
            {
                CollectionTracks.Add(new SpotifyTrackRowViewModel(i + 1, tracks[i], ShowTrackCovers, PlayTrackItemAsync, ToggleRowLikeAsync));
            }

            if (tracks.Count > 0)
            {
                var rows = CollectionTracks.ToList();
                _ = SyncRowLikedStatesAsync(rows);
                if (ShowTrackCovers) _ = LoadRowCoversAsync(rows);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FeedMessage = ex.Message; }
        finally { if (!_disposed) IsBusy = false; }
    }

    [RelayCommand]
    private async Task ToggleSaveActiveAlbumAsync()
    {
        if (_activeAlbumItem is null || !_webApi.IsConnected) return;
        var desired = !IsActiveAlbumSaved;
        IsActiveAlbumSaved = desired;
        var ok = await _webApi.SetAlbumSavedAsync(_activeAlbumItem.Id, desired, CancellationToken.None);
        if (!ok)
        {
            IsActiveAlbumSaved = !desired;
            ShowToast("Could not update saved album");
        }
        else
        {
            ShowToast(desired ? $"Saved '{_activeAlbumItem.Name}'" : $"Removed '{_activeAlbumItem.Name}'");
        }
    }

    [RelayCommand]
    private async Task OpenActiveAlbumArtistAsync()
    {
        if (_activeAlbumItem is null) return;
        if (!string.IsNullOrWhiteSpace(_activeAlbumItem.PrimaryArtistId))
        {
            await OpenArtistProfileAsync(_activeAlbumItem.PrimaryArtistId, _activeAlbumItem.Artists);
        }
        else
        {
            await OpenArtistProfileByNameAsync(_activeAlbumItem.Artists.Split(',')[0].Trim());
        }
    }

    [RelayCommand]
    private void OpenCreatePlaylistModal()
    {
        if (!_webApi.IsConnected)
        {
            ShowToast("Connect Spotify (PKCE) in Settings to create playlists");
            return;
        }

        _isEditingPlaylistModal = false;
        PlaylistModalTitleText = "Create New Playlist";
        PlaylistModalName = string.Empty;
        PlaylistModalDescription = string.Empty;
        PlaylistModalIsPublic = false;
        IsPlaylistModalOpen = true;
    }

    [RelayCommand]
    private void OpenEditActivePlaylistModal()
    {
        if (_activePlaylistItem is null) return;

        _isEditingPlaylistModal = true;
        PlaylistModalTitleText = "Edit Playlist";
        PlaylistModalName = _activePlaylistItem.Name;
        PlaylistModalDescription = _activePlaylistItem.Description;
        PlaylistModalIsPublic = false;
        IsPlaylistModalOpen = true;
    }

    [RelayCommand]
    private void CancelPlaylistModal() => IsPlaylistModalOpen = false;

    [RelayCommand]
    private async Task SubmitPlaylistModalAsync()
    {
        var name = PlaylistModalName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowToast("Playlist name cannot be empty");
            return;
        }

        IsPlaylistModalOpen = false;
        if (_isEditingPlaylistModal && _activePlaylistItem is not null)
        {
            var (ok, err) = await _webApi.UpdatePlaylistDetailsAsync(
                _activePlaylistItem.Id,
                name,
                PlaylistModalDescription.Trim(),
                CancellationToken.None);
            if (ok)
            {
                CollectionTitle = name;
                ShowToast($"Updated '{name}'");
                await LoadPlaylistsAsync();
            }
            else
            {
                await _dialogService.ShowErrorAsync("Edit Playlist Failed", "Could not update playlist.", err);
            }
        }
        else
        {
            var (created, needsReauth, err) = await _webApi.CreatePlaylistAsync(
                name,
                PlaylistModalDescription.Trim(),
                PlaylistModalIsPublic,
                CancellationToken.None);
            if (created is not null)
            {
                ShowToast($"Created playlist '{created.Name}'");
                await LoadPlaylistsAsync();
                var nav = Playlists.FirstOrDefault(p => string.Equals(p.Item.Id, created.Id, StringComparison.Ordinal));
                if (nav is not null) await SelectPlaylistAsync(nav);
            }
            else if (needsReauth)
            {
                await _dialogService.ShowWarningAsync("Spotify Permission Needed", err ?? "Reconnect Spotify in Settings.");
            }
            else
            {
                await _dialogService.ShowErrorAsync("Create Playlist Failed", "Could not create playlist.", err);
            }
        }
    }

    [RelayCommand]
    private Task DeleteActivePlaylistAsync() =>
        _activePlaylistItem is null ? Task.CompletedTask : DeleteOrUnfollowPlaylistAsync(_activePlaylistItem);

    public async Task DeleteOrUnfollowPlaylistAsync(SpotifyPlaylistItem playlist)
    {
        var confirmed = await _dialogService.ShowConfirmAsync(
            "Remove Playlist",
            $"Remove '{playlist.Name}' from your Spotify library? (If you own it, Spotify unfollows/deletes it from your library.)",
            "Remove",
            isDanger: true);
        if (!confirmed) return;

        var ok = await _webApi.UnfollowPlaylistAsync(playlist.Id, CancellationToken.None);
        if (ok)
        {
            ShowToast($"Removed '{playlist.Name}'");
            await LoadPlaylistsAsync();
            if (_activePlaylistItem?.Id == playlist.Id)
            {
                await SelectHomeAsync();
            }
        }
        else
        {
            ShowToast($"Could not remove '{playlist.Name}'");
        }
    }

    public async Task RemoveTrackFromActivePlaylistAsync(SpotifyTrackRowViewModel row)
    {
        if (!IsActivePlaylistEditable || _activePlaylistItem is null)
        {
            return;
        }

        var ok = await _webApi.RemoveTrackFromPlaylistAsync(_activePlaylistItem.Id, row.Item.Uri, CancellationToken.None);
        if (ok)
        {
            CollectionTracks.Remove(row);
            row.Dispose();
            ShowToast($"Removed '{row.Title}' from {_activePlaylistItem.Name}");
        }
        else
        {
            ShowToast($"Could not remove '{row.Title}' (you may not own this playlist)");
        }
    }

    public async Task DownloadTrackViaGrabberAsync(SpotifyTrackItem track, string formatPreset)
    {
        if (_downloadDispatcher is null)
        {
            ShowToast("Media Grabber service unavailable");
            return;
        }

        var cleanArtists = string.IsNullOrWhiteSpace(track.Artists) ? "" : track.Artists.Split(',')[0].Trim();
        var title = string.IsNullOrWhiteSpace(cleanArtists) ? track.Name : $"{cleanArtists} - {track.Name}";
        var query = $"ytsearch1:{title}";
        await _downloadDispatcher.EnqueueAsync(query, title, formatPreset, CancellationToken.None);
        ShowToast($"Grabbing '{track.Name}' ({formatPreset}) in Media Grabber");
    }

    private async Task SelectExternalPlaylistAsync(SpotifyPlaylistItem playlist)
    {
        if (!_isRestoringNav) PushCurrentToNavHistory();
        ResetToStandardSection();
        ActiveSection = SpotifyNavSection.Playlist;
        DeselectPlaylists();
        _activePlaylistItem = playlist;
        OnPropertyChanged(nameof(IsActivePlaylistEditable));

        _activeCollectionContextUri = playlist.Uri;
        CollectionTitle = playlist.Name;
        CollectionSubtitle = $"{playlist.OwnerName} · {playlist.TrackCount} tracks";
        FeedMessage = string.Empty;

        var ct = ResetWorkToken();
        IsBusy = true;
        try
        {
            var res = await _webApi.GetPlaylistItemsAsync(playlist.Id, playlist.Uri, ct);
            ClearTrackCollection(CollectionTracks);
            for (var i = 0; i < res.Tracks.Count; i++)
            {
                CollectionTracks.Add(new SpotifyTrackRowViewModel(i + 1, res.Tracks[i], ShowTrackCovers, PlayTrackItemAsync, ToggleRowLikeAsync));
            }

            if (res.Tracks.Count == 0)
            {
                FeedMessage = res.AccessRestricted
                    ? "Spotify restricts reading tracks for this playlist directly, but you can still click 'Play All'."
                    : "No tracks found.";
            }
            else
            {
                var rows = CollectionTracks.ToList();
                _ = SyncRowLikedStatesAsync(rows);
                if (ShowTrackCovers) _ = LoadRowCoversAsync(rows);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FeedMessage = ex.Message; }
        finally { if (!_disposed) IsBusy = false; }
    }

    private async Task RecordPlayInHistoryAsync(GoLibrespotTrack track)
    {
        try
        {
            await _database.RecordSpotifyPlayAsync(
                track.Uri,
                track.Name,
                track.ArtistsDisplay,
                DateTime.Now.Hour,
                CancellationToken.None);
        }
        catch
        {
        }
    }

    private async Task LoadArtistCardPhotosAsync(List<SpotifyArtistCardViewModel> cards)
    {
        CancelAndDispose(ref _detailCoverCts);
        if (_disposed || cards.Count == 0) return;

        var cts = new CancellationTokenSource();
        _detailCoverCts = cts;
        foreach (var card in cards)
        {
            if (cts.IsCancellationRequested || _disposed) break;
            if (string.IsNullOrWhiteSpace(card.Item.ImageUrl)) continue;
            var bmp = await _webApi.LoadSmallBitmapAsync(card.Item.ImageUrl, 120, cts.Token);
            if (bmp is not null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed || cts.IsCancellationRequested) bmp.Dispose();
                    else card.SetPhoto(bmp);
                });
            }
        }
    }

    private async Task LoadAlbumCardCoversAsync(List<SpotifyAlbumCardViewModel> cards)
    {
        if (_disposed || cards.Count == 0) return;
        foreach (var card in cards)
        {
            if (_disposed || ActiveDetailPage == SpotifyDetailPage.None && !IsSearchSection) break;
            if (string.IsNullOrWhiteSpace(card.Item.ImageUrl)) continue;
            var bmp = await _webApi.LoadSmallBitmapAsync(card.Item.ImageUrl, 120, CancellationToken.None);
            if (bmp is not null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed) bmp.Dispose();
                    else card.SetCover(bmp);
                });
            }
        }
    }

    private async Task LoadPlaylistCardCoversAsync(List<SpotifyPlaylistCardViewModel> cards)
    {
        if (_disposed || cards.Count == 0) return;
        foreach (var card in cards)
        {
            if (_disposed || !IsSearchSection) break;
            if (string.IsNullOrWhiteSpace(card.Item.ImageUrl)) continue;
            var bmp = await _webApi.LoadSmallBitmapAsync(card.Item.ImageUrl, 120, CancellationToken.None);
            if (bmp is not null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed) bmp.Dispose();
                    else card.SetCover(bmp);
                });
            }
        }
    }

    private async Task LoadSingleArtistBannerBitmapAsync(string? url, CancellationToken ct)
    {
        var bmp = string.IsNullOrWhiteSpace(url) ? null : await _webApi.LoadSmallBitmapAsync(url, 180, ct);
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || ct.IsCancellationRequested) { bmp?.Dispose(); return; }
            var prev = ActiveArtistBitmap;
            ActiveArtistBitmap = bmp;
            prev?.Dispose();
        });
    }

    private async Task LoadSingleAlbumCoverBitmapAsync(string? url, CancellationToken ct)
    {
        var bmp = string.IsNullOrWhiteSpace(url) ? null : await _webApi.LoadSmallBitmapAsync(url, 180, ct);
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || ct.IsCancellationRequested) { bmp?.Dispose(); return; }
            var prev = ActiveAlbumBitmap;
            ActiveAlbumBitmap = bmp;
            prev?.Dispose();
        });
    }

    private void ClearArtistCards(ObservableCollection<SpotifyArtistCardViewModel> list)
    {
        foreach (var item in list) item.Dispose();
        list.Clear();
    }

    private void ClearAlbumCards(ObservableCollection<SpotifyAlbumCardViewModel> list)
    {
        foreach (var item in list) item.Dispose();
        list.Clear();
    }

    private void ClearPlaylistCards(ObservableCollection<SpotifyPlaylistCardViewModel> list)
    {
        foreach (var item in list) item.Dispose();
        list.Clear();
    }

    private void ClearDetailCollections()
    {
        CancelAndDispose(ref _detailCoverCts);
        ClearArtistCards(SimilarArtists);
        ClearArtistCards(TopArtists);
        ClearAlbumCards(ArtistAlbums);
        ClearAlbumCards(SavedAlbums);
        ActiveArtistTags.Clear();
        ActiveArtistBio = string.Empty;
        HasActiveArtistBio = false;

        var oldArtistBmp = ActiveArtistBitmap;
        ActiveArtistBitmap = null;
        oldArtistBmp?.Dispose();

        var oldAlbumBmp = ActiveAlbumBitmap;
        ActiveAlbumBitmap = null;
        oldAlbumBmp?.Dispose();
    }

    private void DisposeExtendedAssets()
    {
        CancelAndDispose(ref _detailCoverCts);
        ClearArtistCards(SearchArtists);
        ClearAlbumCards(SearchAlbums);
        ClearPlaylistCards(SearchPlaylists);
        ClearDetailCollections();
    }

    public void ReleaseTransientAssets()
    {
        if (_disposed)
        {
            return;
        }

        ClearTrackCollection(SearchTracks);
        DisposeExtendedAssets();
        MemoryTrimService.ScheduleTrim();
    }

    private void ClearUnusedSharedCovers(ObservableCollection<SpotifyTrackRowViewModel> cleared)
    {
        if (_sharedRowCovers.Count == 0)
        {
            return;
        }

        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var col in new[] { FypTracks, SearchTracks, CollectionTracks })
        {
            if (ReferenceEquals(col, cleared))
            {
                continue;
            }

            foreach (var r in col)
            {
                if (!string.IsNullOrWhiteSpace(r.Item.AlbumCoverUrl))
                {
                    keep.Add(r.Item.AlbumCoverUrl);
                }
            }
        }

        foreach (var url in _sharedRowCovers.Keys.ToList())
        {
            if (!keep.Contains(url) && _sharedRowCovers.Remove(url, out var bmp))
            {
                bmp.Dispose();
            }
        }
    }

    private void ClearAllSharedCovers()
    {
        foreach (var bmp in _sharedRowCovers.Values)
        {
            bmp.Dispose();
        }

        _sharedRowCovers.Clear();
    }
}
