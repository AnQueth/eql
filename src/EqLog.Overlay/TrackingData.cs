using System.IO;
using System.Text.Json;
using System.Windows;
using EqLog.Core;

namespace EqLog.Overlay;

public partial class MainWindow
{
    private async void ResetTrackingData_Click(object sender, RoutedEventArgs e)
    {
        if (_shuttingDown || _updateBusy)
        {
            return;
        }
        if (MessageBox.Show(_dock,
            "Reset all tracking data and close the app?\n\n" +
            "On the next launch, NPC encounters, resistance observations, learned spell durations " +
            "and overrides, spawn timers and overrides, mote history, and tracked inventory will be backed up and cleared.\n\n" +
            "Settings, alert triggers, layout, themes, lookup caches, and original game logs are kept. " +
            "The selected log will be reprocessed. Data not present in your logs or inventory export cannot be rebuilt automatically.\n\n" +
            "Open the app again after it closes to complete the reset.",
            "Reset tracking data", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        ResetTrackingDataButton.IsEnabled = false;
        try
        {
            CommitTriggerEdits();
            _settings.Triggers = [.. _triggers];
            CaptureLayout();
            await _settings.SaveAsync();
            await TrackingDataReset.RequestAsync(AppSettings.Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ResetTrackingDataStatus.Text = $"Reset was not scheduled: {ex.Message}";
            ResetTrackingDataButton.IsEnabled = true;
            return;
        }
        ResetTrackingDataStatus.Text = "Reset scheduled. Closing; open the app again to rebuild tracking data.";
        _dock.Close();
    }
}
