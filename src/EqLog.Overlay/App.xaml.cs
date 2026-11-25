using System.Windows;
using System.Windows.Threading;
using EqLog.Core;

namespace EqLog.Overlay;

public partial class App : System.Windows.Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    // MainWindow is the controller; it shows the dock window, which holds the settings and is the visible main window.
    // The real workspace is built from settings; saved history arrives later.
    private async void OnStartup(object sender, StartupEventArgs e)
    {
        Overlay.MainWindow.StartupState state;
        try
        {
            state = await Overlay.MainWindow.StartupState.LoadShellAsync();
            bool shareTelemetry = state.Settings.ShareUsageTelemetry;
            // Building the telemetry exporter takes a noticeable moment; keep it off the UI thread.
            _ = Task.Run(() =>
            {
                UsageTelemetry.SetEnabled(shareTelemetry);
                UsageTelemetry.TrackApplicationStarted();
            });
            if (state.SettingsError is { } settingsError)
            {
                MessageBox.Show($"Settings could not be loaded: {settingsError.Message}\nTemporary defaults will be used. The existing settings file will not be overwritten; repair it and restart to enable saving.",
                    "Settings error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            UsageTelemetry.TrackError(ex, UsageErrorSource.Startup);
            MessageBox.Show($"EqLogOverlay could not start: {ex.Message}", "Startup error", MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var fallback = state.Themes.FirstOrDefault(t => t.Name == "Neutral Light");
        try
        {
            if ((state.Settings.Theme ?? ThemeManager.GetSystemTheme(state.Themes)) is { } theme)
            {
                ThemeManager.Apply(theme);
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or
            System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            UsageTelemetry.TrackError(ex, UsageErrorSource.Startup);
            MessageBox.Show($"Could not load the theme: {ex.Message}\nThe default theme will be used.",
                "Theme error", MessageBoxButton.OK, MessageBoxImage.Warning);
            state.Settings.Theme = null;
            if (fallback is not null)
            {
                ThemeManager.Apply(fallback);
            }
        }
        try
        {
            var availableData = new TaskCompletionSource<Overlay.MainWindow.StartupState>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pendingData = Overlay.MainWindow.StartupState.LoadDataAsync(state, availableData);
            _ = new Overlay.MainWindow(state, pendingData, availableData.Task);
        }
        catch (Exception ex)
        {
            UsageTelemetry.TrackError(ex, UsageErrorSource.Startup);
            MessageBox.Show($"EqLogOverlay could not open your workspace: {ex.Message}",
                "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        UsageTelemetry.Shutdown();
        base.OnExit(e);
    }

    /// <summary>Brings the main window forward when the app is launched again.</summary>
    internal void ActivateMainWindow()
    {
        if (MainWindow is not { IsLoaded: true } window)
        {
            return;
        }
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }
        window.Show();
        window.Activate();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        UsageTelemetry.TrackError(e.Exception, UsageErrorSource.Dispatcher);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            UsageTelemetry.TrackError(exception, UsageErrorSource.AppDomain);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        UsageTelemetry.TrackError(e.Exception, UsageErrorSource.TaskScheduler);
    }
}