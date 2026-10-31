using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using EqLog.Core;

namespace EqLog.Overlay;

/// <summary>The inventory/quest and Ready to merge overlays share the current character's inventory export.</summary>
public partial class MainWindow
{
    private StackPanel? _mergeHost;
    private TextBlock? _mergeSummary;
    private TextBox? _mergeFilter;
    private OverlayPanel? _mergePanel;
    private IReadOnlyList<InventoryExportItem> _inventoryRows = [];
    private string? _inventoryRowsCharacter;

    private void ConfigureInventoryOverlay(OverlayPanel panel)
    {
        // The Loot quests controls (and their XAML event handlers) move here intact; the settings tab goes away.
        LootQuestsTab.Content = null;
        Tabs.Items.Remove(LootQuestsTab);
        panel.SetBody(LootQuestsContent);
    }

    private void ConfigureMergeOverlay(OverlayPanel panel)
    {
        _mergePanel = panel;
        _mergeFilter = new TextBox
        {
            ToolTip = "Filter by item name or location (case-insensitive). Clear to show all.",
            Margin = new Thickness(0, 0, 0, 6),
        };
        _mergeFilter.TextChanged += (_, _) => RefreshMergeView();
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

    /// <summary>Keeps the latest export rows so the merge view can show exact locations.</summary>
    private void SetInventoryRows(string character, IReadOnlyList<InventoryExportItem> items)
    {
        _inventoryRows = items;
        _inventoryRowsCharacter = character;
        _exaltationLocations = Exaltations.Locate(items).Concat(Exaltations.WikiDefaults(items)).ToArray();
        RefreshMergeView();
        RefreshMotePlan();
    }

    private void RefreshMergeView()
    {
        if (_mergeHost is null || _mergeSummary is null || _mergeFilter is null)
        {
            return;
        }

        _mergeHost.Children.Clear();
        bool current = _inventoryRowsCharacter is not null &&
            _inventoryRowsCharacter.Equals(_characterName, StringComparison.OrdinalIgnoreCase);
        if (!current || _inventoryRows.Count == 0)
        {
            _mergeSummary.Text = "Waiting for this character's inventory export. Run /outputfile inventory in game.";
            return;
        }

        var groups = InventoryExport.MergeGroups(_inventoryRows);
        string filter = _mergeFilter.Text.Trim();
        var shown = filter.Length == 0
            ? groups
            : groups.Where(g => g.BaseName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                g.Items.Any(i => i.Where.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
        _mergeSummary.Text = groups.Count == 0
            ? "Nothing to merge: no item has copies at different +N ranks."
            : $"{groups.Count:N0} item(s) have more than one copy at different ranks" +
                (shown.Count == groups.Count ? "." : $"; {shown.Count:N0} match the filter.") +
                " Plain copies count as +0. Two copies that were never upgraded are not listed, because they look like stackable items." +
                " Collections and installed augments are left out.";

        double fontSize = _mergePanel?.FontSize ?? 13;
        foreach (var group in shown)
        {
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
            _mergeHost.Children.Add(heading);

            var rows = group.Items
                .Select(i => (IReadOnlyList<string>)[i.Rank > 0 ? $"+{i.Rank}" : "+0", i.Count.ToString("N0"), i.Where])
                .ToArray();
            var table = OverlayPanel.BuildTable(new OverlayTable(["Rank", "Qty", "Location"], rows), fontSize);
            _mergeHost.Children.Add(table);
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
