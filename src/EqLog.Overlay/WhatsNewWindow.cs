using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;

namespace EqLog.Overlay;

/// <summary>Displays the bundled help for the installed release without needing an internet connection.</summary>
public sealed partial class WhatsNewWindow : Window
{
    /// <summary>Creates a themed, scrollable What's New help window.</summary>
    public WhatsNewWindow()
    {
        InitializeComponent();
        WhatsNewViewer.Document = (FlowDocument)System.Windows.Application.LoadComponent(
            new Uri("/EqLogOverlay;component/Help/WhatsNew.xaml", UriKind.Relative));
    }

    private void Close_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        Close();
    }
}
