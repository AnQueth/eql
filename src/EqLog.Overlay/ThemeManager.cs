using System.IO;
using System.Windows;
using System.Windows.Media;
using EqLog.Core;

namespace EqLog.Overlay;

public static class ThemeManager
{
    public static string BuiltInFolder => Path.Combine(AppContext.BaseDirectory, "Themes");
    public static ThemePalette Load(string path) => ThemePalette.Parse(File.ReadAllText(path));

    public static void Apply(ThemePalette theme)
    {
        theme.Validate();
        var resources = Application.Current.Resources;
        foreach (string key in ThemePalette.ColorKeys)
        {
            var color = (Color)ColorConverter.ConvertFromString(theme.Colors[key]);
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
