using EqLog.Application.Inventory;
using EqLog.Application.Motes;
using EqLog.Core.Parsing;
using EqLog.Core;
using EqLog.Core.Tracking;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class MoteOverlayTests
{
    [Fact]
    public void PlannerUsesOwnedResourcesAndReportsMissingMotes()
    {
        var request = Request(4, 6);
        var model = MotePlanningService.Prepare(request, new Dictionary<int, int> { [5] = 2 }, []);

        Assert.Equal(2, model.Steps.Count);
        Assert.True(model.TotalRequiredPoints > 0);
        Assert.NotEmpty(model.Missing);
        Assert.Equal(MoteUpgradeKind.Equipment, model.Kind);
    }

    [Fact]
    public void PlannerValidatesPointsAndStopsAtSelectedTarget()
    {
        var request = Request(0, 3) with
        {
            Points = "not-a-number",
            Page = MotePlanPage.Farm,
            Fodder = "1024",
        };
        var model = MotePlanningService.Prepare(request, new Dictionary<int, int>(), []);

        Assert.Equal(MoteInputStatus.Invalid, model.PointsStatus);
        Assert.Equal(MoteInputStatus.Invalid, model.FodderStatus);
        Assert.Equal(3, model.FarmTargets.Last().TargetRank);
        Assert.Equal(0, model.Progress);
    }

    [Fact]
    public void InventoryQueriesSeparateTargetRanksFromUsableSpareCopies()
    {
        InventoryExportItem[] rows =
        [
            new("Held", "Cloak of Scales +4", 1, 1, InventoryStorage.Carried),
            new("Bank1", "Cloak of Scales +9", 2, 1, InventoryStorage.Bank),
            new("Bank2", "Cloak of Scales +4", 3, 2, InventoryStorage.Bank),
            new("Equipment", "Cloak of Scales (Exaltation)", 4, 1, InventoryStorage.Other, Collection: true),
        ];

        Assert.Equal(9, MotePlanningQueries.TargetRank(rows, "Cloak of Scales"));
        Assert.Equal([4, 4, 4, 9], MotePlanningQueries.CopyRanks(rows, "Cloak of Scales"));
        Assert.Contains("Cloak of Scales", MotePlanningQueries.TargetNames(rows, "cloak"));
        var copies = MotePlanningQueries.SelectCopies(rows, "Cloak of Scales", 4);
        Assert.True(copies.RemovedTarget);
        Assert.Equal([4, 4], copies.Spares);
        Assert.Equal([9], copies.HigherCopies);
    }

    [Fact]
    public void SpellPlansDoNotIncludeEquipmentCopiesOrFarmTargets()
    {
        var request = Request(0, 3) with
        {
            Kind = MoteUpgradeKind.Spell,
            Page = MotePlanPage.Farm,
        };
        var model = MotePlanningService.Prepare(request, new Dictionary<int, int> { [1] = 2 }, [Copy(4)]);

        Assert.Empty(model.FarmTargets);
        Assert.Empty(model.CopiesUsed);
        Assert.Equal(MoteFeedingStatus.NotApplicable, model.FeedingStatus);
    }

    [Theory]
    [InlineData(false, false, "?")]
    [InlineData(true, false, "?")]
    [InlineData(true, true, "0")]
    public void Motes_WithoutDropsOrOwnedMotes_ShowsEveryKnownTier(bool hasInventory, bool hasExport, string expectedOwned)
    {
        var now = new DateTime(2026, 10, 21, 18, 0, 0, DateTimeKind.Utc);
        QuestInventory? inventory = hasInventory ? new QuestInventory() : null;
        if (inventory is not null && hasExport)
        {
            inventory.ApplyExport("Que", [], now);
        }
        var engine = new LogEngine { CharacterName = "Que" };

        var content = RenderMotes(engine.Snapshot(now), engine, inventory);
        var tiers = TierRows(content);

        Assert.Equal(MoteValues.Tiers.SelectMany(tier => new[] { tier, MoteTiers.FragmentItemName(tier) }),
            tiers.Select(row => row.Left));
        Assert.Equal(MoteValues.MaxRank * 2, tiers.Length);
        Assert.All(tiers, row =>
        {
            Assert.Equal("0  (0)", row.Right);
            Assert.Equal(expectedOwned, row.ExtraColumn);
            int rank = Assert.IsType<int>(MoteValues.RankOf(row.Left));
            int multiplier = MoteTiers.IsFragment(row.Left) ? 2 : 1;
            Assert.Equal($"{MoteValues.ItemPoints(rank) * multiplier}/{MoteValues.SpellPoints(rank) * multiplier} " +
                $"≤+{MoteValues.ItemMaxTier(rank)}", row.ValueColumn);
            Assert.Equal(MoteTiers.ItemName(row.Left), row.LootItem);
        });
        var total = Assert.Single(content.Rows, row => row.Left == "Total");
        Assert.Equal("0  (0)", total.Right);
        Assert.Equal(expectedOwned, total.ExtraColumn);
    }

    [Fact]
    public void Motes_WithPartialInventory_ShowsZeroForEveryUnownedTier()
    {
        var now = new DateTime(2026, 10, 21, 18, 0, 0, DateTimeKind.Utc);
        var inventory = new QuestInventory();
        inventory.ApplyExport("Que",
        [
            new InventoryExportItem("General 1", "Mote of Greater Potential", 1, 3, InventoryStorage.Carried),
        ], now);
        var engine = new LogEngine { CharacterName = "Que" };

        var content = RenderMotes(engine.Snapshot(now), engine, inventory);
        var tiers = TierRows(content);

        Assert.Equal(MoteValues.Tiers.SelectMany(tier => new[] { tier, MoteTiers.FragmentItemName(tier) }),
            tiers.Select(row => row.Left));
        Assert.Equal("3", Assert.Single(tiers, row => row.Left == "Greater").ExtraColumn);
        Assert.All(tiers.Where(row => row.Left != "Greater"), row =>
        {
            Assert.Equal("0", row.ExtraColumn);
        });
        Assert.Equal("3", Assert.Single(content.Rows, row => row.Left == "Total").ExtraColumn);
    }

    [Fact]
    public void Motes_OrdersTiersFromAllSourcesByRank_ThenUnknownNames()
    {
        var now = new DateTime(2026, 10, 21, 18, 0, 0);
        var session = new MoteCount[]
        {
            new("Superior", 2),
            new("Lesser", 3),
            new("Potential", 5),
            new("Greater", 1),
            new("transcendent", 100),
        };
        var allTime = new MoteCount[]
        {
            new("Infinite", 1000),
            new("Minor", 800),
            new("Major", 400),
            new("Greater", 50),
            new("Ascendant", 2),
            new("Zulu", 2000),
        };
        var engine = new LogEngine { CharacterName = "Que" };
        var snapshot = engine.Snapshot(now.AddHours(1)) with
        {
            Motes = new MoteSnapshot(null, now, session, session.Sum(c => c.Count),
                allTime, allTime.Sum(c => c.Count), [], []),
        };
        var inventory = new QuestInventory
        {
            ExportTimes = new() { ["QUE"] = now },
            Items =
            [
                new() { Character = "qUE", Item = "Mote of Infinitesimal Potential", Owned = 7 },
                new() { Character = "Que", Item = "Mote of Grand Potential", Owned = 3 },
                new() { Character = "Que", Item = "Mote of minor Potential", Owned = 9 },
                new() { Character = "Que", Item = "Mote of Alpha Potential", Owned = 2 },
                new() { Character = "Other", Item = "Mote of OtherOnly Potential", Owned = 100 },
                new() { Character = "Que", Item = "A sword", Owned = 1 },
            ],
        };

        var content = RenderMotes(snapshot, engine, inventory);
        var tiers = TierRows(content);

        string[] knownAndUnknownTiers = MoteValues.Tiers.Concat(["Alpha", "transcendent", "Zulu"]).ToArray();
        Assert.Equal(knownAndUnknownTiers.SelectMany(tier => new[] { tier, MoteTiers.FragmentItemName(tier) }),
            tiers.Select(row => row.Left));

        Assert.Equal(("Tier", "Gear/Spell", "Session (all)", "Owned"),
            (content.Rows[0].Left, content.Rows[0].ValueColumn, content.Rows[0].Right, content.Rows[0].ExtraColumn));
        Assert.Equal(OverlayRowTone.TableHeader, content.Rows[0].Tone);
        Assert.Equal(OverlayRowTone.TableTotal, content.Rows.Single(row => row.Left == "Total").Tone);
        for (int index = 0; index < tiers.Length; index++)
        {
            int group = index / 2;
            var expectedTone = OverlayRowTone.TableRow;
            if (group % 2 == 1)
            {
                expectedTone = OverlayRowTone.TableRowBanded;
            }

            Assert.Equal(expectedTone, tiers[index].Tone);
            Assert.Equal(index % 2 == 1, tiers[index].Nested);
        }
        Assert.Contains("the first number is for gear; the second is for spells", content.Rows[0].ToolTip);
        Assert.Contains("5/16 ≤+4 means 5 gear points at +4 or lower", content.Rows[0].ToolTip);
        var byTier = tiers.ToDictionary(row => row.Left, StringComparer.OrdinalIgnoreCase);
        Assert.All(tiers, row =>
        {
            Assert.Equal(MoteTiers.TrackedTierOf(row.LootItem!), row.Left);
        });
        var expectedValues = knownAndUnknownTiers.SelectMany(tier =>
        {
            int? rank = MoteValues.RankOf(tier);
            if (rank is null)
            {
                return new[] { "?", "?" };
            }

            return new[]
            {
                $"{MoteValues.ItemPoints(rank.Value)}/{MoteValues.SpellPoints(rank.Value)} ≤+{MoteValues.ItemMaxTier(rank.Value)}",
                $"{MoteValues.ItemPoints(rank.Value) * 2}/{MoteValues.SpellPoints(rank.Value) * 2} ≤+{MoteValues.ItemMaxTier(rank.Value)}",
            };
        });
        Assert.Equal(expectedValues, tiers.Select(row => row.ValueColumn));
        Assert.Contains("Adds 5 points to gear at +4 or lower.", byTier["Major"].ToolTip);
        Assert.Contains("No upgrade value is known for Alpha", byTier["Alpha"].ToolTip);
        Assert.Contains("Adds 10 points to gear at +4 or lower.", byTier["Fragment of Major Potential"].ToolTip);
        Assert.Equal(("0  (0)", "7"), (byTier["Infinitesimal"].Right, byTier["Infinitesimal"].ExtraColumn));
        Assert.Equal(("0  (800)", "9"), (byTier["Minor"].Right, byTier["Minor"].ExtraColumn));
        Assert.Equal(("5  (0)", "0"), (byTier["Potential"].Right, byTier["Potential"].ExtraColumn));
        Assert.Equal(("1  (50)", "0"), (byTier["Greater"].Right, byTier["Greater"].ExtraColumn));
        var total = Assert.Single(content.Rows, row => row.Left == "Total");
        Assert.Equal(("111  (4252)", "21"), (total.Right, total.ExtraColumn));
        Assert.Equal("", total.ValueColumn);
    }

    [Fact]
    public void Motes_FromLogWithoutInventory_OrdersTiersButKeepsRecentDropsNewestFirst()
    {
        var engine = new LogEngine { CharacterName = "Que" };
        engine.ProcessLines(
        [
            "[Wed Oct 21 18:00:10 2026] --You have looted 20 Mote of Major Potential from Reward Chest.--",
            "[Wed Oct 21 18:00:20 2026] --You have looted a Mote of Infinitesimal Potential from Reward Chest.--",
        ]);
        var snapshot = engine.Snapshot(new DateTime(2026, 10, 21, 19, 0, 0));

        var content = RenderMotes(snapshot, engine, inventory: null);
        var tiers = TierRows(content);

        Assert.Equal("Major", snapshot.Motes.Session[0].Tier);
        Assert.Equal(MoteValues.Tiers.SelectMany(tier => new[] { tier, MoteTiers.FragmentItemName(tier) }),
            tiers.Select(row => row.Left));
        Assert.Equal("1  (1)", Assert.Single(tiers, row => row.Left == "Infinitesimal").Right);
        Assert.Equal("20  (20)", Assert.Single(tiers, row => row.Left == "Major").Right);
        Assert.All(tiers.Where(row => row.Left != "Infinitesimal" && row.Left != "Major"), row =>
        {
            Assert.Equal("0  (0)", row.Right);
        });
        Assert.All(tiers, row =>
        {
            Assert.Equal("?", row.ExtraColumn);
        });
        var recent = content.Rows.SkipWhile(row => row.Left != "Recent").Skip(1).ToArray();
        Assert.Equal(["Mote of Infinitesimal Potential", "Mote of Major Potential"], recent.Select(row => row.LootItem));
    }

    [Fact]
    public void MotesRenderFragmentsWithDoubledUpgradeValues()
    {
        var engine = new LogEngine { CharacterName = "Que" };
        engine.ProcessLines(
        [
            "[Wed Oct 21 18:00:10 2026] --You have looted a Mote of Major Potential from Reward Chest.--",
            "[Wed Oct 21 18:00:20 2026] --You have looted a Fragment of Major Potential from Reward Chest.--",
        ]);
        var snapshot = engine.Snapshot(new DateTime(2026, 10, 21, 19, 0, 0));

        var content = RenderMotes(snapshot, engine, inventory: null);
        var mote = Assert.Single(TierRows(content), row => row.Left == "Major");
        var fragment = Assert.Single(TierRows(content), row => row.Left == "Fragment of Major Potential");

        Assert.Equal("5/16 ≤+4", mote.ValueColumn);
        Assert.Equal("Fragment of Major Potential", fragment.LootItem);
        Assert.Equal("10/32 ≤+4", fragment.ValueColumn);
        Assert.Contains("Adds 10 points to gear at +4 or lower.", fragment.ToolTip);
        Assert.Contains("Fragments add twice the points", content.Rows[0].ToolTip);
    }

    [Theory]
    [InlineData("Infinitesimal", "Mote of Infinitesimal Potential", "1/1 ≤+0", 1, 0, 1)]
    [InlineData("Potential", "Mote of Potential", "4/8 ≤+3", 4, 3, 8)]
    [InlineData("Major", "Mote of Major Potential", "5/16 ≤+4", 5, 4, 16)]
    [InlineData("Infinite", "Mote of Infinite Potential", "10/512 ≤+9", 10, 9, 512)]
    public void MotesBuildExactValuesAndExplanationsFromTypedNumbers(
        string name, string item, string value, int rank, int limit, int spellPoints)
    {
        var now = new DateTime(2026, 11, 1, 19, 0, 0, DateTimeKind.Utc);
        var engine = new LogEngine { CharacterName = "Que" };
        var content = RenderMotes(engine.Snapshot(now), engine, inventory: null);
        var tier = Assert.Single(TierRows(content), row => row.Left == name);
        var total = Assert.Single(content.Rows, row => row.Left == "Total");
        string inventoryTip = "No inventory export: export inventory to confirm owned counts." +
            "\nOwned excludes collection, installed items and historical drops." +
            "\nLoot logs do not change Owned. Run /outputfile inventory after looting, spending, or moving motes or fragments to refresh.";
        Assert.Equal(inventoryTip, total.ToolTip);
        Assert.Equal(item, tier.LootItem);
        Assert.Equal(value, tier.ValueColumn);
        Assert.Equal($"{item} (rank {rank})\n" +
            $"Adds {rank} points to gear at +{limit} or lower.\n" +
            $"Adds {spellPoints} points to spells at any rank.\n\n" + inventoryTip, tier.ToolTip);
        Assert.Equal("Fragments add twice the points of their named mote, with the same gear rank limit. " +
            "In the Gear/Spell column, the first number is for gear; the second is for spells.\n" +
            "The ≤+N part means this resource works on gear at +N or lower. Motes and fragments work on spells at any rank.\n" +
            "Example: 5/16 ≤+4 means 5 gear points at +4 or lower, or 16 spell points at any rank. " +
            "Values are from the EQLWiki Mote Guide.\n\n" + inventoryTip, content.Rows[0].ToolTip);
    }

    [Fact]
    public void MotesRenderRecentDropsRatesAndSessionSpeedWithOriginalFormatting()
    {
        var now = new DateTime(2026, 11, 1, 19, 0, 0, DateTimeKind.Utc);
        var engine = new LogEngine { CharacterName = "Que" };
        var recent = Enumerable.Range(0, 6).Select(index =>
            new MoteDrop(now.AddMinutes(-index), index == 0 ? Names.You : "Other", index == 0 ? "Major" : "Minor",
                index == 0 ? 2 : 1, "a rat")).ToArray();
        var snapshot = engine.Snapshot(now) with
        {
            Motes = new MoteSnapshot(null, now.AddHours(-2), [], 6, [], 10, recent,
                [new MoteMobRate("a rat", 2, 1, []), new MoteMobRate("a chest", 0, 2, [])]),
        };
        var inventory = new QuestInventory
        {
            ExportTimes = new() { ["QUE"] = now },
        };
        var content = RenderMotes(snapshot, engine, inventory);
        Assert.Equal("6 this session  3/hr", content.Subtitle);
        var total = Assert.Single(content.Rows, row => row.Left == "Total");
        Assert.Equal("6  (10)", total.Right);
        Assert.StartsWith($"Export {now.ToLocalTime():g}; owned counts are confirmed by this export.", total.ToolTip);
        var drops = content.Rows.SkipWhile(row => row.Left != "Recent").Skip(1)
            .TakeWhile(row => row.Left != "Drops here").ToArray();
        Assert.Equal(5, drops.Length);
        Assert.Equal(("Major x2  a rat", now.ToString("HH:mm"), "Mote of Major Potential"),
            (drops[0].Left, drops[0].Right, drops[0].LootItem));
        Assert.Equal(("[Other] Minor  a rat", now.AddMinutes(-1).ToString("HH:mm"), "Mote of Minor Potential"),
            (drops[1].Left, drops[1].Right, drops[1].LootItem));
        var rates = content.Rows.SkipWhile(row => row.Left != "Drops here").Skip(1).ToArray();
        Assert.Equal($"1/2  {0.5:P0}", rates[0].Right);
        Assert.Equal("2/0", rates[1].Right);
    }

    private static OverlayContent RenderMotes(EngineSnapshot snapshot, LogEngine engine, QuestInventory? inventory)
    {
        var readModel = inventory is null ? null : new InventoryReadModel(inventory);
        DateTime? exportTime = readModel is not null &&
            readModel.ExportTimes.TryGetValue(snapshot.CharacterName.ToUpperInvariant(), out DateTime exported)
            ? exported : null;
        var moteInventory = MoteInventoryReadModel.Capture(readModel?.Items ?? [], snapshot.CharacterName, exportTime);
        var view = new OverlayView(new HashSet<ChatChannel>(), [], Inventory: readModel, MoteInventory: moteInventory);
        var presenter = Assert.Single(OverlayPresenters.All, definition => definition.Key == "motes");
        return presenter.Render(snapshot, EngineQuerySnapshot.Capture(engine), view);
    }

    private static OverlayRow[] TierRows(OverlayContent content)
    {
        return content.Rows.Skip(1).TakeWhile(row => row.Left != "Total").ToArray();
    }

    private static MotePlanningRequest Request(int start, int target)
    {
        return new MotePlanningRequest(MoteUpgradeKind.Equipment, start, target, "0", "0", 0,
            false, true, true, true, MotePlanPage.Upgrade, "Cloak", true, []);
    }

    private static InventoryExportItem Copy(int rank, int count = 1)
    {
        return new InventoryExportItem("Bank1", $"Cloak +{rank}", 1, count, InventoryStorage.Bank);
    }
}


