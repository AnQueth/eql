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

        foreach (string key in ColorKeys)
        {
            if (!Colors.TryGetValue(key, out string? color) || color is null ||
                !Regex.IsMatch(color, @"\A#[0-9a-fA-F]{6}\z", RegexOptions.CultureInvariant))
            {
                throw new FormatException($"Color '{key}' must be a #RRGGBB value.");
            }
        }
        if (Colors.Keys.Except(ColorKeys, StringComparer.Ordinal).Any())
        {
            throw new FormatException("Theme contains an unknown color name.");
        }
    }
}
