namespace EqLog.Core.Tests;

public class QuestInventoryTests
{
    [Fact]
    public void QuestMarkerNotifiesOnlyWhenItsValueChanges()
    {
        var item = new QuestInventoryItem { Item = "Gnoll Fang" };
        var notifications = new List<string?>();
        item.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        item.HasQuestReferences = false;
        item.HasQuestReferences = true;
        item.HasQuestReferences = true;
        item.HasQuestReferences = false;
        Assert.Equal([nameof(QuestInventoryItem.HasQuestReferences), nameof(QuestInventoryItem.HasQuestReferences)], notifications);
    }

    [Fact]
    public void QuestMarkerIsNotSavedAndCloningDoesNotCopyUiSubscribers()
    {
        var item = new QuestInventoryItem { Item = "Gnoll Fang", Owned = 2, HasQuestReferences = true };
        int notifications = 0;
        item.PropertyChanged += (_, _) => notifications++;

        var clone = item.Clone();
        Assert.True(clone.HasQuestReferences);
        clone.HasQuestReferences = false;
        Assert.Equal(0, notifications);
        Assert.True(item.HasQuestReferences);

        string json = System.Text.Json.JsonSerializer.Serialize(item);
        Assert.DoesNotContain(nameof(QuestInventoryItem.HasQuestReferences), json);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<QuestInventoryItem>(json)!;
        Assert.False(loaded.HasQuestReferences);
        Assert.Equal(2, loaded.Owned);
    }

    [Fact]
    public void EngineReportsLootOutsideRecentSnapshotWithStableLineNumbers()
    {
        var engine = new LogEngine();
        var observed = new List<(string Item, long Line)>();
        engine.LootObserved += (loot, line) => observed.Add((loot.Item, line));
        engine.ProcessLines([
            "[Tue Oct 27 12:00:00 2026] You have entered Blackburrow.",
            "[Tue Oct 27 12:00:01 2026] --You have looted a Gnoll Fang from a gnoll's corpse.--",
        ]);
        var entry = Assert.Single(observed);
        Assert.Equal("Gnoll Fang", entry.Item);
        Assert.Equal(2, entry.Line);
    }

    [Fact]
    public void HistoryIsUnconfirmedAndReplayDoesNotCountTwice()
    {
        var inventory = new QuestInventory();
        inventory.NoteLoot("Que", "log", 1, "Gnoll Fang", 2, false);
        inventory.NoteLoot("Que", "log", 1, "Gnoll Fang", 2, false);
        var item = Assert.Single(inventory.Items);
        Assert.Equal(2, item.Historical);
        Assert.Equal(0, item.Owned);
    }

    [Fact]
    public void LiveLootTracksItemsAndRecencyWithoutClaimingOwnershipBeforeAnExport()
    {
        var inventory = new QuestInventory();
        inventory.NoteLoot("Que", "log", 1, "Gnoll Fang", 1, false);
        inventory.NoteLoot("Que", "log", 2, "Gnoll Fang", 3, true);
        inventory.NoteLoot("Other", "otherlog", 1, "Gnoll Fang", 1, true);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<QuestInventory>(
            System.Text.Json.JsonSerializer.Serialize(inventory))!;
        loaded.NoteLoot("Que", "log", 2, "Gnoll Fang", 3, true);
        Assert.Equal(0, loaded.Items.Single(i => i.Character == "Que").Owned);
        Assert.Equal(0, loaded.Items.Single(i => i.Character == "Other").Owned);
        Assert.Empty(loaded.RecentLoot);
    }

    [Fact]
    public void RotatedLogWithRestartedLineNumbersIsStillCounted()
    {
        var at = DateTime.UtcNow;
        var inventory = new QuestInventory();
        inventory.NoteLoot("Que", "log", 50, "Fang", 1, false, at.AddHours(-1));
        inventory.NoteLoot("Que", "log", 3, "Fang", 1, false, at);
        inventory.NoteLoot("Que", "log", 3, "Fang", 1, false, at);
        Assert.Equal(2, inventory.Items.Single().Historical);
    }

