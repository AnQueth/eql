using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using EqLog.Core.Tracking;
using EqLog.Core.Triggers;
using System.Collections.ObjectModel;

namespace EqLog.Overlay;

/// <summary>One line in an overlay: left/right text with an optional proportional bar behind it.</summary>
public sealed record AbilityBreakdown(string Title, string Context, string RateLabel, IReadOnlyList<AbilityStats> Abilities,
    IReadOnlyList<DamageTimelinePoint>? Timeline = null, double TimelineSeconds = 0, DateTime? TimelineStart = null,
    IReadOnlyList<AbilityTimeline>? AbilityTimelines = null);

public sealed record SpawnTrackChange(string Zone, string Mob, bool Tracked);

public sealed record SpellTrackChange(string Spell, bool Tracked);

public sealed record OverlayRow(string Left, string Right = "", double Fraction = 0, Brush? Bar = null, Brush? Foreground = null, bool Wrap = false, bool Bold = false, double Scale = 1, string? LootItem = null, AbilityBreakdown? Breakdown = null, bool QuestHeading = false, string? Details = null, bool Stacked = false, string? ExtraColumn = null, string? ToolTip = null, string? WatchKey = null, bool Watched = false, OverlayTable? Table = null, string? Link = null, bool HasQuestReferences = false, AlertEffect Effect = AlertEffect.None, SpawnTrackChange? SpawnTrack = null, SpellTrackChange? SpellTrack = null);

/// <summary>
/// A small grid shown inside a row's card, below its title. With <see cref="Header"/> it is a column table
/// (numeric columns right-aligned); with <see cref="RowLabels"/> even columns are labels and odd columns their values.
/// <see cref="Then"/> chains another table underneath. <see cref="Links"/> maps value-cell text to a URL that the
/// cell opens when clicked. Equality is by value so unchanged rows are not rebuilt.
/// </summary>
public sealed record OverlayTable(IReadOnlyList<string>? Header, IReadOnlyList<IReadOnlyList<string>> Rows,
    bool RowLabels = false, OverlayTable? Then = null, IReadOnlyDictionary<string, string>? Links = null)
{
    /// <summary>Label/value table, <paramref name="pairsPerRow"/> pairs side by side.</summary>
    public static OverlayTable Pairs(IEnumerable<(string Label, string Value)> pairs, int pairsPerRow = 1, OverlayTable? then = null)
    {
        var rows = pairs
            .Chunk(Math.Max(1, pairsPerRow))
            .Select(chunk => (IReadOnlyList<string>)chunk.SelectMany(p => new[] { p.Label, p.Value }).ToArray())
            .ToArray();
        return new(null, rows, RowLabels: true, Then: then);
    }

    /// <summary>One header row and one value row.</summary>
    public static OverlayTable Columns(IEnumerable<(string Header, string Value)> columns, OverlayTable? then = null)
    {
        var list = columns.ToArray();
        return new(list.Select(c => c.Header).ToArray(), [list.Select(c => c.Value).ToArray()], Then: then);
    }

    /// <summary>Label/value pairs where a list of values shares one label, shown only on its first line.</summary>
    public static IEnumerable<(string Label, string Value)> Labeled(string label, IEnumerable<string> values)
    {
        bool first = true;
        foreach (string value in values)
        {
            yield return (first ? label : "", value);
            first = false;
        }
    }

    /// <summary>Copy with extra cell-text links merged into <see cref="Links"/>; empty texts are ignored.</summary>
    public OverlayTable WithLinks(IEnumerable<(string Text, string Url)> links)
    {
        var merged = Links is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(Links, StringComparer.Ordinal);
        foreach (var (text, url) in links)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                merged.TryAdd(text, url);
            }
        }
        return this with { Links = merged };
    }

    public bool Equals(OverlayTable? other)
    {
        if (other is null)
        {
            return false;
        }
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return RowLabels == other.RowLabels &&
            SameCells(Header, other.Header) &&
            Rows.Count == other.Rows.Count &&
            Rows.Zip(other.Rows).All(pair => SameCells(pair.First, pair.Second)) &&
            SameLinks(Links, other.Links) &&
            Equals(Then, other.Then);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(RowLabels);
        hash.Add(Rows.Count);
        foreach (var row in Rows)
        {
            foreach (string cell in row)
            {
                hash.Add(cell);
            }
        }
        hash.Add(Then);
        return hash.ToHashCode();
    }

    private static bool SameCells(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        return a.SequenceEqual(b, StringComparer.Ordinal);
    }

    private static bool SameLinks(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        return a.Count == b.Count &&
            a.All(pair => b.TryGetValue(pair.Key, out string? url) && string.Equals(url, pair.Value, StringComparison.Ordinal));
    }
}

