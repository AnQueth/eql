using System.IO;
using System.Text.Json;
using System.Windows;
using EqLog.Core;
using EqLog.Overlay.Views;

namespace EqLog.Overlay;

public partial class MainWindow
{
    private async void ResetTrackingData_Click(object sender, RoutedEventArgs e)
    {
        if (_shuttingDown || _updateBusy)
        {
            return;
        }
        bool? ignoreExistingLogLines = ResetTrackingDialog.Prompt(_dock);
        if (ignoreExistingLogLines is null)
        {
            return;
        }

        ResetTrackingDataButton.IsEnabled = false;
        try
        {
            CommitTriggerEdits();
            _settings.Triggers = [.. _triggers];
            CaptureLayout();
            await _settingsPersistence.SaveAsync(_settings);
            DateTime? ignoreBefore = null;
            if (ignoreExistingLogLines == true)
            {
                ignoreBefore = DateTime.Now;
            }
            await _tracking.RequestResetAsync(ignoreBefore);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ResetTrackingDataStatus.Text = $"Reset was not scheduled: {ex.Message}";
            ResetTrackingDataButton.IsEnabled = true;
            return;
        }
        if (ignoreExistingLogLines == true)
        {
            ResetTrackingDataStatus.Text = "Reset scheduled. Closing; open the app again to start tracking fresh.";
        }
        else
        {
            ResetTrackingDataStatus.Text = "Reset scheduled. Closing; open the app again to rebuild tracking data.";
        }
        _dock.Close();
    }
}
