namespace REM.Models;

public enum DialogKind
{
    Info,
    Warning,
    Error,
    Confirm
}

public sealed record DialogButtonOption(
    string Key,
    string Label,
    bool IsPrimary = false,
    bool IsDanger = false);

public sealed record DialogRequest(
    DialogKind Kind,
    string Title,
    string Message,
    string? Details,
    IReadOnlyList<DialogButtonOption> Buttons);

