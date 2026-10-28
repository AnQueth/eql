namespace EqLog.Core.Tests;

public class InventoryExportTests
{
    [Fact]
    public void ReadsEveryNonemptySampleEntryAndSeparatesCollectionsAndInstalledAugments()
    {
        var items = InventoryExport.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "InventorySample.txt")));
        Assert.Contains(items, i => i.Name == "Water Flask" && i.Count == 927);
        Assert.Contains(items, i => i.Storage == InventoryStorage.Bank);
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
    public void ClassifiesEveryStorageLocation()
    {
        var items = InventoryExport.Parse(string.Join("\n",
            "Location\tName\tID\tCount\tSlots",
            "Ear\tPearl Earring\t1\t1\t0",
            "Head-Slot1\tHelm Gem\t2\t1\t0",
            "General 1-Slot2\tGnoll Fang\t3\t4\t0",
            "Held\tCursor Thing\t4\t1\t0",
            "Bank3-Slot1\tBanked Fang\t5\t6\t0",
            "Bank3-Slot1-Slot2\tBanked Aug\t6\t1\t0",
            "SharedBank2\tShared Fang\t7\t2\t0",
            "Hoard 12\tHoarded Sword\t8\t1\t2",
            "Hoard 12-Slot1\tHoard Aug\t9\t1\t0",
            "Personal-Depot7\tSilk Swatch\t10\t250\t0",
            "Mystery Vault 3-Slot1\tVault Relic\t12\t5\t0",
            "Hoardings\tLookalike Gem\t13\t1\t0",
            "",
            "KeyRing\tName\tID\t",
            "Augmentation\tCollected Aug\t11"));

        InventoryExportItem Row(string name)
        {
            return items.Single(i => i.Name == name);
        }

        Assert.Equal(InventoryStorage.Carried, Row("Pearl Earring").Storage);
        Assert.True(Row("Helm Gem").Installed);
        Assert.Equal(InventoryStorage.Carried, Row("Gnoll Fang").Storage);
        Assert.False(Row("Gnoll Fang").Installed);
        Assert.Equal(InventoryStorage.Carried, Row("Cursor Thing").Storage);
        Assert.Equal(InventoryStorage.Bank, Row("Banked Fang").Storage);
        Assert.False(Row("Banked Fang").Installed);
        Assert.True(Row("Banked Aug").Installed);
        Assert.Equal(InventoryStorage.Bank, Row("Shared Fang").Storage);
        Assert.Equal(InventoryStorage.Hoard, Row("Hoarded Sword").Storage);
        Assert.False(Row("Hoarded Sword").Installed);
        Assert.True(Row("Hoard Aug").Installed);
        Assert.Equal(InventoryStorage.Depot, Row("Silk Swatch").Storage);
        Assert.True(Row("Collected Aug").Collection);
        Assert.Equal(InventoryStorage.Other, Row("Vault Relic").Storage);
        Assert.False(Row("Vault Relic").Installed);
        Assert.Equal(InventoryStorage.Other, Row("Lookalike Gem").Storage);
        Assert.Equal(["Hoardings", "Mystery Vault 3"], InventoryExport.UnknownLocations(items));

        var inventory = new QuestInventory();
        inventory.ApplyExport("Que", items, DateTime.UtcNow);
        var sword = inventory.Items.Single(i => i.Item == "Hoarded Sword");
        Assert.Equal(1, sword.Hoard);
        Assert.Equal(0, sword.Carried);
        Assert.Equal(1, sword.Owned);
        var silk = inventory.Items.Single(i => i.Item == "Silk Swatch");
        Assert.Equal(250, silk.Depot);
        Assert.Equal(0, silk.Carried);
        Assert.Equal(250, silk.Owned);
        var relic = inventory.Items.Single(i => i.Item == "Vault Relic");
        Assert.Equal(5, relic.OtherStorage);
        Assert.Equal(0, relic.Carried);
        Assert.Equal(5, relic.Owned);
    }

    [Fact]
    public void NumbersRepeatedWornSlotsByOrderIncludingEmptyOnes()
    {
        var items = InventoryExport.Parse(string.Join("\n",
            "Location\tName\tID\tCount\tSlots",
            "Any Slot\tCharm Thing\t1\t1\t0",
            "Ear\tEmpty\t0\t0\t0",
            "Ear-Slot7\tEmpty\t0\t0\t0",
            "Head\tHelm +2\t2\t1\t0",
            "Ear\tHoop +3\t3\t1\t0",
            "Ear-Slot8\tHoop Aug (Exaltation)\t4\t1\t0",
            "Wrist\tBracer +9\t5\t1\t0",
            "Wrist\tBracer +1\t5\t1\t0"));

        var descriptions = InventoryExport.DescribeAll(items);
        string Where(string name)
        {
            return descriptions[items.ToList().FindIndex(i => i.Name == name)];
        }

        Assert.Equal("Worn Any Slot, equipment slot 0", Where("Charm Thing"));
        Assert.Equal("Worn Head, equipment slot 2", Where("Helm +2"));
        Assert.Equal("Worn Ear, equipment slot 4", Where("Hoop +3"));
        Assert.Equal("Worn Ear, equipment slot 4, augment slot 8 of Hoop +3", Where("Hoop Aug (Exaltation)"));
        Assert.Equal("Worn Wrist, equipment slot 9", Where("Bracer +9"));
        Assert.Equal("Worn Wrist, equipment slot 10", Where("Bracer +1"));
    }

    [Fact]
    public void GroupsUpgradeRanksReadyToMergeWithDetailedLocations()
    {
        var items = InventoryExport.Parse(string.Join("\n",
            "Location\tName\tID\tCount\tSlots",
            "Wrist\tBracelet of Quiescence +9\t12806\t1\t11",
            "General 4\tSpacious Rucksack\t100\t1\t40",
            "General 4-Slot5\tBracelet of Quiescence\t12806\t1\t11",
            "General 4-Slot6\tBracelet of Quiescence +4\t12806\t1\t11",
            "General 4-Slot7\tBerries\t200\t20\t0",
            "General 4-Slot8\tBerries\t200\t20\t0",
            "Bank10\tStorage Trunk\t101\t1\t50",
            "Bank10-Slot3\tBone-Clasped Girdle +4\t300\t1\t11",
            "Bank10-Slot3-Slot7\tGirdle Aug +2 (Exaltation)\t301\t1\t11",
            "Bank2\tBone-Clasped Girdle +1\t300\t1\t11",
            "SharedBank1\tBone-Clasped Girdle\t300\t1\t11",
            "Hoard 12\tBone-Clasped Girdle +8\t300\t1\t11",
            "Personal-Depot3\tLone Item +2\t400\t1\t0",
            "",
            "KeyRing\tName\tID\t",
            "Equipment\tLone Item\t400"));

        var groups = InventoryExport.MergeGroups(items);

        Assert.Equal(["Bone-Clasped Girdle", "Bracelet of Quiescence"], groups.Select(g => g.BaseName));
        var girdle = groups[0];
        Assert.Equal([8, 4, 1, 0], girdle.Items.Select(i => i.Rank));
        Assert.Equal("Dragon's Hoard slot 12", girdle.Items[0].Where);
        Assert.Equal("Bank slot 10 (Storage Trunk), bag slot 3", girdle.Items[1].Where);
        Assert.Equal("Bank slot 2", girdle.Items[2].Where);
        Assert.Equal("Shared bank slot 1", girdle.Items[3].Where);
        var bracelet = groups[1];
        Assert.Equal(3, bracelet.Total);
        Assert.Equal("Worn Wrist, equipment slot 9", bracelet.Items[0].Where);
        Assert.Equal("Inventory slot 4 (Spacious Rucksack), bag slot 6", bracelet.Items[1].Where);
        Assert.Equal("Inventory slot 4 (Spacious Rucksack), bag slot 5", bracelet.Items[2].Where);
        Assert.Equal(("Bracelet of Quiescence", 4), InventoryExport.SplitRank("Bracelet of Quiescence +4"));
        Assert.Equal(("Berries", 0), InventoryExport.SplitRank("Berries"));
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
            new("Equipment", "Mask", 1, 1, InventoryStorage.Carried, Collection: true),
            new("Head-Slot7", "Mask", 1, 1, InventoryStorage.Carried, Installed: true),
            new("General 1-Slot1", "Mask", 1, 2, InventoryStorage.Carried),
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
        inventory.ApplyExport("Que", [new("General 1", "Gnoll Fang", 1, 4, InventoryStorage.Carried), new("Bank1", "Gnoll Fang", 1, 5, InventoryStorage.Bank)], at);
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
        inventory.ApplyExport("Que", [new("General 1", "Fang", 1, 2, InventoryStorage.Carried)], at);
        inventory.NoteLoot("Que", "log", 1, "Fang", 2, false, at.AddSeconds(-1));
        inventory.NoteLoot("Que", "log", 2, "Fang", 1, false, at.AddSeconds(1));
        Assert.Equal(3, Assert.Single(inventory.Items).Owned);
    }
}
