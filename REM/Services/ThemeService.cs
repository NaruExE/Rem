using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using REM.Models;

namespace REM.Services;

public sealed class ThemeService
{
    public const string DefaultThemeName = "Obsidian";

    private static readonly string[] TransparentSurfaceKeys =
    [
        "AppBackgroundBrush",
        "SidebarBackgroundBrush",
        "SurfaceBrush",
        "SurfaceAltBrush",
        "CardBrush"
    ];

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
    private readonly Dictionary<string, Color> _baseSurfaceColors = new(StringComparer.Ordinal);
    private double _lastModuleOpacity = 1.0;
    private bool _lastHasWallpaper;

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
        CaptureBaseSurfaceColors(loaded);
        ApplyTransparencyToActiveDictionary();
        return errorMessage is null;
    }

    public void ApplyModuleTransparency(double moduleOpacity, bool hasWallpaper)
    {
        _lastModuleOpacity = Math.Clamp(moduleOpacity, 0.25, 1.0);
        _lastHasWallpaper = hasWallpaper;
        ApplyTransparencyToActiveDictionary();
    }

    private void CaptureBaseSurfaceColors(IResourceProvider provider)
    {
        _baseSurfaceColors.Clear();
        if (provider is not ResourceDictionary dict)
        {
            return;
        }

        foreach (var key in TransparentSurfaceKeys)
        {
            if (dict.TryGetResource(key, null, out var res) && res is ISolidColorBrush brush)
            {
                _baseSurfaceColors[key] = brush.Color;
            }
        }
    }

    private void ApplyTransparencyToActiveDictionary()
    {
        if (_activeThemeDictionary is not ResourceDictionary dict || _baseSurfaceColors.Count == 0)
        {
            return;
        }

        var restoreOpaque = !_lastHasWallpaper || _lastModuleOpacity >= 0.99;
        foreach (var key in TransparentSurfaceKeys)
        {
            if (!_baseSurfaceColors.TryGetValue(key, out var c))
            {
                continue;
            }

            if (restoreOpaque)
            {
                dict[key] = new SolidColorBrush(c);
                continue;
            }

            byte alpha = key switch
            {
                "AppBackgroundBrush" => (byte)Math.Clamp(_lastModuleOpacity * 0.68 * 255.0, 18, 250),
                "SidebarBackgroundBrush" => (byte)Math.Clamp((_lastModuleOpacity * 0.78 + 0.08) * 255.0, 45, 252),
                _ => (byte)Math.Clamp((_lastModuleOpacity * 0.86 + 0.08) * 255.0, 58, 252)
            };

            dict[key] = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        }
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
