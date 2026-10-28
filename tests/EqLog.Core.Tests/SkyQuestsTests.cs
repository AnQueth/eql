namespace EqLog.Core.Tests;

public class SkyQuestsTests
{
    [Fact]
    public void ParseDropSourcesReturnsMobsAndSkipsZoneLink()
    {
        const string page = """
            {{Itempage
            |itemname    = Azure Ring
            |dropsfrom =

            [[Plane of Sky]]

            *[[Gorgalosk]]

            |relatedquests =

            * [[Plane of Sky#Warrior Tests|Warrior Test of Skill]]
            }}
            """;

        Assert.Equal(["Gorgalosk"], SkyQuests.ParseDropSources(page));
    }

    [Fact]
    public void ParseDropSourcesIsEmptyWhenOnlyZoneListed()
    {
        const string page = """
            {{Itempage
            |dropsfrom      =

            * [[Plane of Sky]]

            |relatedquests  =
            }}
            """;

        Assert.Empty(SkyQuests.ParseDropSources(page));
    }

    [Fact]
    public void DroppedByMatchesItemNamesCaseInsensitively()
    {
        var catalog = new SkyQuestCatalog(DateTime.UtcNow, [],
            new Dictionary<string, IReadOnlyList<string>> { ["Azure Ring"] = ["Gorgalosk"] });

        Assert.Equal(["Gorgalosk"], catalog.DroppedBy("azure ring"));
        Assert.Empty(catalog.DroppedBy("Stone Amulet"));
    }

    [Fact]
    public void ItemLinksPreserveAliasesQuantitiesAndParentheticalNamesAcrossCacheReload()
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
            | [[Wind Rune Meda]]
            | <ul><li>2 x [[Horn|War Horn]] (3-Boss)</li><li>[[Soul Leech (Exaltation)]]</li></ul>
            |}
            """;
        var catalog = new SkyQuestCatalog(DateTime.UtcNow, SkyQuests.Parse(table));
        string json = System.Text.Json.JsonSerializer.Serialize(catalog);
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<SkyQuestCatalog>(json)!;
        var quest = Assert.Single(reloaded.Quests);
        Assert.Equal([new SkyItemLink("2 x War Horn (3-Boss)", "Horn"),
            new("Soul Leech (Exaltation)", "Soul Leech (Exaltation)")], quest.ItemLinks);
        Assert.Equal(2, quest.Requirements!.Single(r => r.Item == "Horn").Quantity);
        Assert.Equal("https://eqlwiki.com/Soul_Leech_%28Exaltation%29",
            QuestLookup.PageUrl(QuestLookup.ItemTitle(quest.ItemLinks![1].Item)));
    }

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
        Assert.Equal([new SkyItemLink("Woolen Mask (3-Boss)", "Woolen Mask"), new("War Horn", "Horn")],
            quests[0].ItemLinks);
        Assert.Equal([new SkyItemLink("Wind Rune Meda", "Wind Rune Meda")], quests[0].RuneLinks);
        Assert.EndsWith("#Shadow_Knight_Tests", quests[1].Url);
    }

    [Fact]
    public void ParseRejectsMissingTablesRatherThanShowingEmptySuccess()
    {
        Assert.Throws<FormatException>(() => SkyQuests.Parse("Wiki format changed"));
    }

    private static readonly SkyQuest WarriorSkill = new("Warrior", "Warrior Test of Skill", "Azure Ruby Ring", "Torgon Blademaster", "skill",
        "Wind Rune Neza", "Azure Ring (3-Gorga)", "url", [new("Wind Rune Neza", 1), new("Azure Ring", 1)],
        [new("Wind Rune Neza", "Wind Rune Neza")], [new("Azure Ring (3-Gorga)", "Azure Ring")]);

    private static readonly SkyQuest ClericBlessing = new("Cleric", "Cleric Test of Blessing", "Silver Hoop", "Tagnar", "blessing",
        "Wind Rune Neza", "Azure Ring", "url", [new("Wind Rune Neza", 1), new("Azure Ring", 2)]);

    private static readonly SkyQuestCatalog DropCatalog = new(DateTime.UtcNow, [WarriorSkill, ClericBlessing],
        new Dictionary<string, IReadOnlyList<string>> { ["Azure Ring"] = ["Gorgalosk"], ["Wind Rune Neza"] = [] });

    [Theory]
    [InlineData("", true)]
    [InlineData("test of skill", true)]
    [InlineData("ruby", true)]
    [InlineData("azure ring", true)]
    [InlineData("3-gorga", true)]
    [InlineData("gorgalosk", true)]
    [InlineData("blessing", false)]
    public void MatchesSearchesQuestRewardItemsLabelsAndDroppers(string filter, bool expected)
    {
        Assert.Equal(expected, SkyQuests.Matches(WarriorSkill, filter, DropCatalog));
    }

    [Fact]
    public void ByDropperGroupsItemsAcrossClassesWithUnknownDroppersLast()
    {
        var groups = SkyQuests.ByDropper(DropCatalog.Quests, DropCatalog);

        Assert.Equal(["Gorgalosk", ""], groups.Select(g => g.Mob));
        var ring = Assert.Single(groups[0].Items);
        Assert.Equal("Azure Ring (3-Gorga)", ring.Label);
        Assert.Equal([WarriorSkill, ClericBlessing], ring.Quests);
        Assert.Equal("Wind Rune Neza", Assert.Single(groups[1].Items).Item);
    }

    [Fact]
    public void ByDropperFilterKeepsWholeMobOrOnlyMatchingQuests()
    {
        var byMob = SkyQuests.ByDropper(DropCatalog.Quests, DropCatalog, "gorgalosk");
        Assert.Equal(2, Assert.Single(Assert.Single(byMob).Items).Quests.Count);

        var byQuest = SkyQuests.ByDropper(DropCatalog.Quests, DropCatalog, "blessing");
        Assert.All(byQuest.SelectMany(g => g.Items), i => Assert.Equal([ClericBlessing], i.Quests));
        Assert.Equal(2, byQuest.Count);
    }

    [Fact]
    public void KeyDistinguishesClassesAndProgressAllMatchesSingleQuestProgress()
    {
        Assert.Equal("Warrior|Warrior Test of Skill", SkyQuests.Key(WarriorSkill));
        var inventory = new QuestInventory
        {
            Items = [new() { Character = "Que", Item = "Azure Ring +1", Owned = 1, Bank = 1 }],
        };

        var all = SkyQuests.ProgressAll(DropCatalog.Quests, inventory, "Que");

        Assert.True(all[WarriorSkill][1].Satisfied);
        Assert.False(all[ClericBlessing][1].Satisfied);
        Assert.Equal(SkyQuests.Progress(ClericBlessing, inventory, "Que"), all[ClericBlessing]);
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
        Assert.Equal(progress.Carried + progress.Bank + progress.Hoard + progress.Depot + progress.OtherStorage + progress.Collection, progress.Owned);
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
