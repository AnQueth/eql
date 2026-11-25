using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using EqLog.Overlay.ViewModels;
using EqLog.Overlay.Views;

namespace EqLog.Overlay;

/// <summary>
/// The app's main window (e.g. for a second monitor): settings in a collapsible side panel on the left and
/// user-selected dock workspaces on a scrollable freeform canvas on the right.
/// Drag a tile's header grip to move it; drag its bottom-right corner to resize it. Closing this window exits the app.
/// </summary>
public sealed partial class DockWindow : Window
{
    private const double MinTileWidth = 140;
    private const double MinTileHeight = 60;
    private const double DefaultSideWidth = 560;
    private const double MinSideWidth = 380;
    private const string PanelDragFormat = "EqLogOverlay.DockPanel";

    private readonly Dictionary<string, DockTile> _byKey = [];
    private readonly Dictionary<string, DockPanelSettings> _placements = [];
    private readonly HashSet<string> _temporaryPanels = [];
    private readonly HashSet<(string WorkspaceId, string PanelKey)> _appendOnAdd = [];
    private Point? _dragStart;
    private bool _previewing;
    private string? _draggedKey;
    private Dictionary<DockTile, Rect>? _dragOrigins;
    private Point? _lastPreviewTarget;
    private bool _dropCommitted;
    private Vector _dragOffset;
    private string? _anchorKey;
    private bool _splitterDragging;
    private WhatsNewWindow? _whatsNew;

    public DockWindow(DockWindowSettings settings)
    {
        Settings = settings;
        ViewModel = new DockWindowViewModel { AutoArrange = settings.AutoArrange };
        DataContext = ViewModel;
        InitializeComponent();
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

        RefreshWorkspaceTabs();
        SetSidePanelOpen(settings.SidePanelOpen);
        Loaded += (_, _) =>
        {
            if (Settings.Maximized)
            {
                WindowState = WindowState.Maximized;
            }
        };
    }

    public event EventHandler? WorkspaceChanged;

    public DockWindowSettings Settings
    {
        get;
    }

    /// <summary>State bound by the top bar and workspace tabs.</summary>
    public DockWindowViewModel ViewModel
    {
        get;
    }

    private void SideToggle_Click(object sender, RoutedEventArgs e)
    {
        SetSidePanelOpen(SideToggle.IsChecked == true);
    }

    private void AutoArrange_Click(object sender, RoutedEventArgs e)
    {
        Settings.AutoArrange = ViewModel.AutoArrange;
        if (Settings.AutoArrange)
        {
            // Re-pack the current layout now that automatic rearranging is back on.
            _anchorKey = null;
            CompactLayout();
        }
    }

    private void WhatsNew_Click(object sender, RoutedEventArgs e)
    {
        ShowWhatsNew();
    }

