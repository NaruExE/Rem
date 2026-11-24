namespace REM.Models;

public sealed class AppSettings
{
    public bool SkipIntro { get; set; }
    public string ThemeName { get; set; } = "Obsidian";
    public string WelcomeAudioPath { get; set; } = string.Empty;
    public bool ShowAppHeaderBar { get; set; }
    public bool MinimizeToTray { get; set; } = true;
}
