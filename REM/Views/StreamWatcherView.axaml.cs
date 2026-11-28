using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using REM.Services;
using REM.ViewModels;

namespace REM.Views;

public partial class StreamWatcherView : UserControl
{
    private const double ControlsAutoHideSeconds = 2.8;

    private readonly DispatcherTimer _controlsIdleTimer;
    private StreamWatcherViewModel? _boundVm;
    private Window? _hookedWindow;
    private MainWindowViewModel? _observedMainVm;
    private DialogService? _observedDialogService;
    private DashboardViewModel? _observedDashboardVm;
    private bool _isAttached;
    private bool _isScrubbing;
    private bool _hasLastCursor;
    private int _lastCursorX;
    private int _lastCursorY;
    private DateTime _lastActivityUtc = DateTime.UtcNow;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    public StreamWatcherView()
    {
        InitializeComponent();

        _controlsIdleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _controlsIdleTimer.Tick += OnControlsIdleTick;

        SeekSlider.AddHandler(PointerPressedEvent, OnSeekPointerPressed, RoutingStrategies.Tunnel);
        SeekSlider.AddHandler(PointerReleasedEvent, OnSeekPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        SeekSlider.AddHandler(PointerCaptureLostEvent, OnSeekPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        SeekSlider.PropertyChanged += OnSeekSliderPropertyChanged;

        BottomControlBar.AddHandler(PointerMovedEvent, OnControlsBarActivity, RoutingStrategies.Tunnel);
        BottomControlBar.AddHandler(PointerPressedEvent, OnControlsBarActivity, RoutingStrategies.Tunnel);
        ChatScrollViewer.ScrollChanged += OnChatScrollChanged;

        DataContextChanged += OnViewDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    public void DetachSurface()
    {
        _isScrubbing = false;
        VlcVideoView.MediaPlayer = null;
        _boundVm?.NotifySurfaceDetached();
    }

    private void OnViewDataContextChanged(object? sender, EventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.PropertyChanged -= OnBoundViewModelPropertyChanged;
            _boundVm.FullscreenChanged -= OnViewModelFullscreenChanged;
            _boundVm.ChatMessages.CollectionChanged -= OnChatMessagesCollectionChanged;
            _boundVm.WriteClipboardTextRequested = null;
            _boundVm = null;
        }

        if (DataContext is StreamWatcherViewModel vm)
        {
            _boundVm = vm;
            vm.PropertyChanged += OnBoundViewModelPropertyChanged;
            vm.FullscreenChanged += OnViewModelFullscreenChanged;
            vm.ChatMessages.CollectionChanged += OnChatMessagesCollectionChanged;
            vm.WriteClipboardTextRequested = WriteClipboardAsync;

            if (_isAttached && vm.IsWatchMode)
            {
                _ = AttachSurfaceAsync(vm);
            }
        }
    }

    private void OnBoundViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_boundVm is not { } vm)
        {
            return;
        }

        if (e.PropertyName == nameof(StreamWatcherViewModel.IsWatchMode))
        {
            if (vm.IsWatchMode && _isAttached)
            {
                _lastActivityUtc = DateTime.UtcNow;
                vm.ShowControls();
                _ = AttachSurfaceAsync(vm);
            }
            else
            {
                DetachSurface();
            }
        }
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _isAttached = true;
        _hasLastCursor = false;
        _lastActivityUtc = DateTime.UtcNow;
        _boundVm?.ShowControls();
        _controlsIdleTimer.Start();

        if (TopLevel.GetTopLevel(this) is Window win && !ReferenceEquals(_hookedWindow, win))
        {
            UnhookWindowKeys();
            _hookedWindow = win;
            _hookedWindow.AddHandler(KeyDownEvent, OnHostWindowKeyDown, RoutingStrategies.Tunnel);
            HookOverlayObservers(win);
        }
        else if (_hookedWindow is not null)
        {
            HookOverlayObservers(_hookedWindow);
        }

        if (_boundVm is { IsWatchMode: true } vm)
        {
            _ = AttachSurfaceAsync(vm);
        }
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _controlsIdleTimer.Stop();
        _isAttached = false;
        DetachSurface();

        if (_boundVm is { IsFullscreen: true } vm)
        {
            vm.ExitFullscreenCommand.Execute(null);
        }

        if (_hookedWindow is MainWindow mw)
        {
            mw.SetVideoFullscreen(false);
        }

        UnhookOverlayObservers();
        UnhookWindowKeys();
        VlcVideoView.IsVisible = true;
    }

