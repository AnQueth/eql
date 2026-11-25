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
        var colors = new Dictionary<string, Color>();
        foreach (string key in ThemePalette.AllColorKeys)
        {
            var color = (Color)ColorConverter.ConvertFromString(ThemeQueries.Color(theme, key));
            colors[key] = color;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
            resources[key + "Color"] = color;
        }
        PublishDockChrome(resources, colors);

        resources[SystemColors.WindowBrushKey] = resources["NightSurface"];
        resources[SystemColors.WindowTextBrushKey] = resources["NightText"];
        resources[SystemColors.ControlBrushKey] = resources["NightRaised"];
        resources[SystemColors.ControlTextBrushKey] = resources["NightText"];
        resources[SystemColors.HighlightBrushKey] = resources["Selection"];
        resources[SystemColors.HighlightTextBrushKey] = resources["NightText"];
    }

    /// <summary>
    /// Publishes the shaded colors of the docked panel frame: a lit bevel, a sunken inset, and a raised title bar, all
    /// blended from the palette's accent, border, raised, and background colors.
    /// </summary>
    private static void PublishDockChrome(ResourceDictionary resources, IReadOnlyDictionary<string, Color> colors)
    {
        Color metal = colors["NightBorder"];
        Color accent = colors["Pumpkin"];
        Color raised = colors["NightRaised"];
        Color back = colors["NightBackground"];
        resources["DockBevelTopColor"] = Blend(accent, Colors.White, 0.35);
        resources["DockBevelBottomColor"] = Blend(metal, Colors.Black, 0.8);
        resources["DockInsetBottomColor"] = Blend(back, Colors.Black, 0.25);
        resources["DockInsetBorderBottomColor"] = Blend(metal, Colors.White, 0.2);
        resources["DockTitleTopColor"] = Blend(raised, accent, 0.12);
        resources["DockTitleBottomColor"] = Blend(raised, Colors.Black, 0.3);
        resources["DockTitleBorderBottomColor"] = Blend(metal, Colors.Black, 0.5);
    }

    private static Color Blend(Color from, Color to, double amount)
    {
        return Color.FromRgb(
            (byte)(from.R + (to.R - from.R) * amount),
            (byte)(from.G + (to.G - from.G) * amount),
            (byte)(from.B + (to.B - from.B) * amount));
    }
}
