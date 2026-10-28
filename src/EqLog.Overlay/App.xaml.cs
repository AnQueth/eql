using System.Windows;
using EqLog.Core;

namespace EqLog.Overlay;

public partial class App : Application
{
    // MainWindow is the controller; it shows the dock window, which holds the settings and is the visible main window.
    // ShutdownMode is OnExplicitShutdown, so the app keeps running while startup data loads without a window.
    private async void OnStartup(object sender, StartupEventArgs e)
    {
        Overlay.MainWindow.StartupState state;
        try
        {
            state = await Overlay.MainWindow.StartupState.LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"EqLogOverlay could not start: {ex.Message}", "Startup error", MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        var fallback = state.Themes.FirstOrDefault(t => t.Name == "Halloween");
        try
        {
            if ((state.Settings.Theme ?? fallback) is { } theme)
            {
                ThemeManager.Apply(theme);
            }
        }
        catch (FormatException ex)
        {
            MessageBox.Show($"Could not load the saved theme: {ex.Message}\nThe default theme will be used.",
                "Theme error", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (fallback is not null)
            {
                ThemeManager.Apply(fallback);
            }
        }
        _ = new Overlay.MainWindow(state);
    }
}