/// <summary>
/// The content of one overlay (title, subtitle, rows). It lives either in its own floating <see cref="OverlayWindow"/>
/// or as a tile in the <see cref="DockWindow"/>.
/// </summary>
public sealed class OverlayPanel : ContentControl
{
    private readonly TextBlock _title = new() { FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _subtitle = new() { Foreground = Brushes.LightGray, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _rows = new();
    private readonly ScrollViewer _scroll;
    private readonly ObservableCollection<OverlayRow> _virtualRows = [];
    private readonly VirtualRowList? _virtualList;
    private readonly TextBlock _hint;
    private readonly TextBlock _dockDragHandle;
    private readonly Button _dockButton;
    private readonly Slider _backgroundSlider;
    private readonly StackPanel _backgroundControls;
    private readonly Border _frame;
    private readonly Border _titleFrame;
    private readonly Border _inset;
    private readonly Border _bevel;
    private readonly Grid _ornaments = new() { IsHitTestVisible = false };
    private readonly ContentControl _toolbar = new();
    private readonly DockPanel _layout = new();
    private IReadOnlyList<OverlayRow> _current = [];
    private string? _titleLink;
    private bool _invalidateRows;
    private double _opacity = 0.55;
    private bool _locked;
    private bool _docked;
    private bool _chromeHidden;

    public OverlayPanel(string key, string caption, bool autoScrollToEnd)
    {
        Key = key;
        Caption = caption;
        AutoScrollToEnd = autoScrollToEnd;
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Pumpkin");
        _subtitle.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        _title.HorizontalAlignment = HorizontalAlignment.Left;
        _title.Background = Brushes.Transparent;
        // A linked title is a click target, so the press must not start a window drag.
        _title.MouseLeftButtonDown += (_, e) =>
        {
            if (_titleLink is not null)
            {
                e.Handled = true;
            }
        };
        _title.MouseLeftButtonUp += (_, e) =>
        {
            if (_titleLink is { } url)
            {
                e.Handled = true;
                LinkRequested?.Invoke(url);
            }
        };

        _hint = new TextBlock
        {
            Text = "\u2725",
            ToolTip = "Unlocked: drag to move, drag edges to resize. Lock with Ctrl+Shift+L.",
            Foreground = Brushes.Gray,
            FontSize = 10,
            Margin = new Thickness(4, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        _dockButton = new Button
        {
            FontSize = 11,
            Padding = new Thickness(5, 0, 5, 0),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = Brushes.LightGray,
            Background = Frozen(Color.FromArgb(90, 80, 84, 100)),
            BorderBrush = Frozen(Color.FromArgb(120, 120, 124, 140)),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
        };
        _dockButton.Click += (_, _) => DockToggleRequested?.Invoke(this);
        _dockButton.SetResourceReference(BackgroundProperty, "NightRaised");
        _dockButton.SetResourceReference(BorderBrushProperty, "NightBorder");
        _dockButton.SetResourceReference(ForegroundProperty, "NightText");

        var header = new DockPanel();
        _dockDragHandle = new TextBlock
        {
            Text = "\u2630",
            FontSize = 16,
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = System.Windows.Input.Cursors.SizeAll,
            ToolTip = "Drag this panel anywhere on the dock canvas. Other panels stay put.",
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _dockDragHandle.SetResourceReference(TextBlock.ForegroundProperty, "Pumpkin");
        DockPanel.SetDock(_dockDragHandle, Dock.Left);
        header.Children.Add(_dockDragHandle);
        DockPanel.SetDock(_dockButton, Dock.Right);
        DockPanel.SetDock(_hint, Dock.Right);
        header.Children.Add(_dockButton);
        header.Children.Add(_hint);
        header.Children.Add(new StackPanel { Children = { _title, _subtitle } });

        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _rows,
            Margin = new Thickness(0, 3, 0, 0),
        };
        if (key == "alerts")
        {
            _rows.VerticalAlignment = VerticalAlignment.Center;
        }

        _backgroundSlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = _opacity,
            TickFrequency = 0.05,
            SmallChange = 0.05,
            LargeChange = 0.1,
            IsSnapToTickEnabled = true,
            Width = 56,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4, 0, 0, 0),
            Focusable = false,
            ToolTip = $"Background opacity: {_opacity:P0}",
            AutoToolTipPlacement = System.Windows.Controls.Primitives.AutoToolTipPlacement.BottomRight,
            AutoToolTipPrecision = 2,
        };
        _backgroundSlider.ValueChanged += (_, e) =>
        {
            SetBackgroundOpacity(e.NewValue);
            _backgroundSlider.ToolTip = $"Background opacity: {e.NewValue:P0}";
            BackgroundOpacityChanged?.Invoke(e.NewValue);
        };
        _backgroundControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Children =
            {
                _backgroundSlider,
            },
        };
        _backgroundControls.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        DockPanel.SetDock(_backgroundControls, Dock.Right);
        header.Children.Insert(header.Children.IndexOf(_dockButton) + 1, _backgroundControls);
        _titleFrame = new Border { Child = header };
        var layout = _layout;
        DockPanel.SetDock(_titleFrame, Dock.Top);
        layout.Children.Add(_titleFrame);
        DockPanel.SetDock(_toolbar, Dock.Top);
        layout.Children.Add(_toolbar);
        if (key is "quests" or "sky" or "npcs" or "exaltations")
        {
            _virtualList = new VirtualRowList(BuildRow)
            {
                ItemsSource = _virtualRows,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Margin = new Thickness(0, 3, 0, 0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            ScrollViewer.SetVerticalScrollBarVisibility(_virtualList, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(_virtualList, ScrollBarVisibility.Disabled);
            ScrollViewer.SetCanContentScroll(_virtualList, true);
            VirtualizingPanel.SetIsVirtualizing(_virtualList, true);
            VirtualizingPanel.SetVirtualizationMode(_virtualList, VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(_virtualList, ScrollUnit.Pixel);
            var itemsPanel = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
            _virtualList.ItemsPanel = new ItemsPanelTemplate(itemsPanel);
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            var itemStyle = new Style(typeof(ListBoxItem));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            itemStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
            itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            itemStyle.Setters.Add(new Setter(Control.FocusableProperty, false));
            itemStyle.Setters.Add(new Setter(Control.TemplateProperty,
                new ControlTemplate(typeof(ListBoxItem)) { VisualTree = presenter }));
            _virtualList.ItemContainerStyle = itemStyle;
            layout.Children.Add(_virtualList);
        }
        else
        {
            layout.Children.Add(_scroll);
        }

        _inset = new Border { Child = layout };
        _bevel = new Border { Child = _inset };
        var frameContent = new Grid();
        frameContent.Children.Add(_bevel);
        frameContent.Children.Add(_ornaments);
        foreach (var horizontal in new[] { HorizontalAlignment.Left, HorizontalAlignment.Right })
        {
            foreach (var vertical in new[] { VerticalAlignment.Top, VerticalAlignment.Bottom })
            {
                var stud = new Border
                {
                    Width = 5,
                    Height = 5,
                    CornerRadius = new CornerRadius(1),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Brushes.Black,
                    HorizontalAlignment = horizontal,
                    VerticalAlignment = vertical,
                    Margin = new Thickness(3),
                };
                stud.SetResourceReference(Border.BackgroundProperty, "Pumpkin");
                _ornaments.Children.Add(stud);
            }
        }
        _frame = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 4, 6, 4),
            BorderThickness = new Thickness(1),
            Child = frameContent,
        };
        Content = _frame;
        Focusable = false;
        _title.Text = caption;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Apply();
    }

    public string Key
    {
        get;
    }

    public string Caption
    {
        get;
    }

    public bool AutoScrollToEnd
    {
        get;
    }

    /// <summary>Raised when the user clicks the panel's dock / pop-out button.</summary>
    public event Action<OverlayPanel>? DockToggleRequested;

    public event Action<string>? LootLookupRequested;
    public event Action<string>? ItemWikiRequested;

    /// <summary>A row or table cell with a URL was clicked.</summary>
    public event Action<string>? LinkRequested;
    public event Action<AbilityBreakdown>? BreakdownRequested;

    /// <summary>Raised when the star on a row with a <see cref="OverlayRow.WatchKey"/> is clicked.</summary>
    public event Action<string>? WatchToggleRequested;
    public event Action<SpawnTrackChange>? SpawnTrackToggleRequested;
    public event Action<SpellTrackChange>? SpellTrackToggleRequested;
    public event Action<double>? BackgroundOpacityChanged;

    public void SetToolbar(UIElement toolbar)
    {
        _toolbar.Content = toolbar;
    }

    /// <summary>Replaces the row list with custom interactive content that fills the panel below the toolbar.</summary>
    public void SetBody(UIElement body)
    {
        _layout.Children.Remove(_scroll);
        if (_virtualList is not null)
        {
            _layout.Children.Remove(_virtualList);
        }
        _layout.Children.Add(body);
    }

    internal static bool IsInteractive(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase or
                System.Windows.Controls.Primitives.TextBoxBase or
                System.Windows.Controls.Primitives.ScrollBar or System.Windows.Controls.Primitives.Thumb or ComboBox or TabItem or Hyperlink or
                DataGrid or GridSplitter or Slider)
            {
                return true;
            }
            if (source is ListBox and not VirtualRowList)
            {
                return true;
            }

            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    public void SetBackgroundOpacity(double opacity)
    {
        _opacity = Math.Clamp(opacity, 0, 1);
        if (_backgroundSlider.Value != _opacity)
        {
            _backgroundSlider.Value = _opacity;
        }
        Apply();
    }

    public void SetLocked(bool locked)
    {
        _locked = locked;
        Apply();
    }

    public void SetChromeHidden(bool hidden)
    {
        if (_chromeHidden == hidden)
        {
            return;
        }
        _chromeHidden = hidden;
        Apply();
    }

    /// <summary>Docked tiles remain interactive and use theme-colored, beveled fantasy frames.</summary>
    public void SetDocked(bool docked)
    {
        _docked = docked;
        Apply();
    }

    private void Apply()
    {
        double opacity = _opacity;
        var color = ((SolidColorBrush)FindResource("NightBackground")).Color;
        var bg = new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), color.R, color.G, color.B));
        bg.Freeze();
        _frame.Background = bg;
        ApplyDockFrame();
        _hint.Visibility = _locked || _docked ? Visibility.Collapsed : Visibility.Visible;
        _dockDragHandle.Visibility = _docked ? Visibility.Visible : Visibility.Collapsed;

        // Floating overlays are click-through when locked, so the button only shows while unlocked.
        _dockButton.Visibility = _docked || !_locked ? Visibility.Visible : Visibility.Collapsed;
        _backgroundControls.Visibility = !_docked && !_locked ? Visibility.Visible : Visibility.Collapsed;
        _dockButton.Content = _docked ? "\u2197 Pop out" : "\u2199 Dock";
        _dockButton.ToolTip = _docked
            ? "Pop this panel out into its own floating overlay"
            : "Move this overlay into the docked window";
        _frame.SetResourceReference(Border.BorderBrushProperty, _docked ? "NightBorder" : "Pumpkin");
        if (!_docked && _locked)
        {
            _frame.BorderBrush = Brushes.Transparent;
        }
        bool hideChrome = _chromeHidden && !_docked;
        // Reserve the header's space so hovering does not shift content under the pointer.
        _titleFrame.Visibility = hideChrome ? Visibility.Hidden : Visibility.Visible;
        if (hideChrome)
        {
            _frame.BorderBrush = Brushes.Transparent;
            _ornaments.Visibility = Visibility.Hidden;
        }
    }

