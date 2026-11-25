using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using EqLog.Overlay.Views;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class ResetTrackingDialogTests
{
    [Fact]
    public void DefaultsToIgnoringExistingLogLinesAndReportsRebuildChoice()
    {
        RunSta(() =>
        {
            var dialog = new ResetTrackingDialog();
            Assert.True(dialog.IgnoreExistingLogLines);

            Assert.IsType<RadioButton>(dialog.FindName("RebuildOption")).IsChecked = true;
            Assert.False(dialog.IgnoreExistingLogLines);
            dialog.Close();
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
