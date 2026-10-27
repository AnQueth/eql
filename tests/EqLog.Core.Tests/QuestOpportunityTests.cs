namespace EqLog.Core.Tests;

public class QuestOpportunityTests
{
    [Fact]
    public void SortsByLootTimeOrQuestNameAndFiltersByEitherName()
    {
        var inventory = new QuestInventory();
        var at = DateTime.UtcNow;
        inventory.NoteLoot("Que", "log", 1, "Old Fang", 1, false, at.AddMinutes(-1));
        inventory.NoteLoot("Que", "log", 2, "New Mask +1", 1, true, at);
        var cache = new Dictionary<string, QuestLookupResult>
        {
            ["Old Fang"] = new("Old Fang", at, [new("Alpha quest", "url")]),
            ["New Mask"] = new("New Mask", at, [new("Zulu quest", "url")]),
        };
        Assert.Equal(["Zulu quest", "Alpha quest"], inventory.Opportunities("Que", cache, "", true).Select(q => q.Title));
        Assert.Equal(["Alpha quest", "Zulu quest"], inventory.Opportunities("Que", cache, "", false).Select(q => q.Title));
        Assert.Equal("Zulu quest", Assert.Single(inventory.Opportunities("Que", cache, " MASK ", true)).Title);
        Assert.Equal("Alpha quest", Assert.Single(inventory.Opportunities("Que", cache, "ALPHA", true)).Title);
        Assert.Empty(inventory.Opportunities("Other", cache, "", true));
        Assert.Empty(inventory.Opportunities("Que", cache, "missing", true));
        inventory.Completed.Add(QuestInventory.CompletionKey("Que", "Zulu quest"));
        Assert.Equal("Alpha quest", Assert.Single(inventory.Opportunities("Que", cache, "", true)).Title);
    }

    [Fact]
    public void OlderReplayCannotMoveLastLootTimeBackwards()
    {
        var at = DateTime.UtcNow;
        var inventory = new QuestInventory();
        inventory.NoteLoot("Que", "log", 2, "Fang", 1, true, at);
        inventory.NoteLoot("Que", "log", 1, "Fang", 1, false, at.AddDays(-1));
        var loaded = System.Text.Json.JsonSerializer.Deserialize<QuestInventory>(
            System.Text.Json.JsonSerializer.Serialize(inventory))!;
        Assert.Equal(at, Assert.Single(loaded.Items).LastLootUtc);
    }
}
