namespace EqLog.Core.Tests;

public class SkyOwnedQuestTests
{
    [Fact]
    public void OwnedQuests_FilterClassAndCharacter_IncludeCollectionAndUpgradeVariants()
    {
        static SkyQuest Quest(string name, string playerClass, string ingredient)
        {
            return new(playerClass, name, "Reward", "Turn-in NPC", "say", "", "", "url",
                [new(ingredient, 1), new("Missing rune", 1)]);
        }

        var catalog = new SkyQuestCatalog(DateTime.UtcNow, [
            Quest("Owned quest", "Cleric", "Raiment"),
            Quest("Other class", "Warrior", "Raiment"),
            Quest("History only", "Cleric", "History"),
            Quest("Installed only", "Cleric", "Augment"),
            Quest("Other character", "Cleric", "Other item"),
        ]);
        var inventory = new QuestInventory
        {
            Items = [
                new() { Character = "Que", Item = "Raiment + 4", Collection = 2 },
                new() { Character = "Que", Item = "History", Historical = 5 },
                new() { Character = "Que", Item = "Augment", Installed = 1 },
                new() { Character = "Other", Item = "Other item", Owned = 1 },
            ],
        };
        var result = Assert.Single(SkyQuests.OwnedQuests(catalog, inventory, "Que", "Cleric"));
        Assert.Equal("Owned quest", result.Quest.Name);
        Assert.Equal("Turn-in NPC", result.Quest.Giver);
        Assert.Equal("Reward", result.Quest.Reward);
        Assert.Equal(2, result.Progress[0].Owned);
        Assert.False(result.Progress[1].Satisfied);
        Assert.Equal(2, SkyQuests.OwnedQuests(catalog, inventory, "Que", "All classes").Count);
        inventory.Items.Clear();
        Assert.Empty(SkyQuests.OwnedQuests(catalog, inventory, "Que", "Cleric"));
    }
}