    private void ApplyDockFrame()
    {
        _ornaments.Visibility = _docked ? Visibility.Visible : Visibility.Collapsed;
        _frame.Padding = _docked ? new Thickness(2) : new Thickness(6, 4, 6, 4);
        _frame.CornerRadius = new CornerRadius(_docked ? 8 : 4);
        _frame.BorderThickness = new Thickness(_docked ? 2 : 1);
        _bevel.BorderThickness = _docked ? new Thickness(1, 1, 2, 2) : new Thickness(0);
        _bevel.CornerRadius = new CornerRadius(5);
        _inset.Margin = _docked ? new Thickness(3) : new Thickness(0);
        _inset.Padding = _docked ? new Thickness(5, 3, 5, 5) : new Thickness(0);
        _inset.BorderThickness = _docked ? new Thickness(1) : new Thickness(0);
        _inset.CornerRadius = new CornerRadius(3);
        _titleFrame.Padding = _docked ? new Thickness(5, 5, 5, 7) : new Thickness(0);
        _titleFrame.Margin = _docked ? new Thickness(-5, -3, -5, 5) : new Thickness(0);
        _titleFrame.BorderThickness = _docked ? new Thickness(0, 0, 0, 2) : new Thickness(0);
        _titleFrame.CornerRadius = new CornerRadius(3, 3, 0, 0);
        if (!_docked)
        {
            _bevel.Background = null;
            _bevel.BorderBrush = null;
            _inset.Background = null;
            _inset.BorderBrush = null;
            _titleFrame.Background = null;
            _titleFrame.BorderBrush = null;
            return;
        }

        Color metal = ((SolidColorBrush)FindResource("NightBorder")).Color;
        Color accent = ((SolidColorBrush)FindResource("Pumpkin")).Color;
        Color raised = ((SolidColorBrush)FindResource("NightRaised")).Color;
        Color background = ((SolidColorBrush)FindResource("NightBackground")).Color;
        _bevel.Background = null;
        _bevel.BorderBrush = Gradient(
            Blend(accent, Colors.White, 0.35), metal, Blend(metal, Colors.Black, 0.8));
        _inset.Background = Gradient(background, background, Blend(background, Colors.Black, 0.25), _opacity);
        _inset.BorderBrush = Gradient(Colors.Black, metal, Blend(metal, Colors.White, 0.2));
        _titleFrame.Background = Gradient(
            Blend(raised, accent, 0.12), raised, Blend(raised, Colors.Black, 0.3), _opacity);
        _titleFrame.BorderBrush = Gradient(accent, metal, Blend(metal, Colors.Black, 0.5));
    }

