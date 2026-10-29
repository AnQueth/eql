using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace EqLog.Overlay;

/// <summary>Displays the bundled help for the installed release without needing an internet connection.</summary>
public sealed class WhatsNewWindow : Window
{
    /// <summary>Creates a themed, scrollable What's New help window.</summary>
    public WhatsNewWindow()
    {
        Title = "What's New - EverQuest Log Overlay";
        Width = 740;
        Height = 680;
        MinWidth = 380;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        SetResourceReference(BackgroundProperty, "NightBackground");
        SetResourceReference(ForegroundProperty, "NightText");

        var document = (FlowDocument)Application.LoadComponent(
            new Uri("/EqLogOverlay;component/Help/WhatsNew.xaml", UriKind.Relative));
        var viewer = new FlowDocumentScrollViewer
        {
            Name = "WhatsNewViewer",
            Document = document,
            IsToolBarVisible = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        viewer.SetResourceReference(BackgroundProperty, "NightBackground");

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Close, (_, _) =>
        {
            Close();
        }));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Close, Key.Escape, ModifierKeys.None));
        var close = new Button
        {
            Content = "Close",
            Command = ApplicationCommands.Close,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12),
        };
        var root = new DockPanel();
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        root.Children.Add(viewer);
        Content = root;
    }
}