    [Fact]
    public void ApplyExport_ReusesNormalizedItemsAndKeepsConfirmedCountsAndOtherCharacters()
    {
        var written = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        var item = new QuestInventoryItem
        {
            Character = "Que",
            Item = "Gnoll Fang +2",
            Historical = 7,
            Owned = 99,
        };
        var absent = new QuestInventoryItem { Character = "Que", Item = "Old item", Owned = 5, Bank = 5 };
        var other = new QuestInventoryItem { Character = "Other", Item = "Gnoll Fang", Owned = 42, Bank = 42 };
        var inventory = new QuestInventory { Items = [item, absent, other] };
        inventory.NoteLoot("QUE", "log", 1, "gnoll fang +3", 2, true, written.AddMinutes(-1));
        inventory.NoteLoot("QUE", "log", 2, "GNOLL FANG +4", 3, true, written.AddMinutes(1));

        inventory.ApplyExport("que",
        [
            new("Held", "Gnoll Fang", 1, 2, InventoryStorage.Carried),
            new("Bank1", "GNOLL FANG +5", 1, 4, InventoryStorage.Bank),
            new("Hoard 1", "Gnoll Fang", 1, 6, InventoryStorage.Hoard),
            new("Personal-Depot1", "Gnoll Fang", 1, 8, InventoryStorage.Depot),
            new("Unknown", "Gnoll Fang", 1, 10, InventoryStorage.Other),
            new("Collection", "Gnoll Fang", 1, 12, InventoryStorage.Other, Collection: true),
            new("Installed", "Gnoll Fang", 1, 14, InventoryStorage.Carried, Installed: true),
            new("Held", "New item", 2, 1, InventoryStorage.Carried),
        ], written);

        Assert.Same(item, inventory.Items[0]);
        Assert.Equal("GNOLL FANG +5", item.Item);
        Assert.Equal(4, inventory.Items.Count);
        Assert.Equal(30, item.Owned);
        Assert.Equal(2, item.Carried);
        Assert.Equal(4, item.Bank);
        Assert.Equal(6, item.Hoard);
        Assert.Equal(8, item.Depot);
        Assert.Equal(10, item.OtherStorage);
        Assert.Equal(12, item.Collection);
        Assert.Equal(14, item.Installed);
        Assert.Equal(7, item.Historical);
        Assert.Equal(0, absent.Owned);
        Assert.Equal(0, absent.Bank);
        Assert.Equal(42, other.Owned);
        Assert.Equal(42, other.Bank);
        Assert.Empty(inventory.RecentLoot);
        Assert.Equal(written, inventory.ExportTimes["QUE"]);

        inventory.ApplyExport("QUE", [], written.AddMinutes(2));
        Assert.Equal(0, item.Owned);
        Assert.Empty(inventory.RecentLoot);
    }

    [Fact]
    public void ConfirmedOwnedMatchesLocationsDespiteLaterLootMergesAndLegacyDeltas()
    {
        var at = new DateTime(2026, 11, 1, 15, 20, 0, DateTimeKind.Utc);
        var inventory = new QuestInventory
        {
            RecentLoot = [new("Que", "Bracer", 1, at.AddHours(1))],
        };
        inventory.ApplyExport("Que",
        [
            new("Wrist", "Bracer +9", 1, 1, InventoryStorage.Carried, WornSlot: 9),
            new("Wrist", "Bracer +10", 1, 1, InventoryStorage.Carried, WornSlot: 10),
        ], at);
        var bracer = Assert.Single(inventory.Items);
        Assert.Equal(2, bracer.Owned);
        Assert.Empty(inventory.RecentLoot);
        inventory.NoteLoot("Que", "log", 1, "Bracer", 1, true, at.AddHours(1));
        inventory.NoteLoot("Que", "log", 2, "Bracer", 1, false, at.AddHours(2));
        inventory.NoteLoot("Que", "log", 3, "Bracer", 1, true);
        Assert.Equal(2, bracer.Owned);
        Assert.Equal(at.AddHours(2), bracer.LastLootUtc);
        Assert.Empty(inventory.RecentLoot);
        inventory.ApplyExport("Que",
            [new("Wrist", "Bracer +10", 1, 1, InventoryStorage.Carried, WornSlot: 10)], at.AddHours(3));
        Assert.Equal(1, bracer.Owned);
        inventory.ApplyExport("Que", [], at.AddHours(4));
        Assert.Equal(0, bracer.Owned);
    }

