using EqLog.Core;

namespace EqLog.Application.Settings;

/// <summary>Palette validation, copy and fallback rules shared by native presentation and storage workflows.</summary>
public static class ThemeQueries
{
    public static void Validate(ThemePalette palette)
    {
        palette.Validate();
    }

    public static ThemePalette Copy(ThemePalette palette)
    {
        return palette.Clone();
    }

    public static string Color(ThemePalette palette, string key)
    {
        return palette.GetColor(key);
    }
}
