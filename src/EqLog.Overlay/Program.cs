namespace EqLog.Overlay;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
