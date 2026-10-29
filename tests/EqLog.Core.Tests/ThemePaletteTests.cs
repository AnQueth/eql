using System.Text.Json;

namespace EqLog.Core.Tests;

public class ThemePaletteTests
{
    [Theory]
    [InlineData("Halloween.json", "Halloween")]
    [InlineData("NeutralDark.json", "Neutral Dark")]
    [InlineData("NeutralLight.json", "Neutral Light")]
    [InlineData("Midnight.json", "Midnight Ocean")]
    [InlineData("Forest.json", "Enchanted Forest")]
    [InlineData("Crimson.json", "Crimson Ember")]
    [InlineData("Amethyst.json", "Royal Amethyst")]
    [InlineData("Arctic.json", "Arctic Frost")]
    [InlineData("Desert.json", "Desert Sand")]
    [InlineData("HighContrastDark.json", "High Contrast Dark")]
    [InlineData("HighContrastLight.json", "High Contrast Light")]
    [InlineData("ColorblindRedGreen.json", "Colorblind Safe (Red-Green)")]
    [InlineData("ColorblindBlueYellow.json", "Colorblind Safe (Blue-Yellow)")]
    public void Parse_BundledTheme_ContainsCompletePalette(string file, string name)
    {
        var theme = ThemePalette.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Themes", file)));
        Assert.Equal(name, theme.Name);
        Assert.All(ThemePalette.ColorKeys, key => Assert.True(theme.Colors.ContainsKey(key), key));
    }

    [Theory]
    [InlineData("HighContrastDark.json", 7.0)]
    [InlineData("HighContrastLight.json", 7.0)]
    [InlineData("ColorblindRedGreen.json", 4.5)]
    [InlineData("ColorblindBlueYellow.json", 4.5)]
    [InlineData("NeutralDark.json", 4.5)]
    [InlineData("NeutralLight.json", 4.5)]
    public void AccessibilityTheme_TextMeetsWcagContrast(string file, double minimum)
    {
        var theme = ThemePalette.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Themes", file)));
        Assert.Equal(ThemePalette.AllColorKeys.Length, theme.Colors.Count);
        string[] pairs =
        [
            "NightText/NightBackground", "NightText/NightSurface", "NightText/NightRaised", "NightText/Selection",
            "NightText/Hover", "NightText/Alternate", "NightMuted/NightBackground", "NightMuted/NightSurface",
            "Pumpkin/NightBackground", "Pumpkin/NightSurface", "AccentText/Pumpkin",
            "OverlayHeader/NightBackground", "OverlayDim/NightBackground", "OverlayGood/NightBackground",
            "OverlayWarn/NightBackground", "OverlayBad/NightBackground",
        ];
        Assert.All(pairs, pair =>
        {
            string[] keys = pair.Split('/');
            double ratio = Contrast(theme.Colors[keys[0]], theme.Colors[keys[1]]);
            Assert.True(ratio >= minimum, $"{pair} contrast {ratio:0.00} is below {minimum}");
        });
        if (theme.Name.StartsWith("Neutral ", StringComparison.Ordinal))
        {
            foreach (string background in new[] { "NightRaised", "Selection", "Hover", "Pressed" })
            {
                Assert.True(Contrast(theme.Colors["NightMuted"], theme.Colors[background]) >= minimum,
                    $"Muted text on {background} must meet {minimum}:1 contrast.");
                Assert.True(Contrast(theme.Colors["NightText"], theme.Colors[background]) >= minimum,
                    $"Text on {background} must meet {minimum}:1 contrast.");
            }
        }
    }

    private static double Contrast(string a, string b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int offset)
        {
            double c = Convert.ToInt32(hex.Substring(offset, 2), 16) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }

    [Fact]
    public void Validate_OptionalOverlayColorsOmitted_UsesDefaults()
    {
        var theme = Valid();
        theme.Validate();
        Assert.Equal(ThemePalette.OptionalColorDefaults["OverlayGood"], theme.GetColor("OverlayGood"));
        theme.Colors["OverlayGood"] = "#123456";
        theme.Validate();
        Assert.Equal("#123456", theme.GetColor("OverlayGood"));
    }

    [Fact]
    public void Validate_InvalidOptionalColor_RejectsPalette()
    {
        var theme = Valid();
        theme.Colors["OverlayBad"] = "red";
        Assert.Throws<FormatException>(theme.Validate);
    }

    private static ThemePalette Valid()
    {
        return new()
        {
            Name = "Test palette",
            Colors = ThemePalette.ColorKeys.ToDictionary(k => k, _ => "#aB12EF"),
        };
    }

    [Fact]
    public void Parse_RoundTrip_PreservesPalette()
    {
        var original = Valid();
        var parsed = ThemePalette.Parse(JsonSerializer.Serialize(original));
        Assert.Equal(original.Name, parsed.Name);
        Assert.Equal(original.Colors, parsed.Colors);
    }

    [Theory]
    [InlineData("#123")]
    [InlineData("#11223344")]
    [InlineData("red")]
    [InlineData("#GG0000")]
    [InlineData("")]
    [InlineData(null)]
    public void Validate_InvalidColor_RejectsPalette(string? color)
    {
        var theme = Valid();
        theme.Colors["Pumpkin"] = color!;
        Assert.Throws<FormatException>(theme.Validate);
    }

    [Fact]
    public void Validate_MissingColor_RejectsPalette()
    {
        var theme = Valid();
        theme.Colors.Remove("NightText");
        Assert.Throws<FormatException>(theme.Validate);
    }

    [Fact]
    public void Validate_UnknownColor_RejectsPalette()
    {
        var theme = Valid();
        theme.Colors["Typo"] = "#000000";
        Assert.Throws<FormatException>(theme.Validate);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Name\":\"Test\",\"Colors\":null}")]
    public void Parse_InvalidShape_RejectsPalette(string json)
    {
        Assert.Throws<FormatException>(() => ThemePalette.Parse(json));
    }
}
