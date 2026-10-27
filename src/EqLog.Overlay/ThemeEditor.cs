using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EqLog.Core;
using Microsoft.Win32;

namespace EqLog.Overlay;

public sealed class ThemeEditor : UserControl
{
    private readonly TextBox _name = new() { MaxLength = 80 };
    private readonly Dictionary<string, TextBox> _colors = [];
    private readonly Dictionary<string, Border> _swatches = [];
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly Border _preview = new() { CornerRadius = new CornerRadius(6), Padding = new Thickness(14), Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _previewTitle = new() { Text = "Your adventure, your colors", FontWeight = FontWeights.Bold };
    private readonly TextBlock _previewText = new() { Text = "Sample text and inventory details", Margin = new Thickness(0, 6, 0, 6) };
    private readonly Border _previewAccent = new() { Padding = new Thickness(8), CornerRadius = new CornerRadius(4) };
    private readonly TextBlock _previewAccentText = new() { Text = "Accent / selected control" };
    private ThemePalette? _original;
    private bool _loading;

    public Func<ThemePalette, bool>? ApplyRequested
    {
        get; set;
    }

    public ThemeEditor()
    {
        var root = new StackPanel { Margin = new Thickness(8) };
        var heading = new TextBlock { Text = "THEME STUDIO", FontSize = 20, FontWeight = FontWeights.Bold };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Pumpkin");
        root.Children.Add(heading);
        root.Children.Add(new TextBlock
        {
            Text = "Edit #RRGGBB colors below. Draft changes affect only this preview until Apply & save. Choosing another theme replaces this draft.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 8),
        });
        root.Children.Add(new TextBlock { Text = "Theme name", Margin = new Thickness(0, 0, 0, 4) });
        _name.TextChanged += (_, _) => UpdatePreview();
        root.Children.Add(_name);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        AddButton(actions, "Apply & save", Apply);
        AddButton(actions, "Export JSON...", Export);
        AddButton(actions, "Reset draft", () =>
        {
            if (_original is not null)
            {
                LoadPalette(_original);
            }
        });
        root.Children.Add(actions);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Pumpkin");
        root.Children.Add(_status);
        var previewContent = new StackPanel();
        previewContent.Children.Add(_previewTitle);
        previewContent.Children.Add(_previewText);
        _previewAccent.Child = _previewAccentText;
        previewContent.Children.Add(_previewAccent);
        _preview.Child = previewContent;
        root.Children.Add(_preview);

        foreach (string key in ThemePalette.ColorKeys)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.Children.Add(new TextBlock
            {
                Text = Label(key),
                ToolTip = key,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 8, 0),
            });
            var swatch = new Border { CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 3, 8, 3), BorderThickness = new Thickness(1) };
            swatch.SetResourceReference(Border.BorderBrushProperty, "NightMuted");
            Grid.SetColumn(swatch, 1);
            row.Children.Add(swatch);
            var input = new TextBox { MaxLength = 7, ToolTip = $"{key}: #RRGGBB (for example #FFAA57)", Tag = key };
            input.TextChanged += (_, _) => UpdatePreview();
            Grid.SetColumn(input, 2);
            row.Children.Add(input);
            _colors[key] = input;
            _swatches[key] = swatch;
            root.Children.Add(row);
        }
        Content = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    public void LoadPalette(ThemePalette palette)
    {
        _loading = true;
        _original = ThemePalette.Parse(JsonSerializer.Serialize(palette));
        _name.Text = palette.Name;
        foreach (string key in ThemePalette.ColorKeys)
        {
            _colors[key].Text = palette.Colors[key];
        }

        _loading = false;
        UpdatePreview();
    }

    public ThemePalette ReadPalette()
    {
        var palette = new ThemePalette
        {
            Name = _name.Text.Trim(),
            Colors = _colors.ToDictionary(c => c.Key, c => c.Value.Text.Trim()),
        };
        palette.Validate();
        return palette;
    }

    private void UpdatePreview()
    {
        if (_loading)
        {
            return;
        }

        foreach (var (key, input) in _colors)
        {
            string value = input.Text.Trim();
            bool valid = value.Length == 7 && value[0] == '#' && value[1..].All(Uri.IsHexDigit);
            _swatches[key].Background = valid ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)) : Brushes.Transparent;
        }
        try
        {
            var palette = ReadPalette();
            Brush BrushFor(string key)
            {
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Colors[key]));
            }

            _preview.Background = BrushFor("NightSurface");
            _previewTitle.Foreground = BrushFor("Pumpkin");
            _previewText.Foreground = BrushFor("NightText");
            _previewAccent.Background = BrushFor("Pumpkin");
            _previewAccentText.Foreground = BrushFor("AccentText");
            _status.Text = "Valid draft. Apply to save for future starts, or export without changing your active theme.";
        }
        catch (FormatException ex)
        {
            _status.Text = $"Draft needs attention: {ex.Message} Preview shows the last valid palette.";
        }
    }

    private void Apply()
    {
        try
        {
            var palette = ReadPalette();
            if (ApplyRequested is null)
            {
                _status.Text = "Cannot apply: no theme handler is available.";
            }
            else if (ApplyRequested(palette))
            {
                LoadPalette(palette);
                _status.Text = $"{palette.Name} applied and saved.";
            }
            else
            {
                _status.Text = "Could not save this theme. See the Theme status above; your draft is retained.";
            }
        }
        catch (FormatException ex)
        {
            _status.Text = $"Cannot apply: {ex.Message}";
        }
    }

    private void Export()
    {
        try
        {
            var palette = ReadPalette();
            var dialog = new SaveFileDialog
            {
                Title = "Export theme JSON",
                Filter = "JSON themes (*.json)|*.json",
                FileName = "CustomTheme.json",
                DefaultExt = ".json",
                AddExtension = true,
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            {
                return;
            }

            AtomicFile.WriteAllText(dialog.FileName, JsonSerializer.Serialize(palette, new JsonSerializerOptions { WriteIndented = true }));
            _status.Text = $"Exported {palette.Name} to {dialog.FileName}. Active theme unchanged.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            _status.Text = $"Could not export theme: {ex.Message}";
        }
    }

    private static void AddButton(Panel panel, string caption, Action action)
    {
        var button = new Button { Content = caption };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }

    private static string Label(string key)
    {
        return key switch
        {
            "NightBackground" => "Window background",
            "NightSurface" => "Panel / card background",
            "NightRaised" => "Raised controls / headers",
            "NightBorder" => "Borders",
            "NightText" => "Main text",
            "NightMuted" => "Secondary text",
            "Pumpkin" => "Accent / headings",
            "Hover" => "Hovered controls",
            "Pressed" => "Pressed controls",
            "Selection" => "Selected rows / text",
            "Alternate" => "Alternating table rows",
            "GridLine" => "Table grid lines",
            "ScrollThumb" => "Scrollbar thumb",
            "AccentText" => "Text on accent color",
            _ => key,
        };
    }
}
