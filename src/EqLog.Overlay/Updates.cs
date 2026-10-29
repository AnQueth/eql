using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Velopack;
using Velopack.Sources;

namespace EqLog.Overlay;

public partial class MainWindow
{
    private const string UpdateRepository = "https://github.com/AnQueth/eql";
    private UpdateManager? _updater;
    private UpdateInfo? _availableUpdate;
    private bool _updateBusy;
    private bool _updateDownloaded;
    private static readonly string AppVersion = typeof(App).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";

    private async Task InitializeUpdatesAsync()
    {
        AppVersionText.Text = $"EverQuest Log Overlay {AppVersion}";
        CheckUpdatesBox.IsChecked = _settings.CheckForUpdatesOnStartup;
        DownloadUpdatesBox.IsChecked = _settings.AutomaticallyDownloadUpdates;
        try
        {
            _updater = new UpdateManager(new GithubSource(UpdateRepository, accessToken: null, prerelease: false));
            if (Debugger.IsAttached || !_updater.IsInstalled)
            {
                CheckUpdateButton.IsEnabled = false;
                UpdateStatusText.Text = "Automatic updates require the installed GitHub release. Development/unpackaged builds are not updated.";
                return;
            }
            UpdateStatusText.Text = "Updates come from stable GitHub releases. Restart is always your choice.";
            if (_settings.CheckForUpdatesOnStartup)
            {
                await CheckForUpdatesAsync();
            }
        }
        catch (Exception ex)
        {
            // Update failures must not prevent the log reader or overlay windows from starting.
            UpdateStatusText.Text = $"Could not initialize updates: {ex.Message}";
            CheckUpdateButton.IsEnabled = false;
        }
    }

    private void UpdatePreferences_Click(object sender, RoutedEventArgs e)
    {
        _settings.CheckForUpdatesOnStartup = CheckUpdatesBox.IsChecked == true;
        _settings.AutomaticallyDownloadUpdates = DownloadUpdatesBox.IsChecked == true;
        SaveSettingsInBackground(error => UpdateStatusText.Text = $"Could not save update preferences: {error}");
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_updateBusy || _updater is null || !_updater.IsInstalled)
        {
            return;
        }

        SetUpdateBusy(true);
        UpdateStatusText.Text = "Checking GitHub for a newer stable release...";
        try
        {
            var update = await _updater.CheckForUpdatesAsync();
            if (_shutDown)
            {
                return;
            }

            _availableUpdate = update;
            _updateDownloaded = false;
            RefreshUpdateNotice();
            if (update is null)
            {
                UpdateStatusText.Text = "You are running the latest stable release.";
            }
            else
            {
                UpdateStatusText.Text = $"Version {update.TargetFullRelease.Version} is available.";
                if (_settings.AutomaticallyDownloadUpdates)
                {
                    await DownloadAvailableUpdateAsync();
                }
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Update check failed: {ex.Message}";
        }
        finally
        {
            SetUpdateBusy(false);
        }
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy)
        {
            return;
        }

        SetUpdateBusy(true);
        try
        {
            await DownloadAvailableUpdateAsync();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Update download failed: {ex.Message}";
        }
        finally
        {
            SetUpdateBusy(false);
        }
    }

    private async Task DownloadAvailableUpdateAsync()
    {
        if (_updater is null || _availableUpdate is null)
        {
            return;
        }

        var update = _availableUpdate;
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateStatusText.Text = $"Downloading version {update.TargetFullRelease.Version}: 0%";
        try
        {
            await _updater.DownloadUpdatesAsync(update, percent =>
                Dispatcher.BeginInvoke(() =>
                {
                    if (_shutDown || _updateDownloaded || UpdateProgress.Visibility != Visibility.Visible)
                    {
                        return;
                    }

                    UpdateProgress.Value = percent;
                    UpdateStatusText.Text = $"Downloading version {update.TargetFullRelease.Version}: {percent}%";
                }, DispatcherPriority.Background));
            if (_shutDown)
            {
                return;
            }

            _updateDownloaded = true;
            RefreshUpdateNotice();
            UpdateStatusText.Text = $"Version {update.TargetFullRelease.Version} is ready. Restart to update when convenient.";
        }
        finally
        {
            UpdateProgress.Visibility = Visibility.Hidden;
        }
    }

    private void SetUpdateBusy(bool busy)
    {
        _updateBusy = busy;
        CheckUpdateButton.IsEnabled = !busy;
        DownloadUpdateButton.IsEnabled = !busy && _availableUpdate is not null && !_updateDownloaded;
        RestartUpdateButton.IsEnabled = !busy && _updateDownloaded;
        RefreshUpdateNotice();
    }

    /// <summary>Shows an "update available" banner with its action button in the main window's top bar.</summary>
    private void RefreshUpdateNotice()
    {
        if (_availableUpdate is null)
        {
            _dock.SetNotice(null);
            return;
        }

        var version = _availableUpdate.TargetFullRelease.Version;
        var text = new System.Windows.Controls.TextBlock
        {
            Text = _updateDownloaded
                ? $"\u2B06 Version {version} is ready"
                : _updateBusy
                    ? $"\u2B06 Downloading version {version}..."
                    : $"\u2B06 Version {version} is available",
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Pumpkin");
        var button = new System.Windows.Controls.Button
        {
            Content = _updateDownloaded ? "Restart to update" : "Download update",
            IsEnabled = !_updateBusy,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
        };
        button.Click += _updateDownloaded ? RestartUpdate_Click : DownloadUpdate_Click;
        var panel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        panel.Children.Add(text);
        panel.Children.Add(button);
        _dock.SetNotice(panel);
    }

    private async void RestartUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy || !_updateDownloaded || _updater is null || _availableUpdate is null)
        {
            return;
        }

        if (MessageBox.Show(_dock, "Save your tracking data and restart to install the downloaded update?",
            "Restart to update", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        SetUpdateBusy(true);
        try
        {
            CommitTriggerEdits();
            _settings.Triggers = [.. _triggers];
            // Verify persistence before stopping anything; refuse to restart if data cannot be saved.
            await SaveAllAsync(throwOnError: true);
            await StopTailingAsync();
            var disposingWiki = _wikiRepository.DisposeAsync();
            _wikiCts.Cancel();
            await disposingWiki;
            await SaveAllAsync(throwOnError: true);
            _updater.WaitExitThenApplyUpdates(_availableUpdate, restart: true);
            _dock.Close();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Update was not applied: {ex.Message} If log reading stopped, select the log again using Browse. " +
                "If wiki lookups stopped, restart the app to resume them.";
            SetUpdateBusy(false);
        }
    }
}
