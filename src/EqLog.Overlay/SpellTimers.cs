using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EqLog.Overlay;

public partial class MainWindow
{
    internal static CheckBox ConfigureKnownSpellsControl(OverlayPanel panel, bool showKnownSpells, Action<bool> visibilityChanged)
    {
        var visibility = new CheckBox
        {
            Content = "Known spells",
            IsChecked = showKnownSpells,
            FontSize = 11,
            Margin = new Thickness(3, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "Show or hide known spells without affecting active timers or spell tracking.",
        };
        UsageTracking.SetAction(visibility, UsageAction.ToggleKnownSpells);
        RoutedEventHandler changed = (_, _) =>
        {
            visibilityChanged(visibility.IsChecked == true);
        };
        visibility.Checked += changed;
        visibility.Unchecked += changed;
        panel.AddHeaderControl(visibility);
        return visibility;
    }

    /// <summary>Adds a known-spells name filter that is shown only while the known-spells list is visible.</summary>
    internal static TextBox ConfigureKnownSpellsFilter(OverlayPanel panel, CheckBox knownSpells, Action<string> filterChanged)
    {
        var filter = new TextBox
        {
            Margin = new Thickness(0, 5, 0, 5),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Filter known spells by name (case-insensitive). Clear to show all. Unlock floating panels to type.",
        };
        System.Windows.Automation.AutomationProperties.SetName(filter, "Known spells filter");
        TextFilterBinding.Bind(filter, filterChanged);

        void UpdateVisibility()
        {
            filter.Visibility = knownSpells.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        knownSpells.Checked += (_, _) => UpdateVisibility();
        knownSpells.Unchecked += (_, _) => UpdateVisibility();
        UpdateVisibility();
        panel.SetToolbar(filter);
        return filter;
    }
}
