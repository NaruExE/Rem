using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using REM.Services;
using REM.ViewModels;
using REM.Views;

namespace REM;

public sealed class App : Application
{
    private DatabaseService? _database;
    private ThemeService? _themeService;
    private AudioService? _audioService;
    private YtDlpService? _ytDlpService;
    private YouTubeAudioService? _youtubeService;
    private TwitchHelixService? _twitchService;
    private SpotifySoloistService? _soloistService;
    private SpotifyWebApiService? _spotifyWebApiService;
    private LastFmService? _lastFmService;
    private LrclibService? _lrclibService;
    private VideoMediaResolverService? _videoResolverService;
    private VideoPlayerService? _videoPlayerService;
    private TmdbService? _tmdbService;
    private ModuleManager? _moduleManager;
    private ImageAssetService? _imageAssets;
    private ActionLogService? _actionLog;
    private DialogService? _dialogService;
    private StartupService? _startupService;
    private TrayService? _trayService;
    private TaskbarMediaControlService? _taskbarMedia;
    private MainWindowViewModel? _mainViewModel;
    private bool _exceptionHooksAttached;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _actionLog = new ActionLogService();
            _actionLog.Log("System", "REM starting up.");

            _database = new DatabaseService();
            _database.Initialize();

            _themeService = new ThemeService();
            _audioService = new AudioService();
            _imageAssets = new ImageAssetService();
            _dialogService = new DialogService(_imageAssets, _actionLog);
            _startupService = new StartupService();
            _ytDlpService = new YtDlpService();
            _youtubeService = new YouTubeAudioService(_ytDlpService, _actionLog);
            _twitchService = new TwitchHelixService(_database, _actionLog);
            _soloistService = new SpotifySoloistService(_database, _actionLog);
            _spotifyWebApiService = new SpotifyWebApiService(_database, _actionLog);
            _lastFmService = new LastFmService(_database, _actionLog);
            _lrclibService = new LrclibService(_actionLog);
            _videoResolverService = new VideoMediaResolverService(_ytDlpService, _actionLog);
            _videoPlayerService = new VideoPlayerService(_database, _videoResolverService, _actionLog);
            _tmdbService = new TmdbService(_database, _actionLog);

            _moduleManager = new ModuleManager(
                _database,
                _actionLog,
                _dialogService,
                _imageAssets,
                _ytDlpService,
                _youtubeService,
                _twitchService,
                _soloistService,
                _spotifyWebApiService,
                _lastFmService,
                _lrclibService,
                _videoResolverService,
                _videoPlayerService,
                _tmdbService);

            AttachUnhandledExceptionHandlers();

            var settings = _database.LoadSettings();
            var themeOk = _themeService.ApplyTheme(settings.ThemeName, out var themeError);
            if (!themeOk && !string.IsNullOrWhiteSpace(themeError))
            {
                _actionLog.Log("Warning", $"Startup theme '{settings.ThemeName}' failed: {themeError}");
            }

            _mainViewModel = new MainWindowViewModel(
                _database,
                _themeService,
                _audioService,
                _moduleManager,
                _imageAssets,
                _actionLog,
                _dialogService,
                _startupService,
                _ytDlpService,
                _twitchService,
                _soloistService,
                _spotifyWebApiService,
                _lastFmService);

            var mainWindow = new MainWindow
            {
                DataContext = _mainViewModel
            };

            _taskbarMedia = new TaskbarMediaControlService(
                mainWindow,
                _soloistService,
                _moduleManager.AudioEngine,
                _actionLog);

            var icon = _imageAssets.TryLoadWindowIcon();
            if (icon is not null)
            {
                mainWindow.Icon = icon;
            }

            void RestoreWindow()
            {
                if (!mainWindow.IsVisible)
                {
                    mainWindow.Show();
                }

                if (mainWindow.WindowState == WindowState.Minimized)
                {
                    mainWindow.WindowState = WindowState.Normal;
                }

                mainWindow.Activate();
            }

            _trayService = new TrayService(
                _moduleManager,
                _imageAssets,
                RestoreWindow,
                moduleId => _mainViewModel.OpenModuleFromTray(moduleId),
                () =>
                {
                    mainWindow.AllowRealClose = true;
                    mainWindow.Close();
                    desktop.Shutdown();
                });

            _mainViewModel.RefreshShellIcons = () =>
            {
                var nextIcon = _imageAssets.TryLoadWindowIcon();
                if (nextIcon is not null)
                {
                    mainWindow.Icon = nextIcon;
                }

                _trayService?.RefreshIcon();
            };

            desktop.MainWindow = mainWindow;
            desktop.ShutdownRequested += (_, _) =>
            {
                _actionLog?.Log("System", "REM shutting down.");
                _taskbarMedia?.Dispose();
                _trayService?.Dispose();
                _mainViewModel?.Shutdown();
                _tmdbService?.Dispose();
                _videoPlayerService?.Dispose();
                _soloistService?.Dispose();
                _spotifyWebApiService?.Dispose();
                _lastFmService?.Dispose();
                _lrclibService?.Dispose();
                _youtubeService?.Dispose();
                _twitchService?.Dispose();
                _imageAssets?.Dispose();
            };

            _ = _mainViewModel.StartAsync();

            if (!themeOk && !string.IsNullOrWhiteSpace(themeError))
            {
                _ = _dialogService.ShowWarningAsync(
                    "Theme Mod Could Not Load",
                    $"Saved theme '{settings.ThemeName}' failed to load on startup. Using Obsidian instead.",
                    themeError);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void AttachUnhandledExceptionHandlers()
    {
        if (_exceptionHooksAttached)
        {
            return;
        }

        _exceptionHooksAttached = true;

        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            _actionLog?.Log("Error", $"Unhandled UI exception: {args.Exception.Message}");
            args.Handled = true;
            if (_dialogService is not null)
            {
                _ = _dialogService.ShowErrorAsync(
                    "Unexpected Error",
                    "Something went wrong while updating the interface.",
                    args.Exception.Message);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _actionLog?.Log("Error", $"Unobserved task exception: {args.Exception.GetBaseException().Message}");
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                _actionLog?.Log("Fatal", $"Unhandled domain exception: {ex.Message}");
            }
        };
    }
}
