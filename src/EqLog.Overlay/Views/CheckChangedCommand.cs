using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EqLog.Overlay.Views;

/// <summary>Executes an ICommand when a CheckBox changes state, passing its new checked value.</summary>
public static class CheckChangedCommand
{
    /// <summary>The checked-state command.</summary>
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(CheckChangedCommand), new PropertyMetadata(null, CommandChanged));

    /// <summary>Gets the checked-state command.</summary>
    public static ICommand? GetCommand(DependencyObject element)
    {
        return (ICommand?)element.GetValue(CommandProperty);
    }

    /// <summary>Sets the checked-state command.</summary>
    public static void SetCommand(DependencyObject element, ICommand? value)
    {
        element.SetValue(CommandProperty, value);
    }

    private static void CommandChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not CheckBox checkBox)
        {
            return;
        }
        checkBox.Checked -= OnCheckedStateChanged;
        checkBox.Unchecked -= OnCheckedStateChanged;
        if (args.NewValue is not null)
        {
            checkBox.Checked += OnCheckedStateChanged;
            checkBox.Unchecked += OnCheckedStateChanged;
        }
    }

    private static void OnCheckedStateChanged(object sender, RoutedEventArgs args)
    {
        var checkBox = (CheckBox)sender;
        object parameter = checkBox.IsChecked == true;
        if (GetCommand(checkBox) is { } command && command.CanExecute(parameter))
        {
            command.Execute(parameter);
        }
    }
}
