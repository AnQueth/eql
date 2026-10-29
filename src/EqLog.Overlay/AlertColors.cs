using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace EqLog.Overlay;

/// <summary>Resolves an alert trigger's color text (a WPF color name or #RRGGBB) the same way everywhere.</summary>
public static class AlertColors
{
    public const string Default = "#FF5A5A";

    public static bool TryParse(string? text, out Color color)
    {
        string value = string.IsNullOrWhiteSpace(text) ? Default : text.Trim();
        try
        {
            color = (Color)ColorConverter.ConvertFromString(value);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException)
        {
            color = default;
            return false;
        }
    }

    /// <summary>A frozen brush for the alert; invalid colors fall back to orange-red.</summary>
    public static Brush ToBrush(string? text)
    {
        if (!TryParse(text, out var color))
        {
            return Brushes.OrangeRed;
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>Binds an alert trigger's color text to the swatch brush shown in the trigger editor.</summary>
public sealed class AlertColorBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return AlertColors.ToBrush(value as string);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
