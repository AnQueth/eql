using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using EqLog.Core;
using Microsoft.Win32;

namespace EqLog.Overlay;

public partial class MainWindow
{
    private bool _changingTheme;
    private readonly List<ThemePalette> _themes = [];
    private ThemeEditor? _themeEditor;
    private ThemePalette? _appliedTheme;

    private void InitializeThemes(StartupState state)
    {
        _changingTheme = true;
        try
        {
            if (state.ThemesError is { } error)
            {
                ThemeStatus.Text = $"Could not load themes: {error.Message}";
                return;
            }
            _themes.AddRange(state.Themes);

            if (_settings.Theme is { } saved)
            {
                try
                {
                    saved.Validate();
                    var bundled = _themes.FirstOrDefault(t => t.Name == saved.Name &&
                        t.Colors.Count == saved.Colors.Count && t.Colors.All(c => saved.Colors.TryGetValue(c.Key, out var value) && value == c.Value));
                    if (bundled is not null)
                    {
                        _settings.Theme = bundled;
                    }
                    else
                    {
                        _themes.Add(saved);
                    }
                }
                catch (FormatException ex)
                {
                    UsageTelemetry.TrackError(ex, UsageErrorSource.StartupData);
                    _settings.Theme = null;
                    ThemeStatus.Text = $"Saved theme is invalid: {ex.Message}. Following Windows appearance.";
                }
            }
            ThemePicker.ItemsSource = _themes;
            _themeEditor = new ThemeEditor { ApplyRequested = ApplyEditedThemeAsync };
            ThemeEditorHost.Content = _themeEditor;
            var theme = _settings.Theme ?? ThemeManager.GetSystemTheme(_themes);
            ApplyTheme(theme);
            ShowSelectedTheme(theme);
            if (string.IsNullOrEmpty(ThemeStatus.Text))
            {
                ThemeStatus.Text = "Follow Windows appearance, choose a fixed theme, or import a JSON palette. Changes are saved immediately.";
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or
            IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            UsageTelemetry.TrackError(ex, UsageErrorSource.StartupData);
            ThemeStatus.Text = $"Could not load themes: {ex.Message}";
        }
        finally
        {
            _changingTheme = false;
        }
    }

    private void ShowSelectedTheme(ThemePalette theme)
    {
        _appliedTheme = theme;
        bool changingTheme = _changingTheme;
        _changingTheme = true;
        try
        {
            FollowWindowsThemeBox.IsChecked = _settings.Theme is null;
            ThemePicker.IsEnabled = _settings.Theme is not null;
            if (_themes.Contains(theme))
            {
                ThemePicker.SelectedItem = theme;
            }
            _themeEditor?.LoadPalette(theme);
        }
        finally
        {
            _changingTheme = changingTheme;
        }
    }

    private void ApplyTheme(ThemePalette theme)
    {
        ThemeManager.Apply(theme);
        foreach (var overlay in _overlays.Values)
        {
            overlay.Panel.SetBackgroundOpacity(_settings.Overlays[overlay.Panel.Key].BackgroundOpacity ?? _settings.BackgroundOpacity);
        }
    }

    private void RefreshSystemTheme()
    {
        if (_shuttingDown || _settings.Theme is not null || _themes.Count == 0)
        {
            return;
        }

        try
        {
            var theme = ThemeManager.GetSystemTheme(_themes);
            if (ReferenceEquals(_appliedTheme, theme))
            {
                return;
            }
            ApplyTheme(theme);
            ShowSelectedTheme(theme);
            ThemeStatus.Text = $"Following Windows appearance: {theme.Name}.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or
            IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            UsageTelemetry.TrackError(ex, UsageErrorSource.StartupData);
            ThemeStatus.Text = $"Could not follow Windows appearance: {ex.Message}";
        }
    }

    private async void FollowWindowsTheme_Changed(object sender, RoutedEventArgs e)
    {
        if (_changingTheme)
        {
            return;
        }
        if (FollowWindowsThemeBox.IsChecked == true)
        {
            await SelectThemeAsync(null);
        }
        else if (ThemePicker.SelectedItem is ThemePalette theme)
        {
            await SelectThemeAsync(theme);
        }
    }

    private async void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_changingTheme && ThemePicker.SelectedItem is ThemePalette theme)
        {
            await SelectThemeAsync(theme);
        }
    }

    private async Task<bool> SelectThemeAsync(ThemePalette? theme)
    {
        var previous = _settings.Theme;
        var previousSelection = _appliedTheme;
        try
        {
            var applied = theme ?? ThemeManager.GetSystemTheme(_themes);
            applied.Validate();
            _settings.Theme = theme;
            await _settings.SaveAsync();
            ApplyTheme(applied);

            UsageTelemetry.TrackFeature(UsageFeature.ThemeChanged);
            ThemeStatus.Text = theme is null
                ? $"Following Windows appearance: {applied.Name}. Saved for future starts."
                : $"{theme.Name} applied and saved for future starts.";
            ShowSelectedTheme(applied);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            FormatException or InvalidOperationException or System.Security.SecurityException)
        {
            _settings.Theme = previous;
            _changingTheme = true;
            FollowWindowsThemeBox.IsChecked = previous is null;
            ThemePicker.IsEnabled = previous is not null;
            ThemePicker.SelectedItem = previous ?? previousSelection;
            _changingTheme = false;
            ThemeStatus.Text = $"Theme was not changed: {ex.Message}";
            return false;
        }
    }

    private async Task<bool> ApplyEditedThemeAsync(ThemePalette theme)
    {
        if (!await SelectThemeAsync(theme))
        {
            return false;
        }

        _changingTheme = true;
        try
        {
            _themes.Add(theme);
            ThemePicker.Items.Refresh();
            ThemePicker.SelectedItem = theme;
        }
        finally
        {
            _changingTheme = false;
        }
        return true;
    }

    private async void ImportTheme_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import theme JSON", Filter = "JSON themes (*.json)|*.json" };
        if (dialog.ShowDialog(_dock) != true)
        {
            return;
        }

        try
        {
            var theme = await ThemeManager.LoadAsync(dialog.FileName);
            _themes.Add(theme);
            ThemePicker.Items.Refresh();
            ThemePicker.SelectedItem = theme;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            ThemeStatus.Text = $"Could not import theme: {ex.Message}";
        }
    }
}
