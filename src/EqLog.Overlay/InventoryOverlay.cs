using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using EqLog.Core;

namespace EqLog.Overlay;

/// <summary>The "Inventory & loot quests" overlay: the former Loot quests settings tab plus a Ready to merge view.</summary>
public partial class MainWindow
{
    private const int InventoryItemsTab = 0;

    private TabControl? _inventoryTabs;
    private StackPanel? _mergeHost;
    private TextBlock? _mergeSummary;
    private TextBox? _mergeFilter;
    private OverlayPanel? _inventoryPanel;
    private IReadOnlyList<InventoryExportItem> _inventoryRows = [];
    private string? _inventoryRowsCharacter;

    private void ConfigureInventoryOverlay(OverlayPanel panel)
    {
        _inventoryPanel = panel;
        // The Loot quests controls (and their XAML event handlers) move here intact; the settings tab goes away.
        LootQuestsTab.Content = null;
        Tabs.Items.Remove(LootQuestsTab);

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

        _inventoryTabs = new TabControl
        {
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 4, 0, 0),
        };
        _inventoryTabs.Items.Add(new TabItem
        {
            Header = "Items & quests",
            Content = LootQuestsContent,
            ToolTip = "Owned counts, wiki item pages and quest references for every item you have or looted",
        });
        _inventoryTabs.Items.Add(new TabItem
        {
            Header = "Ready to merge",
            Content = mergeView,
            ToolTip = "Copies of the same item at different +N ranks, with where each one is",
        });
        panel.SetBody(_inventoryTabs);
        RefreshMergeView();
    }

    /// <summary>Keeps the latest export rows so the merge view can show exact locations.</summary>
    private void SetInventoryRows(string character, IReadOnlyList<InventoryExportItem> items)
    {
        _inventoryRows = items;
        _inventoryRowsCharacter = character;
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

        double fontSize = _inventoryPanel?.FontSize ?? 13;
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

    /// <summary>Shows the inventory overlay (docked or floating) on the given tab.</summary>
    private void ShowInventoryOverlay(int tab)
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
        if (_inventoryTabs is not null)
        {
            _inventoryTabs.SelectedIndex = tab;
        }
    }
}
