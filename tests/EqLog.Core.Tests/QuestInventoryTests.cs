namespace EqLog.Core.Tests;

public class QuestInventoryTests
{
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
}
