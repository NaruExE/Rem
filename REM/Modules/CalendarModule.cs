using Avalonia.Media;
using REM.Services;

namespace REM.Modules;

public sealed class CalendarModule : AppModuleBase
{
    public override string Id => "calendar";
    public override string Name => "Calendar";
    public override string Description => "Minimal schedule & desktop reminders";
    public override string IconGlyph => "C";
    public override Geometry IconGeometry => IconCatalog.Calendar;
    public override string AccentHex => "#E07A5F";

    protected override string IdleSummary => "Stopped · Reminder watcher inactive";
    protected override string PausedSummary => "Paused · Schedule cached, alerts muted";
    protected override string PlaceholderHeadline => "Today's Schedule";
    protected override IReadOnlyList<string> PlaceholderItems { get; } =
    [
        "11:00 — Deep focus block (45 min)",
        "14:30 — Break & reset",
        "17:00 — Review daily notes"
    ];

    protected override string FormatActiveSummary(int tickCount, bool isHeadless)
    {
        var elapsed = TimeSpan.FromSeconds(tickCount * 2);
        return isHeadless
            ? $"Headless · Watching reminders ({elapsed:mm\\:ss}) · UI disposed"
            : $"Running UI · Synced ({elapsed:mm\\:ss})";
    }
}
