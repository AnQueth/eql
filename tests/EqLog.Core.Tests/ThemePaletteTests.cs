using System.Text.Json;

namespace EqLog.Core.Tests;

public class ThemePaletteTests
{
    [Theory]
    [InlineData("Halloween.json", "Halloween")]
    [InlineData("Midnight.json", "Midnight Ocean")]
    [InlineData("Forest.json", "Enchanted Forest")]
    public void Parse_BundledTheme_ContainsCompletePalette(string file, string name)
    {
        var theme = ThemePalette.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Themes", file)));
        Assert.Equal(name, theme.Name);
        Assert.Equal(ThemePalette.ColorKeys.Length, theme.Colors.Count);
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
