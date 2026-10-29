using System.Windows;
using System.Windows.Threading;
using EqLog.Core;

namespace EqLog.Overlay;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    // MainWindow is the controller; it shows the dock window, which holds the settings and is the visible main window.
    // ShutdownMode is OnExplicitShutdown, so the app keeps running while startup data loads without a window.
    private async void OnStartup(object sender, StartupEventArgs e)
    {
        Overlay.MainWindow.StartupState state;
        try
        {
            var settings = await AppSettings.LoadAsync();
            UsageTelemetry.SetEnabled(settings.ShareUsageTelemetry);
            state = await Overlay.MainWindow.StartupState.LoadAsync(settings);
        }
        catch (Exception ex)
        {
            UsageTelemetry.TrackError(ex, UsageErrorSource.Startup);
            MessageBox.Show($"EqLogOverlay could not start: {ex.Message}", "Startup error", MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        UsageTelemetry.TrackApplicationStarted();

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
            UsageTelemetry.TrackError(ex, UsageErrorSource.Startup);
            MessageBox.Show($"Could not load the saved theme: {ex.Message}\nThe default theme will be used.",
                "Theme error", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (fallback is not null)
            {
                ThemeManager.Apply(fallback);
            }
        }
        _ = new Overlay.MainWindow(state);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        UsageTelemetry.Shutdown();
        base.OnExit(e);
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