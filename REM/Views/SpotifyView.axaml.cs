using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using REM.ViewModels;

namespace REM.Views;

public partial class SpotifyView : UserControl
{
    private sealed class ScrollAnim
    {
        public ScrollViewer Viewer { get; init; } = null!;
        public double StartY { get; set; }
        public double TargetY { get; set; }
        public DateTimeOffset StartTime { get; set; }
        public bool Active { get; set; }
    }

    private SpotifyViewModel? _boundVm;
    private readonly DispatcherTimer _scrollTimer;
    private readonly ScrollAnim _drawerAnim;
    private readonly ScrollAnim _fullscreenAnim;
    private DateTimeOffset _userScrollPauseUntil = DateTimeOffset.MinValue;
    private bool _isScrubbing;

    public SpotifyView()
    {
        InitializeComponent();

        _drawerAnim = new ScrollAnim { Viewer = LyricsScrollViewer };
        _fullscreenAnim = new ScrollAnim { Viewer = FullscreenLyricsScrollViewer };
        _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _scrollTimer.Tick += OnScrollAnimationTick;

        LyricsScrollViewer.AddHandler(PointerWheelChangedEvent, OnLyricsPointerWheel, RoutingStrategies.Tunnel);
        FullscreenLyricsScrollViewer.AddHandler(PointerWheelChangedEvent, OnLyricsPointerWheel, RoutingStrategies.Tunnel);

        SeekSlider.AddHandler(PointerPressedEvent, OnSeekPointerPressed, RoutingStrategies.Tunnel);
        SeekSlider.AddHandler(PointerReleasedEvent, OnSeekPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        SeekSlider.AddHandler(PointerCaptureLostEvent, OnSeekPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        SeekSlider.PropertyChanged += OnSeekSliderPropertyChanged;

        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Tunnel);

        DataContextChanged += OnViewDataContextChanged;
        DetachedFromVisualTree += (_, _) => UnsubscribeVm();
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_boundVm is null)
        {
            return;
        }

        if (e.Key == Key.Escape && _boundVm.IsLyricsFullscreen)
        {
            _boundVm.CloseLyricsFullscreenCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnLyricsPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        _userScrollPauseUntil = DateTimeOffset.UtcNow.AddSeconds(3.5);
        _drawerAnim.Active = false;
        _fullscreenAnim.Active = false;
        _scrollTimer.Stop();
    }

    private void OnViewDataContextChanged(object? sender, EventArgs e)
    {
        UnsubscribeVm();
        if (DataContext is SpotifyViewModel vm)
        {
            _boundVm = vm;
            vm.ActiveLyricChanged += OnActiveLyricChanged;
        }
        else if (DataContext is null)
        {
            SeekSlider.PropertyChanged -= OnSeekSliderPropertyChanged;
            _scrollTimer.Tick -= OnScrollAnimationTick;
        }
    }

    private void UnsubscribeVm()
    {
        _isScrubbing = false;
        _drawerAnim.Active = false;
        _fullscreenAnim.Active = false;
        _scrollTimer.Stop();
        if (_boundVm is not null)
        {
            _boundVm.ActiveLyricChanged -= OnActiveLyricChanged;
            _boundVm = null;
        }
    }

    private void OnSeekPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _isScrubbing = true;
        _boundVm?.BeginScrub();
    }