    private void TilesArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The settings splitter only resizes the viewport; panels keep their places and scroll instead.
        if (e.WidthChanged && !_previewing && !_splitterDragging)
        {
            CompactLayout();
        }
        UpdateCanvasSize();
    }

    private void TilesArea_PreviewDragOver(object sender, DragEventArgs e)
    {
        bool valid = e.Data.GetData(PanelDragFormat) is string key && key == _draggedKey;
        e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;
        if (valid)
        {
            UpdateDropPreview(e.GetPosition(Tiles));
            double y = e.GetPosition(TilesArea).Y;
            if (y < 35)
            {
                TilesArea.ScrollToVerticalOffset(TilesArea.VerticalOffset - 12);
            }
            else if (y > TilesArea.ActualHeight - 35)
            {
                TilesArea.ScrollToVerticalOffset(TilesArea.VerticalOffset + 12);
            }

            double x = e.GetPosition(TilesArea).X;
            if (x < 35)
            {
                TilesArea.ScrollToHorizontalOffset(TilesArea.HorizontalOffset - 12);
            }
            else if (x > TilesArea.ActualWidth - 35)
            {
                TilesArea.ScrollToHorizontalOffset(TilesArea.HorizontalOffset + 12);
            }
        }
        e.Handled = true;
    }

    private void TilesArea_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(PanelDragFormat) is string source && source == _draggedKey)
        {
            UpdateDropPreview(e.GetPosition(Tiles));
            _dropCommitted = true;
            e.Handled = true;
        }
    }

    private void Splitter_DragStarted(object sender, DragStartedEventArgs e)
    {
        _splitterDragging = true;
    }

    private void Splitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        // Layout passes for the final width run before this flag is cleared.
        Dispatcher.BeginInvoke(() =>
        {
            _splitterDragging = false;
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ShowWhatsNew()
    {
        UsageTelemetry.TrackFeature(UsageFeature.WhatsNewOpened);
        if (_whatsNew is null)
        {
            _whatsNew = new WhatsNewWindow { Owner = this };
            _whatsNew.Closed += (_, _) =>
            {
                _whatsNew = null;
            };
            _whatsNew.Show();
        }

        if (_whatsNew.WindowState == WindowState.Minimized)
        {
            _whatsNew.WindowState = WindowState.Normal;
        }

        _whatsNew.Activate();
    }

    private void RefreshWorkspaceTabs()
    {
        ViewModel.SetWorkspaces(Settings.Workspaces, Settings.ActiveWorkspaceId);
    }

    private DockWorkspaceSettings? WorkspaceFor(object sender)
    {
        return sender is FrameworkElement { DataContext: DockWorkspaceTab tab }
            ? Settings.Workspaces.FirstOrDefault(workspace => workspace.Id == tab.Id)
            : null;
    }

    private void WorkspaceTab_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceFor(sender) is { } workspace)
        {
            SelectWorkspace(workspace.Id);
        }
    }

    private void RenameWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceFor(sender) is { } workspace)
        {
            RenameWorkspace(workspace);
        }
    }

    private void DeleteWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceFor(sender) is { } workspace)
        {
            DeleteWorkspace(workspace);
        }
    }

    private void CreateWorkspace_Click(object sender, RoutedEventArgs e)
    {
        CreateWorkspace();
    }

    private void RestoreWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.DeletedWorkspaces.Count == 0 || RestoreWorkspaceButton.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = RestoreWorkspaceButton;
        menu.IsOpen = true;
    }

    private void RestoreWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceFor(sender) is { } workspace && Settings.RestoreWorkspace(workspace.Id))
        {
            RefreshWorkspaceTabs();
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SelectWorkspace(string id)
    {
        if (Settings.ActiveWorkspaceId == id)
        {
            return;
        }

        Settings.ActiveWorkspaceId = id;
        RefreshWorkspaceTabs();
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CreateWorkspace()
    {
        string? name = WorkspaceNameDialog.Prompt(this, "Create workspace", "New workspace");
        if (name is null)
        {
            return;
        }

        Settings.CreateWorkspace(name);
        RefreshWorkspaceTabs();
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RenameWorkspace(DockWorkspaceSettings workspace)
    {
        string? name = WorkspaceNameDialog.Prompt(this, "Rename workspace", workspace.Name);
        if (name is null || string.Equals(name, workspace.Name, StringComparison.Ordinal))
        {
            return;
        }

        workspace.Name = name;
        RefreshWorkspaceTabs();
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteWorkspace(DockWorkspaceSettings workspace)
    {
        if (Settings.Workspaces.Count(candidate => !candidate.Deleted) <= 1)
        {
            MessageBox.Show(this, "At least one workspace must remain.", "Delete workspace",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var choice = MessageBox.Show(this,
            $"Delete \"{workspace.Name}\"? Its panels and placements will be kept so you can restore this workspace later.",
            "Delete workspace", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (choice != MessageBoxResult.Yes || !Settings.DeleteWorkspace(workspace.Id))
        {
            return;
        }

        RefreshWorkspaceTabs();
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows a notice (such as an <see cref="UpdateNoticeViewModel"/>) in the top bar, or hides it when null.</summary>
    public void SetNotice(object? notice)
    {
        ViewModel.Notice = notice;
    }

    /// <summary>Shows the history loading notice bound to <paramref name="progress"/>, or hides it when null.</summary>
    internal void SetLoadingNotice(object? progress)
    {
        ViewModel.LoadingNotice = progress;
    }

    /// <summary>Puts the settings UI into the side panel.</summary>
    public void SetSidePanel(FrameworkElement content)
    {
        SidePanel.Child = content;
    }

    public void SetSidePanelOpen(bool open)
    {
        if (!open && ViewModel.IsSidePanelOpen && SideColumn.ActualWidth > 0)
        {
            Settings.SidePanelWidth = SideColumn.ActualWidth;
        }

        Settings.SidePanelOpen = open;
        ViewModel.IsSidePanelOpen = open;
        SideColumn.MinWidth = open ? MinSideWidth : 0;
        SideColumn.Width = open
            ? new GridLength(Settings.SidePanelWidth > 0 ? Settings.SidePanelWidth : DefaultSideWidth)
            : new GridLength(0);
    }
    public bool HasPanel(string key)
    {
        return _byKey.ContainsKey(key);
    }

    internal bool IsTemporaryPanel(string key)
    {
        return _temporaryPanels.Contains(key);
    }

    public bool IsPanelInActiveWorkspace(string key)
    {
        return Settings.ActiveWorkspace.Panels.ContainsKey(key);
    }

    public bool IsPanelInAnyWorkspace(string key)
    {
        return Settings.IsPanelInAnyWorkspace(key);
    }

    public void MovePanelToWorkspace(string key, string workspaceId)
    {
        var fallback = _placements.TryGetValue(key, out var currentPlacement)
            ? currentPlacement.Clone()
            : new DockPanelSettings();
        string? activeWorkspaceId = Settings.ActiveWorkspaceId;
        Settings.MovePanelToWorkspace(key, workspaceId, fallback);
        if (activeWorkspaceId != Settings.ActiveWorkspaceId)
        {
            RefreshWorkspaceTabs();
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RefreshWorkspaceMenus()
    {
        foreach (var tile in _byKey.Values)
        {
            if (tile.HostedPanel is { } panel)
            {
                SetWorkspaceMenu(panel);
            }
        }
    }

    public void SetPanelInActiveWorkspace(string key, bool docked, OverlayPlacement fallback, bool append = false)
    {
        var workspace = Settings.ActiveWorkspace;
        if (docked)
        {
            workspace.DockPanel(key, DockPanelSettings.From(fallback));
            if (append)
            {
                _appendOnAdd.Add((Settings.ActiveWorkspace.Id, key));
            }
        }
        else
        {
            workspace.UndockPanel(key);
            _appendOnAdd.Remove((Settings.ActiveWorkspace.Id, key));
        }
    }

    /// <summary>Brings a docked panel forward and scrolls it into view.</summary>
    public void RevealPanel(string key)
    {
        if (!_byKey.TryGetValue(key, out var tile))
        {
            return;
        }

        BringToFront(tile);
        Tiles.UpdateLayout();
        TilesArea.ScrollToHorizontalOffset(Canvas.GetLeft(tile));
        TilesArea.ScrollToVerticalOffset(Canvas.GetTop(tile));
    }

    public void AddPanel(OverlayPanel panel, OverlayPlacement placement)
    {
        AddPanel(panel, placement, temporary: false);
    }

    internal void AddTemporaryPanel(OverlayPanel panel, OverlayPlacement placement)
    {
        AddPanel(panel, placement, temporary: true);
    }

    private void AddPanel(OverlayPanel panel, OverlayPlacement placement, bool temporary)
    {
        if (_byKey.ContainsKey(panel.Key))
        {
            return;
        }

        var workspace = Settings.ActiveWorkspace;
        bool appendRequested = !temporary && _appendOnAdd.Remove((workspace.Id, panel.Key));
        bool append = appendRequested && Settings.AutoArrange;
        DockPanelSettings dockPlacement;
        if (temporary)
        {
            dockPlacement = DockPanelSettings.From(placement);
        }
        else if (workspace.Panels.TryGetValue(panel.Key, out var savedPlacement))
        {
            dockPlacement = savedPlacement;
        }
        else
        {
            dockPlacement = DockPanelSettings.From(placement);
            workspace.Panels[panel.Key] = dockPlacement;
        }

        panel.ViewModel.SetDocked(true, temporary);
        SetWorkspaceMenu(panel);
        var tile = new DockTile(panel)
        {
            Width = Math.Max(MinTileWidth, dockPlacement.DockWidth > 0 ? dockPlacement.DockWidth : placement.Width),
            Height = Math.Max(MinTileHeight, dockPlacement.DockHeight > 0 ? dockPlacement.DockHeight : placement.Height),
        };
        var position = temporary
            ? FindEmptyPosition(tile.Width, tile.Height)
            : RedockPosition(dockPlacement, appendRequested, Settings.AutoArrange,
                _byKey.Values.Select(other => new Rect(Canvas.GetLeft(other), Canvas.GetTop(other), other.Width, other.Height)))
                ?? FindEmptyPosition(tile.Width, tile.Height);
        Canvas.SetLeft(tile, position.X);
        Canvas.SetTop(tile, position.Y);
        dockPlacement.DockLeft = position.X;
        dockPlacement.DockTop = position.Y;

        tile.ResizeGrip.DragDelta += (_, e) =>
        {
            tile.Width = Math.Max(MinTileWidth, tile.Width + e.HorizontalChange);
            tile.Height = Math.Max(MinTileHeight, tile.Height + e.VerticalChange);
            dockPlacement.DockWidth = tile.Width;
            dockPlacement.DockHeight = tile.Height;
            _anchorKey = panel.Key;
            CompactLayout();
            ScrollIntoView(tile);
        };
        tile.ResizeGrip.DragCompleted += (_, _) =>
        {
            CompactLayout();
        };

        tile.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, e) =>
        {
            if (e.OriginalSource == panel.DragHandle)
            {
                _dragStart = Mouse.GetPosition(this);
                e.Handled = true;
            }
        }));
        tile.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, e) =>
        {
            if (e.OriginalSource == panel.DragHandle)
            {
                _dragStart = null;
                e.Handled = true;
            }
        }));
        tile.AddHandler(Thumb.DragDeltaEvent, new DragDeltaEventHandler((_, e) =>
        {
            if (e.OriginalSource != panel.DragHandle || _dragStart is not Point start)
            {
                return;
            }

            e.Handled = true;
            var delta = Mouse.GetPosition(this) - start;
            if (Math.Abs(delta.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(delta.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _dragStart = null;
                _draggedKey = panel.Key;
                _dropCommitted = false;
                _lastPreviewTarget = null;
                _dragOrigins = _byKey.Values.ToDictionary(t => t,
                    t => new Rect(Canvas.GetLeft(t), Canvas.GetTop(t), t.Width, t.Height));
                _dragOffset = start - tile.TranslatePoint(new Point(), this);
                DropPreview.Width = tile.Width;
                DropPreview.Height = tile.Height;
                DropPreviewText.Text = $"Drop {panel.Caption} here";
                Canvas.SetLeft(DropPreview, Canvas.GetLeft(tile));
                Canvas.SetTop(DropPreview, Canvas.GetTop(tile));
                BringToFront(tile);
                Panel.SetZIndex(DropPreview, _byKey.Count + 1);
                DropPreview.Visibility = Visibility.Visible;
                _previewing = true;
                tile.Opacity = 0.3;
                try
                {
                    panel.DragHandle.CancelDrag();
                    DragDrop.DoDragDrop(tile, new DataObject(PanelDragFormat, panel.Key), DragDropEffects.Move);
                }
                finally
                {
                    FinishPanelDrag(tile);
                }
            }
        }));

        _byKey[panel.Key] = tile;
        _placements[panel.Key] = dockPlacement;
        if (temporary)
        {
            _temporaryPanels.Add(panel.Key);
        }
        Tiles.Children.Add(tile);
        if (append)
        {
            _anchorKey = panel.Key;
        }
        BringToFront(tile);
        UpdateEmpty();
        if (temporary)
        {
            UpdateCanvasSize();
        }
        else
        {
            CompactLayout();
            RepositionTemporaryPanels();
        }
    }

    internal static Point? RedockPosition(DockPanelSettings placement, bool appendRequested, bool autoArrange,
        IEnumerable<Rect> bounds)
    {
        if (appendRequested && autoArrange)
        {
            return AppendPosition(bounds);
        }

        if (placement.DockLeft is double left && double.IsFinite(left) &&
            placement.DockTop is double top && double.IsFinite(top))
        {
            return new Point(Math.Max(CanvasMargin, left), Math.Max(CanvasMargin, top));
        }

        return null;
    }

    internal static Point AppendPosition(IEnumerable<Rect> bounds)
    {
        double bottom = bounds
            .Select(rect => rect.Bottom)
            .DefaultIfEmpty(CanvasMargin - TileGap)
            .Max();
        return new Point(CanvasMargin, bottom + TileGap);
    }

    private void SetWorkspaceMenu(OverlayPanel panel)
    {
        panel.ViewModel.SetWorkspaceOptions(
            Settings.Workspaces
                .Where(workspace => !workspace.Deleted)
                .Select(workspace => (workspace.Id, workspace.Name)),
            Settings.ActiveWorkspaceId ?? "");
    }

    public void RemovePanel(string key)
    {
        if (_byKey.Remove(key, out var tile))
        {
            bool temporary = _temporaryPanels.Remove(key);
            _placements.Remove(key);
            tile.ReleasePanel();
            Tiles.Children.Remove(tile);
            if (_anchorKey == key)
            {
                _anchorKey = null;
            }
            UpdateEmpty();
            if (temporary)
            {
                UpdateCanvasSize();
            }
            else
            {
                CompactLayout();
            }
        }
    }

    public void ClearPanelsForWorkspaceChange()
    {
        PersistPositions();
        foreach (var tile in _byKey.Values)
        {
            tile.ReleasePanel();
            Tiles.Children.Remove(tile);
        }

        _byKey.Clear();
        _placements.Clear();
        _temporaryPanels.Clear();
        _anchorKey = null;
        UpdateEmpty();
        UpdateCanvasSize();
    }

    private void BringToFront(DockTile tile)
    {
        int index = 0;
        foreach (var other in _byKey.Values.Where(t => t != tile).OrderBy(Panel.GetZIndex))
        {
            Panel.SetZIndex(other, index++);
        }

        Panel.SetZIndex(tile, index);
    }

    private const double TileGap = 8;
    private const double CanvasMargin = 4;

    private double AvailableWidth()
    {
        return TilesArea.ViewportWidth > 0 ? TilesArea.ViewportWidth :
            Math.Max(MinTileWidth, Width - (Settings.SidePanelOpen ? SideColumn.Width.Value : 0) - 40);
    }

    /// <summary>
    /// Reflows the docked panels with vertical "gravity": every panel keeps its column (left edge), panels the
    /// anchor (the panel most recently dropped or resized, which stays exactly where the user put it) would cover
    /// are pushed down below it, and every panel then floats straight up into any empty space above it.
    /// Panels that are not displaced and have nothing empty above them never move.
    /// </summary>
    private void CompactLayout()
    {
        if (_byKey.Count == 0)
        {
            return;
        }

        if (!Settings.AutoArrange)
        {
            // Manual mode: respect the user's placement; just remember it and keep the canvas sized.
            PersistPositions();
            UpdateCanvasSize();
            return;
        }

        var bounds = _byKey.Values.Where(t => !_temporaryPanels.Contains(t.Key)).ToDictionary(t => t,
            t => new Rect(Canvas.GetLeft(t), Canvas.GetTop(t), t.Width, t.Height));
        ApplyLayout(bounds, _anchorKey, persist: true);
        RepositionTemporaryPanels();
    }

    /// <summary>Saves every tile's current canvas position into its placement without moving anything.</summary>
    private void PersistPositions()
    {
        foreach (var tile in _byKey.Values)
        {
            var placement = _placements[tile.Key];
            placement.DockLeft = Canvas.GetLeft(tile);
            placement.DockTop = Canvas.GetTop(tile);
        }
    }

    /// <summary>Packs <paramref name="bounds"/> around the anchor and moves the tiles; returns the anchor's final rect.</summary>
    private Rect? ApplyLayout(Dictionary<DockTile, Rect> bounds, string? anchorKey, bool persist)
    {
        (string, Rect)? anchor = null;
        if (anchorKey is not null && _byKey.TryGetValue(anchorKey, out var pinned) && bounds.TryGetValue(pinned, out var pinnedRect))
        {
            var rect = pinnedRect;
            rect.X = Math.Max(CanvasMargin, rect.X);
            rect.Y = Math.Max(CanvasMargin, rect.Y);
            bounds[pinned] = rect;
            anchor = (anchorKey, rect);
        }

        var order = bounds.Keys
            .OrderBy(t => bounds[t].Top)
            .ThenBy(t => bounds[t].Left)
            .ThenBy(t => t.Key, StringComparer.Ordinal)
            .Select(t => (t.Key, bounds[t]))
            .ToList();
        var placed = Pack(order, anchor);

        foreach (var tile in bounds.Keys)
        {
            var rect = placed[tile.Key];
            if (Canvas.GetLeft(tile) != rect.Left || Canvas.GetTop(tile) != rect.Top)
            {
                Canvas.SetLeft(tile, rect.Left);
                Canvas.SetTop(tile, rect.Top);
            }
            if (persist)
            {
                var placement = _placements[tile.Key];
                placement.DockLeft = rect.Left;
                placement.DockTop = rect.Top;
            }
        }
        UpdateCanvasSize();
        return anchor?.Item2;
    }
    /// <summary>Gravity compaction; see <see cref="CompactLayout"/>. Panels never move sideways; wide layouts scroll.</summary>
    internal static Dictionary<string, Rect> Pack(IReadOnlyList<(string Key, Rect Bounds)> order,
        (string Key, Rect Bounds)? anchor)
    {
        static bool Overlaps(Rect first, Rect second)
        {
            return first.Left < second.Right + TileGap && first.Right + TileGap > second.Left &&
                first.Top < second.Bottom + TileGap && first.Bottom + TileGap > second.Top;
        }

        var result = new Dictionary<string, Rect>();
        var placed = new List<Rect>();
        if (anchor is { } pin)
        {
            result[pin.Key] = pin.Bounds;
            placed.Add(pin.Bounds);
        }

        foreach (var (key, bounds) in order)
        {
            if (result.ContainsKey(key))
            {
                continue;
            }

            var rect = bounds;
            rect.X = Math.Max(CanvasMargin, rect.X);
            rect.Y = Math.Max(CanvasMargin, rect.Y);

            // Float up until something in the same column (that started above) blocks it.
            var column = rect;
            rect.Y = placed
                .Where(r => r.Left < column.Right + TileGap && r.Right + TileGap > column.Left && r.Top <= column.Top)
                .Select(r => r.Bottom + TileGap)
                .Append(CanvasMargin)
                .Max();

            // Then push down past anything it still covers (e.g. the anchor dropped on top of it).
            bool moved = true;
            while (moved)
            {
                moved = false;
                foreach (var blocker in placed)
                {
                    if (Overlaps(rect, blocker))
                    {
                        rect.Y = blocker.Bottom + TileGap;
                        moved = true;
                    }
                }
            }

            result[key] = rect;
            placed.Add(rect);
        }
        return result;
    }
    private void RepositionTemporaryPanels()
    {
        var permanentBounds = _byKey
            .Where(pair => !_temporaryPanels.Contains(pair.Key))
            .Select(pair => new Rect(Canvas.GetLeft(pair.Value), Canvas.GetTop(pair.Value), pair.Value.Width, pair.Value.Height))
            .ToArray();
        foreach (string key in _temporaryPanels)
        {
            var tile = _byKey[key];
            var bounds = new Rect(Canvas.GetLeft(tile), Canvas.GetTop(tile), tile.Width, tile.Height);
            if (!permanentBounds.Any(bounds.IntersectsWith))
            {
                continue;
            }

            var position = FindEmptyPosition(tile.Width, tile.Height, tile);
            Canvas.SetLeft(tile, position.X);
            Canvas.SetTop(tile, position.Y);
            _placements[key].DockLeft = position.X;
            _placements[key].DockTop = position.Y;
        }

        UpdateCanvasSize();
    }

    private Point FindEmptyPosition(double width, double height, DockTile? ignoredTile = null)
    {
        var bounds = _byKey.Values.Where(t => t != ignoredTile).Select(t =>
            new Rect(Canvas.GetLeft(t), Canvas.GetTop(t), t.Width + 8, t.Height + 8)).ToArray();
        double available = AvailableWidth();
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
        if (!_previewing)
        {
            return;
        }

        var target = new Point(Math.Max(CanvasMargin, pointer.X - _dragOffset.X),
            Math.Max(CanvasMargin, pointer.Y - _dragOffset.Y));
        if (!Settings.AutoArrange || _draggedKey is null || _dragOrigins is null || !_byKey.TryGetValue(_draggedKey, out var dragged))
        {
            // Manual mode (or no reflow data): the panel lands exactly where the user drops it.
            Canvas.SetLeft(DropPreview, target.X);
            Canvas.SetTop(DropPreview, target.Y);
            UpdateCanvasSize();
            return;
        }
        if (_lastPreviewTarget == target)
        {
            return;
        }
        _lastPreviewTarget = target;

        // Live preview: reflow from the pre-drag layout as if the panel were dropped here.
        var bounds = _dragOrigins.ToDictionary(o => o.Key, o => o.Value);
        bounds[dragged] = new Rect(target, bounds[dragged].Size);
        var landed = ApplyLayout(bounds, _draggedKey, persist: false) ?? bounds[dragged];
        Canvas.SetLeft(DropPreview, landed.Left);
        Canvas.SetTop(DropPreview, landed.Top);
        UpdateCanvasSize();
    }

    private void FinishPanelDrag(DockTile tile)
    {
        if (!_previewing)
        {
            return;
        }

        if (_dropCommitted)
        {
            Canvas.SetLeft(tile, Canvas.GetLeft(DropPreview));
            Canvas.SetTop(tile, Canvas.GetTop(DropPreview));
            _anchorKey = tile.Key;
        }
        else if (_dragOrigins is not null)
        {
            // Canceled: put every panel the preview moved back where it was.
            foreach (var (other, rect) in _dragOrigins)
            {
                Canvas.SetLeft(other, rect.Left);
                Canvas.SetTop(other, rect.Top);
            }
        }
        DropPreview.Visibility = Visibility.Collapsed;
        tile.Opacity = 1;
        _previewing = false;
        _draggedKey = null;
        _dragOrigins = null;
        _lastPreviewTarget = null;
        CompactLayout();
    }

    /// <summary>Scrolls so a growing panel's bottom-right grip stays in view (the canvas extends instead of shifting panels).</summary>
    private void ScrollIntoView(DockTile tile)
    {
        double right = Canvas.GetLeft(tile) + tile.Width + CanvasMargin;
        double bottom = Canvas.GetTop(tile) + tile.Height + CanvasMargin;
        if (right > TilesArea.HorizontalOffset + TilesArea.ViewportWidth)
        {
            TilesArea.ScrollToHorizontalOffset(right - TilesArea.ViewportWidth);
        }
        if (bottom > TilesArea.VerticalOffset + TilesArea.ViewportHeight)
        {
            TilesArea.ScrollToVerticalOffset(bottom - TilesArea.ViewportHeight);
        }
    }

    private void UpdateCanvasSize()
    {
        var children = Tiles.Children.OfType<FrameworkElement>()
            .Where(child => child.Visibility == Visibility.Visible)
            .ToArray();
        double width = children.Select(t => Canvas.GetLeft(t) + t.Width + 12).DefaultIfEmpty(0).Max();
        double height = children.Select(t => Canvas.GetTop(t) + t.Height + 12).DefaultIfEmpty(0).Max();
        Tiles.Width = Math.Max(Math.Max(0, TilesArea.ActualWidth - 20), width);
        Tiles.Height = Math.Max(Math.Max(0, TilesArea.ActualHeight - 20), height);
    }

    private void UpdateEmpty()
    {
        ViewModel.IsWorkspaceEmpty = _byKey.Count == 0;
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
        if (Settings.SidePanelOpen && SideColumn.ActualWidth > 0)
        {
            Settings.SidePanelWidth = SideColumn.ActualWidth;
        }
    }
}
