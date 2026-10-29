using System.IO;
using System.Windows;
using System.Windows.Media;
using EqLog.Core;

namespace EqLog.Overlay;

public static class ThemeManager
{
    public static string BuiltInFolder
    {
        get
        {
            return Path.Combine(AppContext.BaseDirectory, "Themes");
        }
    }

    public static async Task<ThemePalette> LoadAsync(string path, CancellationToken ct = default)
    {
        return ThemePalette.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
    }

    public static void Apply(ThemePalette theme)
    {
        theme.Validate();
        var resources = Application.Current.Resources;
        foreach (string key in ThemePalette.AllColorKeys)
        {
            var color = (Color)ColorConverter.ConvertFromString(theme.GetColor(key));
            resources[key] = new SolidColorBrush(color);
        }

        resources[SystemColors.WindowBrushKey] = resources["NightSurface"];
        resources[SystemColors.WindowTextBrushKey] = resources["NightText"];
        resources[SystemColors.ControlBrushKey] = resources["NightRaised"];
        resources[SystemColors.ControlTextBrushKey] = resources["NightText"];
        resources[SystemColors.HighlightBrushKey] = resources["Selection"];
        resources[SystemColors.HighlightTextBrushKey] = resources["NightText"];
    }
}
