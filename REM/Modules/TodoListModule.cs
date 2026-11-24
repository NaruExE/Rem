using Avalonia.Media;
using REM.Services;

namespace REM.Modules;

public sealed class TodoListModule : AppModuleBase
{
    public override string Id => "todo-list";
    public override string Name => "To-Do List";
    public override string Description => "Clutter-free daily tasks & checklists";
    public override string IconGlyph => "T";
    public override Geometry IconGeometry => IconCatalog.TodoList;
    public override string AccentHex => "#4E9AF1";

    protected override string IdleSummary => "Stopped · Task list unloaded";
    protected override string PausedSummary => "Paused · Task draft kept lightweight";
    protected override string PlaceholderHeadline => "Quick Tasks";
    protected override IReadOnlyList<string> PlaceholderItems { get; } =
    [
        "[ ] Add custom Rem PNGs to Assets/Images",
        "[ ] Try switching themes in Settings",
        "[x] Keep background RAM minimal"
    ];

    protected override string FormatActiveSummary(int tickCount, bool isHeadless)
    {
        var elapsed = TimeSpan.FromSeconds(tickCount * 2);
        return isHeadless
            ? $"Headless · Checklist state saved ({elapsed:mm\\:ss})"
            : $"Running UI · Active ({elapsed:mm\\:ss})";
    }
}