    private void OnSeekSliderPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (_isScrubbing && e.Property == Slider.ValueProperty && e.NewValue is double val)
        {
            _boundVm?.UpdateScrubPreview(val);
        }
    }

    private void OnSeekPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        CommitCurrentScrub();
    }

    private void OnSeekPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        CommitCurrentScrub();
    }

    private void CommitCurrentScrub()
    {
        if (!_isScrubbing)
        {
            return;
        }

        _isScrubbing = false;
        if (_boundVm is not null)
        {
            _ = _boundVm.CommitScrubAsync(SeekSlider.Value);
        }
    }

    private void OnActiveLyricChanged(object? sender, int index)
    {
        if (DateTimeOffset.UtcNow < _userScrollPauseUntil)
        {
            return;
        }

        ScrollLyricIntoCenter(LyricsItemsControl, LyricsScrollViewer, _drawerAnim, index);
        if (_boundVm is { IsLyricsFullscreen: true })
        {
            ScrollLyricIntoCenter(FullscreenLyricsItemsControl, FullscreenLyricsScrollViewer, _fullscreenAnim, index);
        }
    }

    private void ScrollLyricIntoCenter(ItemsControl itemsControl, ScrollViewer scrollViewer, ScrollAnim anim, int index)
    {
        if (!scrollViewer.IsVisible)
        {
            return;
        }

        var container = itemsControl.ContainerFromIndex(index);
        if (container is not Control ctrl)
        {
            return;
        }

        var topLeft = ctrl.TranslatePoint(new Point(0, 0), scrollViewer);
        if (topLeft is { } pt && scrollViewer.Viewport.Height > 40)
        {
            var maxScroll = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
            var targetY = Math.Clamp(scrollViewer.Offset.Y + pt.Y - (scrollViewer.Viewport.Height * 0.36), 0, maxScroll);

            if (_boundVm is { LyricsAnimate: false } || Math.Abs(targetY - scrollViewer.Offset.Y) < 2)
            {
                anim.Active = false;
                scrollViewer.Offset = new Vector(scrollViewer.Offset.X, targetY);
                return;
            }

            anim.StartY = scrollViewer.Offset.Y;
            anim.TargetY = targetY;
            anim.StartTime = DateTimeOffset.UtcNow;
            anim.Active = true;
            if (!_scrollTimer.IsEnabled)
            {
                _scrollTimer.Start();
            }
        }
        else if (ctrl.FindAncestorOfType<ScrollViewer>() is not null)
        {
            ctrl.BringIntoView();
        }
    }

    private void OnScrollAnimationTick(object? sender, EventArgs e)
    {
        const double durationMs = 380.0;
        var now = DateTimeOffset.UtcNow;
        var anyActive = false;

        foreach (var anim in new[] { _drawerAnim, _fullscreenAnim })
        {
            if (!anim.Active || !anim.Viewer.IsVisible)
            {
                anim.Active = false;
                continue;
            }

            var elapsed = (now - anim.StartTime).TotalMilliseconds;
            var t = Math.Clamp(elapsed / durationMs, 0, 1);
            var eased = 1.0 - Math.Pow(1.0 - t, 3);
            var y = anim.StartY + ((anim.TargetY - anim.StartY) * eased);
            anim.Viewer.Offset = new Vector(anim.Viewer.Offset.X, y);

            if (t >= 1.0)
            {
                anim.Active = false;
            }
            else
            {
                anyActive = true;
            }
        }

        if (!anyActive)
        {
            _scrollTimer.Stop();
        }
    }

    private void OnTrackRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_boundVm is null || sender is not Control ctrl || ctrl.DataContext is not SpotifyTrackRowViewModel row)
        {
            return;
        }

        e.Handled = true;
        var menu = new ContextMenu();

        var playItem = new MenuItem { Header = "Play Now" };
        playItem.Click += (_, _) => _ = _boundVm.PlayTrackFromMenuAsync(row.Item);
        menu.Items.Add(playItem);

        var queueItem = new MenuItem { Header = "Add to Queue" };
        queueItem.Click += (_, _) => _ = _boundVm.AddTrackToQueueFromMenuAsync(row.Item);
        menu.Items.Add(queueItem);

        var radioItem = new MenuItem { Header = "Start Song Radio" };
        radioItem.Click += (_, _) => _ = _boundVm.StartSongRadioAsync(row.Item);
        menu.Items.Add(radioItem);

        var artistItem = new MenuItem { Header = "Go to Artist" };
        artistItem.Click += (_, _) => _ = _boundVm.OpenTrackArtistFromMenuAsync(row.Item);
        menu.Items.Add(artistItem);

        var albumItem = new MenuItem { Header = "Go to Album" };
        albumItem.Click += (_, _) => _ = _boundVm.OpenTrackAlbumFromMenuAsync(row.Item);
        menu.Items.Add(albumItem);

        menu.Items.Add(new Separator());

        var likeItem = new MenuItem { Header = row.LikeMenuHeader };
        likeItem.Click += (_, _) => _ = _boundVm.ToggleLikeRowFromMenuAsync(row);
        menu.Items.Add(likeItem);

        var addToPlaylistRoot = new MenuItem { Header = "Add to Playlist" };
        if (_boundVm.Playlists.Count == 0)
        {
            addToPlaylistRoot.Items.Add(new MenuItem { Header = "No playlists loaded", IsEnabled = false });
        }
        else
        {
            foreach (var pl in _boundVm.Playlists)
            {
                var targetPl = pl.Item;
                var plItem = new MenuItem { Header = pl.Name };
                plItem.Click += (_, _) => _ = _boundVm.AddTrackToPlaylistFromMenuAsync(targetPl, row.Item);
                addToPlaylistRoot.Items.Add(plItem);
            }
        }
        menu.Items.Add(addToPlaylistRoot);

        if (_boundVm.IsActivePlaylistEditable)
        {
            var removeFromPl = new MenuItem { Header = "Remove from this Playlist" };
            removeFromPl.Click += (_, _) => _ = _boundVm.RemoveTrackFromActivePlaylistAsync(row);
            menu.Items.Add(removeFromPl);
        }

        var grabRoot = new MenuItem { Header = "Download via Media Grabber" };
        foreach (var preset in new[] { "MP3 Audio", "M4A Audio", "Best MP4" })
        {
            var captured = preset;
            var presetItem = new MenuItem { Header = captured };
            presetItem.Click += (_, _) => _ = _boundVm.DownloadTrackViaGrabberAsync(row.Item, captured);
            grabRoot.Items.Add(presetItem);
        }
        menu.Items.Add(grabRoot);

        menu.Items.Add(new Separator());

        var shareItem = new MenuItem { Header = "Share (Copy Spotify Link)" };
        shareItem.Click += async (_, _) =>
        {
            var id = row.Item.Id;
            if (string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(row.Item.Uri))
            {
                id = row.Item.Uri.Split(':').LastOrDefault() ?? string.Empty;
            }

            var shareUrl = string.IsNullOrWhiteSpace(id)
                ? row.Item.Uri
                : $"https://open.spotify.com/track/{id}";

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null && !string.IsNullOrWhiteSpace(shareUrl))
            {
                await clipboard.SetTextAsync(shareUrl);
                _boundVm.NotifyLinkCopied(row.Title);
            }
        };
        menu.Items.Add(shareItem);

        menu.Open(ctrl);
    }

    private void OnPlaylistRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_boundVm is null || sender is not Control ctrl || ctrl.DataContext is not SpotifyPlaylistNavViewModel tab)
        {
            return;
        }

        e.Handled = true;
        var menu = new ContextMenu();

        var playItem = new MenuItem { Header = "Play Playlist" };
        playItem.Click += (_, _) => _ = _boundVm.PlayPlaylistFromMenuAsync(tab.Item);
        menu.Items.Add(playItem);

        var pinItem = new MenuItem { Header = tab.IsPinned ? "Unpin from Top" : "Pin to Top" };
        pinItem.Click += (_, _) => _boundVm.TogglePinPlaylistFromMenu(tab);
        menu.Items.Add(pinItem);

        var deleteItem = new MenuItem { Header = "Remove / Delete Playlist" };
        deleteItem.Click += (_, _) => _ = _boundVm.DeleteOrUnfollowPlaylistAsync(tab.Item);
        menu.Items.Add(deleteItem);

        menu.Items.Add(new Separator());

        var shareItem = new MenuItem { Header = "Share (Copy Playlist Link)" };
        shareItem.Click += async (_, _) =>
        {
            var shareUrl = string.IsNullOrWhiteSpace(tab.Item.Id)
                ? tab.Item.Uri
                : $"https://open.spotify.com/playlist/{tab.Item.Id}";

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null && !string.IsNullOrWhiteSpace(shareUrl))
            {
                await clipboard.SetTextAsync(shareUrl);
                _boundVm.NotifyLinkCopied(tab.Name);
            }
        };
        menu.Items.Add(shareItem);

        menu.Open(ctrl);
    }
}
