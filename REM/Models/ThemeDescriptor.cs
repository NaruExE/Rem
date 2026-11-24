namespace REM.Models;

public sealed class ThemeDescriptor
{
    public ThemeDescriptor(string name, string displayName, string source, bool isExternal)
    {
        Name = name;
        DisplayName = displayName;
        Source = source;
        IsExternal = isExternal;
    }

    public string Name { get; }
    public string DisplayName { get; }
    public string Source { get; }
    public bool IsExternal { get; }

    public override string ToString() => DisplayName;
}

