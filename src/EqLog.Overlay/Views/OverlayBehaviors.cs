using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace EqLog.Overlay.Views;

/// <summary>Maps a boolean to configurable visibilities (default: visible / collapsed).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>The visibility for <see langword="true"/>.</summary>
    public Visibility TrueValue { get; set; } = Visibility.Visible;

    /// <summary>The visibility for <see langword="false"/> or non-boolean values.</summary>
    public Visibility FalseValue { get; set; } = Visibility.Collapsed;

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? TrueValue : FalseValue;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Equals(value, TrueValue);
    }
}

/// <summary>Offsets a font size and keeps it at or above a minimum, for secondary text such as card details.</summary>
public sealed class FontSizeOffsetConverter : IValueConverter
{
    /// <summary>The amount added to the source size.</summary>
    public double Offset { get; set; } = -1;

    /// <summary>The smallest size produced.</summary>
    public double Minimum { get; set; } = 10;

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is double size ? Math.Max(Minimum, size + Offset) : DependencyProperty.UnsetValue;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

/// <summary>Multiplies numeric binding values, for example an overlay font size by a row scale.</summary>
public sealed class ProductConverter : IMultiValueConverter
{
    /// <inheritdoc />
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        double product = 1;
        foreach (object value in values)
        {
            if (value is not double number)
            {
                return DependencyProperty.UnsetValue;
            }
            product *= number;
        }
        return product;
    }

    /// <inheritdoc />
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        return [];
    }
}

/// <summary>
/// Runs a command when an element is clicked with the left button. The press is marked handled while the command can
/// run, so a surrounding drag surface or clickable card does not also act on it.
/// </summary>
public static class ClickCommand
{
    /// <summary>The command to run.</summary>
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(ClickCommand), new PropertyMetadata(null, CommandChanged));

    /// <summary>The command parameter.</summary>
    public static readonly DependencyProperty CommandParameterProperty = DependencyProperty.RegisterAttached(
        "CommandParameter", typeof(object), typeof(ClickCommand), new PropertyMetadata(null));

    /// <summary>Gets the command.</summary>
    public static ICommand? GetCommand(DependencyObject element)
    {
        return (ICommand?)element.GetValue(CommandProperty);
    }

    /// <summary>Sets the command.</summary>
    public static void SetCommand(DependencyObject element, ICommand? value)
    {
        element.SetValue(CommandProperty, value);
    }

    /// <summary>Gets the command parameter.</summary>
    public static object? GetCommandParameter(DependencyObject element)
    {
        return element.GetValue(CommandParameterProperty);
    }

    /// <summary>Sets the command parameter.</summary>
    public static void SetCommandParameter(DependencyObject element, object? value)
    {
        element.SetValue(CommandParameterProperty, value);
    }

    private static void CommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }
        element.MouseLeftButtonDown -= Pressed;
        element.MouseLeftButtonUp -= Released;
        if (e.NewValue is not null)
        {
            element.MouseLeftButtonDown += Pressed;
            element.MouseLeftButtonUp += Released;
        }
    }

    private static void Pressed(object sender, MouseButtonEventArgs e)
    {
        var element = (DependencyObject)sender;
        if (GetCommand(element) is { } command && command.CanExecute(GetCommandParameter(element)))
        {
            e.Handled = true;
        }
    }

    private static void Released(object sender, MouseButtonEventArgs e)
    {
        var element = (DependencyObject)sender;
        object? parameter = GetCommandParameter(element);
        if (GetCommand(element) is { } command && command.CanExecute(parameter))
        {
            e.Handled = true;
            command.Execute(parameter);
        }
    }
}

/// <summary>Opens a button's <see cref="FrameworkElement.ContextMenu"/> below it when the button is clicked.</summary>
public static class ContextMenuOnClick
{
    /// <summary>Whether clicking opens the context menu.</summary>
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ContextMenuOnClick), new PropertyMetadata(false, IsEnabledChanged));

    /// <summary>Gets whether clicking opens the context menu.</summary>
    public static bool GetIsEnabled(DependencyObject element)
    {
        return (bool)element.GetValue(IsEnabledProperty);
    }

    /// <summary>Sets whether clicking opens the context menu.</summary>
    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        element.SetValue(IsEnabledProperty, value);
    }

    private static void IsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not System.Windows.Controls.Primitives.ButtonBase button)
        {
            return;
        }
        button.Click -= Clicked;
        if (e.NewValue is true)
        {
            button.Click += Clicked;
        }
    }

    private static void Clicked(object sender, RoutedEventArgs e)
    {
        var button = (FrameworkElement)sender;
        if (button.ContextMenu is { } menu)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}

/// <summary>Keeps a <see cref="ScrollViewer"/> at the end while new content arrives, unless the user scrolled up.</summary>
public static class AutoScrollToEnd
{
    /// <summary>Whether the scroll viewer follows its end.</summary>
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(AutoScrollToEnd), new PropertyMetadata(false, IsEnabledChanged));

    private static readonly DependencyProperty WasAtEndProperty = DependencyProperty.RegisterAttached(
        "WasAtEnd", typeof(bool), typeof(AutoScrollToEnd), new PropertyMetadata(true));

    /// <summary>Gets whether the scroll viewer follows its end.</summary>
    public static bool GetIsEnabled(DependencyObject element)
    {
        return (bool)element.GetValue(IsEnabledProperty);
    }

    /// <summary>Sets whether the scroll viewer follows its end.</summary>
    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        element.SetValue(IsEnabledProperty, value);
    }

    private static void IsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer)
        {
            return;
        }
        viewer.ScrollChanged -= ScrollChanged;
        if (e.NewValue is true)
        {
            viewer.ScrollChanged += ScrollChanged;
        }
    }

    private static void ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var viewer = (ScrollViewer)sender;
        if (e.ExtentHeightChange == 0)
        {
            viewer.SetValue(WasAtEndProperty, viewer.VerticalOffset >= viewer.ScrollableHeight - 2);
            return;
        }
        if ((bool)viewer.GetValue(WasAtEndProperty))
        {
            viewer.ScrollToEnd();
        }
    }
}
