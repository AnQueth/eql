using System.Windows;

namespace EqLog.Overlay.Views;

/// <summary>Confirms a tracking reset and asks whether existing log lines are ignored or replayed afterward.</summary>
public partial class ResetTrackingDialog : Window
{
    public ResetTrackingDialog()
    {
        InitializeComponent();
    }

    /// <summary>True when existing log lines should be ignored after the reset.</summary>
    public bool IgnoreExistingLogLines
    {
        get
        {
            return IgnoreOption.IsChecked == true;
        }
    }

    /// <summary>Shows the dialog; returns whether to ignore existing log lines, or null when canceled.</summary>
    public static bool? Prompt(Window owner)
    {
        var dialog = new ResetTrackingDialog { Owner = owner };
        if (dialog.ShowDialog() != true)
        {
            return null;
        }
        return dialog.IgnoreExistingLogLines;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
