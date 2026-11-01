using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;
using EqLog.Overlay;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class ItemFilterTests
{
    [Fact]
    public void ItemCopyLocations_ReturnsEveryRankAndStorageWithContainerContext()
    {
        InventoryExportItem[] rows =
        [
            new("General 1", "Spacious Rucksack", 1, 1, InventoryStorage.Carried),
            new("General 1-Slot4", "Sword +3", 2, 2, InventoryStorage.Carried),
            new("Bank10", "Storage Trunk", 3, 1, InventoryStorage.Bank),
            new("Bank10-Slot6", "SWORD +10", 2, 1, InventoryStorage.Bank),
            new("Wrist", "Sword +7", 2, 1, InventoryStorage.Carried, WornSlot: 9),
            new("Equipment", "Sword +9", 2, 1, InventoryStorage.Other, Collection: true),
            new("SharedBank1", "Sword", 2, 3, InventoryStorage.Bank),
            new("Hoard 12", "Sword +2", 2, 1, InventoryStorage.Hoard),
            new("Personal-Depot3", "Sword", 2, 4, InventoryStorage.Depot),
            new("Back", "Host Cloak", 4, 1, InventoryStorage.Carried, WornSlot: 8),
            new("Back-Slot7", "Sword +1", 2, 1, InventoryStorage.Carried, Installed: true, WornSlot: 8),
            new("Unknown", "Sword", 2, 1, InventoryStorage.Other),
            new("Held", "Sword (Exaltation)", 5, 1, InventoryStorage.Carried),
            new("Held", "Swordfish", 6, 1, InventoryStorage.Carried),
        ];

        var copies = MainWindow.ItemCopyLocations(rows, "sword +3");

        Assert.Equal(9, copies.Count);
        Assert.Equal(15, copies.Sum(copy => copy.Quantity));
        Assert.Equal([10, 9, 7, 3, 2, 1, 0, 0, 0], copies.Select(copy => copy.Rank));
        Assert.Contains(copies, copy => copy.Location == "Inventory slot 1 (Spacious Rucksack), bag slot 4" && copy.Quantity == 2);
        Assert.Contains(copies, copy => copy.Location == "Bank slot 10 (Storage Trunk), bag slot 6" && copy.Rank == 10);
        Assert.Contains(copies, copy => copy.Location == "Worn Wrist, equipment slot 9");
        Assert.Contains(copies, copy => copy.Location == "Collection (Equipment)" && copy.Kind == "Collection");
        Assert.Contains(copies, copy => copy.Location == "Worn Back, equipment slot 8, augment slot 7 of Host Cloak" && copy.Kind == "Installed");
        Assert.Contains(copies, copy => copy.Location == "Shared bank slot 1");
        Assert.Contains(copies, copy => copy.Location == "Dragon's Hoard slot 12");
        Assert.Contains(copies, copy => copy.Location == "Tradeskill depot slot 3");
        Assert.Contains(copies, copy => copy.Location == "Unknown");
        var exportedAt = new DateTime(2026, 11, 1, 15, 20, 0, DateTimeKind.Utc);
        var inventory = new QuestInventory();
        inventory.ApplyExport("Que", rows, exportedAt);
        inventory.NoteLoot("Que", "log", 1, "Sword", 8, true, exportedAt.AddMinutes(1));
        var item = Assert.Single(inventory.Items, entry => ItemNames.Comparer.Equals(entry.Item, "Sword"));
        Assert.Equal(13, item.Owned);
        Assert.Equal(copies.Sum(copy => copy.Quantity), item.Owned + item.Collection + item.Installed);
        Assert.Empty(MainWindow.ItemCopyLocations(rows, "Missing"));
        Assert.Empty(MainWindow.ItemCopyLocations([], "Sword"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Loot_FilterMatchesOnlyItemNamesInEveryGroupingAndKeepsTotals(int grouping)
    {
        var now = new DateTime(2026, 10, 21, 18, 0, 0);
        var tracker = new LootTracker();
        tracker.OnLoot(new(now, Names.You, "Sword +2", 2, null, null, false));
        tracker.OnLoot(new(now.AddSeconds(1), "SwordCollector", "Shield", 1, null, null, false));
        tracker.OnLoot(new(now.AddSeconds(2), "Other", "SWORD +3", 3, null, null, true));
        for (int i = 0; i < 15; i++)
        {
            tracker.OnLoot(new(now.AddSeconds(3 + i), Names.You, "Apple", 1, null, null, false));
        }
        var engine = new LogEngine { CharacterName = "Que" };
        var snapshot = engine.Snapshot(now) with { Loot = tracker.Snapshot(60, 10) };
        var view = new OverlayView(new HashSet<ChatChannel>(), [], LootGrouping: grouping, ItemFilter: " sWoRd ");
        var presenter = OverlayPresenters.All.Single(definition => definition.Key == "loot");

        var content = presenter.Render(snapshot, engine, view);

        var items = content.Rows.Where(row => row.LootItem is not null).ToArray();
        Assert.Equal(2, items.Length);
        Assert.All(items, row => Assert.True(row.LootItem!.Contains("sword", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(items, row => row.Left.Contains("[Other]"));
        Assert.Equal("17 items this session", content.Subtitle);
        Assert.Contains(content.Rows, row => row.Left == "Coin looted");
        var cleared = presenter.Render(snapshot, engine, view with { ItemFilter = "  " });
        Assert.Equal(grouping == 0 ? 12 : 4, cleared.Rows.Count(row => row.LootItem is not null));
        var unmatched = presenter.Render(snapshot, engine, view with { ItemFilter = "missing" });
        Assert.DoesNotContain(unmatched.Rows, row => row.LootItem is not null);
        Assert.Contains(unmatched.Rows, row => row.Left == "No loot matches the item filter.");
        Assert.Equal(content.Subtitle, unmatched.Subtitle);
    }

    [Fact]
    public void Motes_AlwaysShowsAllTiersAndRecentDropsRegardlessOfItemFilter()
    {
        var engine = new LogEngine { CharacterName = "Que" };
        engine.ProcessLines(
        [
            "[Wed Oct 21 18:00:10 2026] --You have looted 20 Mote of Major Potential from Reward Chest.--",
            "[Wed Oct 21 18:00:20 2026] --You have looted a Mote of Minor Potential from Reward Chest.--",
        ]);
        var snapshot = engine.Snapshot(new DateTime(2026, 10, 21, 19, 0, 0));
        var presenter = OverlayPresenters.All.Single(definition => definition.Key == "motes");
        var view = new OverlayView(new HashSet<ChatChannel>(), [], ItemFilter: " MOTE OF MAJOR ");
        var baseline = presenter.Render(snapshot, engine, view with { ItemFilter = "" });

        var content = presenter.Render(snapshot, engine, view);

        var items = content.Rows.Where(row => row.LootItem is not null).ToArray();
        Assert.Equal(MoteValues.Tiers.Count + 2, items.Length);
        Assert.Contains(items, row => row.LootItem == "Mote of Minor Potential");
        Assert.Contains(content.Rows, row => row.Left == "Recent");
        Assert.Equal(baseline.Subtitle, content.Subtitle);
        Assert.Equal(baseline.Rows.Single(row => row.Left == "Total"), content.Rows.Single(row => row.Left == "Total"));
        var cleared = presenter.Render(snapshot, engine, view with { ItemFilter = " " });
        Assert.Equal(baseline.Rows.Select(row => (row.Left, row.Right, row.LootItem)),
            cleared.Rows.Select(row => (row.Left, row.Right, row.LootItem)));
        var unmatched = presenter.Render(snapshot, engine, view with { ItemFilter = "missing" });
        Assert.Equal(baseline.Rows.Select(row => (row.Left, row.Right, row.LootItem)),
            unmatched.Rows.Select(row => (row.Left, row.Right, row.LootItem)));
    }

    [Fact]
    public void ItemUpgrades_FilterPreservesCharacterSelectionAndHistory()
    {
        var now = new DateTime(2026, 10, 21, 18, 0, 0, DateTimeKind.Utc);
        var inventory = new QuestInventory
        {
            RecentUpgrades =
            [
                new("Que", "Sword", 1, 2, now),
                new("Que", "Shield", 0, 1, now.AddMinutes(1)),
                new("Other", "Sword", 2, 3, now.AddMinutes(2)),
            ],
        };
        var view = new OverlayView(new HashSet<ChatChannel>(), [], Inventory: inventory,
            CharacterName: "Que", ItemFilter: " sWo ");
        var presenter = OverlayPresenters.All.Single(definition => definition.Key == "itemupgrades");

        var content = presenter.Render(null!, null!, view);

        Assert.Equal("Sword", Assert.Single(content.Rows).Left);
        Assert.Equal("+1 to +2", content.Rows[0].Right);
        var cleared = presenter.Render(null!, null!, view with { ItemFilter = "" });
        Assert.Equal(["Shield", "Sword"], cleared.Rows.Select(row => row.Left));
        var unmatched = presenter.Render(null!, null!, view with { ItemFilter = "missing" });
        Assert.Equal("No item rank-ups match the item filter.", Assert.Single(unmatched.Rows).Left);
        Assert.Equal(3, inventory.RecentUpgrades.Count);
    }

    [Fact]
    public void InventoryFilter_NarrowsGridAndIncludesNewMatchingRowsWithoutChangingSource()
    {
        RunSta(() =>
        {
            var sword = new QuestInventoryItem { Item = "Sword", Owned = 3 };
            var shield = new QuestInventoryItem { Item = "Shield", Owned = 2 };
            var source = new ObservableCollection<QuestInventoryItem> { sword, shield };
            var grid = new DataGrid { ItemsSource = source, CanUserAddRows = false };
            var filter = new TextBox();
            MainWindow.ConfigureInventoryItemFilter(grid, filter);

            filter.Text = " sWo ";
            Assert.Same(sword, Assert.Single(grid.Items.Cast<QuestInventoryItem>()));
            var newSword = new QuestInventoryItem { Item = "Sword of Fire", Owned = 1 };
            source.Add(newSword);
            Assert.Equal([sword, newSword], grid.Items.Cast<QuestInventoryItem>());
            filter.Text = "missing";
            Assert.Empty(grid.Items);
            filter.Clear();
            Assert.Equal(source, grid.Items.Cast<QuestInventoryItem>());
            Assert.Equal(3, sword.Owned);
            Assert.Equal(2, shield.Owned);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
