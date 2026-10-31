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
/// user-selected dock workspaces on a scrollable freeform canvas on the right.
/// Drag a tile's header grip to move it; drag its bottom-right corner to resize it. Closing this window exits the app.
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
    private readonly Dictionary<string, DockPanelSettings> _placements = [];
    private readonly HashSet<OverlayPanel> _workspaceMoveHandlers = [];
    private readonly HashSet<(string WorkspaceId, string PanelKey)> _appendOnAdd = [];
    private readonly TextBlock _empty;
    private readonly Border _side = new() { Padding = new Thickness(0) };
    private readonly ColumnDefinition _sideColumn = new() { MinWidth = MinSideWidth };
    private readonly GridSplitter _splitter;
    private readonly ToggleButton _sideToggle;
    private readonly StackPanel _workspaceTabs = new() { Orientation = Orientation.Horizontal };
    private readonly Button _restoreWorkspaceButton = new() { Content = "Restore", Margin = new Thickness(4, 0, 0, 4) };
    private Point? _dragStart;
    private const string PanelDragFormat = "EqLogOverlay.DockPanel";
    private Border? _dropPreview;
    private string? _draggedKey;
    private Dictionary<Grid, Rect>? _dragOrigins;
    private Point? _lastPreviewTarget;
    private bool _dropCommitted;
    private Vector _dragOffset;
    private string? _anchorKey;
    private bool _splitterDragging;
    private WhatsNewWindow? _whatsNew;
    private readonly ContentControl _notice = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 10, 0) };

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
        UsageTracking.SetAction(_sideToggle, UsageAction.ToggleSettings);

        var autoArrange = new CheckBox
        {
            Content = "Auto-arrange",
            IsChecked = settings.AutoArrange,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "Automatically rearrange docked panels to fill empty space. Untick so panels only move when you drag or resize them.",
        };
        autoArrange.SetResourceReference(ForegroundProperty, "NightText");
        UsageTracking.SetAction(autoArrange, UsageAction.ToggleAutoArrange);

        var whatsNew = new Button
        {
            Name = "WhatsNewButton",
            Content = "What's New",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "Read what's new and how to use this release's features",
        };
        whatsNew.Click += (_, _) =>
        {
            ShowWhatsNew();
        };

        var bar = new DockPanel { Margin = new Thickness(8, 6, 8, 2) };
        DockPanel.SetDock(_sideToggle, Dock.Left);
        DockPanel.SetDock(autoArrange, Dock.Right);
        DockPanel.SetDock(_notice, Dock.Left);
        DockPanel.SetDock(whatsNew, Dock.Right);
        bar.Children.Add(_sideToggle);
        bar.Children.Add(_notice);
        bar.Children.Add(autoArrange);
        bar.Children.Add(whatsNew);
        var dragHint = new TextBlock
        {
            Text = DragHintText(settings.AutoArrange),
            VerticalAlignment = VerticalAlignment.Center,
        };
        dragHint.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        bar.Children.Add(dragHint);
        autoArrange.Click += (_, _) =>
        {
            Settings.AutoArrange = autoArrange.IsChecked == true;
            dragHint.Text = DragHintText(Settings.AutoArrange);
            if (Settings.AutoArrange)
            {
                // Re-pack the current layout now that automatic rearranging is back on.
                _anchorKey = null;
                CompactLayout();
            }
        };

        _empty = new TextBlock
        {
            Text = "No panels are in this workspace. Tick \"docked\" next to an overlay in Settings, or click \u2199 Dock on a floating overlay (while unlocked).",
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
        tilesArea.SizeChanged += (_, e) =>
        {
            // The settings splitter only resizes the viewport; panels keep their places and scroll instead.
            if (e.WidthChanged && _dropPreview is null && !_splitterDragging)
            {
                CompactLayout();
            }
            UpdateCanvasSize();
        };
        tilesArea.PreviewDragOver += (_, e) =>
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
        tilesArea.PreviewDrop += (_, e) =>
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
        _splitter.DragStarted += (_, _) =>
        {
            _splitterDragging = true;
        };
        _splitter.DragCompleted += (_, _) =>
        {
            // Layout passes for the final width run before this flag is cleared.
            Dispatcher.BeginInvoke(() =>
            {
                _splitterDragging = false;
            }, System.Windows.Threading.DispatcherPriority.Background);
        };

        var workspaceScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _workspaceTabs,
        };
        var workspaceActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4, 0, 8, 0),
        };
        var addWorkspace = new Button
        {
            Content = "+",
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(4, 0, 0, 4),
            ToolTip = "Create a workspace",
        };
        addWorkspace.Click += (_, _) => CreateWorkspace();
        _restoreWorkspaceButton.Click += (_, _) => OpenRestoreWorkspaceMenu();
        workspaceActions.Children.Add(addWorkspace);
        workspaceActions.Children.Add(_restoreWorkspaceButton);
        var workspaceBar = new DockPanel { Margin = new Thickness(8, 4, 8, 0) };
        DockPanel.SetDock(workspaceActions, Dock.Right);
        workspaceBar.Children.Add(workspaceActions);
        workspaceBar.Children.Add(workspaceScroller);
        RefreshWorkspaceTabs();

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
        DockPanel.SetDock(workspaceBar, Dock.Top);
        root.Children.Add(workspaceBar);
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

    public event EventHandler? WorkspaceChanged;

    public DockWindowSettings Settings
    {
        get;
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
        _workspaceTabs.Children.Clear();
        foreach (var workspace in Settings.Workspaces.Where(workspace => !workspace.Deleted))
        {
            bool selected = workspace.Id == Settings.ActiveWorkspaceId;
            var tab = new Button
            {
                Content = workspace.Name,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 4, 4),
                BorderThickness = new Thickness(1),
                ToolTip = "Select workspace. Right-click for rename or delete.",
                Tag = workspace.Id,
            };
            tab.SetResourceReference(Control.ForegroundProperty, "NightText");
            tab.SetResourceReference(Control.BackgroundProperty, selected ? "NightRaised" : "NightBackground");
            tab.SetResourceReference(Control.BorderBrushProperty, selected ? "Pumpkin" : "NightBorder");
            tab.Click += (_, _) => SelectWorkspace(workspace.Id);

            var menu = new ContextMenu();
            var rename = new MenuItem { Header = "Rename" };
            rename.Click += (_, _) => RenameWorkspace(workspace);
            var delete = new MenuItem { Header = "Delete" };
            delete.Click += (_, _) => DeleteWorkspace(workspace);
            menu.Items.Add(rename);
            menu.Items.Add(delete);
            tab.ContextMenu = menu;
            _workspaceTabs.Children.Add(tab);
        }

        _restoreWorkspaceButton.IsEnabled = Settings.Workspaces.Any(workspace => workspace.Deleted);
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
        string? name = PromptWorkspaceName("Create workspace", "New workspace");
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
        string? name = PromptWorkspaceName("Rename workspace", workspace.Name);
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

    private void OpenRestoreWorkspaceMenu()
    {
        var menu = new ContextMenu();
        foreach (var workspace in Settings.Workspaces.Where(workspace => workspace.Deleted))
        {
            var restore = new MenuItem { Header = workspace.Name };
            restore.Click += (_, _) =>
            {
                if (Settings.RestoreWorkspace(workspace.Id))
                {
                    RefreshWorkspaceTabs();
                    WorkspaceChanged?.Invoke(this, EventArgs.Empty);
                }
            };
            menu.Items.Add(restore);
        }

        if (menu.Items.Count == 0)
        {
            return;
        }

        _restoreWorkspaceButton.ContextMenu = menu;
        menu.PlacementTarget = _restoreWorkspaceButton;
        menu.IsOpen = true;
    }

    private string? PromptWorkspaceName(string title, string initialName)
    {
        var nameBox = new TextBox
        {
            Text = initialName,
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(4, 3, 4, 3),
            MinWidth = 300,
        };
        var save = CreateDialogButton("Save");
        save.IsDefault = true;
        var cancel = CreateDialogButton("Cancel");
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(8, 0, 0, 0);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Children =
            {
                save,
                cancel,
            },
        };
        var dialog = new Window
        {
            Title = title,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = "Workspace name" },
                    nameBox,
                    buttons,
                },
            },
        };
        dialog.SetResourceReference(BackgroundProperty, "NightBackground");
        dialog.SetResourceReference(ForegroundProperty, "NightText");
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                MessageBox.Show(dialog, "Enter a workspace name.", title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            dialog.DialogResult = true;
        };

        dialog.Loaded += (_, _) =>
        {
            nameBox.Focus();
            nameBox.SelectAll();
        };
        return dialog.ShowDialog() == true ? nameBox.Text.Trim() : null;
    }

    private static Button CreateDialogButton(string text)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(0),
            Width = 88,
            Height = 30,
            Padding = new Thickness(14, 0, 14, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1),
        };
        button.SetResourceReference(BackgroundProperty, "NightRaised");
        button.SetResourceReference(BorderBrushProperty, "NightBorder");
        button.SetResourceReference(ForegroundProperty, "NightText");
        return button;
    }

    /// <summary>Shows a notice (such as an available update) in the top bar, or hides it when null.</summary>
    public void SetNotice(UIElement? notice)
    {
        _notice.Content = notice;
        _notice.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
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
        foreach (var (key, tile) in _byKey)
        {
            if (tile.Children.OfType<OverlayPanel>().FirstOrDefault() is { } panel)
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

        var workspace = Settings.ActiveWorkspace;
        bool appendRequested = _appendOnAdd.Remove((workspace.Id, panel.Key));
        bool append = appendRequested && Settings.AutoArrange;
        if (!workspace.Panels.TryGetValue(panel.Key, out var dockPlacement))
        {
            dockPlacement = DockPanelSettings.From(placement);
            workspace.Panels[panel.Key] = dockPlacement;
        }

        panel.SetDocked(true);
        if (_workspaceMoveHandlers.Add(panel))
        {
            panel.WorkspaceMoveRequested += (_, workspaceId) =>
            {
                MovePanelToWorkspace(panel.Key, workspaceId);
            };
        }

        SetWorkspaceMenu(panel);
        var tile = new Grid
        {
            Width = Math.Max(MinTileWidth, dockPlacement.DockWidth > 0 ? dockPlacement.DockWidth : placement.Width),
            Height = Math.Max(MinTileHeight, dockPlacement.DockHeight > 0 ? dockPlacement.DockHeight : placement.Height),
            VerticalAlignment = VerticalAlignment.Top,
            Tag = panel.Key,
            AllowDrop = true,
        };
        var position = RedockPosition(dockPlacement, appendRequested, Settings.AutoArrange,
            _byKey.Values.Select(other => new Rect(Canvas.GetLeft(other), Canvas.GetTop(other), other.Width, other.Height)))
            ?? FindEmptyPosition(tile.Width, tile.Height);
        Canvas.SetLeft(tile, position.X);
        Canvas.SetTop(tile, position.Y);
        dockPlacement.DockLeft = position.X;
        dockPlacement.DockTop = position.Y;
        tile.Children.Add(panel);

        var grip = MakeGrip();
        grip.DragDelta += (_, e) =>
        {
            tile.Width = Math.Max(MinTileWidth, tile.Width + e.HorizontalChange);
            tile.Height = Math.Max(MinTileHeight, tile.Height + e.VerticalChange);
            dockPlacement.DockWidth = tile.Width;
            dockPlacement.DockHeight = tile.Height;
            _anchorKey = panel.Key;
            CompactLayout();
            ScrollIntoView(tile);
        };
        grip.DragCompleted += (_, _) => CompactLayout();
        tile.Children.Add(grip);

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
        _tiles.Children.Add(tile);
        if (append)
        {
            _anchorKey = panel.Key;
        }
        BringToFront(tile);
        UpdateEmpty();
        CompactLayout();
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
        panel.SetWorkspaceOptions(
            Settings.Workspaces
                .Where(workspace => !workspace.Deleted)
                .Select(workspace => (workspace.Id, workspace.Name)),
            Settings.ActiveWorkspaceId ?? "");
    }

    public void RemovePanel(string key)
    {
        if (_byKey.Remove(key, out var tile))
        {
            _placements.Remove(key);
            tile.Children.Clear();
            _tiles.Children.Remove(tile);
            if (_anchorKey == key)
            {
                _anchorKey = null;
            }
            UpdateEmpty();
            CompactLayout();
        }
    }

    public void ClearPanelsForWorkspaceChange()
    {
        PersistPositions();
        foreach (var tile in _byKey.Values)
        {
            tile.Children.Clear();
        }

        _byKey.Clear();
        _placements.Clear();
        _tiles.Children.Clear();
        _anchorKey = null;
        UpdateEmpty();
        UpdateCanvasSize();
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

    private const double TileGap = 8;
    private const double CanvasMargin = 4;

    private double AvailableWidth()
    {
        return _tilesArea.ViewportWidth > 0 ? _tilesArea.ViewportWidth :
            Math.Max(MinTileWidth, Width - (Settings.SidePanelOpen ? _sideColumn.Width.Value : 0) - 40);
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

        var bounds = _byKey.Values.ToDictionary(t => t,
            t => new Rect(Canvas.GetLeft(t), Canvas.GetTop(t), t.Width, t.Height));
        ApplyLayout(bounds, _anchorKey, persist: true);
    }

    /// <summary>Saves every tile's current canvas position into its placement without moving anything.</summary>
    private void PersistPositions()
    {
        foreach (var tile in _byKey.Values)
        {
            var placement = _placements[(string)tile.Tag];
            placement.DockLeft = Canvas.GetLeft(tile);
            placement.DockTop = Canvas.GetTop(tile);
        }
    }

    /// <summary>Packs <paramref name="bounds"/> around the anchor and moves the tiles; returns the anchor's final rect.</summary>
    private Rect? ApplyLayout(Dictionary<Grid, Rect> bounds, string? anchorKey, bool persist)
    {
        (string, Rect)? anchor = null;
        if (anchorKey is not null && _byKey.TryGetValue(anchorKey, out var pinned))
        {
            var rect = bounds[pinned];
            rect.X = Math.Max(CanvasMargin, rect.X);
            rect.Y = Math.Max(CanvasMargin, rect.Y);
            bounds[pinned] = rect;
            anchor = (anchorKey, rect);
        }

        var order = bounds.Keys
            .OrderBy(t => bounds[t].Top)
            .ThenBy(t => bounds[t].Left)
            .ThenBy(t => (string)t.Tag, StringComparer.Ordinal)
            .Select(t => ((string)t.Tag, bounds[t]))
            .ToList();
        var placed = Pack(order, anchor);

        foreach (var tile in _byKey.Values)
        {
            var rect = placed[(string)tile.Tag];
            if (Canvas.GetLeft(tile) != rect.Left || Canvas.GetTop(tile) != rect.Top)
            {
                Canvas.SetLeft(tile, rect.Left);
                Canvas.SetTop(tile, rect.Top);
            }
            if (persist)
            {
                var placement = _placements[(string)tile.Tag];
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
    private Point FindEmptyPosition(double width, double height)
    {
        var bounds = _byKey.Values.Select(t =>
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
        if (_dropPreview is null)
        {
            return;
        }

        var target = new Point(Math.Max(CanvasMargin, pointer.X - _dragOffset.X),
            Math.Max(CanvasMargin, pointer.Y - _dragOffset.Y));
        if (!Settings.AutoArrange || _draggedKey is null || _dragOrigins is null || !_byKey.TryGetValue(_draggedKey, out var dragged))
        {
            // Manual mode (or no reflow data): the panel lands exactly where the user drops it.
            Canvas.SetLeft(_dropPreview, target.X);
            Canvas.SetTop(_dropPreview, target.Y);
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
        Canvas.SetLeft(_dropPreview, landed.Left);
        Canvas.SetTop(_dropPreview, landed.Top);
        UpdateCanvasSize();
    }

    private void FinishPanelDrag(Grid tile)
    {
        if (_dropPreview is null)
        {
            return;
        }

        if (_dropCommitted)
        {
            Canvas.SetLeft(tile, Canvas.GetLeft(_dropPreview));
            Canvas.SetTop(tile, Canvas.GetTop(_dropPreview));
            _anchorKey = (string)tile.Tag;
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
        _tiles.Children.Remove(_dropPreview);
        tile.Opacity = 1;
        _dropPreview = null;
        _draggedKey = null;
        _dragOrigins = null;
        _lastPreviewTarget = null;
        CompactLayout();
    }

    /// <summary>Scrolls so a growing panel's bottom-right grip stays in view (the canvas extends instead of shifting panels).</summary>
    private void ScrollIntoView(Grid tile)
    {
        double right = Canvas.GetLeft(tile) + tile.Width + CanvasMargin;
        double bottom = Canvas.GetTop(tile) + tile.Height + CanvasMargin;
        if (right > _tilesArea.HorizontalOffset + _tilesArea.ViewportWidth)
        {
            _tilesArea.ScrollToHorizontalOffset(right - _tilesArea.ViewportWidth);
        }
        if (bottom > _tilesArea.VerticalOffset + _tilesArea.ViewportHeight)
        {
            _tilesArea.ScrollToVerticalOffset(bottom - _tilesArea.ViewportHeight);
        }
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

    private static string DragHintText(bool autoArrange)
    {
        return autoArrange
            ? "Drag header grips to reorder panels; panels reflow to fill empty space."
            : "Drag header grips to move panels; panels stay exactly where you put them.";
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
