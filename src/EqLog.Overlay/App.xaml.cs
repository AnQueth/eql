using System.Windows;

namespace EqLog.Overlay;

public partial class App : Application
{
    // MainWindow is the controller; it shows the dock window, which holds the settings and is the visible main window.
    private void OnStartup(object sender, StartupEventArgs e)
    {
        var settings = AppSettings.Load();
        try
        {
            ThemeManager.Apply(settings.Theme ?? ThemeManager.Load(
                System.IO.Path.Combine(ThemeManager.BuiltInFolder, "Halloween.json")));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or
            System.Text.Json.JsonException or FormatException)
        {
            MessageBox.Show($"Could not load the saved theme: {ex.Message}\nThe default theme will be used.",
                "Theme error", MessageBoxButton.OK, MessageBoxImage.Warning);
            ThemeManager.Apply(ThemeManager.Load(System.IO.Path.Combine(ThemeManager.BuiltInFolder, "Halloween.json")));
        }
        _ = new MainWindow();
    }
}
