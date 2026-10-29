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
                    ThemeStatus.Text = $"Saved theme is invalid: {ex.Message}. Using Halloween.";
                }
            }
            ThemePicker.ItemsSource = _themes;
            ThemePicker.SelectedItem = _settings.Theme ??
                _themes.Single(t => t.Name == "Halloween");
            _themeEditor = new ThemeEditor { ApplyRequested = ApplyEditedThemeAsync };
            ThemeEditorHost.Content = _themeEditor;
            _themeEditor.LoadPalette((ThemePalette)ThemePicker.SelectedItem);
            if (string.IsNullOrEmpty(ThemeStatus.Text))
            {
                ThemeStatus.Text = "Choose a theme or import a JSON palette. Changes are saved immediately.";
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            UsageTelemetry.TrackError(ex, UsageErrorSource.StartupData);
            ThemeStatus.Text = $"Could not load themes: {ex.Message}";
        }
        finally
        {
            _changingTheme = false;
        }
    }

    private async void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_changingTheme && ThemePicker.SelectedItem is ThemePalette theme)
        {
            await SelectThemeAsync(theme);
        }
    }

    private async Task<bool> SelectThemeAsync(ThemePalette theme)
    {
        var previous = _settings.Theme;
        try
        {
            theme.Validate();
            _settings.Theme = theme;
            await _settings.SaveAsync();
            ThemeManager.Apply(theme);
            foreach (var overlay in _overlays.Values)
            {
                overlay.Panel.SetBackgroundOpacity(_settings.Overlays[overlay.Panel.Key].BackgroundOpacity ?? _settings.BackgroundOpacity);
            }

            UsageTelemetry.TrackFeature(UsageFeature.ThemeChanged);
            ThemeStatus.Text = $"{theme.Name} applied and saved for future starts.";
            _themeEditor?.LoadPalette(theme);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            _settings.Theme = previous;
            _changingTheme = true;
            ThemePicker.SelectedItem = previous ?? _themes.FirstOrDefault(t => t.Name == "Halloween");
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
