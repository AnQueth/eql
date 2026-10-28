namespace EqLog.Core.Tests;

public class ItemNamesTests
{
    [Theory]
    [InlineData("Mask+1", "mask + 10")]
    [InlineData("Sword + 2 of Power +9", "Sword of Power")]
    [InlineData("  High  Quality Raiment +4 ", "High Quality Raiment")]
    public void ComparisonAndHashIgnoreUpgradeModifiers(string left, string right)
    {
        Assert.True(ItemNames.Comparer.Equals(left, right));
        Assert.Equal(ItemNames.Comparer.GetHashCode(left), ItemNames.Comparer.GetHashCode(right));
    }

    [Fact]
    public void InventoryCombinesLootAndExportVariantsWithoutLosingCounts()
    {
        var inventory = new QuestInventory();
        var at = DateTime.UtcNow;
        inventory.NoteLoot("Que", "log", 1, "Mask +1", 1, true, at.AddMinutes(1));
        inventory.NoteLoot("Que", "log", 2, "Mask + 2", 2, false, at.AddMinutes(-1));
        inventory.ApplyExport("Que", [
            new("General 1", "Mask +3", 1, 2, InventoryStorage.Carried),
            new("Bank1", "Mask + 4", 1, 3, InventoryStorage.Bank),
            new("Equipment", "Mask +5", 1, 4, InventoryStorage.Carried, Collection: true),
        ], at);
        var item = Assert.Single(inventory.Items);
        Assert.Equal(6, item.Owned);
        Assert.Equal(2, item.Historical);
        Assert.Equal(4, item.Collection);
        Assert.Equal("Mask +1", item.Item);
    }
}
