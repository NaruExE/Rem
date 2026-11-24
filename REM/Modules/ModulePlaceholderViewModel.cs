using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace REM.Modules;

public sealed partial class ModulePlaceholderViewModel : ObservableObject, IDisposable
{
    private readonly IAppModule _module;
    private bool _disposed;

    public ModulePlaceholderViewModel(
        IAppModule module,
        string placeholderHeadline,
        IReadOnlyList<string> placeholderItems)
    {
        _module = module;
        PlaceholderHeadline = placeholderHeadline;
        PlaceholderItems = placeholderItems;
        AccentBrush = new SolidColorBrush(Color.Parse(module.AccentHex));

        _module.StateChanged += OnModuleStateChanged;
        _module.StatusSummaryChanged += OnModuleStatusSummaryChanged;

        RefreshStatus();
    }

    public string ModuleId => _module.Id;
    public string Name => _module.Name;
    public string Description => _module.Description;
    public Geometry IconGeometry => _module.IconGeometry;
    public string AccentHex => _module.AccentHex;
    public IBrush AccentBrush { get; }
    public string PlaceholderHeadline { get; }
    public IReadOnlyList<string> PlaceholderItems { get; }

    [ObservableProperty]
    private string _stateLabel = string.Empty;

    [ObservableProperty]
    private string _statusSummary = string.Empty;

    [ObservableProperty]
    private bool _isHeaderVisible;

    public Action? RequestCloseToDashboard { get; set; }

    [RelayCommand]
    private void ToggleHeader() => IsHeaderVisible = !IsHeaderVisible;

    [RelayCommand]
    private void MinimizeToHeadless()
    {
        _module.RunHeadless();
        RequestCloseToDashboard?.Invoke();
    }

    [RelayCommand]
    private void Pause()
    {
        _module.Pause();
        RequestCloseToDashboard?.Invoke();
    }

    [RelayCommand]
    private void Kill()
    {
        _module.Kill();
        RequestCloseToDashboard?.Invoke();
    }

    [RelayCommand]
    private void BackToDashboard()
    {
        RequestCloseToDashboard?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _module.StateChanged -= OnModuleStateChanged;
        _module.StatusSummaryChanged -= OnModuleStatusSummaryChanged;
        RequestCloseToDashboard = null;
    }

    private void OnModuleStateChanged(object? sender, ModuleState state) => RefreshStatus();

    private void OnModuleStatusSummaryChanged(object? sender, EventArgs e) => RefreshStatus();

    private void RefreshStatus()
    {
        StateLabel = _module.State switch
        {
            ModuleState.RunningUI => "Running (UI)",
            ModuleState.RunningHeadless => "Running (Headless)",
            ModuleState.Paused => "Paused",
            _ => "Stopped"
        };
        StatusSummary = _module.StatusSummary;
    }
}
