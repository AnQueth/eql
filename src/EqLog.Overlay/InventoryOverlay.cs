using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using EqLog.Application.Inventory;
using EqLog.Core;

namespace EqLog.Overlay;

/// <summary>The inventory/quest and Ready to merge overlays share the current character's inventory export.</summary>
public partial class MainWindow
{
    private StackPanel? _mergeHost;
    private TextBlock? _mergeSummary;
    private TextBox? _mergeFilter;
    private OverlayPanel? _mergePanel;
    private InventoryFilterWork? _mergeWork;
    private bool _mergeSuspended;
    private IReadOnlyList<InventoryExportItem> _inventoryRows
    {
        get
        {
            return _inventoryView.Export?.Rows ?? [];
        }
    }
    private string? _inventoryRowsCharacter
    {
        get
        {
            return _inventoryView.Export?.Character;
        }
    }
    private readonly Dictionary<string, string> _itemFilters = [];

    /// <summary>Adds an item-name filter above any existing options in an item overlay's toolbar.</summary>
    internal static TextBox ConfigureItemFilterToolbar(OverlayPanel panel, Action<string> filterChanged, UIElement? options = null)
    {
        var filter = new TextBox
        {
            Margin = new Thickness(0, 5, 0, 5),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Filter by item name (case-insensitive). Clear to show all. Unlock floating panels to type.",
        };
        System.Windows.Automation.AutomationProperties.SetName(filter, "Item name filter");
        TextFilterBinding.Bind(filter, filterChanged);
        var toolbar = new StackPanel { Children = { filter } };
        if (options is not null)
        {
            toolbar.Children.Add(options);
        }
        panel.SetToolbar(toolbar);
        return filter;
    }

    /// <summary>Filters only the displayed inventory rows, preserving ownership data and automatic lookups.</summary>
    internal static InventoryFilterWork ConfigureInventoryItemFilter(DataGrid grid, TextBox filter)
    {
        return InventoryFilterBinding.Attach(grid, filter);
    }

    private void ConfigureInventoryOverlay(OverlayPanel panel)
    {
        // The Loot quests controls (and their XAML event handlers) move here intact; the settings tab goes away.
        LootQuestsTab.Content = null;
        Tabs.Items.Remove(LootQuestsTab);
        panel.SetBody(LootQuestsContent);
        LootQuestsContent.Unloaded += (_, _) => _inventoryView.SuspendFilters();
        LootQuestsContent.Loaded += (_, _) => _inventoryView.ResumeFilters();
    }

    private void ConfigureMergeOverlay(OverlayPanel panel)
    {
        _mergePanel = panel;
        _mergeWork = new InventoryFilterWork(Dispatcher);
        _mergeFilter = new TextBox
        {
            ToolTip = "Filter by item name or location (case-insensitive). Clear to show all.",
            Margin = new Thickness(0, 0, 0, 6),
        };
        _mergeFilter.TextChanged += (_, _) => RefreshMergeView(true);
        _mergeSummary = new FixedLineTextBlock { Margin = new Thickness(0, 0, 0, 6) };
        _mergeSummary.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        _mergeHost = new StackPanel();
        var mergeTop = new StackPanel { Children = { _mergeFilter, _mergeSummary } };
        DockPanel.SetDock(mergeTop, Dock.Top);
        var mergeScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _mergeHost,
        };
        var mergeView = new DockPanel { Margin = new Thickness(2, 6, 2, 2) };
        mergeView.Children.Add(mergeTop);
        mergeView.Children.Add(mergeScroll);

