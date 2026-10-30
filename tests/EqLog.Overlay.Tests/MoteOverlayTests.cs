using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class MoteOverlayTests
{
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

        Assert.Equal(MoteValues.Tiers, tiers.Select(row => row.Left));
        Assert.Equal(MoteValues.MaxRank, tiers.Length);
        Assert.All(tiers, row =>
        {
            Assert.Equal("0  (0)", row.Right);
            Assert.Equal(expectedOwned, row.ExtraColumn);
            Assert.False(string.IsNullOrEmpty(row.ValueColumn));
            Assert.NotEqual("?", row.ValueColumn);
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

        Assert.Equal(MoteValues.Tiers, tiers.Select(row => row.Left));
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

        Assert.Equal(
            ["Infinitesimal", "Minor", "Lesser", "Potential", "Major", "Greater", "Superior", "Grand", "Ascendant", "Infinite",
                "Alpha", "transcendent", "Zulu"],
            tiers.Select(row => MoteTiers.TierOf(row.LootItem!)));

        Assert.Equal(("Tier", "Gear/Spell", "session (all)", "Owned"),
            (content.Rows[0].Left, content.Rows[0].ValueColumn, content.Rows[0].Right, content.Rows[0].ExtraColumn));
        Assert.Contains("the first number is for gear; the second is for spells", content.Rows[0].ToolTip);
        Assert.Contains("5/16 ≤+4 means 5 gear points at +4 or lower", content.Rows[0].ToolTip);
        var byTier = tiers.ToDictionary(row => MoteTiers.TierOf(row.LootItem!)!, StringComparer.OrdinalIgnoreCase);
        Assert.All(tiers, row =>
        {
            Assert.Equal(MoteTiers.TierOf(row.LootItem!), row.Left);
        });
        Assert.Equal(
            ["1/1 ≤+0", "1/2 ≤+1", "2/4 ≤+2", "4/8 ≤+3", "5/16 ≤+4",
                "6/32 ≤+5", "7/64 ≤+6", "8/128 ≤+7", "9/256 ≤+8", "10/512 ≤+9", "?", "?", "?"],
            tiers.Select(row => row.ValueColumn));
        Assert.Contains("Adds 5 points to gear at +4 or lower.", byTier["Major"].ToolTip);
        Assert.Contains("No upgrade value is known for Alpha", byTier["Alpha"].ToolTip);
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
        Assert.Equal(MoteValues.Tiers, tiers.Select(row => MoteTiers.TierOf(row.LootItem!)));
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

    private static OverlayContent RenderMotes(EngineSnapshot snapshot, LogEngine engine, QuestInventory? inventory)
    {
        var view = new OverlayView(new HashSet<ChatChannel>(), [], Inventory: inventory);
        var presenter = Assert.Single(OverlayPresenters.All, definition => definition.Key == "motes");
        return presenter.Render(snapshot, engine, view);
    }

    private static OverlayRow[] TierRows(OverlayContent content)
    {
        return content.Rows.Skip(1).TakeWhile(row => row.Left != "Total").ToArray();
    }
}
