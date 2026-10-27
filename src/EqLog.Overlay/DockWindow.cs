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
/// docked overlay panels on a scrollable freeform canvas on the right.
/// Drag a tile to move it; drag its bottom-right corner to resize it. Closing this window exits the app.
/// </summary>
public sealed class DockWindow : Window
{
    private const double MinTileWidth = 140;
    private const double MinTileHeight = 60;
    private const double DefaultSideWidth = 560;
    private const double MinSideWidth = 380;

    private readonly Canvas _tiles = new() { Background = Brushes.Transparent };
    private readonly ScrollViewer _tilesArea;
    private readonly Dictionary<string, Grid> _byKey = [];
    private readonly Dictionary<string, OverlayPlacement> _placements = [];
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
    private Vector _dragOffset;

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
            Text = "Drag panels anywhere; resizing moves overlapping panels into nearby clear space.",
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

        _tilesArea = new ScrollViewer
        {
            Background = Brushes.Transparent,
            AllowDrop = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _tiles,
        };
        var tilesArea = _tilesArea;
        var canvasArea = new Grid { Children = { tilesArea, _empty } };
        _empty.IsHitTestVisible = false;
        _empty.VerticalAlignment = VerticalAlignment.Top;
        tilesArea.SizeChanged += (_, _) => UpdateCanvasSize();
        tilesArea.DragOver += (_, e) =>
        {
            bool valid = e.Data.GetData(PanelDragFormat) is string key && key == _draggedKey;
            e.Effects = valid
                ? DragDropEffects.Move : DragDropEffects.None;
            if (valid)
            {
                UpdateDropPreview(e.GetPosition(_tiles));
                double y = e.GetPosition(tilesArea).Y;
                if (y < 35)
                {
                    tilesArea.ScrollToVerticalOffset(tilesArea.VerticalOffset - 12);
                }
                else if (y > tilesArea.ActualHeight - 35)
                {
                    tilesArea.ScrollToVerticalOffset(tilesArea.VerticalOffset + 12);
                }

                double x = e.GetPosition(tilesArea).X;
                if (x < 35)
                {
                    tilesArea.ScrollToHorizontalOffset(tilesArea.HorizontalOffset - 12);
                }
                else if (x > tilesArea.ActualWidth - 35)
                {
                    tilesArea.ScrollToHorizontalOffset(tilesArea.HorizontalOffset + 12);
                }
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
        Grid.SetColumn(canvasArea, 2);
        body.Children.Add(_side);
        body.Children.Add(_splitter);
        body.Children.Add(canvasArea);

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

    public DockWindowSettings Settings
    {
        get;
    }

    /// <summary>Puts the settings UI into the side panel.</summary>
    public void SetSidePanel(FrameworkElement content)
    {
        _side.Child = content;
    }

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

    public bool HasPanel(string key)
    {
        return _byKey.ContainsKey(key);
    }

    /// <summary>Brings a docked panel forward and scrolls it into view.</summary>
    public void RevealPanel(string key)
    {
        if (!_byKey.TryGetValue(key, out var tile))
        {
            return;
        }

        BringToFront(tile);
        _tiles.UpdateLayout();
        _tilesArea.ScrollToHorizontalOffset(Canvas.GetLeft(tile));
        _tilesArea.ScrollToVerticalOffset(Canvas.GetTop(tile));
    }

    public void AddPanel(OverlayPanel panel, OverlayPlacement placement)
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
            VerticalAlignment = VerticalAlignment.Top,
            Tag = panel.Key,
            AllowDrop = true,
        };
        var position = placement.DockLeft is double left && double.IsFinite(left) &&
            placement.DockTop is double top && double.IsFinite(top)
            ? new Point(Math.Max(4, left), Math.Max(4, top))
            : FindEmptyPosition(tile.Width, tile.Height);
        Canvas.SetLeft(tile, position.X);
        Canvas.SetTop(tile, position.Y);
        placement.DockLeft = position.X;
        placement.DockTop = position.Y;
        tile.Children.Add(panel);

        var grip = MakeGrip();
        grip.DragDelta += (_, e) =>
        {
            double previousWidth = tile.Width;
            double previousHeight = tile.Height;
            tile.Width = Math.Max(MinTileWidth, tile.Width + e.HorizontalChange);
            tile.Height = Math.Max(MinTileHeight, tile.Height + e.VerticalChange);
            placement.DockWidth = tile.Width;
            placement.DockHeight = tile.Height;
            CloseResizeGaps(tile, previousWidth, previousHeight);
            ResolveResizeOverlaps(tile);
            UpdateCanvasSize();
        };
        grip.DragCompleted += (_, _) => UpdateCanvasSize();
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
                _draggedKey = panel.Key;
                _dropCommitted = false;
                _dragOffset = start - tile.TranslatePoint(new Point(), this);
                _dropPreview = new Border
                {
                    Width = tile.Width,
                    Height = tile.Height,
                    BorderThickness = new Thickness(2),
                    CornerRadius = new CornerRadius(6),
                    VerticalAlignment = VerticalAlignment.Top,
                    IsHitTestVisible = false,
                    Child = new TextBlock
                    {
                        Text = $"Drop {panel.Caption} here",
                        TextWrapping = TextWrapping.Wrap,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(10),
                        FontWeight = FontWeights.SemiBold,
                    },
                };
                _dropPreview.SetResourceReference(Border.BorderBrushProperty, "Pumpkin");
                _dropPreview.SetResourceReference(Border.BackgroundProperty, "NightRaised");
                _dropPreview.SetResourceReference(TextElement.ForegroundProperty, "Pumpkin");
                Canvas.SetLeft(_dropPreview, Canvas.GetLeft(tile));
                Canvas.SetTop(_dropPreview, Canvas.GetTop(tile));
                BringToFront(tile);
                Panel.SetZIndex(_dropPreview, _byKey.Count + 1);
                _tiles.Children.Add(_dropPreview);
                tile.Opacity = 0.3;
                try
                {
                    DragDrop.DoDragDrop(tile, new DataObject(PanelDragFormat, panel.Key), DragDropEffects.Move);
                }
                finally
                {
                    FinishPanelDrag(tile, placement);
                }
            }
        };

        _byKey[panel.Key] = tile;
        _placements[panel.Key] = placement;
        _tiles.Children.Add(tile);
        BringToFront(tile);
        UpdateEmpty();
        UpdateCanvasSize();
    }

    public void RemovePanel(string key)
    {
        if (_byKey.Remove(key, out var tile))
        {
            _placements.Remove(key);
            tile.Children.Clear();
            _tiles.Children.Remove(tile);
            UpdateEmpty();
            UpdateCanvasSize();
        }
    }

    private void BringToFront(Grid tile)
    {
        int index = 0;
        foreach (var other in _byKey.Values.Where(t => t != tile).OrderBy(Panel.GetZIndex))
        {
            Panel.SetZIndex(other, index++);
        }

        Panel.SetZIndex(tile, index);
    }

    private void CloseResizeGaps(Grid resized, double previousWidth, double previousHeight)
    {
        var original = _byKey.Values.ToDictionary(t => t,
            t => new Rect(Canvas.GetLeft(t), Canvas.GetTop(t), t.Width, t.Height));
        original[resized] = new Rect(Canvas.GetLeft(resized), Canvas.GetTop(resized),
            previousWidth, previousHeight);
        PullNeighbors(horizontal: true, resized.Width - previousWidth);
        PullNeighbors(horizontal: false, resized.Height - previousHeight);

        void PullNeighbors(bool horizontal, double change)
        {
            if (change >= 0)
            {
                return;
            }

            var connected = new HashSet<Grid> { resized };
            var queue = new Queue<Grid>();
            queue.Enqueue(resized);
            while (queue.TryDequeue(out var current))
            {
                var source = original[current];
                foreach (var (candidate, bounds) in original)
                {
                    if (connected.Contains(candidate))
                    {
                        continue;
                    }

                    double gap = horizontal ? bounds.Left - source.Right : bounds.Top - source.Bottom;
                    bool sharesSpan = horizontal
                        ? bounds.Top < source.Bottom && bounds.Bottom > source.Top
                        : bounds.Left < source.Right && bounds.Right > source.Left;
                    if (gap >= -0.5 && gap <= 8.5 && sharesSpan)
                    {
                        connected.Add(candidate);
                        queue.Enqueue(candidate);
                    }
                }
            }

            foreach (var neighbor in connected.Where(t => t != resized))
            {
                var placement = _placements[(string)neighbor.Tag];
                if (horizontal)
                {
                    double left = Math.Max(4, Canvas.GetLeft(neighbor) + change);
                    Canvas.SetLeft(neighbor, left);
                    placement.DockLeft = left;
                }
                else
                {
                    double top = Math.Max(4, Canvas.GetTop(neighbor) + change);
                    Canvas.SetTop(neighbor, top);
                    placement.DockTop = top;
                }
            }
        }
    }

    private void ResolveResizeOverlaps(Grid resized)
    {
        const double gap = 8;
        Rect Bounds(Grid tile)
        {
            return new Rect(Canvas.GetLeft(tile), Canvas.GetTop(tile), tile.Width, tile.Height);
        }

        bool Overlaps(Rect first, Rect second)
        {
            return first.Left < second.Right + gap && first.Right + gap > second.Left &&
                first.Top < second.Bottom + gap && first.Bottom + gap > second.Top;
        }

        var anchor = Bounds(resized);
        var placed = new List<Rect> { anchor };
        // Keep unaffected neighbors in place before finding space for displaced panels.
        var others = _byKey.Values.Where(t => t != resized)
            .OrderBy(t => Overlaps(Bounds(t), anchor)).ToArray();
        foreach (var tile in others)
        {
            var original = Bounds(tile);
            if (!placed.Any(r => Overlaps(original, r)))
            {
                placed.Add(original);
                continue;
            }

            var xs = placed.SelectMany(r => new[] { r.Left - tile.Width - gap, r.Right + gap })
                .Append(original.Left).Append(4).Where(x => x >= 4).Distinct().ToArray();
            var ys = placed.SelectMany(r => new[] { r.Top - tile.Height - gap, r.Bottom + gap })
                .Append(original.Top).Append(4).Where(y => y >= 4).Distinct().ToArray();
            var destination = (from x in xs
                               from y in ys
                               let candidate = new Rect(x, y, tile.Width, tile.Height)
                               where !placed.Any(r => Overlaps(candidate, r))
                               orderby (x - original.Left) * (x - original.Left) +
                                   (y - original.Top) * (y - original.Top), y, x
                               select candidate).First();
            Canvas.SetLeft(tile, destination.Left);
            Canvas.SetTop(tile, destination.Top);
            var placement = _placements[(string)tile.Tag];
            placement.DockLeft = destination.Left;
            placement.DockTop = destination.Top;
            placed.Add(destination);
        }
    }

    private Point FindEmptyPosition(double width, double height)
    {
        var bounds = _byKey.Values.Select(t =>
            new Rect(Canvas.GetLeft(t), Canvas.GetTop(t), t.Width + 8, t.Height + 8)).ToArray();
        double available = _tilesArea.ViewportWidth > 0 ? _tilesArea.ViewportWidth :
            Math.Max(MinTileWidth, Width - (Settings.SidePanelOpen ? _sideColumn.Width.Value : 0) - 40);
        var xs = bounds.Select(r => r.Right).Append(4).Distinct().Order().ToArray();
        var ys = bounds.Select(r => r.Bottom).Append(4).Distinct().Order();
        foreach (double y in ys)
        {
            foreach (double x in xs)
            {
                if (x > 4 && x + width + 4 > available)
                {
                    continue;
                }

                var candidate = new Rect(x, y, width + 8, height + 8);
                if (!bounds.Any(r => r.Left < candidate.Right && r.Right > candidate.Left &&
                    r.Top < candidate.Bottom && r.Bottom > candidate.Top))
                {
                    return new Point(x, y);
                }
            }
        }
        return new Point(4, bounds.Length == 0 ? 4 : bounds.Max(r => r.Bottom) + 1);
    }

    private void UpdateDropPreview(Point pointer)
    {
        if (_dropPreview is null)
        {
            return;
        }

        Canvas.SetLeft(_dropPreview, Math.Max(4, pointer.X - _dragOffset.X));
        Canvas.SetTop(_dropPreview, Math.Max(4, pointer.Y - _dragOffset.Y));
        UpdateCanvasSize();
    }

    private void FinishPanelDrag(Grid tile, OverlayPlacement placement)
    {
        if (_dropPreview is null)
        {
            return;
        }

        if (_dropCommitted)
        {
            placement.DockLeft = Canvas.GetLeft(_dropPreview);
            placement.DockTop = Canvas.GetTop(_dropPreview);
            Canvas.SetLeft(tile, placement.DockLeft.Value);
            Canvas.SetTop(tile, placement.DockTop.Value);
        }
        _tiles.Children.Remove(_dropPreview);
        tile.Opacity = 1;
        _dropPreview = null;
        _draggedKey = null;
        UpdateCanvasSize();
    }

    private void UpdateCanvasSize()
    {
        var children = _tiles.Children.OfType<FrameworkElement>().ToArray();
        double width = children.Select(t => Canvas.GetLeft(t) + t.Width + 12).DefaultIfEmpty(0).Max();
        double height = children.Select(t => Canvas.GetTop(t) + t.Height + 12).DefaultIfEmpty(0).Max();
        _tiles.Width = Math.Max(Math.Max(0, _tilesArea.ActualWidth - 20), width);
        _tiles.Height = Math.Max(Math.Max(0, _tilesArea.ActualHeight - 20), height);
    }

    private void UpdateEmpty()
    {
        _empty.Visibility = _byKey.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

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
