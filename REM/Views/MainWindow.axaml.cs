using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using REM.Services;
using REM.ViewModels;

namespace REM.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closing += OnWindowClosing;

        AddHandler(InputElement.PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        PropertyChanged += OnWindowPropertyChanged;
    }

    private WindowState _preFullscreenState = WindowState.Normal;
    private bool _isVideoFullscreen;

    public bool AllowRealClose { get; set; }

    public void SetVideoFullscreen(bool isFullscreen)
    {
        if (isFullscreen == _isVideoFullscreen)
        {
            return;
        }

        _isVideoFullscreen = isFullscreen;
        if (isFullscreen)
        {
            _preFullscreenState = WindowState == WindowState.FullScreen
                ? WindowState.Normal
                : WindowState;

            if (this.FindControl<Border>("CustomTitleBar") is { } bar)
            {
                bar.IsVisible = false;
            }

            WindowState = WindowState.FullScreen;
        }
        else
        {
            if (this.FindControl<Border>("CustomTitleBar") is { } bar)
            {
                bar.IsVisible = true;
            }

            if (WindowState == WindowState.FullScreen)
            {
                WindowState = _preFullscreenState == WindowState.FullScreen
                    ? WindowState.Normal
                    : _preFullscreenState;
            }
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.PickAudioFileAsync = PickAudioFileAsync;
            vm.PickImageFileAsync = PickImageFileAsync;
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!AllowRealClose &&
            e.CloseReason != WindowCloseReason.ApplicationShutdown &&
            e.CloseReason != WindowCloseReason.OSShutdown &&
            DataContext is MainWindowViewModel vm &&
            vm.ShouldMinimizeToTray())
        {
            e.Cancel = true;
            Hide();
            vm.OnWindowVisibilityChanged(false);
            vm.LogMinimizedToTray();
            return;
        }

        if (DataContext is MainWindowViewModel mainVm)
        {
            mainVm.Shutdown();
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty || e.Property == IsVisibleProperty)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.OnWindowVisibilityChanged(IsVisible && WindowState != WindowState.Minimized);
            }
        }

        if (e.Property != WindowStateProperty)
        {
            return;
        }

        var isMaximized = WindowState == WindowState.Maximized;
        if (this.FindControl<Border>("RootFrame") is { } frame)
        {
            frame.Padding = isMaximized ? new Thickness(7) : new Thickness(0);
        }

        if (this.FindControl<PathIcon>("MaximizeRestoreIcon") is { } icon)
        {
            icon.Data = isMaximized ? IconCatalog.WindowRestore : IconCatalog.WindowMaximize;
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
        }
        else
        {
            BeginMoveDrag(e);
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreClick(object? sender, RoutedEventArgs e) =>
        ToggleMaximizeRestore();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (kind == PointerUpdateKind.XButton1Pressed)
        {
            if (vm.NavigateBack())
            {
                e.Handled = true;
            }
        }
        else if (kind == PointerUpdateKind.XButton2Pressed)
        {
            if (vm.NavigateForward())
            {
                e.Handled = true;
            }
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (vm.DialogService.TryDismissActive())
        {
            e.Handled = true;
            return;
        }

        if (vm.IsSettingsOpen)
        {
            vm.CloseSettings();
            e.Handled = true;
            return;
        }

        if (vm.CurrentScreen is ProfilesViewModel pvm && pvm.TryCloseModal())
        {
            e.Handled = true;
            return;
        }

        if (vm.CurrentScreen is DashboardViewModel dvm && dvm.TryCloseModal())
        {
            e.Handled = true;
        }
    }

    private async Task<string?> PickAudioFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose Welcome Voice Audio",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Audio Files")
                {
                    Patterns = ["*.wav", "*.mp3", "*.ogg", "*.flac", "*.m4a"]
                }
            ]
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickImageFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose Profile Picture",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Image Files")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"]
                }
            ]
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
