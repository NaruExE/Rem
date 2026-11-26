namespace REM.Models;

public sealed class AppSettings
{
    public bool SkipIntro { get; set; }
    public string ThemeName { get; set; } = "Obsidian";
    public string WelcomeAudioPath { get; set; } = string.Empty;
    public bool ShowAppHeaderBar { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool AutoCheckUpdates { get; set; } = true;
    public string WallpaperPath { get; set; } = string.Empty;
    public double WallpaperDim { get; set; } = 0.35;
    public bool WallpaperSoftBlur { get; set; }
    public double ModuleOpacity { get; set; } = 0.82;
}
