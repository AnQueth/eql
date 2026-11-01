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
}
