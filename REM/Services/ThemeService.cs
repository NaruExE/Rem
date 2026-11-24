using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using REM.Models;

namespace REM.Services;

public sealed class ThemeService
{
    public const string DefaultThemeName = "Obsidian";

    private static readonly ThemeDescriptor[] BuiltInThemes =
    [
        new("Obsidian", "Obsidian (Default)", "avares://REM/Themes/Obsidian.axaml", false),
        new("PitchBlack", "Pitch Black (OLED)", "avares://REM/Themes/PitchBlack.axaml", false),
        new("MidnightMono", "Midnight Mono", "avares://REM/Themes/MidnightMono.axaml", false),
        new("CarbonViolet", "Carbon Violet", "avares://REM/Themes/CarbonViolet.axaml", false),
        new("CozyDark", "Cozy Dark", "avares://REM/Themes/CozyDark.axaml", false),
        new("FluffyPastel", "Fluffy Pastel", "avares://REM/Themes/FluffyPastel.axaml", false),
        new("MatchaCream", "Matcha Cream", "avares://REM/Themes/MatchaCream.axaml", false)
    ];

    private IResourceProvider? _activeThemeDictionary;

    public ThemeService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        ExternalThemesDirectory = Path.Combine(appData, "REM", "Themes");
        Directory.CreateDirectory(ExternalThemesDirectory);
    }

    public string ExternalThemesDirectory { get; }

    public string CurrentThemeName { get; private set; } = DefaultThemeName;

    public IReadOnlyList<ThemeDescriptor> GetAvailableThemes()
    {
        var list = new List<ThemeDescriptor>(BuiltInThemes);
        var seen = new HashSet<string>(BuiltInThemes.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

        var scanDirs = new[]
        {
            ExternalThemesDirectory,
            Path.Combine(AppContext.BaseDirectory, "Themes")
        };

        foreach (var dir in scanDirs)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.axaml", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (string.Equals(name, "SharedStyles", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (seen.Add(name))
                {
                    list.Add(new ThemeDescriptor(name, $"{name} (Mod)", file, true));
                }
            }
        }

        return list;
    }

    public bool ApplyTheme(string themeName) => ApplyTheme(themeName, out _);

    public bool ApplyTheme(string themeName, out string? errorMessage)
    {
        errorMessage = null;
        var app = Application.Current;
        if (app is null)
        {
            errorMessage = "Application instance is not initialized.";
            return false;
        }

        var themes = GetAvailableThemes();
        var descriptor = themes.FirstOrDefault(t =>
            string.Equals(t.Name, themeName, StringComparison.OrdinalIgnoreCase));

        if (descriptor is null)
        {
            errorMessage = $"Theme '{themeName}' was not found.";
            descriptor = BuiltInThemes[0];
        }

        var loaded = TryLoadDescriptor(descriptor, out var loadError);
        if (loaded is null)
        {
            errorMessage = loadError ?? $"Failed to load theme '{descriptor.DisplayName}'.";
            if (!string.Equals(descriptor.Name, DefaultThemeName, StringComparison.OrdinalIgnoreCase))
            {
                descriptor = BuiltInThemes[0];
                loaded = TryLoadDescriptor(descriptor, out _);
            }
        }

        if (loaded is null)
        {
            return false;
        }

        var merged = app.Resources.MergedDictionaries;
        if (_activeThemeDictionary is not null)
        {
            merged.Remove(_activeThemeDictionary);
        }
        else if (merged.Count > 0)
        {
            merged.RemoveAt(0);
        }

        merged.Add(loaded);
        _activeThemeDictionary = loaded;
        CurrentThemeName = descriptor.Name;
        return errorMessage is null;
    }

    private static IResourceProvider? TryLoadDescriptor(ThemeDescriptor descriptor, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            if (!descriptor.IsExternal)
            {
                return AvaloniaXamlLoader.Load(new Uri(descriptor.Source)) as IResourceProvider;
            }

            var xaml = File.ReadAllText(descriptor.Source);
            return AvaloniaRuntimeXamlLoader.Parse<ResourceDictionary>(xaml);
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return null;
        }
    }
}
