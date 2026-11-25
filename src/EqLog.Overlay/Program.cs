namespace EqLog.Overlay;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        if (!SingleInstance.TryAcquire())
        {
            return;
        }
        try
        {
            var app = new App();
            app.InitializeComponent();
            SingleInstance.ListenForActivation(() =>
            {
                app.Dispatcher.BeginInvoke(app.ActivateMainWindow);
            });
            app.Run();
        }
        finally
        {
            SingleInstance.Release();
        }
    }
}
