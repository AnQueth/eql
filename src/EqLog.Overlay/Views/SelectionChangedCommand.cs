using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EqLog.Overlay.Views;

/// <summary>Executes an ICommand when a ComboBox selection changes, passing its newly selected item.</summary>
public static class SelectionChangedCommand
{
    /// <summary>The selection-change command.</summary>
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(SelectionChangedCommand), new PropertyMetadata(null, CommandChanged));

    /// <summary>Gets the selection-change command.</summary>
    public static ICommand? GetCommand(DependencyObject element)
    {
        return (ICommand?)element.GetValue(CommandProperty);
    }

    /// <summary>Sets the selection-change command.</summary>
    public static void SetCommand(DependencyObject element, ICommand? value)
    {
        element.SetValue(CommandProperty, value);
    }

    private static void CommandChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not ComboBox comboBox)
        {
            return;
        }
        comboBox.SelectionChanged -= OnSelectionChanged;
        if (args.NewValue is not null)
        {
            comboBox.SelectionChanged += OnSelectionChanged;
        }
    }

    private static void OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        var comboBox = (ComboBox)sender;
        object? parameter = comboBox.SelectedItem;
        if (GetCommand(comboBox) is { } command && command.CanExecute(parameter))
        {
            command.Execute(parameter);
        }
    }
}
