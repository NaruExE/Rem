using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using REM.Services;
using REM.ViewModels;

namespace REM.Views;

public partial class MoviesTvView : UserControl
{
    private MoviesTvViewModel? _boundVm;
    private Window? _hookedWindow;
    private MainWindowViewModel? _observedMainVm;
    private DialogService? _observedDialogService;
    private DashboardViewModel? _observedDashboardVm;
    private bool _isAttached;

    public MoviesTvView()
    {
        InitializeComponent();

        DataContextChanged += OnViewDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    public void ShutdownHost()
    {
        EmbedWebView.Shutdown();
    }

    private void OnViewDataContextChanged(object? sender, EventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.RegisterHost(null);
            _boundVm.NavigateWebViewRequested = null;
            _boundVm.ShutdownWebViewRequested = null;
            _boundVm.FullscreenChanged -= OnViewModelFullscreenChanged;
            _boundVm.PropertyChanged -= OnVmPropertyChanged;
            _boundVm = null;
        }

        EmbedWebView.BlockedAttempt -= OnWebViewBlockedAttempt;

        if (DataContext is MoviesTvViewModel vm)
        {
            _boundVm = vm;
            vm.RegisterHost(EmbedWebView);
            vm.NavigateWebViewRequested = NavigateWebViewAsync;
            vm.ShutdownWebViewRequested = () => EmbedWebView.Shutdown();
            vm.FullscreenChanged += OnViewModelFullscreenChanged;
            vm.PropertyChanged += OnVmPropertyChanged;
            EmbedWebView.BlockedAttempt += OnWebViewBlockedAttempt;
            SyncWebViewVisibility();
        }
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _isAttached = true;

        if (TopLevel.GetTopLevel(this) is Window win && !ReferenceEquals(_hookedWindow, win))
        {
            UnhookWindowListeners();
            _hookedWindow = win;
            _hookedWindow.AddHandler(KeyDownEvent, OnHostWindowKeyDown, RoutingStrategies.Tunnel);
            HookOverlayObservers(win);
        }
        else if (_hookedWindow is not null)
        {
            HookOverlayObservers(_hookedWindow);
        }

        SyncWebViewVisibility();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        EmbedWebView.Shutdown();

        if (_boundVm is { IsFullscreen: true } vm)
        {
            vm.ExitFullscreenCommand.Execute(null);
        }

        if (_hookedWindow is MainWindow mw)
        {
            mw.SetVideoFullscreen(false);
        }

        UnhookOverlayObservers();
        UnhookWindowListeners();
    }

    private async Task NavigateWebViewAsync(string url, IReadOnlyList<string> extraBlockedDomains)
    {
        if (!_isAttached)
        {
            return;
        }

        EmbedWebView.SetAdditionalBlockedDomains(extraBlockedDomains);
        SyncWebViewVisibility();
        await EmbedWebView.NavigateAsync(url);
    }

    private void OnWebViewBlockedAttempt(object? sender, string reason) =>
        _boundVm?.ReportBlockedAttempt(reason);

    private void OnViewModelFullscreenChanged(object? sender, bool isFullscreen)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow mw)
        {
            mw.SetVideoFullscreen(isFullscreen);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MoviesTvViewModel.IsPlayerView) or nameof(MoviesTvViewModel.IsSettingsOpen))
        {
            SyncWebViewVisibility();
        }
    }

    private void OnHostWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isAttached || _boundVm is null || e.Key != Key.Escape)
        {
            return;
        }

        if (_boundVm.IsSettingsOpen)
        {
            _boundVm.CloseSettingsModalCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (_boundVm.IsFullscreen)
        {
            _boundVm.ExitFullscreenCommand.Execute(null);
            e.Handled = true;
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

        SyncWebViewVisibility();
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

    private void UnhookWindowListeners()
    {
        if (_hookedWindow is not null)
        {
            _hookedWindow.RemoveHandler(KeyDownEvent, OnHostWindowKeyDown);
            _hookedWindow = null;
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
            SyncWebViewVisibility();
        }
    }

    private void SyncWebViewVisibility()
    {
        var globalModalOpen = (_observedMainVm?.IsSettingsOpen ?? false) ||
                              (_observedDialogService?.IsDialogOpen ?? false) ||
                              (_observedDashboardVm?.HasOpenModal ?? false);

        var showWebView = _boundVm is { IsPlayerView: true, IsSettingsOpen: false } && !globalModalOpen;
        EmbedWebView.IsVisible = showWebView;
    }
}
