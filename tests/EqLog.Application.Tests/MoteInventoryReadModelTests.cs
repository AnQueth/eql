using EqLog.Application.Inventory;
using EqLog.Application.Motes;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;

namespace EqLog.Application.Tests;

public sealed class MoteInventoryReadModelTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 19, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, 0)]
    public void EmptyInventoryAlwaysPreparesEveryKnownTierWithExportDependentOwnedCounts(bool exported, int? expected)
    {
        var inventory = MoteInventoryReadModel.Capture([], "Queth", exported ? Now : null);
        var model = inventory.Prepare(Snapshot(), Now);
        Assert.Equal(["Infinitesimal", "Minor", "Lesser", "Potential", "Major", "Greater",
            "Superior", "Grand", "Ascendant", "Infinite"], model.Tiers.Select(t => t.Tier));
        Assert.All(model.Tiers, tier =>
        {
            Assert.Equal(expected, tier.Owned);
            Assert.Equal(0, tier.SessionCount);
            Assert.Equal(0, tier.AllTimeCount);
        });
        Assert.Equal(expected, model.OwnedTotal);
        Assert.Equal(0, model.PerHour);
        Assert.Equal(0, model.SessionTotal);
        Assert.Equal(0, model.AllTimeTotal);
        Assert.Equal(exported ? Now : (DateTime?)null, model.ExportTime);
    }

    [Fact]
    public void CaptureUsesOnlyConfirmedOwnedForTheSelectedCharacterAndDetachesItsCounts()
    {
        InventoryItemSummary[] items =
        [
            Item("Queth", "Mote of Major Potential", 3),
            Item("QUETH", "mote of major potential", 2),
            Item("SomeoneElse", "Mote of Major Potential", 999),
            Item("Queth", "Mote of Condensing Potential", 4),
            Item("Queth", "Cloak", 500),
            Item("Queth", "Mote of Minor Potential", 0),
        ];
        var captured = MoteInventoryReadModel.Capture(items, "queth");
        items[0] = Item("Queth", "Mote of Major Potential", 900);
        var model = captured.Prepare(Snapshot(), Now);
        Assert.Equal(5, Assert.Single(model.Tiers, t => t.Tier == "Major").Owned);
        Assert.Equal(0, Assert.Single(model.Tiers, t => t.Tier == "Minor").Owned);
        Assert.Null(Assert.Single(model.Tiers, t => t.Tier == "Greater").Owned);
        Assert.Equal(4, Assert.Single(model.Tiers, t => t.Tier == "Condensing").Owned);
        Assert.Equal(9, model.OwnedTotal);
        Assert.Equal(new Dictionary<int, int> { [5] = 5 }, captured.OwnedByRank);
        Assert.Throws<NotSupportedException>(() =>
        {
            ((IDictionary<int, int>)captured.OwnedByRank)[5] = 900;
        });
    }

    [Theory]
    [InlineData("Infinitesimal", "Mote of Infinitesimal Potential", 1, 0, 1)]
    [InlineData("Potential", "Mote of Potential", 4, 3, 8)]
    [InlineData("Major", "Mote of Major Potential", 5, 4, 16)]
    [InlineData("Infinite", "Mote of Infinite Potential", 10, 9, 512)]
    public void KnownTiersHaveTypedUpgradeValues(
        string name, string item, int points, int limit, int spellPoints)
    {
        var model = MoteInventoryReadModel.Empty.Prepare(Snapshot(), Now);
        var tier = Assert.Single(model.Tiers, t => t.Tier == name);
        Assert.Equal(item, tier.ItemName);
        Assert.Equal(points, tier.Rank);
        Assert.Equal(points, tier.EquipmentPoints);
        Assert.Equal(limit, tier.MaximumEquipmentRank);
        Assert.Equal(spellPoints, tier.SpellPoints);
    }

    [Fact]
    public void UnknownTiersFromInventoryAndLogFollowKnownRanksAndHaveNoInventedUpgradeValue()
    {
        var inventory = MoteInventoryReadModel.Capture([Item("Queth", "Mote of Condensing Potential", 2)], "Queth", Now);
        var snapshot = Snapshot() with
        {
            Session = new[] { new MoteCount("Major", 3), new MoteCount("Void-Touched", 1) },
            AllTime = new[] { new MoteCount("Infinite", 8), new MoteCount("Mystery", 7), new MoteCount("Major", 12) },
        };
        var model = inventory.Prepare(snapshot, Now);
        Assert.Equal(["Condensing", "Mystery", "Void-Touched"], model.Tiers.Skip(10).Select(t => t.Tier));
        Assert.Equal(3, Assert.Single(model.Tiers, t => t.Tier == "Major").SessionCount);
        Assert.Equal(12, Assert.Single(model.Tiers, t => t.Tier == "Major").AllTimeCount);
        Assert.All(model.Tiers.Skip(10), tier =>
        {
            Assert.Null(tier.Rank);
            Assert.Null(tier.EquipmentPoints);
            Assert.Null(tier.SpellPoints);
            Assert.Null(tier.MaximumEquipmentRank);
        });
    }

    [Fact]
    public void RecentDropsRemainInSnapshotOrderAndRatesAndSessionSpeedArePrepared()
    {
        var recent = Enumerable.Range(0, 6).Select(index =>
            new MoteDrop(Now.AddMinutes(-index), index == 0 ? Names.You : "Other", index == 0 ? "Major" : "Minor",
                index == 0 ? 2 : 1, "a rat")).ToArray();
        var snapshot = Snapshot() with
        {
            SessionStart = Now.AddHours(-2),
            SessionTotal = 6,
            AllTimeTotal = 10,
            Recent = recent,
            ZoneRates = new[] { new MoteMobRate("a rat", 2, 1, []), new MoteMobRate("a chest", 0, 2, []) },
        };
        var model = MoteInventoryReadModel.Empty.Prepare(snapshot, Now);
        Assert.Equal(3, model.PerHour);
        Assert.Equal(6, model.SessionTotal);
        Assert.Equal(10, model.AllTimeTotal);
        Assert.Equal(5, model.Recent.Count);
        Assert.Equal(new MoteRecentReadModel("Major", Names.You, 2, "a rat", Now,
            "Mote of Major Potential"), model.Recent[0]);
        Assert.Equal(new MoteRecentReadModel("Minor", "Other", 1, "a rat", Now.AddMinutes(-1),
            "Mote of Minor Potential"), model.Recent[1]);
        Assert.Equal(new MoteRateReadModel("a rat", 1, 2, 0.5), model.ZoneRates[0]);
        Assert.Equal(new MoteRateReadModel("a chest", 2, 0, null), model.ZoneRates[1]);
    }

    private static InventoryItemSummary Item(string character, string name, int owned)
    {
        return new InventoryItemSummary(character, name, owned, 100, 100, 100, 100, 100, 100, 100, 100, Now);
    }

    private static MoteSnapshot Snapshot()
    {
        return new MoteSnapshot(null, null, [], 0, [], 0, [], []);
    }
}