    private static Color Blend(Color from, Color to, double amount)
    {
        return Color.FromRgb(
            (byte)(from.R + (to.R - from.R) * amount),
            (byte)(from.G + (to.G - from.G) * amount),
            (byte)(from.B + (to.B - from.B) * amount));
    }

    private static Brush Gradient(Color top, Color middle, Color bottom, double opacity = 1)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0.35, 1),
            Opacity = opacity,
            GradientStops =
            {
                new GradientStop(top, 0),
                new GradientStop(middle, 0.45),
                new GradientStop(bottom, 1),
            },
        };
        brush.Freeze();
        return brush;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FontSizeProperty)
        {
            // Scaled rows bake in the font size; rebuild them on the next update.
            _invalidateRows = true;
        }
    }

    public void Update(string title, string? subtitle, IReadOnlyList<OverlayRow> rows, string? titleLink = null)
    {
        _title.Text = title;
        if (_titleLink != titleLink)
        {
            _titleLink = titleLink;
            bool linked = titleLink is not null;
            _title.TextDecorations = linked ? TextDecorations.Underline : null;
            _title.Cursor = linked ? System.Windows.Input.Cursors.Hand : null;
            _title.ToolTip = linked ? "Open on EQLWiki" : null;
        }
        _subtitle.Text = subtitle ?? "";
        // Once a subtitle has appeared, keep its line reserved so rows below never jump under the mouse.
        if (!string.IsNullOrEmpty(subtitle))
        {
            _subtitle.Visibility = Visibility.Visible;
        }
        else if (_subtitle.Visibility == Visibility.Visible)
        {
            _subtitle.Visibility = Visibility.Hidden;
        }

        if (!_invalidateRows && rows.SequenceEqual(_current))
        {
            return;
        }

        if (_virtualList is not null)
        {
            if (_invalidateRows)
            {
                _virtualRows.Clear();
                _current = [];
                _invalidateRows = false;
            }
            int shared = Math.Min(_virtualRows.Count, rows.Count);
            for (int i = 0; i < shared; i++)
            {
                if (_virtualRows[i] != rows[i])
                {
                    _virtualRows[i] = rows[i];
                }
            }

            while (_virtualRows.Count > rows.Count)
            {
                _virtualRows.RemoveAt(_virtualRows.Count - 1);
            }

            for (int i = shared; i < rows.Count; i++)
            {
                _virtualRows.Add(rows[i]);
            }

            _current = rows;
            return;
        }

        bool wasAtEnd = _scroll.VerticalOffset >= _scroll.ScrollableHeight - 2;
        if (_invalidateRows)
        {
            _rows.Children.Clear();
            _current = [];
            _invalidateRows = false;
        }
        int prefix = 0;
        while (prefix < _current.Count && prefix < rows.Count && _current[prefix] == rows[prefix])
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < _current.Count - prefix && suffix < rows.Count - prefix &&
            _current[_current.Count - 1 - suffix] == rows[rows.Count - 1 - suffix])
        {
            suffix++;
        }

        int remove = _current.Count - prefix - suffix;
        if (remove > 0)
        {
            _rows.Children.RemoveRange(prefix, remove);
        }

        for (int i = prefix; i < rows.Count - suffix; i++)
        {
            _rows.Children.Insert(i, BuildRow(rows[i]));
        }
        _current = rows;

        if (AutoScrollToEnd && wasAtEnd)
        {
            _scroll.ScrollToEnd();
        }
    }

    private FrameworkElement BuildRow(OverlayRow row)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 1) };
        if (row.ToolTip is not null)
        {
            grid.ToolTip = row.ToolTip;
        }
        if (row.QuestHeading)
        {
            grid.Margin = new Thickness(0, 12, 0, 5);
        }
        if (row.Scale != 1)
        {
            grid.SetValue(TextElement.FontSizeProperty, FontSize * row.Scale);
        }

        if (row.Bar is not null && row.Fraction > 0)
        {
            double f = Math.Clamp(row.Fraction, 0, 1);
            var bar = new Grid();
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(f, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - f, GridUnitType.Star) });
            bar.Children.Add(new Border { Background = row.Bar, CornerRadius = new CornerRadius(2) });
            grid.Children.Add(bar);
        }

        grid.SetResourceReference(TextElement.ForegroundProperty, "NightText");
        if (row.QuestHeading)
        {
            grid.SetResourceReference(TextElement.ForegroundProperty, "Pumpkin");
        }

        var weight = row.Bold ? FontWeights.Bold : FontWeights.Normal;
        var text = new DockPanel { Margin = new Thickness(3, 0, 3, 0) };
        if (row.SpawnTrack is { } spawnTrack)
        {
            var track = new CheckBox
            {
                IsChecked = spawnTrack.Tracked,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
                Focusable = false,
                ToolTip = spawnTrack.Tracked
                    ? "Stop tracking this mob"
                    : "Track this mob in the Spawn Timers overlay",
            };
            track.Click += (_, e) =>
            {
                SpawnTrackToggleRequested?.Invoke(spawnTrack with { Tracked = track.IsChecked == true });
                e.Handled = true;
            };
            DockPanel.SetDock(track, Dock.Left);
            text.Children.Add(track);
        }
        if (row.SpellTrack is { } spellTrack)
        {
            var track = new CheckBox
            {
                IsChecked = spellTrack.Tracked,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
                Focusable = false,
                ToolTip = spellTrack.Tracked ? "Stop tracking this spell" : "Track this spell in the Spell Timers overlay",
            };
            track.Click += (_, e) =>
            {
                SpellTrackToggleRequested?.Invoke(spellTrack with { Tracked = track.IsChecked == true });
                e.Handled = true;
            };
            DockPanel.SetDock(track, Dock.Left);
            text.Children.Add(track);
        }
        if (row.WatchKey is { } watchKey)
        {
            var star = new Button
            {
                Content = row.Watched ? "\u2605" : "\u2606",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = System.Windows.Input.Cursors.Hand,
                Focusable = false,
                ToolTip = row.Watched ? "Stop watching this quest" : "Watch this quest (shown in the Watching tab)",
            };
            star.SetResourceReference(ForegroundProperty, "Pumpkin");
            star.Click += (_, e) =>
            {
                WatchToggleRequested?.Invoke(watchKey);
                e.Handled = true;
            };
            DockPanel.SetDock(star, Dock.Left);
            text.Children.Add(star);
        }
        if (row.ExtraColumn is not null)
        {
            var extra = new TextBlock
            {
                Text = row.ExtraColumn,
                FontWeight = weight,
                Width = 48,
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(8, 0, 0, 0),
            };
            if (row.Foreground is not null)
            {
                extra.Foreground = row.Foreground;
            }
            DockPanel.SetDock(extra, Dock.Right);
            text.Children.Add(extra);
        }
        if (!string.IsNullOrEmpty(row.Right) && !row.Stacked)
        {
            var right = new TextBlock { Text = row.Right, FontWeight = weight, Margin = new Thickness(8, 0, 0, 0) };
            if (row.ExtraColumn is not null)
            {
                right.MinWidth = 100;
                right.TextAlignment = TextAlignment.Right;
            }
            if (row.Foreground is not null)
            {
                right.Foreground = row.Foreground;
            }

            DockPanel.SetDock(right, Dock.Right);
            text.Children.Add(right);
        }

        var left = new TextBlock
        {
            Text = row.Left,
            FontWeight = weight,
            TextWrapping = row.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = row.Wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            TextAlignment = Key == "alerts" ? TextAlignment.Center : TextAlignment.Left,
        };
        if (row.Foreground is not null)
        {
            left.Foreground = row.Foreground;
        }
        AlertEffects.Attach(left, row.Effect);
        if (row.LootItem is not null || row.Link is not null)
        {
            left.TextDecorations = TextDecorations.Underline;
        }
        if (row.HasQuestReferences && row.LootItem is { } questItem)
        {
            left.Text = "";
            var badge = new Hyperlink(new Run("[Quest]"))
            {
                FontWeight = FontWeights.SemiBold,
                ToolTip = "Quest references available: click to view. Wiki mentions may include ingredients or rewards, not a turn-in checklist.",
            };
            badge.SetResourceReference(TextElement.ForegroundProperty, "Pumpkin");
            badge.Click += (_, e) =>
            {
                e.Handled = true;
                LootLookupRequested?.Invoke(questItem);
            };
            left.Inlines.Add(badge);
            left.Inlines.Add(new Run(" " + row.Left));
        }

        if (row.Details is not null || row.Table is not null)
        {
            left.FontWeight = FontWeights.SemiBold;
            var itemContent = new StackPanel();
            if (!string.IsNullOrEmpty(row.Left))
            {
                itemContent.Children.Add(left);
            }
            if (row.Stacked && !string.IsNullOrEmpty(row.Right))
            {
                var result = new TextBlock
                {
                    Text = row.Right,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 5, 0, 0),
                };
                result.SetResourceReference(TextBlock.ForegroundProperty, "Pumpkin");
                if (row.Foreground is not null)
                {
                    result.Foreground = row.Foreground;
                }
                itemContent.Children.Add(result);
            }
            for (var table = row.Table; table is not null; table = table.Then)
            {
                var element = BuildTable(table, FontSize, url => LinkRequested?.Invoke(url));
                if (itemContent.Children.Count == 0)
                {
                    element.Margin = new Thickness(0);
                }
                itemContent.Children.Add(element);
            }
            if (!string.IsNullOrEmpty(row.Details))
            {
                var description = new TextBlock
                {
                    Text = row.Details,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = Math.Max(10, FontSize - 1),
                    Margin = new Thickness(0, itemContent.Children.Count == 0 ? 0 : 4, 0, 0),
                };
                description.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
                itemContent.Children.Add(description);
            }
            text.Children.Add(itemContent);
            text.Margin = new Thickness(10, 8, 10, 8);
            var glass = new Border
            {
                CornerRadius = new CornerRadius(6),
                Opacity = 0.6,
                BorderThickness = new Thickness(1),
            };
            glass.SetResourceReference(Border.BackgroundProperty, "NightRaised");
            glass.SetResourceReference(Border.BorderBrushProperty, "NightBorder");
            grid.Children.Insert(0, glass);
            if (row.Stacked)
            {
                grid.Margin = new Thickness(0, 0, 0, 8);
            }
        }
        else
        {
            text.Children.Add(left);
        }

        grid.Children.Add(text);
        if (row.LootItem is not null || row.Breakdown is not null || row.Link is not null)
        {
            var button = new Button
            {
                Content = grid,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = row.Breakdown is not null ? "Click for ability breakdown" :
                    row.LootItem is not null ? $"Open {row.LootItem} on EQLWiki (right-click for quest references)" :
                    row.ToolTip ?? "Open on EQLWiki",
                Focusable = false,
            };
            if (row.Details is not null || row.Table is not null)
            {
                button.Margin = new Thickness(12, 0, 0, 5);
            }

            button.Click += (_, _) =>
            {
                if (row.Breakdown is { } breakdown)
                {
                    BreakdownRequested?.Invoke(breakdown);
                }
                else if (row.LootItem is { } item)
                {
                    ItemWikiRequested?.Invoke(item);
                }
                else if (row.Link is { } link)
                {
                    LinkRequested?.Invoke(link);
                }
            };
            if (row.LootItem is { } lootItem)
            {
                var menu = new ContextMenu();
                var wiki = new MenuItem { Header = "Open item on EQLWiki" };
                wiki.Click += (_, _) => ItemWikiRequested?.Invoke(lootItem);
                var quests = new MenuItem { Header = "Look up quest references" };
                quests.Click += (_, _) => LootLookupRequested?.Invoke(lootItem);
                menu.Items.Add(wiki);
                menu.Items.Add(quests);
                button.ContextMenu = menu;
            }
            return button;
        }

        return grid;
    }

    /// <summary>
    /// Renders an <see cref="OverlayTable"/> as a real grid. Label/value tables use muted auto-width label columns;
    /// column tables get a header rule, zebra rows, and right-aligned numeric columns.
    /// </summary>
    public static FrameworkElement BuildTable(OverlayTable table, double fontSize, Action<string>? openLink = null)
    {
        int columns = Math.Max(table.Header?.Count ?? 0, table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.Count));
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.SetValue(TextElement.FontSizeProperty, Math.Max(10, fontSize - 1));
        if (columns == 0)
        {
            return grid;
        }

        var numeric = new bool[columns];
        for (int c = 0; c < columns; c++)
        {
            var cells = table.Rows.Where(r => c < r.Count && r[c].Length > 0).Select(r => r[c]).ToArray();
            numeric[c] = !table.RowLabels && cells.Length > 0 && cells.All(IsNumeric);
        }

        bool allNumeric = numeric.All(n => n);
        int stretch = table.RowLabels ? -1 : Array.IndexOf(numeric, false);
        for (int c = 0; c < columns; c++)
        {
            GridLength width;
            if (table.RowLabels)
            {
                width = c % 2 == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            }
            else if (allNumeric || c == stretch)
            {
                width = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                width = GridLength.Auto;
            }
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        }

        int gridRow = 0;
        if (table.Header is { } header)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rule = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
            rule.SetResourceReference(Border.BorderBrushProperty, "GridLine");
            Grid.SetColumnSpan(rule, columns);
            grid.Children.Add(rule);
            for (int c = 0; c < header.Count; c++)
            {
                var cell = TableCell(header[c], c, columns, numeric[c], wrap: false);
                cell.FontWeight = FontWeights.SemiBold;
                cell.TextTrimming = TextTrimming.CharacterEllipsis;
                cell.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
                grid.Children.Add(cell);
            }
            gridRow++;
        }

        for (int r = 0; r < table.Rows.Count; r++, gridRow++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (table.Header is not null && r % 2 == 1)
            {
                var stripe = new Border { CornerRadius = new CornerRadius(2), Opacity = 0.7 };
                stripe.SetResourceReference(Border.BackgroundProperty, "Alternate");
                Grid.SetRow(stripe, gridRow);
                Grid.SetColumnSpan(stripe, columns);
                grid.Children.Add(stripe);
            }

            var cells = table.Rows[r];
            for (int c = 0; c < cells.Count; c++)
            {
                bool label = table.RowLabels && c % 2 == 0;
                var cell = TableCell(cells[c], c, columns, numeric[c], wrap: !label);
                if (label)
                {
                    cell.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
                }
                else if (table.RowLabels)
                {
                    cell.FontWeight = FontWeights.SemiBold;
                }
                if (!label && openLink is not null && table.Links is { } links && links.TryGetValue(cells[c], out string? url))
                {
                    MakeLink(cell, url, openLink);
                }
                Grid.SetRow(cell, gridRow);
                grid.Children.Add(cell);
            }
        }
        return grid;
    }

    /// <summary>
    /// Turns a table cell into a link. The press is marked handled so a clickable card around the table
    /// (for example an item row) doesn't also act on it.
    /// </summary>
    private static void MakeLink(TextBlock cell, string url, Action<string> openLink)
    {
        cell.TextDecorations = TextDecorations.Underline;
        cell.Cursor = System.Windows.Input.Cursors.Hand;
        cell.ToolTip = "Open on EQLWiki";
        cell.HorizontalAlignment = HorizontalAlignment.Left;
        cell.Background = Brushes.Transparent;
        cell.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
        };
        cell.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            openLink(url);
        };
    }

    private static TextBlock TableCell(string text, int column, int columns, bool numeric, bool wrap)
    {
        var cell = new TextBlock
        {
            Text = text,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextAlignment = numeric ? TextAlignment.Right : TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(column == 0 ? 2 : 6, 1, column == columns - 1 ? 2 : 6, 1),
        };
        Grid.SetColumn(cell, column);
        return cell;
    }

    private static bool IsNumeric(string text)
    {
        return text.Length > 0 && (char.IsDigit(text[0]) || (text.Length > 1 && text[0] is '-' or '+' && char.IsDigit(text[1])) || text == "?");
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private sealed class VirtualRowList(Func<OverlayRow, FrameworkElement> build) : ListBox
    {
        protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
        {
            base.PrepareContainerForItemOverride(element, item);
            if (element is ListBoxItem container && item is OverlayRow row)
            {
                container.Content = build(row);
            }
        }

        protected override void ClearContainerForItemOverride(DependencyObject element, object item)
        {
            if (element is ListBoxItem container)
            {
                container.Content = null;
            }

            base.ClearContainerForItemOverride(element, item);
        }
    }
}