    private async Task AttachSurfaceAsync(StreamWatcherViewModel vm)
    {
        try
        {
            var player = await vm.EnsurePlayerReadyForSurfaceAsync();
            if (!_isAttached || !ReferenceEquals(_boundVm, vm) || !vm.IsWatchMode)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_isAttached || !ReferenceEquals(_boundVm, vm) || !vm.IsWatchMode)
                {
                    return;
                }

                VlcVideoView.MediaPlayer = null;
                VlcVideoView.MediaPlayer = player;
                vm.NotifySurfaceAttached(reattachVideoTrack: true);
            }, DispatcherPriority.Loaded);
        }
        catch
        {
            // Ignore surface attach errors during rapid view teardown.
        }
    }

    private void OnChatMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_isAttached || _boundVm is null || _boundVm.IsChatAutoScrollPaused)
        {
            return;
        }

        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_isAttached && _boundVm is { IsChatAutoScrollPaused: false })
                {
                    ChatScrollViewer.ScrollToEnd();
                }
            }, DispatcherPriority.Background);
        }
    }

    private void OnChatScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_boundVm is null || !_isAttached)
        {
            return;
        }

        var maxY = Math.Max(0, ChatScrollViewer.Extent.Height - ChatScrollViewer.Viewport.Height);
        if (maxY <= 4)
        {
            _boundVm.IsChatAutoScrollPaused = false;
            return;
        }

        var nearBottom = ChatScrollViewer.Offset.Y >= maxY - 36;
        _boundVm.IsChatAutoScrollPaused = !nearBottom;
    }

    private void OnJumpToLatestChatClick(object? sender, RoutedEventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.IsChatAutoScrollPaused = false;
        }

        ChatScrollViewer.ScrollToEnd();
    }

    private void OnControlsBarActivity(object? sender, RoutedEventArgs e)
    {
        _lastActivityUtc = DateTime.UtcNow;
        _boundVm?.ShowControls();
    }

    private void OnControlsIdleTick(object? sender, EventArgs e)
    {
        if (!_isAttached || _boundVm is null || !_boundVm.IsWatchMode)
        {
            return;
        }

        if (!_boundVm.IsPlaying ||
            !_boundVm.HasMedia ||
            _boundVm.IsBusy ||
            _isScrubbing ||
            BottomControlBar.IsPointerOver ||
            SpeedComboBox.IsDropDownOpen ||
            QualityComboBox.IsDropDownOpen)
        {
            _lastActivityUtc = DateTime.UtcNow;
            _boundVm.ShowControls();
            return;
        }

        if (TopLevel.GetTopLevel(this) is not Window win ||
            win.WindowState == WindowState.Minimized ||
            !win.IsActive ||
            !IsVisible ||
            Bounds.Width <= 0 ||
            Bounds.Height <= 0)
        {
            return;
        }

        if (!GetCursorPos(out var cursor))
        {
            return;
        }

        var insideVideo = false;
        try
        {
            var topLeft = VlcVideoView.PointToScreen(new Point(0, 0));
            var bottomRight = VlcVideoView.PointToScreen(new Point(VlcVideoView.Bounds.Width, VlcVideoView.Bounds.Height));
            insideVideo = cursor.X >= topLeft.X &&
                          cursor.X <= bottomRight.X &&
                          cursor.Y >= topLeft.Y &&
                          cursor.Y <= bottomRight.Y;
        }
        catch
        {
            return;
        }

        if (!_hasLastCursor)
        {
            _hasLastCursor = true;
            _lastCursorX = cursor.X;
            _lastCursorY = cursor.Y;
            return;
        }

        var moved = Math.Abs(cursor.X - _lastCursorX) >= 3 || Math.Abs(cursor.Y - _lastCursorY) >= 3;
        _lastCursorX = cursor.X;
        _lastCursorY = cursor.Y;

        if (insideVideo && moved)
        {
            _lastActivityUtc = DateTime.UtcNow;
            _boundVm.ShowControls();
            return;
        }

        if ((DateTime.UtcNow - _lastActivityUtc).TotalSeconds >= ControlsAutoHideSeconds)
        {
            _boundVm.HideControlsIfEligible();
        }
    }

    private void OnViewModelFullscreenChanged(object? sender, bool isFullscreen)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow mw)
        {
            mw.SetVideoFullscreen(isFullscreen);
        }
    }

    private void UnhookWindowKeys()
    {
        if (_hookedWindow is not null)
        {
            _hookedWindow.RemoveHandler(KeyDownEvent, OnHostWindowKeyDown);
            _hookedWindow = null;
        }
    }

    private void HookOverlayObservers(Window win)
    {
        UnhookOverlayObservers();
        if (win.DataContext is MainWindowViewModel mainVm)
        {
            _observedMainVm = mainVm;
            _observedMainVm.PropertyChanged += OnOverlayStatePropertyChanged;

            _observedDialogService = mainVm.DialogService;
            _observedDialogService.PropertyChanged += OnOverlayStatePropertyChanged;

            if (mainVm.CurrentScreen is DashboardViewModel dashVm)
            {
                _observedDashboardVm = dashVm;
                _observedDashboardVm.PropertyChanged += OnOverlayStatePropertyChanged;
            }
        }

        SyncVideoSurfaceVisibilityForOverlays();
    }

    private void UnhookOverlayObservers()
    {
        if (_observedMainVm is not null)
        {
            _observedMainVm.PropertyChanged -= OnOverlayStatePropertyChanged;
            _observedMainVm = null;
        }

        if (_observedDialogService is not null)
        {
            _observedDialogService.PropertyChanged -= OnOverlayStatePropertyChanged;
            _observedDialogService = null;
        }

        if (_observedDashboardVm is not null)
        {
            _observedDashboardVm.PropertyChanged -= OnOverlayStatePropertyChanged;
            _observedDashboardVm = null;
        }
    }

    private void OnOverlayStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsSettingsOpen)
            or nameof(DialogService.IsDialogOpen)
            or nameof(DashboardViewModel.HasOpenModal)
            or nameof(DashboardViewModel.IsActionSheetOpen)
            or nameof(DashboardViewModel.IsAppPickerOpen)
            or nameof(DashboardViewModel.IsModuleSettingsOpen))
        {
            SyncVideoSurfaceVisibilityForOverlays();
        }
    }

    private void SyncVideoSurfaceVisibilityForOverlays()
    {
        var modalOpen = (_observedMainVm?.IsSettingsOpen ?? false) ||
                        (_observedDialogService?.IsDialogOpen ?? false) ||
                        (_observedDashboardVm?.HasOpenModal ?? false);

        VlcVideoView.IsVisible = !modalOpen;
    }

    private void OnHostWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isAttached || _boundVm is null || !_boundVm.IsWatchMode || IsTextInputFocused(e.Source))
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is MainWindow { DataContext: MainWindowViewModel mainVm })
        {
            if (mainVm.IsSettingsOpen || mainVm.DialogService.IsDialogOpen)
            {
                return;
            }

            if (mainVm.CurrentScreen is DashboardViewModel { HasOpenModal: true })
            {
                return;
            }
        }

        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        switch (e.Key)
        {
            case Key.Space:
                _lastActivityUtc = DateTime.UtcNow;
                _boundVm.ShowControls();
                _boundVm.TogglePlayPauseCommand.Execute(null);
                (_hookedWindow as MainWindow)?.MarkSpaceHandled();
                e.Handled = true;
                break;
            case Key.Left when _boundVm.CanSeek:
                _lastActivityUtc = DateTime.UtcNow;
                _boundVm.ShowControls();
                _ = _boundVm.SeekRelativeAsync(shift ? -30000 : -5000);
                e.Handled = true;
                break;
            case Key.Right when _boundVm.CanSeek:
                _lastActivityUtc = DateTime.UtcNow;
                _boundVm.ShowControls();
                _ = _boundVm.SeekRelativeAsync(shift ? 30000 : 5000);
                e.Handled = true;
                break;
            case Key.Up:
                _lastActivityUtc = DateTime.UtcNow;
                _boundVm.ShowControls();
                _boundVm.AdjustVolume(5);
                e.Handled = true;
                break;
            case Key.Down:
                _lastActivityUtc = DateTime.UtcNow;
                _boundVm.ShowControls();
                _boundVm.AdjustVolume(-5);
                e.Handled = true;
                break;
            case Key.F:
                _lastActivityUtc = DateTime.UtcNow;
                _boundVm.ShowControls();
                _boundVm.ToggleFullscreenCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.M:
                _lastActivityUtc = DateTime.UtcNow;
                _boundVm.ShowControls();
                _boundVm.ToggleMuteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.L when !_boundVm.IsLiveStream:
                _lastActivityUtc = DateTime.UtcNow;
                _boundVm.ShowControls();
                _boundVm.ToggleLoopCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.A:
                _lastActivityUtc = DateTime.UtcNow;
                _boundVm.ShowControls();
                _boundVm.CycleAspectRatioCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.E:
                _lastActivityUtc = DateTime.UtcNow;
                _boundVm.ShowControls();
                _boundVm.FrameStepCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.S when shift:
                _lastActivityUtc = DateTime.UtcNow;
                _boundVm.ShowControls();
                _boundVm.TakeSnapshotCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.OemOpenBrackets when !_boundVm.IsLiveStream:
                _lastActivityUtc = DateTime.UtcNow;
                _boundVm.ShowControls();
                _boundVm.StepSpeed(-1);
                e.Handled = true;
                break;
            case Key.OemCloseBrackets when !_boundVm.IsLiveStream:
                _lastActivityUtc = DateTime.UtcNow;
                _boundVm.ShowControls();
                _boundVm.StepSpeed(1);
                e.Handled = true;
                break;
            case Key.Escape when _boundVm.IsFullscreen:
                _lastActivityUtc = DateTime.UtcNow;
                _boundVm.ShowControls();
                _boundVm.ExitFullscreenCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private static bool IsTextInputFocused(object? source)
    {
        if (source is not Visual visual)
        {
            return false;
        }

        return visual is TextBox || visual.FindAncestorOfType<TextBox>() is not null;
    }

    private void OnSeekPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _isScrubbing = true;
        _boundVm?.BeginScrub();
    }

    private void OnSeekSliderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_isScrubbing && e.Property == Slider.ValueProperty && e.NewValue is double val)
        {
            _boundVm?.UpdateScrubPreview(val);
        }
    }

    private void OnSeekPointerReleased(object? sender, PointerReleasedEventArgs e) =>
        CommitCurrentScrub();

    private void OnSeekPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        CommitCurrentScrub();

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

    private async Task WriteClipboardAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null && !string.IsNullOrWhiteSpace(text))
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
