using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.UI.Rendering;

namespace BIMDesigner.UI;

/// <summary>
/// Light or dark: chosen once, at start-up, and remembered between sessions.
///
/// Every brush in the application is looked up from one palette dictionary, so the theme is
/// that dictionary swapped before the first window is built. The drawings are not brushes but
/// colours chosen in code, so they ask here which set of colours to draw with.
/// </summary>
public static class AppTheme
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BIMDesigner", "settings.json");

    /// <summary>Whether the light theme is in use. Light unless the dark one has been chosen.</summary>
    public static bool IsLight { get; private set; }

    /// <summary>The colours the plan and sections are drawn in on screen.</summary>
    public static DrawingPalette Drawing => IsLight ? DrawingPalette.Paper : DrawingPalette.Screen;

    /// <summary>The colours the storey below is drawn in, faintly.</summary>
    public static DrawingPalette Underlay => IsLight ? DrawingPalette.UnderlayLight : DrawingPalette.Underlay;

    /// <summary>Reads the saved choice and puts its palette in place. Called before any window exists.</summary>
    public static void Apply(Application application)
    {
        IsLight = ReadSavedChoice() ?? true;
        if (!IsLight) return;

        // Every theme dictionary is loaded again after the light palette, rather than just the
        // palette replaced: styles look their colours up as they load, and would otherwise keep
        // the dark ones they found the first time.
        var dictionaries = application.Resources.MergedDictionaries;
        var sources = dictionaries
            .Select(d => d.Source)
            .Where(source => source is not null)
            .Select(source => source!.OriginalString.EndsWith("Palette.xaml", StringComparison.OrdinalIgnoreCase)
                ? "Themes/Palette.Light.xaml"
                : source!.OriginalString)
            .Select(path => path.StartsWith("pack:", StringComparison.OrdinalIgnoreCase)
                ? new Uri(path)
                : new Uri($"pack://application:,,,/{path.TrimStart('/')}"))
            .ToList();

        dictionaries.Clear();
        foreach (var source in sources) dictionaries.Add(new ResourceDictionary { Source = source });
    }

    /// <summary>Remembers a choice for the next start. The running window keeps the theme it was built with.</summary>
    public static void Choose(bool light)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings { Theme = light ? "Light" : "Dark" }));
        }
        catch (IOException)
        {
            // Not being able to save a preference is not worth stopping anyone's work for.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A colour for the current theme: the light one or the dark one.</summary>
    public static Color Pick(Color light, Color dark) => IsLight ? light : dark;

    private static bool? ReadSavedChoice()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            return settings?.Theme switch { "Dark" => false, "Light" => true, _ => null };
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class Settings
    {
        public string Theme { get; set; } = "Light";
    }
}