        panel.SetBody(mergeView);
        mergeView.Unloaded += (_, _) =>
        {
            _mergeSuspended = true;
            _mergeWork.Cancel();
        };
        mergeView.Loaded += (_, _) =>
        {
            _mergeSuspended = false;
            RefreshMergeView();
        };
        RefreshMergeView();
    }

    /// <summary>Adds the split-out merge panel once, preserving existing inventory layouts and visibility.</summary>
    internal static void InitializeMergeOverlay(AppSettings settings, Rect work)
    {
        const string key = "merge";
        if (settings.Overlays.ContainsKey(key))
        {
            return;
        }

        var definition = OverlayPresenters.All.Single(def => def.Key == key);
        var placement = DefaultPlacement(definition, OverlayPresenters.All.Count - 1, work);
        if (settings.Overlays.TryGetValue("inventory", out var inventory))
        {
            var activeWorkspace = settings.DockWindow.EnsureActiveWorkspace(settings.Overlays);
            if (inventory.Docked && !settings.DockWindow.IsPanelInAnyWorkspace("inventory"))
            {
                activeWorkspace.Panels["inventory"] = DockPanelSettings.From(inventory);
            }
            placement = inventory.Clone();
            if (inventory.Left + inventory.Width + 8 + placement.Width <= work.Right)
            {
                placement.Left = inventory.Left + inventory.Width + 8;
            }
            else if (inventory.Top + inventory.Height + 8 + placement.Height <= work.Bottom)
            {
                placement.Top = inventory.Top + inventory.Height + 8;
            }
            else
            {
                placement.Left = work.Left + 40;
                placement.Top = work.Top + 40;
            }

            foreach (var workspace in settings.DockWindow.Workspaces)
            {
                if (!workspace.Panels.TryGetValue("inventory", out var inventoryTile) ||
                    workspace.Panels.ContainsKey(key))
                {
                    continue;
                }

                var bounds = workspace.Panels.Select(pair =>
                {
                    settings.Overlays.TryGetValue(pair.Key, out var floating);
                    var tile = pair.Value;
                    return new Rect(tile.DockLeft ?? 4, tile.DockTop ?? 4,
                        tile.DockWidth > 0 ? tile.DockWidth : floating?.Width ?? definition.Width,
                        tile.DockHeight > 0 ? tile.DockHeight : floating?.Height ?? definition.Height);
                });
                var position = DockWindow.AppendPosition(bounds);
                var mergeTile = inventoryTile.Clone();
                mergeTile.DockLeft = position.X;
                mergeTile.DockTop = position.Y;
                workspace.Panels[key] = mergeTile;
            }

            placement.Docked = settings.DockWindow.IsPanelInAnyWorkspace(key);
        }

        settings.Overlays[key] = placement;
    }

    /// <summary>Finds all ranks of an item while retaining container names from the complete export.</summary>
    internal static IReadOnlyList<InventoryItemLocation> ItemCopyLocations(
        IReadOnlyList<InventoryExportItem> items, string item)
    {
        return InventoryQueries.CopyLocations(items, item);
    }

    private void RefreshInventoryItemLocations()
    {
        _inventoryView.SelectedItem = QuestOwnedGrid.SelectedItem as QuestInventoryItem;
        _inventoryView.RefreshCopies();
    }

    private void RefreshMergeView()
    {
        RefreshMergeView(false);
    }

    /// <summary>Call on the dispatcher when the inventory/session lifetime ends.</summary>
    internal void CancelInventoryFilters()
    {
        Dispatcher.VerifyAccess();
        _inventoryView.SuspendFilters();
        _mergeSuspended = true;
        _mergeWork?.Cancel();
    }

    private void RefreshMergeView(bool debounce)
    {
        Dispatcher.VerifyAccess();
        if (_mergeHost is null || _mergeSummary is null || _mergeFilter is null || _mergeWork is null)
        {
            return;
        }
        _mergeWork.Cancel();
        if (_mergeSuspended)
        {
            return;
        }
        bool current = _inventoryRowsCharacter is not null &&
            _inventoryRowsCharacter.Equals(_characterName, StringComparison.OrdinalIgnoreCase);
        if (!current || _inventoryRows.Count == 0)
        {
            _mergeHost.Children.Clear();
            _mergeSummary.Text = "Waiting for this character's inventory export. Run /outputfile inventory in game.";
            return;
        }

        var rows = _inventoryRows.ToArray();
        string filter = _mergeFilter.Text;
        long revision = _inventoryView.Revision;
        string? character = _characterName;
        _mergeWork.Start(
            cancellation => InventoryFilterWork.QueryMerge(rows, filter, cancellation),
            async (result, cancellation) =>
            {
                if (revision != _inventoryView.Revision || character != _characterName)
                {
                    return;
                }
                await RenderMergeAsync(result, cancellation, revision, character);
            },
            error =>
            {
                _mergeSummary.Text = $"Merge filter failed: {error.Message}";
                UsageTelemetry.TrackError(error, UsageErrorSource.InventoryImport);
            },
            debounce);
    }

    private async Task RenderMergeAsync(
        InventoryMergeResult result, CancellationToken cancellation, long revision, string? character)
    {
        var host = _mergeHost!;
        _mergeSummary!.Text = result.Total == 0
            ? "Nothing to merge: no item has copies at different +N ranks."
            : $"{result.Total:N0} item(s) have more than one copy at different ranks" +
                (result.Shown.Count == result.Total ? "." : $"; {result.Shown.Count:N0} match the filter.") +
                " Plain copies count as +0. Two copies that were never upgraded are not listed, because they look like stackable items." +
                " Collections and installed augments are left out.";

        double fontSize = _mergePanel?.FontSize ?? 13;
        host.Children.Clear();
        foreach (var group in result.Shown)
        {
            cancellation.ThrowIfCancellationRequested();
            if (revision != _inventoryView.Revision || character != _characterName)
            {
                return;
            }
            var heading = new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 2),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "NightText");
            if (HasQuestReferences(group.BaseName))
            {
                var badge = new Hyperlink(new Run("[Quest]"))
                {
                    Tag = group.BaseName,
                    ToolTip = "Quest references available: click to view. Wiki mentions may include ingredients or rewards, not a turn-in checklist.",
                };
                badge.SetResourceReference(TextElement.ForegroundProperty, "Pumpkin");
                badge.Click += QuestBadge_Click;
                heading.Inlines.Add(badge);
                heading.Inlines.Add(new Run(" "));
            }
            var link = new Hyperlink(new Run(group.BaseName))
            {
                Tag = group.BaseName,
                ToolTip = "Open item on EQLWiki",
            };
            link.SetResourceReference(TextElement.ForegroundProperty, "Pumpkin");
            link.Click += ItemWikiLink_Click;
            heading.Inlines.Add(link);
            heading.Inlines.Add(new Run($"  ·  {group.Total:N0} copies"));
            host.Children.Add(heading);

            var rows = group.Rows;
            // Even a single item can have many exported locations. Bound each table
            // construction and yield between chunks, not just between item groups.
            for (int offset = 0; offset < rows.Count; offset += 32)
            {
                cancellation.ThrowIfCancellationRequested();
                if (revision != _inventoryView.Revision || character != _characterName)
                {
                    return;
                }
                var batch = rows.Skip(offset).Take(32).ToArray();
                var table = OverlayPanel.BuildTable(new OverlayTable(["Rank", "Qty", "Location"], batch), fontSize);
                host.Children.Add(table);
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }
    }

    /// <summary>Shows the inventory overlay (docked or floating) for item and quest references.</summary>
    private void ShowInventoryOverlay()
    {
        const string key = "inventory";
        if (!_settings.Overlays[key].Visible)
        {
            _overlayToggles[key].IsChecked = true;
            SetOverlayVisible(key, true);
        }
        if (_settings.Overlays[key].Docked)
        {
            ShowDock();
            _dock.RevealPanel(key);
        }
        else
        {
            EnsureOnScreen(_overlays[key]);
            _overlays[key].ReassertTopmost();
        }
    }
}
