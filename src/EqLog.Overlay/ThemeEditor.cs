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
    private readonly FixedLineTextBlock _status = new() { Margin = new Thickness(0, 8, 0, 8) };
    private readonly List<(string Key, Action<Brush> Paint)> _painters = [];
    private ThemePalette? _original;
    private bool _loading;

    public Func<ThemePalette, Task<bool>>? ApplyRequested
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
            Text = "Enter #RRGGBB colors or use Pick... to choose a color. Draft changes affect only this preview until Apply & save. Choosing another theme replaces this draft.",
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
        root.Children.Add(BuildPreview());

        foreach (string key in ThemePalette.AllColorKeys)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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
            var picker = new Button
            {
                Content = "Pick...",
                ToolTip = $"Choose {Label(key).ToLowerInvariant()} color",
            };
            picker.Click += (_, _) => PickColor(key, picker);
            Grid.SetColumn(picker, 3);
            row.Children.Add(picker);
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
        foreach (string key in ThemePalette.AllColorKeys)
        {
            _colors[key].Text = palette.GetColor(key);
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

    private void PickColor(string key, FrameworkElement anchor)
    {
        string value = _colors[key].Text.Trim();
        if (!IsHexColor(value))
        {
            value = _original?.GetColor(key) ?? "#000000";
            _status.Text = "The hex value is invalid. The picker starts from the original color; choosing a color will replace it.";
        }

        var color = (Color)ColorConverter.ConvertFromString(value);
        var result = ColorPickerDialog.Pick(anchor, color);
        if (result.Color is { } selected)
        {
            _colors[key].Text = ColorPickerDialog.ToHex(selected);
        }
        if (result.Message is not null)
        {
            _status.Text = result.Message;
        }
    }

    private static bool IsHexColor(string value)
    {
        return value.Length == 7 && value[0] == '#' && value[1..].All(Uri.IsHexDigit);
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
            bool valid = IsHexColor(value);
            _swatches[key].Background = valid ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)) : Brushes.Transparent;
        }
        try
        {
            var palette = ReadPalette();
            var brushes = ThemePalette.AllColorKeys.ToDictionary(
                key => key,
                key => (Brush)new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.GetColor(key))));
            foreach (var (key, paint) in _painters)
            {
                paint(brushes[key]);
            }

            _status.Text = "Valid draft. Apply to save for future starts, or export without changing your active theme.";
        }
        catch (FormatException ex)
        {
            _status.Text = $"Draft needs attention: {ex.Message} Preview shows the last valid palette.";
        }
    }

    private async void Apply()
    {
        try
        {
            var palette = ReadPalette();
            if (ApplyRequested is null)
            {
                _status.Text = "Cannot apply: no theme handler is available.";
            }
            else if (await ApplyRequested(palette))
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

    private async void Export()
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

            await AtomicFile.WriteJsonAsync(dialog.FileName, palette, new JsonSerializerOptions { WriteIndented = true });
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

    private void Paint(string key, Action<Brush> paint)
    {
        _painters.Add((key, paint));
    }

    private static string Tip(params string[] keys)
    {
        return string.Join("\n", keys.Select(k => $"{Label(k)} ({k})"));
    }

    private TextBlock PreviewText(string text, string key, bool bold = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Paint(key, b => block.Foreground = b);
        return block;
    }

    private Border PreviewBox(string? background, string? border, Thickness thickness, string tip)
    {
        var box = new Border { BorderThickness = thickness, ToolTip = tip };
        if (background is not null)
        {
            Paint(background, b => box.Background = b);
        }
        if (border is not null)
        {
            Paint(border, b => box.BorderBrush = b);
        }

        return box;
    }

    /// <summary>A mock of the main window and two overlays, painted with every theme color in the places the app uses it.</summary>
    private UIElement BuildPreview()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        var caption = new TextBlock
        {
            Text = "Preview (hover any part to see which color it uses)",
            Margin = new Thickness(0, 0, 0, 4),
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        panel.Children.Add(caption);
        panel.Children.Add(BuildWindowPreview());
        panel.Children.Add(BuildOverlayPreview());
        return panel;
    }

    private UIElement BuildWindowPreview()
    {
        var window = PreviewBox("NightBackground", "NightBorder", new Thickness(1), Tip("NightBackground", "NightBorder"));
        window.CornerRadius = new CornerRadius(6);
        window.Padding = new Thickness(10);
        var stack = new StackPanel();
        window.Child = stack;

        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        tabs.Children.Add(PreviewTab("Spell durations", "NightRaised", "Pumpkin", "Pumpkin", "Selected tab"));
        tabs.Children.Add(PreviewTab("Spawn timers", "Hover", "NightBorder", "NightText", "Hovered tab"));
        tabs.Children.Add(PreviewTab("Alerts", "NightBackground", "NightBorder", "NightText", "Tab"));
        stack.Children.Add(tabs);

        var card = PreviewBox("NightSurface", "NightBorder", new Thickness(1), Tip("NightSurface", "NightBorder"));
        card.CornerRadius = new CornerRadius(0, 5, 5, 5);
        card.Padding = new Thickness(8);
        var content = new StackPanel();
        card.Child = content;
        stack.Children.Add(card);

        content.Children.Add(PreviewText("Learned spell durations", "Pumpkin", bold: true));
        var hint = PreviewText("Learned from your casts. Relearn clears old samples.", "NightMuted");
        hint.Margin = new Thickness(0, 2, 0, 6);
        hint.ToolTip = Tip("NightMuted");
        content.Children.Add(hint);

        var search = PreviewBox("NightBackground", "Pumpkin", new Thickness(1), "Text box: typed text (NightText), selected text (Selection), focus border and caret (Pumpkin)");
        search.CornerRadius = new CornerRadius(4);
        search.Padding = new Thickness(6, 3, 6, 3);
        search.Margin = new Thickness(0, 0, 0, 6);
        var searchText = new StackPanel { Orientation = Orientation.Horizontal };
        searchText.Children.Add(PreviewText("Clar", "NightText"));
        var selected = PreviewBox("Selection", null, new Thickness(0), Tip("Selection"));
        selected.Child = PreviewText("ity", "NightText");
        searchText.Children.Add(selected);
        var caret = PreviewBox("Pumpkin", null, new Thickness(0), Tip("Pumpkin"));
        caret.Width = 1.5;
        caret.Margin = new Thickness(1, 1, 0, 1);
        searchText.Children.Add(caret);
        search.Child = searchText;
        content.Children.Add(search);

        var table = new Grid();
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        var tableBorder = PreviewBox(null, "NightBorder", new Thickness(1), Tip("NightBorder"));
        tableBorder.Child = table;
        var rows = new StackPanel();
        table.Children.Add(rows);
        rows.Children.Add(PreviewTableRow(["Spell", "Duration", "Samples"], "NightRaised", "Pumpkin", "Pumpkin", Tip("NightRaised", "Pumpkin")));
        rows.Children.Add(PreviewTableRow(["Clarity II", "40:00", "12"], "NightSurface", "NightText", "NightMuted", Tip("NightSurface", "NightText", "GridLine")));
        rows.Children.Add(PreviewTableRow(["Spirit of Wolf", "36:00", "8"], "Alternate", "NightText", "NightMuted", Tip("Alternate", "GridLine")));
        rows.Children.Add(PreviewTableRow(["Allure (charm)", "learning", "0"], "Selection", "NightText", "NightMuted", "Selected row\n" + Tip("Selection")));
        rows.Children.Add(PreviewTableRow(["Visions of Grandeur", "42:00", "5"], "Hover", "NightText", "NightMuted", "Hovered row\n" + Tip("Hover")));
        var track = PreviewBox("NightBackground", null, new Thickness(0), Tip("ScrollThumb"));
        var thumb = PreviewBox("ScrollThumb", null, new Thickness(0), Tip("ScrollThumb"));
        thumb.CornerRadius = new CornerRadius(4);
        thumb.Margin = new Thickness(2);
        thumb.Height = 46;
        thumb.VerticalAlignment = VerticalAlignment.Top;
        track.Child = thumb;
        Grid.SetColumn(track, 1);
        table.Children.Add(track);
        content.Children.Add(tableBorder);

        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(PreviewButton("Relearn", "NightRaised", "NightBorder", "Button\n" + Tip("NightRaised", "NightBorder", "NightText")));
        actions.Children.Add(PreviewButton("Remove", "Hover", "Pumpkin", "Hovered button\n" + Tip("Hover", "Pumpkin")));
        actions.Children.Add(PreviewButton("Save", "Pressed", "Pumpkin", "Pressed button\n" + Tip("Pressed", "Pumpkin")));
        var check = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Checked box\n" + Tip("Pumpkin", "AccentText") };
        var box = PreviewBox("Pumpkin", "Pumpkin", new Thickness(1), Tip("Pumpkin", "AccentText"));
        box.Width = 16;
        box.Height = 16;
        box.CornerRadius = new CornerRadius(3);
        var tick = PreviewText("\u2713", "AccentText", bold: true);
        tick.HorizontalAlignment = HorizontalAlignment.Center;
        tick.FontSize = 11;
        box.Child = tick;
        check.Children.Add(box);
        var checkLabel = PreviewText("Sound", "NightText");
        checkLabel.Margin = new Thickness(5, 0, 0, 0);
        check.Children.Add(checkLabel);
        actions.Children.Add(check);
        content.Children.Add(actions);
        return window;
    }

    private Border PreviewTab(string text, string background, string border, string foreground, string name)
    {
        var tab = PreviewBox(background, border, new Thickness(1, 1, 1, 0), name + "\n" + Tip(background, foreground));
        tab.CornerRadius = new CornerRadius(5, 5, 0, 0);
        tab.Padding = new Thickness(10, 5, 10, 5);
        tab.Margin = new Thickness(0, 0, 3, 0);
        tab.Child = PreviewText(text, foreground, bold: foreground == "Pumpkin");
        return tab;
    }

    private Border PreviewTableRow(string[] cells, string background, string text, string lastText, string tip)
    {
        var row = PreviewBox(background, "GridLine", new Thickness(0, 0, 0, 1), tip);
        row.Padding = new Thickness(6, 3, 6, 3);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        bool header = text == "Pumpkin";
        for (int i = 0; i < cells.Length; i++)
        {
            var cell = PreviewText(cells[i], i == cells.Length - 1 ? lastText : text, bold: header);
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }

        row.Child = grid;
        return row;
    }

    private Border PreviewButton(string text, string background, string border, string tip)
    {
        var button = PreviewBox(background, border, new Thickness(1), tip);
        button.CornerRadius = new CornerRadius(5);
        button.Padding = new Thickness(12, 4, 12, 4);
        button.Margin = new Thickness(0, 0, 6, 0);
        button.Child = PreviewText(text, "NightText");
        return button;
    }

    private UIElement BuildOverlayPreview()
    {
        var scene = new Border
        {
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            ToolTip = "The game behind a floating overlay",
            Background = new LinearGradientBrush(Color.FromRgb(46, 70, 52), Color.FromRgb(120, 104, 70), 20),
        };
        var overlays = new WrapPanel();
        scene.Child = overlays;

        overlays.Children.Add(PreviewOverlay("Spawn Timers", "The Overthere",
        [
            ("Tracked here", "respawn", "OverlayHeader", true),
            ("a sarnak courier", "up  seen 21:40", "OverlayGood", false),
            ("Ixiblat Fer", "DUE +01:12", "OverlayWarn", true),
            ("Lord Bergurgle", "12:45", "NightText", false),
            ("a goblin scout", "dead 03:20 (learning)", "OverlayDim", false),
        ]));
        overlays.Children.Add(PreviewOverlay("Experience", "Level 60",
        [
            ("Deaths", "2  (last: a froglok)", "OverlayWarn", false),
            ("Coin looted", "14p 3g", "OverlayGood", false),
            ("AA POINTS CAPPED - spend AAs!", "", "OverlayBad", true),
        ]));
        return scene;
    }

    private Border PreviewOverlay(string title, string subtitle, (string Left, string Right, string Key, bool Bold)[] rows)
    {
        var frame = PreviewBox(null, "Pumpkin", new Thickness(1), "Overlay background is NightBackground at the overlay's opacity\n" + Tip("NightBackground", "Pumpkin"));
        Paint("NightBackground", b =>
        {
            var color = ((SolidColorBrush)b).Color;
            frame.Background = new SolidColorBrush(Color.FromArgb(217, color.R, color.G, color.B));
        });
        frame.CornerRadius = new CornerRadius(4);
        frame.Padding = new Thickness(8, 6, 8, 6);
        frame.Margin = new Thickness(0, 0, 10, 6);
        frame.Width = 250;
        var stack = new StackPanel();
        frame.Child = stack;
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        heading.Children.Add(PreviewText(title, "Pumpkin", bold: true));
        var sub = PreviewText("  " + subtitle, "NightMuted");
        heading.Children.Add(sub);
        stack.Children.Add(heading);
        foreach (var (left, right, key, bold) in rows)
        {
            var line = new Grid { ToolTip = Tip(key) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var leftText = PreviewText(left, key, bold);
            if (string.IsNullOrEmpty(right))
            {
                leftText.TextTrimming = TextTrimming.None;
                leftText.TextWrapping = TextWrapping.Wrap;
                Grid.SetColumnSpan(leftText, 2);
            }
            line.Children.Add(leftText);
            var rightText = PreviewText(right, key, bold);
            rightText.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(rightText, 1);
            line.Children.Add(rightText);
            stack.Children.Add(line);
        }

        return frame;
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
            "OverlayHeader" => "Overlay section headings",
            "OverlayDim" => "Overlay secondary rows",
            "OverlayGood" => "Overlay good status (spawned, coin)",
            "OverlayWarn" => "Overlay warning status (due, expired)",
            "OverlayBad" => "Overlay bad status (errors, capped AA)",
            _ => key,
        };
    }
}
