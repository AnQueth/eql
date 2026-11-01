using System.IO;
using System.Windows;
using System.Windows.Media;
using EqLog.Core;
using Microsoft.Win32;

namespace EqLog.Overlay;

public static class ThemeManager
{
    internal static ThemePalette GetSystemTheme(IEnumerable<ThemePalette> themes)
    {
        var preference = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1);
        string name = preference is int value && value == 0 ? "Neutral Dark" : "Neutral Light";
        return themes.Single(t => t.Name == name);
    }

    public static void Apply(ThemePalette theme)
    {
        ThemeQueries.Validate(theme);
        var resources = System.Windows.Application.Current.Resources;
        foreach (string key in ThemePalette.AllColorKeys)
        {
            var color = (Color)ColorConverter.ConvertFromString(ThemeQueries.Color(theme, key));
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
