using System.Windows;

namespace EqLog.Overlay.Views;

/// <summary>Asks for a workspace name when creating or renaming a dock workspace.</summary>
public partial class WorkspaceNameDialog : Window
{
    public WorkspaceNameDialog(string title, string initialName)
    {
        InitializeComponent();
        Title = title;
        NameBox.Text = initialName;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>The trimmed name the user entered.</summary>
    public string WorkspaceName
    {
        get
        {
            return NameBox.Text.Trim();
        }
    }

    /// <summary>Shows the dialog; returns the trimmed name, or null when canceled.</summary>
    public static string? Prompt(Window owner, string title, string initialName)
    {
        var dialog = new WorkspaceNameDialog(title, initialName) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.WorkspaceName : null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show(this, "Enter a workspace name.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
