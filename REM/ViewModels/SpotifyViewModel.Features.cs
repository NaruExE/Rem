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

public sealed partial class RemMixCardViewModel : ObservableObject, IDisposable
{
    private readonly Func<string, Task> _onOpen;
    private bool _disposed;

    public RemMixCardViewModel(
        string kind,
        string title,
        string subtitle,
        string badge,
        string accentHex,
        string gradientStartHex,
        string gradientEndHex,
        string coverHeadline,
        string coverBigNumber,
        Geometry iconGeometry,
        bool showCoverArt,
        Func<string, Task> onOpen)
    {
        Kind = kind;
        Title = title;
        Subtitle = subtitle;
        Badge = badge;
        CoverHeadline = coverHeadline;
        CoverBigNumber = coverBigNumber;
        var accentColor = Color.TryParse(accentHex, out var c) ? c : Color.Parse("#1DB954");
        var startColor = Color.TryParse(gradientStartHex, out var s) ? s : accentColor;
        var endColor = Color.TryParse(gradientEndHex, out var e) ? e : Color.Parse("#0B0D14");
        AccentBrush = new SolidColorBrush(accentColor);
        CoverGradientBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(startColor, 0.0),
                new GradientStop(endColor, 1.0)
            ]
        };
        IconGeometry = iconGeometry;
        _showCoverArt = showCoverArt;
        _onOpen = onOpen;
    }

    public string Kind { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string Badge { get; }
    public string CoverHeadline { get; }
    public string CoverBigNumber { get; }
    public IBrush AccentBrush { get; }
    public IBrush CoverGradientBrush { get; }
    public Geometry IconGeometry { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCoverPhoto))]
    private Bitmap? _coverBitmap;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCoverPhoto))]
    private bool _showCoverArt = true;

    public bool HasCoverPhoto => ShowCoverArt && CoverBitmap is not null;

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
    private Task OpenAsync() => _onOpen(Kind);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var prev = CoverBitmap;
        CoverBitmap = null;
        prev?.Dispose();
    }
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

    [ObservableProperty]
    private Geometry _activeMixIconGeometry = IconCatalog.Sparkles;

    [ObservableProperty]
    private IBrush _activeMixAccentBrush = new SolidColorBrush(Color.Parse("#1DB954"));

    [ObservableProperty]
    private IBrush _activeMixCoverGradientBrush = CreateMixGradient("#14532D", "#081C15");

    [ObservableProperty]
    private string _activeMixCoverBigNumber = "MIX";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveMixCoverBitmap))]
    private Bitmap? _activeMixCoverBitmap;

    public bool HasActiveMixCoverBitmap => ShowPlaylistIcons && ActiveMixCoverBitmap is not null;

    private static IBrush CreateMixGradient(string startHex, string endHex)
    {
        var s = Color.TryParse(startHex, out var sc) ? sc : Color.Parse("#1DB954");
        var e = Color.TryParse(endHex, out var ec) ? ec : Color.Parse("#0B0D14");
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(s, 0.0),
                new GradientStop(e, 1.0)
            ]
        };
    }

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

    private void UpdateAlbumTintFromColor(Color dominant)
    {
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
        foreach (var existing in RemMixCards) existing.Dispose();
        RemMixCards.Clear();
        RemMixCards.Add(new RemMixCardViewModel("discover", "Discover Weekly", "Fresh finds filtered against your library", "WEEKLY", "#1DB954", "#14532D", "#081C15", "Discover\nWeekly", "DW", IconCatalog.Compass, ShowPlaylistIcons, OpenRemMixCommandAsync));
        RemMixCards.Add(new RemMixCardViewModel("daylist", "Your Daylist", "Tunes tuned to this time of day", "NOW", "#F59E0B", "#78350F", "#1C1208", "your\ndaylist", "DL", IconCatalog.Clock, ShowPlaylistIcons, OpenRemMixCommandAsync));
        RemMixCards.Add(new RemMixCardViewModel("daily1", "Daily Mix 1", "Top vibe cluster & close artists", "MIX 1", "#38BDF8", "#0C4A6E", "#0B1120", "Daily Mix", "01", IconCatalog.Layers, ShowPlaylistIcons, OpenRemMixCommandAsync));
        RemMixCards.Add(new RemMixCardViewModel("daily2", "Daily Mix 2", "Second vibe cluster & rotation", "MIX 2", "#A78BFA", "#4C1D95", "#0F0A1E", "Daily Mix", "02", IconCatalog.Disc, ShowPlaylistIcons, OpenRemMixCommandAsync));
        RemMixCards.Add(new RemMixCardViewModel("daily3", "Daily Mix 3", "Deep vibe cluster & discoveries", "MIX 3", "#F472B6", "#831843", "#1A0B16", "Daily Mix", "03", IconCatalog.Sparkles, ShowPlaylistIcons, OpenRemMixCommandAsync));
        RemMixCards.Add(new RemMixCardViewModel("release", "Release Radar", "New drops from artists you follow", "NEW", "#FB7185", "#881337", "#160911", "Release\nRadar", "RR", IconCatalog.Radar, ShowPlaylistIcons, OpenRemMixCommandAsync));
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

        ReleaseHomeSectionAssetsIfOnHome();
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

        ReleaseHomeSectionAssetsIfOnHome();
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

        ReleaseHomeSectionAssetsIfOnHome();
        DeselectPlaylists();
        ActiveDetailPage = SpotifyDetailPage.ArtistProfile;
        ActiveArtistName = string.IsNullOrWhiteSpace(fallbackName) ? "Loading Artist..." : fallbackName;
        ActiveArtistSubtitle = "Loading artist profile...";
        FeedMessage = string.Empty;
        ClearTrackCollection(DetailTracks);
        ClearDetailCollections();

        var ct = ResetWorkToken();
        IsBusy = true;
        ListFadeOpacity = 0.4;
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

            _soloist.RememberTrackMetadata(topTracks);
            ClearTrackCollection(DetailTracks);
            for (var i = 0; i < topTracks.Count; i++)
            {
                DetailTracks.Add(new SpotifyTrackRowViewModel(i + 1, topTracks[i], ShowTrackCovers, PlayTrackItemAsync, ToggleRowLikeAsync));
            }

            ClearAlbumCards(ArtistAlbums);
            foreach (var al in albums)
            {
                ArtistAlbums.Add(new SpotifyAlbumCardViewModel(al, OpenAlbumFromCardAsync, PlayAlbumCardAsync));
            }

            ClearArtistCards(SimilarArtists);

            if (ShowTrackCovers && DetailTracks.Count > 0)
            {
                var rows = DetailTracks.ToList();
                _ = SyncRowLikedStatesAsync(rows);
                _ = LoadRowCoversAsync(rows);
            }

            _ = LoadSingleArtistBannerBitmapAsync(artist.ImageUrl, ct);
            _ = LoadAlbumCardCoversAsync(ArtistAlbums.ToList(), ct);

            if (lfmProfile?.SimilarArtistNames is { Count: > 0 })
            {
                _ = LoadSimilarArtistsAsync(lfmProfile.SimilarArtistNames.Take(4).ToList(), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FeedMessage = ex.Message; }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
                ListFadeOpacity = 1.0;
            }
        }
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
        ReleaseHomeSectionAssetsIfOnHome();
        DeselectPlaylists();
        ActiveDetailPage = SpotifyDetailPage.RemMix;
        ActiveMixTitle = $"{track.Name} Radio";
        ActiveMixSubtitle = $"Building radio from {track.Artists}...";
        ActiveMixBadge = "SONG RADIO";
        ActiveMixIconGeometry = IconCatalog.Radio;
        ActiveMixAccentBrush = new SolidColorBrush(Color.Parse("#1DB954"));
        ActiveMixCoverGradientBrush = CreateMixGradient("#14532D", "#081C15");
        ActiveMixCoverBigNumber = "RD";
        var oldRadioCov = ActiveMixCoverBitmap;
        ActiveMixCoverBitmap = null;
        oldRadioCov?.Dispose();
        _activeCollectionContextUri = null;
        FeedMessage = string.Empty;
        ClearTrackCollection(DetailTracks);

        var ct = ResetWorkToken();
        IsBusy = true;
        ListFadeOpacity = 0.4;
        try
        {
            var mix = await _mixGenerator.BuildSongRadioAsync(track, PreferDeepCuts, ct);
            ApplyMixResult(mix, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FeedMessage = ex.Message; }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
                ListFadeOpacity = 1.0;
            }
        }
    }

    public async Task StartArtistRadioAsync(SpotifyArtistItem artist)
    {
        if (!_webApi.IsConnected) return;

        if (!_isRestoringNav) PushCurrentToNavHistory();
        ReleaseHomeSectionAssetsIfOnHome();
        DeselectPlaylists();
        ActiveDetailPage = SpotifyDetailPage.RemMix;
        ActiveMixTitle = $"{artist.Name} Radio";
        ActiveMixSubtitle = "Building artist radio...";
        ActiveMixBadge = "ARTIST RADIO";
        ActiveMixIconGeometry = IconCatalog.Radio;
        ActiveMixAccentBrush = new SolidColorBrush(Color.Parse("#8B5CF6"));
        ActiveMixCoverGradientBrush = CreateMixGradient("#4C1D95", "#0F0A1E");
        ActiveMixCoverBigNumber = "AR";
        var oldArtistRadioCov = ActiveMixCoverBitmap;
        ActiveMixCoverBitmap = null;
        oldArtistRadioCov?.Dispose();
        _activeCollectionContextUri = null;
        FeedMessage = string.Empty;
        ClearTrackCollection(DetailTracks);

        var ct = ResetWorkToken();
        IsBusy = true;
        ListFadeOpacity = 0.4;
        try
        {
            var mix = await _mixGenerator.BuildArtistRadioAsync(artist, PreferDeepCuts, ct);
            ApplyMixResult(mix, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FeedMessage = ex.Message; }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
                ListFadeOpacity = 1.0;
            }
        }
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

        ReleaseHomeSectionAssetsIfOnHome();
        DeselectPlaylists();
        ActiveDetailPage = SpotifyDetailPage.RemMix;
        _activeCollectionContextUri = null;
        FeedMessage = string.Empty;
        ClearTrackCollection(DetailTracks);
        var oldMixCov = ActiveMixCoverBitmap;
        ActiveMixCoverBitmap = null;
        oldMixCov?.Dispose();
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
        ActiveMixIconGeometry = kind switch
        {
            "discover" => IconCatalog.Compass,
            "daylist" => IconCatalog.Clock,
            "daily1" => IconCatalog.Layers,
            "daily2" => IconCatalog.Disc,
            "daily3" => IconCatalog.Sparkles,
            _ => IconCatalog.Radar
        };
        var matchingCard = RemMixCards.FirstOrDefault(c => string.Equals(c.Kind, kind, StringComparison.Ordinal));
        if (matchingCard is not null)
        {
            ActiveMixAccentBrush = matchingCard.AccentBrush;
            ActiveMixCoverGradientBrush = matchingCard.CoverGradientBrush;
            ActiveMixCoverBigNumber = matchingCard.CoverBigNumber;
        }

        var ct = ResetWorkToken();
        IsBusy = true;
        ListFadeOpacity = 0.4;
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

            ApplyMixResult(mix, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FeedMessage = ex.Message; }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
                ListFadeOpacity = 1.0;
            }
        }
    }

    [RelayCommand]
    private async Task RefreshActiveMixAsync()
    {
        if (_activeMix is null) return;
        var kind = _activeMix.Key switch
        {
            var k when k.StartsWith("rem-discover-weekly", StringComparison.Ordinal) => "discover",
            var k when k.StartsWith("rem-daylist", StringComparison.Ordinal) => "daylist",
            var k when k.StartsWith("rem-daily-mix-1", StringComparison.Ordinal) => "daily1",
            var k when k.StartsWith("rem-daily-mix-2", StringComparison.Ordinal) => "daily2",
            var k when k.StartsWith("rem-daily-mix-3", StringComparison.Ordinal) => "daily3",
            _ => "release"
        };
        await OpenRemMixAsync(kind, forceRefresh: true);
    }

    private void ApplyMixResult(RemMixDescriptor mix, CancellationToken ct)
    {
        _activeMix = mix;
        ActiveMixTitle = mix.Title;
        ActiveMixSubtitle = mix.Subtitle;
        ActiveMixBadge = mix.Badge;
        if (Color.TryParse(mix.AccentHex, out var accentCol))
        {
            ActiveMixAccentBrush = new SolidColorBrush(accentCol);
        }

        _soloist.RememberTrackMetadata(mix.Tracks);
        ClearTrackCollection(DetailTracks);
        for (var i = 0; i < mix.Tracks.Count; i++)
        {
            DetailTracks.Add(new SpotifyTrackRowViewModel(i + 1, mix.Tracks[i], ShowTrackCovers, PlayTrackItemAsync, ToggleRowLikeAsync));
        }

        if (mix.Tracks.Count == 0)
        {
            FeedMessage = !string.IsNullOrWhiteSpace(_webApi.LastApiError)
                ? _webApi.LastApiError
                : "Could not gather tracks for this mix yet — play a few songs or add a Last.fm API key in Settings.";
        }
        else
        {
            var rows = DetailTracks.ToList();
            _ = SyncRowLikedStatesAsync(rows);
            if (ShowTrackCovers) _ = LoadRowCoversAsync(rows);
            if (ShowPlaylistIcons) _ = LoadActiveMixCoverAsync(mix, ct);
        }
    }

    private static string GetMixKeyPrefix(string kind) => kind switch
    {
        "discover" => "rem-discover-weekly",
        "daylist" => "rem-daylist",
        "daily1" => "rem-daily-mix-1",
        "daily2" => "rem-daily-mix-2",
        "daily3" => "rem-daily-mix-3",
        _ => "rem-release-radar"
    };

    private async Task LoadActiveMixCoverAsync(RemMixDescriptor mix, CancellationToken ct)
    {
        if (_disposed || !ShowPlaylistIcons)
        {
            return;
        }

        var url = mix.Tracks.Select(t => t.AlbumCoverUrl).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            var bmp = await _webApi.LoadSmallBitmapAsync(url, 160, ct).ConfigureAwait(false);
            Bitmap? cardBmp = null;
            var matchingCard = RemMixCards.FirstOrDefault(c => mix.Key.StartsWith(GetMixKeyPrefix(c.Kind), StringComparison.Ordinal));
            if (matchingCard is not null && !ct.IsCancellationRequested && !_disposed)
            {
                cardBmp = await _webApi.LoadSmallBitmapAsync(url, 160, ct).ConfigureAwait(false);
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || ct.IsCancellationRequested || !ShowPlaylistIcons)
                {
                    bmp?.Dispose();
                    cardBmp?.Dispose();
                    return;
                }

                if (bmp is not null)
                {
                    var prev = ActiveMixCoverBitmap;
                    ActiveMixCoverBitmap = bmp;
                    prev?.Dispose();
                }

                if (cardBmp is not null)
                {
                    matchingCard?.SetCover(cardBmp);
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private async Task LoadRemMixCardCoversAsync(int decodeWidth = 160)
    {
        CancelAndDispose(ref _mixCoverCts);
        if (_disposed || !ShowPlaylistIcons || RemMixCards.Count == 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _mixCoverCts = cts;
        var cards = RemMixCards.ToList();
        using var gate = new SemaphoreSlim(3, 3);

        var tasks = cards.Select(async card =>
        {
            if (cts.IsCancellationRequested || _disposed || !ShowPlaylistIcons)
            {
                return;
            }

            await gate.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                var key = RemMixGeneratorService.GetCurrentMixCacheKey(card.Kind);
                var cached = await _database.GetSpotifyCachedMixAsync(key, cts.Token).ConfigureAwait(false);
                var url = cached?.Tracks.Select(t => t.AlbumCoverUrl).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
                if (string.IsNullOrWhiteSpace(url) || cts.IsCancellationRequested || _disposed || !ShowPlaylistIcons)
                {
                    return;
                }

                var bmp = await _webApi.LoadSmallBitmapAsync(url, decodeWidth, cts.Token).ConfigureAwait(false);
                if (bmp is not null)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_disposed || cts.IsCancellationRequested || !ShowPlaylistIcons)
                        {
                            bmp.Dispose();
                            return;
                        }

                        card.SetCover(bmp);
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private async Task SaveActiveMixToSpotifyAsync()
    {
        if (!_webApi.IsConnected || DetailTracks.Count == 0)
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

        var uris = DetailTracks.Select(t => t.Item.Uri).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
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
        ReleaseHomeSectionAssetsIfOnHome();
        DeselectPlaylists();
        ActiveDetailPage = SpotifyDetailPage.AlbumDetail;
        ActiveAlbumTitle = fallbackTitle ?? "Album";
        ActiveAlbumSubtitle = "Loading album...";
        FeedMessage = string.Empty;
        ClearTrackCollection(DetailTracks);
        var oldAlBmp = ActiveAlbumBitmap;
        ActiveAlbumBitmap = null;
        oldAlBmp?.Dispose();

        var ct = ResetWorkToken();
        IsBusy = true;
        ListFadeOpacity = 0.4;
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

            _soloist.RememberTrackMetadata(tracks);
            ClearTrackCollection(DetailTracks);
            for (var i = 0; i < tracks.Count; i++)
            {
                DetailTracks.Add(new SpotifyTrackRowViewModel(i + 1, tracks[i], ShowTrackCovers, PlayTrackItemAsync, ToggleRowLikeAsync));
            }

            if (tracks.Count > 0)
            {
                var rows = DetailTracks.ToList();
                _ = SyncRowLikedStatesAsync(rows);
                if (ShowTrackCovers) _ = LoadRowCoversAsync(rows);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FeedMessage = ex.Message; }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
                ListFadeOpacity = 1.0;
            }
        }
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
            _allCollectionRows.Remove(row);
            OnPropertyChanged(nameof(CollectionFilterSummary));
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
        ReleaseHomeSectionAssetsIfOnHome();
        ResetToStandardSection();
        ActiveSection = SpotifyNavSection.Playlist;
        DeselectPlaylists();
        _activePlaylistItem = playlist;
        OnPropertyChanged(nameof(IsActivePlaylistEditable));

        _activeCollectionContextUri = playlist.Uri;
        CollectionTitle = playlist.Name;
        CollectionSubtitle = $"{playlist.OwnerName} · {playlist.TrackCount} tracks";
        FeedMessage = string.Empty;
        CollectionFilterQuery = string.Empty;
        CollectionPaginationStatus = string.Empty;
        ClearAllCollectionRows();

        var ct = ResetWorkToken();
        ListFadeOpacity = 0.4;
        IsBusy = true;
        try
        {
            const int pageSize = 50;
            var firstPage = await _webApi.GetPlaylistItemsPageAsync(playlist.Id, playlist.Uri, offset: 0, limit: pageSize, ct);
            if (_disposed || ct.IsCancellationRequested) return;

            if (firstPage.Tracks.Count == 0)
            {
                FeedMessage = firstPage.AccessRestricted
                    ? "Spotify restricts reading tracks for this playlist directly, but you can still click 'Play All'."
                    : "No tracks found.";
                return;
            }

            AppendCollectionPageRows(firstPage.Tracks, isLikedList: false);
            ListFadeOpacity = 1.0;

            var firstRows = CollectionTracks.ToList();
            _ = SyncRowLikedStatesAsync(firstRows);
            if (ShowTrackCovers) _ = LoadRowCoversAsync(firstRows);

            if (firstPage.HasMore)
            {
                CollectionPaginationStatus = $"Loaded {_allCollectionRows.Count} / {(firstPage.Total > 0 ? firstPage.Total.ToString() : "?")} tracks...";
                _ = LoadRemainingPlaylistPagesAsync(playlist.Id, playlist.Uri, firstPage.Tracks.Count, firstPage.Total, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FeedMessage = ex.Message; }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
                ListFadeOpacity = 1.0;
            }
        }
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

    private async Task LoadArtistCardPhotosAsync(List<SpotifyArtistCardViewModel> cards, int decodeWidth = 120)
    {
        CancelAndDispose(ref _detailCoverCts);
        if (_disposed || cards.Count == 0) return;

        var cts = new CancellationTokenSource();
        _detailCoverCts = cts;
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = cards.Select(async card =>
        {
            if (cts.IsCancellationRequested || _disposed || string.IsNullOrWhiteSpace(card.Item.ImageUrl))
            {
                return;
            }

            await gate.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                if (cts.IsCancellationRequested || _disposed) return;
                var bmp = await _webApi.LoadSmallBitmapAsync(card.Item.ImageUrl, decodeWidth, cts.Token).ConfigureAwait(false);
                if (bmp is not null)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_disposed || cts.IsCancellationRequested) bmp.Dispose();
                        else card.SetPhoto(bmp);
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadAlbumCardCoversAsync(List<SpotifyAlbumCardViewModel> cards, CancellationToken ct = default, int decodeWidth = 120)
    {
        if (_disposed || cards.Count == 0) return;
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = cards.Select(async card =>
        {
            if (ct.IsCancellationRequested || _disposed || (ActiveDetailPage == SpotifyDetailPage.None && !IsSearchSection && !IsHomeSection) || string.IsNullOrWhiteSpace(card.Item.ImageUrl))
            {
                return;
            }

            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (ct.IsCancellationRequested || _disposed || (ActiveDetailPage == SpotifyDetailPage.None && !IsSearchSection && !IsHomeSection)) return;
                var bmp = await _webApi.LoadSmallBitmapAsync(card.Item.ImageUrl, decodeWidth, ct).ConfigureAwait(false);
                if (bmp is not null)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_disposed || ct.IsCancellationRequested) bmp.Dispose();
                        else card.SetCover(bmp);
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadPlaylistCardCoversAsync(List<SpotifyPlaylistCardViewModel> cards, CancellationToken ct = default)
    {
        if (_disposed || cards.Count == 0) return;
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = cards.Select(async card =>
        {
            if (ct.IsCancellationRequested || _disposed || !IsSearchSection || string.IsNullOrWhiteSpace(card.Item.ImageUrl))
            {
                return;
            }

            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (ct.IsCancellationRequested || _disposed || !IsSearchSection) return;
                var bmp = await _webApi.LoadSmallBitmapAsync(card.Item.ImageUrl, 120, ct).ConfigureAwait(false);
                if (bmp is not null)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_disposed || ct.IsCancellationRequested) bmp.Dispose();
                        else card.SetCover(bmp);
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
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
        ClearTrackCollection(DetailTracks);
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

        var oldMixBmp = ActiveMixCoverBitmap;
        ActiveMixCoverBitmap = null;
        oldMixBmp?.Dispose();
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

        ReleaseHomeSectionAssets(clearFypRows: true);
        ClearAllCollectionRows();
        ClearTrackCollection(SearchTracks);
        DisposeExtendedAssets();
        ClearUnusedSharedCovers(null);
        MemoryTrimService.ScheduleTrim();
    }

    private void ClearUnusedSharedCovers(ObservableCollection<SpotifyTrackRowViewModel>? cleared)
    {
        if (_sharedRowCovers.Count == 0)
        {
            return;
        }

        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var col in new[] { FypTracks, HomeRecentTracks, SearchTracks, CollectionTracks, DetailTracks })
        {
            if (cleared is not null && ReferenceEquals(col, cleared))
            {
                continue;
            }

            foreach (var r in col)
            {
                if (!string.IsNullOrWhiteSpace(r.CoverCacheKey))
                {
                    keep.Add(r.CoverCacheKey);
                }
            }
        }

        if (!ReferenceEquals(cleared, CollectionTracks))
        {
            foreach (var r in _allCollectionRows)
            {
                if (!string.IsNullOrWhiteSpace(r.CoverCacheKey))
                {
                    keep.Add(r.CoverCacheKey);
                }
            }
        }

        foreach (var key in _sharedRowCovers.Keys.ToList())
        {
            if (!keep.Contains(key) && _sharedRowCovers.Remove(key, out var bmp))
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
