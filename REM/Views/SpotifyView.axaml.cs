using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using REM.ViewModels;

namespace REM.Views;

public partial class SpotifyView : UserControl
{
    private SpotifyViewModel? _boundVm;
    private bool _isScrubbing;

    public SpotifyView()
    {
        InitializeComponent();

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
        if (e.Key == Key.Escape && _boundVm is { IsLyricsFullscreen: true })
        {
            _boundVm.CloseLyricsFullscreenCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnViewDataContextChanged(object? sender, EventArgs e)
    {
        UnsubscribeVm();
        if (DataContext is SpotifyViewModel vm)
        {
            _boundVm = vm;
            vm.ActiveLyricChanged += OnActiveLyricChanged;
        }
    }

    private void UnsubscribeVm()
    {
        _isScrubbing = false;
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
        ScrollLyricIntoCenter(LyricsItemsControl, LyricsScrollViewer, index);
        if (_boundVm is { IsLyricsFullscreen: true })
        {
            ScrollLyricIntoCenter(FullscreenLyricsItemsControl, FullscreenLyricsScrollViewer, index);
        }
    }

    private static void ScrollLyricIntoCenter(ItemsControl itemsControl, ScrollViewer scrollViewer, int index)
    {
        if (!scrollViewer.IsVisible)
        {
            return;
        }

        var container = itemsControl.ContainerFromIndex(index);
        if (container is Control ctrl)
        {
            var topLeft = ctrl.TranslatePoint(new Point(0, 0), scrollViewer);
            if (topLeft is { } pt && scrollViewer.Viewport.Height > 40)
            {
                var targetY = Math.Max(0, scrollViewer.Offset.Y + pt.Y - (scrollViewer.Viewport.Height * 0.36));
                scrollViewer.Offset = new Vector(scrollViewer.Offset.X, targetY);
            }
            else if (ctrl.FindAncestorOfType<ScrollViewer>() is not null)
            {
                ctrl.BringIntoView();
            }
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
