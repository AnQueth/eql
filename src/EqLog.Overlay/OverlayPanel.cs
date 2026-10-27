using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using EqLog.Core.Tracking;
using System.Collections.ObjectModel;

namespace EqLog.Overlay;

/// <summary>One line in an overlay: left/right text with an optional proportional bar behind it.</summary>
public sealed record AbilityBreakdown(string Title, string Context, string RateLabel, IReadOnlyList<AbilityStats> Abilities);

public sealed record OverlayRow(string Left, string Right = "", double Fraction = 0, Brush? Bar = null, Brush? Foreground = null, bool Wrap = false, bool Bold = false, double Scale = 1, string? LootItem = null, AbilityBreakdown? Breakdown = null, bool QuestHeading = false, string? Details = null);

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
    private readonly Border _frame;
    private readonly ContentControl _toolbar = new();
    private IReadOnlyList<OverlayRow> _current = [];
    private bool _invalidateRows;
    private double _opacity = 0.55;
    private bool _locked;
    private bool _docked;

    public OverlayPanel(string key, string caption, bool autoScrollToEnd)
    {
        Key = key;
        Caption = caption;
        AutoScrollToEnd = autoScrollToEnd;
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Pumpkin");
        _subtitle.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");

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

        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        DockPanel.SetDock(_toolbar, Dock.Top);
        layout.Children.Add(_toolbar);
        if (key is "quests" or "sky")
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

        _frame = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 4, 6, 4),
            BorderThickness = new Thickness(1),
            Child = layout,
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
    public event Action<AbilityBreakdown>? BreakdownRequested;

    public void SetToolbar(UIElement toolbar)
    {
        _toolbar.Content = toolbar;
    }

    internal static bool IsInteractive(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase or
                System.Windows.Controls.Primitives.TextBoxBase or
                System.Windows.Controls.Primitives.ScrollBar or System.Windows.Controls.Primitives.Thumb or ComboBox)
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
        Apply();
    }

    public void SetLocked(bool locked)
    {
        _locked = locked;
        Apply();
    }

    /// <summary>Docked tiles remain interactive and show a subtle border.</summary>
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
        _hint.Visibility = _locked || _docked ? Visibility.Collapsed : Visibility.Visible;
        _dockDragHandle.Visibility = _docked ? Visibility.Visible : Visibility.Collapsed;

        // Floating overlays are click-through when locked, so the button only shows while unlocked.
        _dockButton.Visibility = _docked || !_locked ? Visibility.Visible : Visibility.Collapsed;
        _dockButton.Content = _docked ? "\u2197 Pop out" : "\u2199 Dock";
        _dockButton.ToolTip = _docked
            ? "Pop this panel out into its own floating overlay"
            : "Move this overlay into the docked window";
        _frame.SetResourceReference(Border.BorderBrushProperty, _docked ? "NightBorder" : "Pumpkin");
        if (!_docked && _locked)
        {
            _frame.BorderBrush = Brushes.Transparent;
        }
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

    public void Update(string title, string? subtitle, IReadOnlyList<OverlayRow> rows)
    {
        _title.Text = title;
        _subtitle.Text = subtitle ?? "";
        _subtitle.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;

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
        if (!string.IsNullOrEmpty(row.Right))
        {
            var right = new TextBlock { Text = row.Right, FontWeight = weight, Margin = new Thickness(8, 0, 0, 0) };
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
        };
        if (row.Foreground is not null)
        {
            left.Foreground = row.Foreground;
        }

        if (row.Details is { } details)
        {
            left.FontWeight = FontWeights.SemiBold;
            var itemContent = new StackPanel();
            itemContent.Children.Add(left);
            var description = new TextBlock
            {
                Text = details,
                TextWrapping = TextWrapping.Wrap,
                FontSize = Math.Max(10, FontSize - 1),
                Margin = new Thickness(0, 4, 0, 0),
            };
            description.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
            itemContent.Children.Add(description);
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
        }
        else
        {
            text.Children.Add(left);
        }

        grid.Children.Add(text);
        if (row.LootItem is not null || row.Breakdown is not null)
        {
            var button = new Button
            {
                Content = grid,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = row.Breakdown is not null ? "Click for ability breakdown" : $"Look up quest references for {row.LootItem}",
                Focusable = false,
            };
            if (row.Details is not null)
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
                    LootLookupRequested?.Invoke(item);
                }
            };
            return button;
        }

        return grid;
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
