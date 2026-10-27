namespace EqLog.Core.Tests;

public class SkyQuestsTests
{
    [Fact]
    public void ParseSeparatesClassesAndFormatsRequirementsWithoutMarkup()
    {
        string table = """
            === [[Bard]] Tests ===
            '''Quest Giver:''' [[A Singer]]
            {| class="eoTable3"
            |-
            ! Reward || Quest || Trigger Phrases || Rune || Quest Items
            |-
            | {{:Golden Mask}}
            | Bard Test of Song
            | song
            | <div><ul><li>[[Wind Rune Meda]]</li></ul></div>
            | <div><ul>
            <li>'''{{SkyNoDrop|[[Woolen Mask]]}}''' (3-Boss)</li>
            <li>[[Horn|War Horn]]</li>
            </ul></div>
            |}
            === [[Shadow Knight]] Tests ===
            '''Quest Giver:''' [[A Knight]]
            {| class="eoTable3"
            |-
            ! Reward || Quest || Trigger Phrases || Rune || Quest Items
            |-
            | {{:Dark Sword}}
            | Shadow Knight Test of Power
            | power
            | [[Wind Rune Azia]]
            | [[Dark Stone]]
            |}
            """;

        var quests = SkyQuests.Parse(table);

        Assert.Equal(2, quests.Count);
        Assert.Equal("Golden Mask", quests[0].Reward);
        Assert.Equal("A Singer", quests[0].Giver);
        Assert.Equal("Wind Rune Meda", quests[0].Rune);
        Assert.Equal(["Wind Rune Meda", "Woolen Mask", "Horn"], quests[0].Requirements!.Select(r => r.Item));
        Assert.Equal("Woolen Mask (3-Boss)\nWar Horn", quests[0].Items);
        Assert.EndsWith("#Shadow_Knight_Tests", quests[1].Url);
    }

    [Fact]
    public void ParseRejectsMissingTablesRatherThanShowingEmptySuccess()
    {
        Assert.Throws<FormatException>(() => SkyQuests.Parse("Wiki format changed"));
    }

    [Fact]
    public void ProgressRecognizesScreenshotCollectionItemButStillRequiresMissingRune()
    {
        var quest = new SkyQuest("Cleric", "Test", "Reward", "Giver", "say", "Wind Rune Geza",
            "High Quality Raiment", "url", [new("Wind Rune Geza", 1), new("High Quality Raiment", 1)]);
        var inventory = new QuestInventory
        {
            Items =
            [
                new() { Character = "Que", Item = "High Quality Raiment +4", Collection = 4, Historical = 1 },
                new() { Character = "Que", Item = "Wind Rune Geza", Historical = 1 },
                new() { Character = "Que", Item = "Wind Rune Geza +1", Installed = 1 },
                new() { Character = "Other", Item = "Wind Rune Geza", Collection = 1 },
            ],
        };

        var progress = SkyQuests.Progress(quest, inventory, "Que");

        Assert.False(progress[0].Satisfied);
        Assert.Equal(0, progress[0].Owned);
        Assert.Equal(4, progress[1].Owned);
        Assert.Equal(4, progress[1].Collection);
        Assert.True(progress[1].Satisfied);
        Assert.False(progress.All(p => p.Satisfied));
    }

    [Fact]
    public void SampleCollectionRaimentCountsAsOwnedWithoutChangingLooseInventoryCounts()
    {
        var inventory = new QuestInventory();
        inventory.ApplyExport("Que", InventoryExport.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "InventorySample.txt"))), DateTime.UtcNow);
        var quest = new SkyQuest("Cleric", "Test", "Reward", "Giver", "say", "", "", "url",
            [new("High Quality Raiment", 1)]);

        var progress = Assert.Single(SkyQuests.Progress(quest, inventory, "Que"));

        Assert.True(progress.Satisfied);
        Assert.True(progress.Collection > 0);
        Assert.Equal(progress.Carried + progress.Bank + progress.Collection, progress.Owned);
    }

    [Fact]
    public void ProgressCombinesUpgradeVariantsAndBankButNotOtherCharactersOrHistory()
    {
        var quest = new SkyQuest("Bard", "Test", "Reward", "Giver", "say", "Rune", "Mask", "url",
            [new("Mask", 2), new("Rune", 1)]);
        var inventory = new QuestInventory
        {
            Items =
            [
                new() { Character = "Que", Item = "Mask +1", Owned = 1, Carried = 1 },
                new() { Character = "Que", Item = "Mask + 2", Owned = 1, Bank = 1 },
                new() { Character = "Que", Item = "Rune", Historical = 5 },
                new() { Character = "Other", Item = "Rune", Owned = 5 },
            ],
        };
        var progress = SkyQuests.Progress(quest, inventory, "Que");
        Assert.True(progress[0].Satisfied);
        Assert.Equal(2, progress[0].Owned);
        Assert.Equal(1, progress[0].Bank);
        Assert.False(progress[1].Satisfied);
        Assert.Equal(5, progress[1].Historical);

        inventory.NoteLoot("Que", "log", 1, "Rune", 1, true);
        Assert.All(SkyQuests.Progress(quest, inventory, "Que"), p => Assert.True(p.Satisfied));
        inventory.ApplyExport("Que", [], DateTime.UtcNow);
        Assert.All(SkyQuests.Progress(quest, inventory, "Que"), p => Assert.False(p.Satisfied));
    }
}
