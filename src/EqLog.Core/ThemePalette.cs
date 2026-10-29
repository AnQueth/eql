using System.Text.Json;
using System.Text.RegularExpressions;

namespace EqLog.Core;

public sealed class ThemePalette
{
    public string Name { get; set; } = "";
    public Dictionary<string, string> Colors { get; set; } = [];

    public ThemePalette Clone()
    {
        return new ThemePalette
        {
            Name = Name,
            Colors = Colors is null ? null! : new Dictionary<string, string>(Colors, Colors.Comparer),
        };
    }

    public static readonly string[] ColorKeys =
    [
        "NightBackground", "NightSurface", "NightRaised", "NightBorder", "NightText", "NightMuted",
        "Pumpkin", "Hover", "Pressed", "Selection", "Alternate", "GridLine", "ScrollThumb", "AccentText",
    ];

    /// <summary>
    /// Overlay text colors added after the original palette. Themes may omit them, so older and custom
    /// themes keep loading and get these defaults (the colors overlays always used).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> OptionalColorDefaults = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["OverlayHeader"] = "#FFD778",
        ["OverlayDim"] = "#AAAAB4",
        ["OverlayGood"] = "#78E678",
        ["OverlayWarn"] = "#FFAA3C",
        ["OverlayBad"] = "#FF5A5A",
    };

    /// <summary>Every color a theme can set: the required keys followed by the optional overlay keys.</summary>
    public static readonly string[] AllColorKeys = [.. ColorKeys, .. OptionalColorDefaults.Keys];

    /// <summary>The theme's color for <paramref name="key"/>, or the default for an omitted optional key.</summary>
    public string GetColor(string key)
    {
        if (Colors.TryGetValue(key, out string? color))
        {
            return color;
        }

        return OptionalColorDefaults[key];
    }

    public static ThemePalette Parse(string json)
    {
        var theme = JsonSerializer.Deserialize<ThemePalette>(json)
            ?? throw new FormatException("Theme must be a JSON object.");
        theme.Validate();
        return theme;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80)
        {
            throw new FormatException("Theme Name must contain 1 to 80 characters.");
        }

        if (Colors is null)
        {
            throw new FormatException("Theme Colors must be an object.");
        }

        foreach (string key in AllColorKeys)
        {
            bool optional = OptionalColorDefaults.ContainsKey(key);
            if (!Colors.TryGetValue(key, out string? color))
            {
                if (optional)
                {
                    continue;
                }

                throw new FormatException($"Color '{key}' must be a #RRGGBB value.");
            }

            if (color is null || !Regex.IsMatch(color, @"\A#[0-9a-fA-F]{6}\z", RegexOptions.CultureInvariant))
            {
                throw new FormatException($"Color '{key}' must be a #RRGGBB value.");
            }
        }
        if (Colors.Keys.Except(AllColorKeys, StringComparer.Ordinal).Any())
        {
            throw new FormatException("Theme contains an unknown color name.");
        }
    }
}
