namespace EqLog.Core.Tests;

public class InventoryExportTests
{
    [Fact]
    public void ReadsEveryNonemptySampleEntryAndSeparatesCollectionsAndInstalledAugments()
    {
        var items = InventoryExport.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "InventorySample.txt")));
        Assert.Contains(items, i => i.Name == "Water Flask" && i.Count == 927);
        Assert.Contains(items, i => i.Bank);
        Assert.Contains(items, i => i.Location == "Head-Slot7" && i.Installed);
        Assert.Contains(items, i => i.Location == "General 2-Slot1-Slot8" && i.Installed);
        Assert.Contains(items, i => i.Location == "General 2-Slot21" && i.Name == "Soul Leech, Dark Sword of Blood (Exaltation)");
        Assert.Equal(1572, items.Count);
        Assert.Equal(892, items.Count(i => i.Collection));
        Assert.Equal(791, items.Count(i => i.Location == "Equipment" && i.Collection));
        Assert.Equal(75, items.Count(i => i.Location == "Augmentation" && i.Collection));
        Assert.Equal(22, items.Count(i => i.Location == "Activated" && i.Collection));
        Assert.Equal(4, items.Count(i => i.Location.StartsWith("Equipment-") && i.Collection));

        var inventory = new QuestInventory();
        inventory.ApplyExport("Que", items, DateTime.UtcNow);
        Assert.Equal(items.Sum(i => i.Count), inventory.Items.Sum(i => i.Owned + i.Collection + i.Installed));
        Assert.Equal(892, inventory.Items.Sum(i => i.Collection));
    }

    [Fact]
    public void ResolvesMatchingCharacterAndServerAboveLogs()
    {
        Assert.Equal(@"C:\EverQuest\Quethrosar_qeynos-Inventory.txt",
            InventoryExport.PathForLog(@"C:\EverQuest\Logs\eqlog_Quethrosar_qeynos.txt"));
    }

    [Fact]
    public void InvalidRowsDoNotProducePartialSnapshot()
    {
        Assert.Throws<FormatException>(() => InventoryExport.Parse(
            "Location\tName\tID\tCount\tSlots\nGeneral 1\tGnoll Fang\t123\tbad\t0"));
    }

    [Fact]
    public void InvalidCollectionRowsAreNotSilentlySkipped()
    {
        Assert.Throws<FormatException>(() => InventoryExport.Parse(
                "Location\tName\tID\tCount\tSlots\nKeyRing\tName\tID\t\nEquipment\tBroken item\tbad"));
    }

    [Fact]
    public void CollectionAndInstalledCountsAreNotLooseOwnedCountsAndAreResetOnNextExport()
    {
        var inventory = new QuestInventory();
        inventory.ApplyExport("Que", [
            new("Equipment", "Mask", 1, 1, false, Collection: true),
            new("Head-Slot7", "Mask", 1, 1, false, Installed: true),
            new("General 1-Slot1", "Mask", 1, 2, false),
        ], DateTime.UtcNow);
        var item = Assert.Single(inventory.Items);
        Assert.Equal(2, item.Owned);
        Assert.Equal(1, item.Collection);
        Assert.Equal(1, item.Installed);
        inventory.ApplyExport("Que", [], DateTime.UtcNow);
        Assert.Equal(0, item.Collection);
        Assert.Equal(0, item.Installed);
    }

    [Fact]
    public void ExportReplacesCountsAndPreservesOnlyLootNewerThanSnapshot()
    {
        var at = new DateTime(2026, 10, 27, 19, 0, 0, DateTimeKind.Utc);
        var inventory = new QuestInventory();
        inventory.NoteLoot("Que", "log", 1, "Gnoll Fang", 2, true, at.AddMinutes(-1));
        inventory.NoteLoot("Que", "log", 2, "Gnoll Fang", 3, true, at.AddMinutes(1));
        inventory.NoteLoot("Other", "log2", 1, "Gnoll Fang", 8, true, at);
        inventory.ApplyExport("Que", [new("General 1", "Gnoll Fang", 1, 4, false), new("Bank1", "Gnoll Fang", 1, 5, true)], at);
        var item = inventory.Items.Single(i => i.Character == "Que");
        Assert.Equal(12, item.Owned);
        Assert.Equal(4, item.Carried);
        Assert.Equal(5, item.Bank);
        inventory.ApplyExport("Que", [], at.AddMinutes(2));
        Assert.Equal(0, item.Owned);
        Assert.Equal(8, inventory.Items.Single(i => i.Character == "Other").Owned);
    }

    [Fact]
    public void ReadingHistoryDoesNotDoubleCountExportButNewerHistoryRestoresUnexportedLoot()
    {
        var at = DateTime.UtcNow;
        var inventory = new QuestInventory();
        inventory.ApplyExport("Que", [new("General 1", "Fang", 1, 2, false)], at);
        inventory.NoteLoot("Que", "log", 1, "Fang", 2, false, at.AddSeconds(-1));
        inventory.NoteLoot("Que", "log", 2, "Fang", 1, false, at.AddSeconds(1));
        Assert.Equal(3, Assert.Single(inventory.Items).Owned);
    }
}