    [Fact]
    public void ApplyExport_DisplaysHighestCurrentRankAcrossStorageAndCollectionsAndPersistsIt()
    {
        var written = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var inventory = new QuestInventory();
        InventoryExportItem[] rows =
        [
            new("Held", "Umbral Platemail Bracer +3", 1, 2, InventoryStorage.Carried),
            new("Bank1", "Umbral Platemail Bracer +10", 1, 1, InventoryStorage.Bank),
            new("Equipment", "Umbral Platemail Bracer +9", 1, 1, InventoryStorage.Other, Collection: true),
            new("Held", "Umbral Platemail Bracer", 1, 1, InventoryStorage.Carried),
            new("Held", "Umbral Platemail Bracer (Exaltation) +2", 2, 1, InventoryStorage.Carried),
        ];

        inventory.ApplyExport("Que", rows, written);

        var item = Assert.Single(inventory.Items, entry => ItemNames.Comparer.Equals(entry.Item, "Umbral Platemail Bracer"));
        Assert.Equal("Umbral Platemail Bracer +10", item.Item);
        Assert.Equal(4, item.Owned);
        Assert.Equal(3, item.Carried);
        Assert.Equal(1, item.Bank);
        Assert.Equal(1, item.Collection);
        Assert.Equal(2, inventory.Items.Count);
        Assert.Empty(inventory.RecentUpgrades);
        inventory.ApplyExport("Que", rows.Reverse().ToArray(), written.AddMinutes(1));
        Assert.Equal("Umbral Platemail Bracer +10", item.Item);

        var loaded = System.Text.Json.JsonSerializer.Deserialize<QuestInventory>(
            System.Text.Json.JsonSerializer.Serialize(inventory))!;
        Assert.Equal("Umbral Platemail Bracer +10", loaded.Items[0].Item);
        loaded.ApplyExport("Que",
        [
            new("Held", "Umbral Platemail Bracer +3", 1, 1, InventoryStorage.Carried),
            new("Equipment", "Umbral Platemail Bracer +7", 1, 1, InventoryStorage.Other, Collection: true),
        ], written.AddMinutes(2));
        Assert.Equal("Umbral Platemail Bracer +7", loaded.Items[0].Item);
        Assert.Equal(1, loaded.Items[0].Owned);
        Assert.Equal(1, loaded.Items[0].Collection);
        Assert.Empty(loaded.RecentUpgrades);
        loaded.ApplyExport("Que",
            [new("Held", "Umbral Platemail Bracer", 1, 1, InventoryStorage.Carried)], written.AddMinutes(3));
        Assert.Equal("Umbral Platemail Bracer", loaded.Items[0].Item);
    }

    [Fact]
    public void ApplyExport_DuplicateNormalizedSavedRowsStillUseTheFirstRow()
    {
        var first = new QuestInventoryItem { Character = "Que", Item = "Sword +1", Owned = 10 };
        var duplicate = new QuestInventoryItem { Character = "Que", Item = "sword +2", Owned = 20 };
        var inventory = new QuestInventory { Items = [first, duplicate] };

        inventory.ApplyExport("Que",
            [new("Held", "SWORD +3", 1, 2, InventoryStorage.Carried)], DateTime.UtcNow);

        Assert.Equal(2, first.Owned);
        Assert.Equal(0, duplicate.Owned);
        Assert.Equal(2, inventory.Items.Count);
    }

    [Fact]
    public void ApplyExport_RecordsOnlyNewHigherRanksAndPersistsUpgradeHistory()
    {
        var firstExport = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        var secondExport = firstExport.AddHours(1);
        var inventory = new QuestInventory();

        inventory.ApplyExport("Que",
            [new("Held", "Sword +1", 1, 1, InventoryStorage.Carried)], firstExport);
        Assert.Empty(inventory.RecentUpgrades);

        inventory.ApplyExport("Que",
            [new("Held", "sword +2", 1, 30, InventoryStorage.Carried)], secondExport);

        var upgrade = Assert.Single(inventory.RecentUpgrades);
        Assert.Equal("Que", upgrade.Character);
        Assert.Equal("sword", upgrade.Item);
        Assert.Equal(1, upgrade.PreviousRank);
        Assert.Equal(2, upgrade.NewRank);
        Assert.Equal(secondExport, upgrade.TimeUtc);

        var loaded = System.Text.Json.JsonSerializer.Deserialize<QuestInventory>(
            System.Text.Json.JsonSerializer.Serialize(inventory))!;
        loaded.ApplyExport("QUE",
            [new("Held", "SWORD +2", 1, 1, InventoryStorage.Carried)], secondExport.AddHours(1));
        loaded.ApplyExport("QUE",
            [new("Held", "Sword +3", 1, 1, InventoryStorage.Carried)], secondExport.AddHours(2));

        Assert.Equal(2, loaded.RecentUpgrades.Count);
        Assert.Equal(2, loaded.RecentUpgrades[1].PreviousRank);
        Assert.Equal(3, loaded.RecentUpgrades[1].NewRank);
        Assert.Equal(3, Assert.Single(loaded.ItemRanks, item => item.Character == "QUE").Rank);
    }

