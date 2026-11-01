namespace EqLog.Infrastructure.Tests;

public sealed class InventoryHistoryStorageTests
{
    [Fact]
    public async Task UpgradeHistorySurvivesDiskReloadAndRepeatedExports()
    {
        string folder = Path.Combine(Path.GetTempPath(), "eql-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new QuestInventoryRepository(Path.Combine(folder, "quest-inventory.json"));
            var state = new QuestInventory();
            var written = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
            state.ApplyExport("Que", [new("Held", "Sword +1", 1, 1, InventoryStorage.Carried)], written);
            state.ApplyExport("Que", [new("Held", "Sword +2", 1, 1, InventoryStorage.Carried)], written.AddMinutes(1));
            var expected = Assert.Single(state.RecentUpgrades);
            await repository.SaveAsync(state.Clone());
            for (int restart = 0; restart < 3; restart++)
            {
                state = await repository.LoadAsync();
                Assert.Equal(expected, Assert.Single(state.RecentUpgrades));
                Assert.Equal(2, Assert.Single(state.ItemRanks).Rank);
                state.ApplyExport("QUE", [new("Held", "Sword +2", 1, 1, InventoryStorage.Carried)], written.AddMinutes(1));
                await repository.SaveAsync(state.Clone());
            }
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }
}
