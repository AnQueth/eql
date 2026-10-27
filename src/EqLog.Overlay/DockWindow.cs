using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace EqLog.Overlay;

/// <summary>
/// The app's main window (e.g. for a second monitor): settings in a collapsible side panel on the left and
/// docked overlay panels as tiles that wrap to fit on the right.
/// Drag a tile to reorder it; drag its bottom-right corner to resize it. Closing this window exits the app.
/// </summary>
public sealed class DockWindow : Window
{
    private const double MinTileWidth = 140;
    private const double MinTileHeight = 60;
    private const double DefaultSideWidth = 560;
    private const double MinSideWidth = 380;

    private readonly WrapPanel _tiles = new() { Margin = new Thickness(4) };
    private readonly Dictionary<string, Grid> _byKey = [];
    private readonly TextBlock _empty;
    private readonly Border _side = new() { Padding = new Thickness(0) };
    private readonly ColumnDefinition _sideColumn = new() { MinWidth = MinSideWidth };
    private readonly GridSplitter _splitter;
    private readonly ToggleButton _sideToggle;
    private Point? _dragStart;
    private const string PanelDragFormat = "EqLogOverlay.DockPanel";
    private Border? _dropPreview;
    private string? _draggedKey;
    private bool _dropCommitted;

    public DockWindow(DockWindowSettings settings)
    {
        Settings = settings;
        Title = "EverQuest Log Overlay";
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/AppIcon.ico"));
        SetResourceReference(BackgroundProperty, "NightBackground");
        SetResourceReference(ForegroundProperty, "NightText");
        FontFamily = new FontFamily("Segoe UI");
        MinWidth = 420;
        MinHeight = 300;
        Width = settings.Width > 0 ? settings.Width : 1000 + DefaultSideWidth;
        if (settings.Width > 0 && settings.SidePanelWidth <= 0)
        {
            // First run with the settings side panel: make room for it rather than squeezing the tiles.
            Width = Math.Min(settings.Width + DefaultSideWidth, SystemParameters.WorkArea.Width);
        }

        Height = settings.Height > 0 ? settings.Height : 700;
        if (settings.Left is double left && settings.Top is double top)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        Topmost = settings.Topmost;

        var topmost = new CheckBox
        {
            Content = "Always on top",
            IsChecked = settings.Topmost,
            Foreground = Foreground,
            VerticalAlignment = VerticalAlignment.Center,
        };
        topmost.SetResourceReference(ForegroundProperty, "NightText");
        topmost.Click += (_, _) => Topmost = Settings.Topmost = topmost.IsChecked == true;
        _sideToggle = new ToggleButton
        {
            Content = "\u2699 Settings",
            IsChecked = settings.SidePanelOpen,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "Show or hide the settings panel",
            Focusable = false,
        };
        _sideToggle.Click += (_, _) => SetSidePanelOpen(_sideToggle.IsChecked == true);

        var bar = new DockPanel { Margin = new Thickness(8, 6, 8, 2) };
        DockPanel.SetDock(_sideToggle, Dock.Left);
        DockPanel.SetDock(topmost, Dock.Right);
        bar.Children.Add(_sideToggle);
        bar.Children.Add(topmost);
        var dragHint = new TextBlock
        {
            Text = "Drag a panel to reorder it; drag its bottom-right corner to resize it.",
            VerticalAlignment = VerticalAlignment.Center,
        };
        dragHint.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        bar.Children.Add(dragHint);

        _empty = new TextBlock
        {
            Text = "No panels are docked. Tick \"docked\" next to an overlay in Settings, or click \u2199 Dock on a floating overlay (while unlocked).",
            Margin = new Thickness(12),
            TextWrapping = TextWrapping.Wrap,
        };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");

        var tilesArea = new ScrollViewer
        {
            Background = Brushes.Transparent,
            AllowDrop = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new StackPanel { Children = { _empty, _tiles } },
        };
        tilesArea.DragOver += (_, e) =>
        {
            bool valid = e.Data.GetData(PanelDragFormat) is string key && key == _draggedKey;
            e.Effects = valid
                ? DragDropEffects.Move : DragDropEffects.None;
            if (valid)
            {
                UpdateDropPreview(e.GetPosition(_tiles));
                double y = e.GetPosition(tilesArea).Y;
                if (y < 35) tilesArea.ScrollToVerticalOffset(tilesArea.VerticalOffset - 12);
                else if (y > tilesArea.ActualHeight - 35) tilesArea.ScrollToVerticalOffset(tilesArea.VerticalOffset + 12);
            }
            e.Handled = true;
        };
        tilesArea.Drop += (_, e) =>
        {
            if (e.Data.GetData(PanelDragFormat) is string source && source == _draggedKey)
            {
                UpdateDropPreview(e.GetPosition(_tiles));
                _dropCommitted = true;
                e.Handled = true;
            }
        };

        _splitter = new GridSplitter
        {
            Width = 5,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ToolTip = "Drag to resize the settings panel",
        };
        _splitter.SetResourceReference(BackgroundProperty, "NightBorder");

        var body = new Grid();
        body.ColumnDefinitions.Add(_sideColumn);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 150 });
        Grid.SetColumn(_side, 0);
        Grid.SetColumn(_splitter, 1);
        Grid.SetColumn(tilesArea, 2);
        body.Children.Add(_side);
        body.Children.Add(_splitter);
        body.Children.Add(tilesArea);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(body);
        Content = root;
        SetSidePanelOpen(settings.SidePanelOpen);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        Loaded += (_, _) =>
        {
            if (Settings.Maximized)
            {
                WindowState = WindowState.Maximized;
            }
        };
    }

    public DockWindowSettings Settings { get; }

    /// <summary>Puts the settings UI into the side panel.</summary>
    public void SetSidePanel(FrameworkElement content) => _side.Child = content;

    public void SetSidePanelOpen(bool open)
    {
        if (!open && _side.Visibility == Visibility.Visible && _sideColumn.ActualWidth > 0)
        {
            Settings.SidePanelWidth = _sideColumn.ActualWidth;
        }

        Settings.SidePanelOpen = open;
        _sideToggle.IsChecked = open;
        _side.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        _splitter.Visibility = _side.Visibility;
        _sideColumn.MinWidth = open ? MinSideWidth : 0;
        _sideColumn.Width = open
            ? new GridLength(Settings.SidePanelWidth > 0 ? Settings.SidePanelWidth : DefaultSideWidth)
            : new GridLength(0);
    }

    /// <summary>Raised when the user reorders tiles; the argument is the new order of keys.</summary>
    public event Action<IReadOnlyList<string>>? OrderChanged;

    public bool HasPanel(string key) => _byKey.ContainsKey(key);

    public void AddPanel(OverlayPanel panel, OverlayPlacement placement, IReadOnlyList<string> order)
    {
        if (_byKey.ContainsKey(panel.Key))
        {
            return;
        }

        panel.SetDocked(true);
        var tile = new Grid
        {
            Width = Math.Max(MinTileWidth, placement.DockWidth > 0 ? placement.DockWidth : placement.Width),
            Height = Math.Max(MinTileHeight, placement.DockHeight > 0 ? placement.DockHeight : placement.Height),
            Margin = new Thickness(4),
            VerticalAlignment = VerticalAlignment.Top,
            Tag = panel.Key,
            AllowDrop = true,
        };
        tile.Children.Add(panel);

        var grip = MakeGrip();
        grip.DragDelta += (_, e) =>
        {
            tile.Width = Math.Max(MinTileWidth, tile.Width + e.HorizontalChange);
            tile.Height = Math.Max(MinTileHeight, tile.Height + e.VerticalChange);
            placement.DockWidth = tile.Width;
            placement.DockHeight = tile.Height;
        };
        tile.Children.Add(grip);

        tile.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.OriginalSource is DependencyObject d && (IsInside<Thumb>(d) || OverlayPanel.IsInteractive(d)) ? null : e.GetPosition(this);
        };
        tile.PreviewMouseMove += (_, e) =>
        {
            if (_dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var delta = e.GetPosition(this) - start;
            if (Math.Abs(delta.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(delta.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _dragStart = null;
                var original = _tiles.Children.OfType<Grid>().Select(t => (string)t.Tag).ToArray();
                _draggedKey = panel.Key;
                _dropCommitted = false;
                _dropPreview = new Border
                {
                    Width = tile.Width, Height = tile.Height, Margin = tile.Margin,
                    BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(6),
                    VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
                    Child = new TextBlock
                    {
                        Text = $"Drop {panel.Caption} here", TextWrapping = TextWrapping.Wrap,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(10), FontWeight = FontWeights.SemiBold,
                    },
                };
                _dropPreview.SetResourceReference(Border.BorderBrushProperty, "Pumpkin");
                _dropPreview.SetResourceReference(Border.BackgroundProperty, "NightRaised");
                _dropPreview.SetResourceReference(TextElement.ForegroundProperty, "Pumpkin");
                int index = _tiles.Children.IndexOf(tile);
                _tiles.Children.Remove(tile);
                _tiles.Children.Insert(index, _dropPreview);
                try
                {
                    DragDrop.DoDragDrop(tile, new DataObject(PanelDragFormat, panel.Key), DragDropEffects.Move);
                }
                finally
                {
                    var proposed = _tiles.Children.Cast<FrameworkElement>()
                        .Select(t => t == _dropPreview ? panel.Key : (string)t.Tag).ToArray();
                    _dropPreview = null;
                    _draggedKey = null;
                    Arrange(_dropCommitted ? proposed : original);
                    if (_dropCommitted) OrderChanged?.Invoke(proposed);
                }
            }
        };

        _byKey[panel.Key] = tile;
        Arrange(order);
    }

    public void RemovePanel(string key)
    {
        if (_byKey.Remove(key, out var tile))
        {
            tile.Children.Clear();
            _tiles.Children.Remove(tile);
            UpdateEmpty();
        }
    }

    /// <summary>Lays tiles out in the given key order; keys not in the list go last.</summary>
    public void Arrange(IReadOnlyList<string> order)
    {
        _tiles.Children.Clear();
        foreach (var tile in _byKey.Values.OrderBy(t => Rank(order, (string)t.Tag)))
        {
            _tiles.Children.Add(tile);
        }

        UpdateEmpty();
    }

    private static int Rank(IReadOnlyList<string> order, string key)
    {
        for (int i = 0; i < order.Count; i++)
        {
            if (order[i] == key)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private void UpdateDropPreview(Point pointer)
    {
        if (_dropPreview is null) return;
        var placeholderBounds = new Rect(_dropPreview.TranslatePoint(new Point(), _tiles), _dropPreview.RenderSize);
        if (placeholderBounds.Contains(pointer)) return;
        var candidates = _tiles.Children.OfType<Grid>().ToArray();
        int insertion = candidates.Length;
        for (int i = 0; i < candidates.Length; i++)
        {
            var tile = candidates[i];
            var bounds = new Rect(tile.TranslatePoint(new Point(), _tiles), tile.RenderSize);
            if (pointer.Y < bounds.Top || (pointer.Y <= bounds.Bottom && pointer.X < bounds.Left + bounds.Width / 2))
            {
                insertion = i;
                break;
            }
        }
        int current = _tiles.Children.IndexOf(_dropPreview);
        int target = insertion == candidates.Length ? _tiles.Children.Count - 1 :
            _tiles.Children.IndexOf(candidates[insertion]);
        if (target > current && insertion < candidates.Length) target--;
        if (target == current) return;
        _tiles.Children.Remove(_dropPreview);
        _tiles.Children.Insert(target, _dropPreview);
        _tiles.UpdateLayout();
    }

    private void UpdateEmpty() => _empty.Visibility = _byKey.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private static Thumb MakeGrip()
    {
        var triangle = new FrameworkElementFactory(typeof(Path));
        triangle.SetValue(Path.DataProperty, Geometry.Parse("M 12,0 L 12,12 L 0,12 Z"));
        triangle.SetValue(Shape.FillProperty, new SolidColorBrush(Color.FromArgb(170, 255, 215, 0)));
        return new Thumb
        {
            Width = 12,
            Height = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.SizeNWSE,
            ToolTip = "Drag to resize",
            Template = new ControlTemplate(typeof(Thumb)) { VisualTree = triangle },
        };
    }

    private static bool IsInside<T>(DependencyObject d)
        where T : DependencyObject
    {
        for (var e = d; e is not null; e = e is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(e) : LogicalTreeHelper.GetParent(e))
        {
            if (e is T)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Remembers position, size and maximized state.</summary>
    public void SaveBounds()
    {
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        // A window that has never been shown reports NaN for its position.
        if (!r.IsEmpty && double.IsFinite(r.Left) && double.IsFinite(r.Top) && double.IsFinite(r.Width) && double.IsFinite(r.Height))
        {
            Settings.Left = r.Left;
            Settings.Top = r.Top;
            Settings.Width = r.Width;
            Settings.Height = r.Height;
        }

        Settings.Maximized = WindowState == WindowState.Maximized;
        if (Settings.SidePanelOpen && _sideColumn.ActualWidth > 0)
        {
            Settings.SidePanelWidth = _sideColumn.ActualWidth;
        }
    }
}