    [Fact]
    public async Task ApplyExport_UpgradeHistorySurvivesDiskReloadAndRepeatedExports()
    {
        string directory = Path.Combine(Path.GetTempPath(), "eql-inventory-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "quest-inventory.json");
        var written = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        var inventory = new QuestInventory();
        InventoryExportItem[] upgradedRows =
            [new("Held", "Sword +2", 1, 1, InventoryStorage.Carried)];

        try
        {
            inventory.ApplyExport("Que",
                [new("Held", "Sword +1", 1, 1, InventoryStorage.Carried)], written);
            inventory.ApplyExport("Que", upgradedRows, written.AddMinutes(1));
            var expected = Assert.Single(inventory.RecentUpgrades);
            await AtomicFile.WriteJsonAsync(path, inventory.Clone());

            for (int restart = 0; restart < 3; restart++)
            {
                var loaded = Assert.IsType<QuestInventory>(await AtomicFile.ReadJsonAsync<QuestInventory>(path));
                Assert.Equal(expected, Assert.Single(loaded.RecentUpgrades));
                Assert.Equal(2, Assert.Single(loaded.ItemRanks).Rank);

                loaded.ApplyExport("QUE", upgradedRows, written.AddMinutes(1));
                Assert.Equal(expected, Assert.Single(loaded.RecentUpgrades));
                await AtomicFile.WriteJsonAsync(path, loaded.Clone());
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ApplyExport_UsesThePreviousExportRankAfterAnItemDowngrades()
    {
        var inventory = new QuestInventory();
        var written = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

        inventory.ApplyExport("Que",
            [new("Held", "Sword +1", 1, 1, InventoryStorage.Carried)], written);
        inventory.ApplyExport("Que",
            [new("Held", "Sword +2", 1, 1, InventoryStorage.Carried)], written.AddMinutes(1));
        inventory.ApplyExport("Que",
            [new("Held", "Sword +1", 1, 1, InventoryStorage.Carried)], written.AddMinutes(2));
        inventory.ApplyExport("Que",
            [new("Held", "Sword +2", 1, 1, InventoryStorage.Carried)], written.AddMinutes(3));

        Assert.Equal(2, inventory.RecentUpgrades.Count);
        Assert.All(inventory.RecentUpgrades, entry =>
        {
            Assert.Equal(1, entry.PreviousRank);
            Assert.Equal(2, entry.NewRank);
        });
    }

    [Fact]
    public void ApplyExport_SeedsEachCharacterIndependentlyAndKeepsOnlyLatestHundredUpgrades()
    {
        var inventory = new QuestInventory();
        var written = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

        inventory.ApplyExport("Que",
            [new("Held", "Sword +2", 1, 1, InventoryStorage.Carried)], written);
        inventory.ApplyExport("Other",
            [new("Held", "Sword +3", 1, 1, InventoryStorage.Carried)], written);
        Assert.Empty(inventory.RecentUpgrades);

        for (int rank = 1; rank <= 101; rank++)
        {
            string item = $"Item {rank}";
            inventory.ApplyExport("Que",
                [new("Held", $"{item} +1", 1, 1, InventoryStorage.Carried)], written.AddMinutes(rank));
            inventory.ApplyExport("Que",
                [new("Held", $"{item} +2", 1, 1, InventoryStorage.Carried)], written.AddMinutes(rank + 1));
        }

        Assert.Equal(100, inventory.RecentUpgrades.Count(entry => entry.Character == "Que"));
        Assert.DoesNotContain(inventory.RecentUpgrades, entry => entry.Item == "Item 1");
    }
}
