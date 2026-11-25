using Avalonia.Controls;
using Avalonia.Media;

namespace REM.Modules;

public interface IAppModule : IDisposable
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    string IconGlyph { get; }
    Geometry IconGeometry { get; }
    string AccentHex { get; }

    ModuleState State { get; }
    bool HasActiveView { get; }
    string StatusSummary { get; }

    event EventHandler<ModuleState>? StateChanged;
    event EventHandler? StatusSummaryChanged;

    Control OpenUI();
    void RunHeadless();
    void Pause();
    void Kill();
}

public interface IHeaderConfigurableModule
{
    void SetHeaderVisibility(bool visible);
}

public interface ICloseableModule
{
    void AttachCloseCallback(Action closeToDashboard);
}

public interface IFullscreenModule
{
    void AttachFullscreenCallback(Action<bool> onFullscreenChanged);
}

public interface IVlcPlayMediaModule
{
    Action<string>? PlayCompletedMediaInVlcRequested { get; set; }
}

public interface IVlcOpenUiModule
{
    Action? OpenVlcUiRequested { get; set; }
}
