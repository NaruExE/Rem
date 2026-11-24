using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using REM.Modules;

namespace REM.Services;

public sealed class TrayService : IDisposable
{
    private readonly ModuleManager _moduleManager;
    private readonly ImageAssetService _imageAssets;
    private readonly Action _onRestoreWindow;
    private readonly Action<string> _onOpenModuleUi;
    private readonly Action _onExitRequested;
    private TrayIcon? _trayIcon;
    private bool _disposed;

    public TrayService(
        ModuleManager moduleManager,
        ImageAssetService imageAssets,
        Action onRestoreWindow,
        Action<string> onOpenModuleUi,
        Action onExitRequested)
    {
        _moduleManager = moduleManager;
        _imageAssets = imageAssets;
        _onRestoreWindow = onRestoreWindow;
        _onOpenModuleUi = onOpenModuleUi;
        _onExitRequested = onExitRequested;

        InitializeTray();
        _moduleManager.ModuleStateChanged += OnModuleStateChanged;
    }

    public void RefreshIcon()
    {
        if (_disposed || _trayIcon is null)
        {
            return;
        }

        _trayIcon.Icon = _imageAssets.TryLoadWindowIcon();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _moduleManager.ModuleStateChanged -= OnModuleStateChanged;

        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Clicked -= OnTrayClicked;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        var app = Application.Current;
        if (app is not null)
        {
            TrayIcon.SetIcons(app, []);
        }
    }

    private void InitializeTray()
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        _trayIcon = new TrayIcon
        {
            ToolTipText = "REM · RAM Efficient Manager",
            Icon = _imageAssets.TryLoadWindowIcon(),
            IsVisible = true
        };
        _trayIcon.Clicked += OnTrayClicked;

        RebuildMenu();
        TrayIcon.SetIcons(app, [_trayIcon]);
    }

    private void OnTrayClicked(object? sender, EventArgs e) => _onRestoreWindow();

    private void OnModuleStateChanged(object? sender, (string ModuleId, ModuleState State) args)
    {
        if (_disposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(RebuildMenu);
    }

    private void RebuildMenu()
    {
        if (_disposed || _trayIcon is null)
        {
            return;
        }

        var menu = new NativeMenu();

        var openItem = new NativeMenuItem("Open REM");
        openItem.Click += (_, _) => _onRestoreWindow();
        menu.Items.Add(openItem);

        var activeModules = _moduleManager.AllModules
            .Where(m => m.State != ModuleState.Stopped)
            .ToList();

        if (activeModules.Count > 0)
        {
            menu.Items.Add(new NativeMenuItemSeparator());
            foreach (var module in activeModules)
            {
                var stateTag = module.State switch
                {
                    ModuleState.RunningUI => "UI",
                    ModuleState.RunningHeadless => "Headless",
                    ModuleState.Paused => "Paused",
                    _ => "Stopped"
                };

                var modMenu = new NativeMenu();

                var uiItem = new NativeMenuItem("Open UI");
                uiItem.Click += (_, _) =>
                {
                    _onRestoreWindow();
                    _onOpenModuleUi(module.Id);
                };
                modMenu.Items.Add(uiItem);

                if (module.State != ModuleState.RunningHeadless)
                {
                    var headlessItem = new NativeMenuItem("Run Headless");
                    headlessItem.Click += (_, _) => _moduleManager.RunModuleHeadless(module.Id);
                    modMenu.Items.Add(headlessItem);
                }

                if (module.State is ModuleState.RunningUI or ModuleState.RunningHeadless)
                {
                    var pauseItem = new NativeMenuItem("Pause");
                    pauseItem.Click += (_, _) => _moduleManager.PauseModule(module.Id);
                    modMenu.Items.Add(pauseItem);
                }

                modMenu.Items.Add(new NativeMenuItemSeparator());
                var killItem = new NativeMenuItem("Kill");
                killItem.Click += (_, _) => _moduleManager.KillModule(module.Id);
                modMenu.Items.Add(killItem);

                var parentItem = new NativeMenuItem($"{module.Name} ({stateTag})")
                {
                    Menu = modMenu
                };
                menu.Items.Add(parentItem);
            }
        }

        menu.Items.Add(new NativeMenuItemSeparator());
        var exitItem = new NativeMenuItem("Exit REM (Kill All)");
        exitItem.Click += (_, _) => _onExitRequested();
        menu.Items.Add(exitItem);

        _trayIcon.Menu = menu;
    }
}

