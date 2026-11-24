using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace REM.Modules;

public abstract class AppModuleBase : IAppModule
{
    private readonly object _sync = new();
    private Timer? _backgroundTimer;
    private Control? _activeView;
    private IDisposable? _activeViewModel;
    private ModuleState _state = ModuleState.Stopped;
    private int _tickCount;
    private bool _disposed;

    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract string IconGlyph { get; }
    public abstract Geometry IconGeometry { get; }
    public abstract string AccentHex { get; }

    protected abstract string IdleSummary { get; }
    protected abstract string PausedSummary { get; }
    protected abstract string PlaceholderHeadline { get; }
    protected abstract IReadOnlyList<string> PlaceholderItems { get; }

    public ModuleState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public bool HasActiveView
    {
        get
        {
            lock (_sync)
            {
                return _activeView is not null;
            }
        }
    }

    public string StatusSummary { get; private set; }

    public event EventHandler<ModuleState>? StateChanged;
    public event EventHandler? StatusSummaryChanged;

    protected AppModuleBase()
    {
        StatusSummary = "Stopped · 0 B UI memory";
    }

    public Control OpenUI()
    {
        ThrowIfDisposed();

        Control view;
        bool stateChanged = false;
        ModuleState newState;

        lock (_sync)
        {
            EnsureBackgroundTimerLocked();

            if (_activeView is null)
            {
                var (createdView, viewModel) = CreateViewCore();
                _activeView = createdView;
                _activeViewModel = viewModel;
            }

            view = _activeView;
            if (_state != ModuleState.RunningUI)
            {
                _state = ModuleState.RunningUI;
                stateChanged = true;
            }

            newState = _state;
            StatusSummary = FormatActiveSummary(_tickCount, isHeadless: false);
        }

        if (stateChanged)
        {
            StateChanged?.Invoke(this, newState);
        }

        StatusSummaryChanged?.Invoke(this, EventArgs.Empty);
        return view;
    }

    public void RunHeadless()
    {
        ThrowIfDisposed();

        bool stateChanged = false;
        ModuleState newState;

        lock (_sync)
        {
            EnsureBackgroundTimerLocked();
            DisposeViewLocked();

            if (_state != ModuleState.RunningHeadless)
            {
                _state = ModuleState.RunningHeadless;
                stateChanged = true;
            }

            newState = _state;
            StatusSummary = FormatActiveSummary(_tickCount, isHeadless: true);
        }

        if (stateChanged)
        {
            StateChanged?.Invoke(this, newState);
        }

        StatusSummaryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        if (_disposed)
        {
            return;
        }

        bool stateChanged = false;
        ModuleState newState;

        lock (_sync)
        {
            if (_state == ModuleState.Stopped)
            {
                return;
            }

            StopTimerLocked();
            DisposeViewLocked();

            if (_state != ModuleState.Paused)
            {
                _state = ModuleState.Paused;
                stateChanged = true;
            }

            newState = _state;
            StatusSummary = PausedSummary;
        }

        if (stateChanged)
        {
            StateChanged?.Invoke(this, newState);
        }

        StatusSummaryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Kill()
    {
        if (_disposed)
        {
            return;
        }

        KillCore();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        KillCore();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    protected abstract string FormatActiveSummary(int tickCount, bool isHeadless);

    private (Control View, IDisposable ViewModel) CreateViewCore()
    {
        var vm = new ModulePlaceholderViewModel(
            this,
            PlaceholderHeadline,
            PlaceholderItems);

        var view = new REM.Views.ModulePlaceholderView
        {
            DataContext = vm
        };

        return (view, vm);
    }

    private void KillCore()
    {
        bool stateChanged = false;

        lock (_sync)
        {
            StopTimerLocked();
            DisposeViewLocked();
            _tickCount = 0;

            if (_state != ModuleState.Stopped)
            {
                _state = ModuleState.Stopped;
                stateChanged = true;
            }

            StatusSummary = IdleSummary;
        }

        if (stateChanged)
        {
            StateChanged?.Invoke(this, ModuleState.Stopped);
        }

        StatusSummaryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureBackgroundTimerLocked()
    {
        if (_backgroundTimer is null)
        {
            _backgroundTimer = new Timer(OnBackgroundTick, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }
    }

    private void StopTimerLocked()
    {
        _backgroundTimer?.Dispose();
        _backgroundTimer = null;
    }

    private void DisposeViewLocked()
    {
        if (_activeView is not null)
        {
            _activeView.DataContext = null;
            if (_activeView is IDisposable disposableView)
            {
                disposableView.Dispose();
            }

            _activeView = null;
        }

        _activeViewModel?.Dispose();
        _activeViewModel = null;
    }

    private void OnBackgroundTick(object? _)
    {
        lock (_sync)
        {
            if (_disposed || (_state != ModuleState.RunningUI && _state != ModuleState.RunningHeadless))
            {
                return;
            }

            _tickCount++;
            StatusSummary = FormatActiveSummary(_tickCount, _state == ModuleState.RunningHeadless);
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                StatusSummaryChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
