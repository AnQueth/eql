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
    public void LiveLootAddsOwnedCountsPerCharacterAndSurvivesSerialization()
    {
        var inventory = new QuestInventory();
        inventory.NoteLoot("Que", "log", 1, "Gnoll Fang", 1, false);
        inventory.NoteLoot("Que", "log", 2, "Gnoll Fang", 3, true);
        inventory.NoteLoot("Other", "otherlog", 1, "Gnoll Fang", 1, true);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<QuestInventory>(
            System.Text.Json.JsonSerializer.Serialize(inventory))!;
        loaded.NoteLoot("Que", "log", 2, "Gnoll Fang", 3, true);
        Assert.Equal(3, loaded.Items.Single(i => i.Character == "Que").Owned);
        Assert.Equal(1, loaded.Items.Single(i => i.Character == "Other").Owned);
    }

    [Fact]
    public void LegacyImportedLinesBecomeWatermarksAndZeroCountsAreNotWritten()
    {
        string json = """
            {"Items":[{"Character":"Que","Item":"Gnoll Fang","Owned":0,"Historical":2,"Carried":0,"Bank":0}],
             "ImportedLines":["Que\tC:\\logs\\eq.txt\t5","Que\tC:\\logs\\eq.txt\t9","Que\tC:\\logs\\eq.txt\tbad"]}
            """;
        var loaded = System.Text.Json.JsonSerializer.Deserialize<QuestInventory>(json)!;

        Assert.Null(loaded.ImportedLines);
        Assert.Equal(9, Assert.Single(loaded.LootWatermarks).Value.Line);
        loaded.NoteLoot("Que", @"C:\logs\eq.txt", 9, "Gnoll Fang", 2, false, DateTime.UtcNow.AddDays(-1));
        Assert.Equal(2, loaded.Items.Single().Historical);
        loaded.NoteLoot("Que", @"C:\logs\eq.txt", 10, "Gnoll Fang", 1, false, DateTime.UtcNow.AddDays(-1));
        Assert.Equal(3, loaded.Items.Single().Historical);

        string saved = System.Text.Json.JsonSerializer.Serialize(loaded);
        Assert.DoesNotContain("ImportedLines", saved);
        Assert.DoesNotContain("\"Bank\"", saved);
        Assert.Contains("\"Historical\":3", saved);
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
    public void ApplyExport_ReusesNormalizedItemsAndKeepsNewerLootAndOtherCharacters()
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
        Assert.Equal(4, inventory.Items.Count);
        Assert.Equal(33, item.Owned);
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
        Assert.Equal(3, Assert.Single(inventory.RecentLoot).Quantity);
        Assert.Equal(written, inventory.ExportTimes["QUE"]);

        inventory.ApplyExport("QUE", [], written.AddMinutes(2));
        Assert.Equal(0, item.Owned);
        Assert.Empty(inventory.RecentLoot);
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
}
