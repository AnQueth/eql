using EqLog.Core.Tracking;

namespace EqLog.Core.Tests;

public class MoteTrackerTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 18, 0, 0);

    private static string L(int seconds, string msg)
    {
        return $"[{T0.AddSeconds(seconds):ddd MMM dd HH:mm:ss yyyy}] {msg}";
    }

    private static readonly string[] Log =
    [
        L(0, "You have entered Nektropos Castle 4 (Refined)."),
        L(10, "You have slain Guardian Golem!"),
        L(12, "--You have looted a Mote of Infinitesimal Potential from Guardian Golem's corpse.--"),
        L(20, "Guardian Golem has been slain by Feelon!"),
        L(30, "You have slain a chapter of misery!"),
        L(31, "--You have looted a Mote of Major Potential from a chapter of misery's corpse.--"),
        L(31, "--You have looted a Mote of Major Potential from a chapter of misery's corpse.--"),
        L(40, "--You have looted 11 Mote of Major Potential from Reward Chest.--"),
    ];

    [Fact]
    public void CountsTiersRecentDropsAndZoneRates()
    {
        var engine = new LogEngine(motes: new MoteStore()) { CharacterName = "Quethrosar" };
        engine.ProcessLines(Log);
        var m = engine.Snapshot(T0.AddHours(1)).Motes;

        Assert.Equal([new MoteCount("Major", 13), new MoteCount("Infinitesimal", 1)], m.Session);
        Assert.Equal(14, m.SessionTotal);
        Assert.Equal(14, m.AllTimeTotal);
        Assert.Equal(14, m.PerHour(T0.AddHours(1)), 1);

        var recent = m.Recent[0];
        Assert.Equal(("Major", 11, "Reward Chest"), (recent.Tier, recent.Quantity, recent.Source));

        // Reward chests aren't kills, so they don't get a drop rate.
        var golem = Assert.Single(m.ZoneRates, r => r.Mob == "Guardian Golem");
        Assert.Equal((2, 1, 0.5), (golem.Kills, golem.Drops, golem.DropRate));
        var chapter = Assert.Single(m.ZoneRates, r => r.Mob == "A chapter of misery");
        Assert.Equal((1, 2), (chapter.Kills, chapter.Drops));
        Assert.DoesNotContain(m.ZoneRates, r => r.Mob == "Reward Chest");
    }

    [Fact]
    public void RereadingTheLogDoesNotCountAllTimeTwice()
    {
        var store = new MoteStore();
        new LogEngine(motes: store) { CharacterName = "Quethrosar" }.ProcessLines(Log);

        // Next launch reads the same history plus one new same-second drop and one later drop.
        var engine = new LogEngine(motes: store) { CharacterName = "Quethrosar" };
        engine.ProcessLines([
            .. Log,
            L(40, "--You have looted a Mote of Minor Potential from Reward Chest.--"),
            L(50, "--You have looted a Mote of Greater Potential from Reward Chest.--"),
        ]);

        var m = engine.Snapshot(T0.AddHours(1)).Motes;
        Assert.Equal(16, m.AllTimeTotal);
        Assert.Equal(16, m.SessionTotal);
        Assert.Equal(2, Assert.Single(m.ZoneRates, r => r.Mob == "Guardian Golem").Kills);
    }

    [Theory]
    [InlineData("Mote of Major Potential", "Major")]
    [InlineData("Mote of Infinitesimal Potential", "Infinitesimal")]
    [InlineData("Motes of Transcendent Potential", "Transcendent")]
    [InlineData("Mote of Very Small Potential", "Very Small")]
    [InlineData("Mote of Potential", "Potential")]
    [InlineData("Swirling Mist", null)]
    public void TierOf(string item, string? tier)
    {
        Assert.Equal(tier, MoteTiers.TierOf(item));
    }

    [Theory]
    [InlineData("Mote of Major Potential", "Major")]
    [InlineData("Fragment of Major Potential", "Fragment of Major Potential")]
    [InlineData("Fragment of Greater Potential", "Fragment of Greater Potential")]
    [InlineData("Fragment of Potential", "Fragment of Potential")]
    [InlineData("Swirling Mist", null)]
    public void TrackedTierOfKeepsFragmentsSeparateFromMotes(string item, string? tier)
    {
        Assert.Equal(tier, MoteTiers.TrackedTierOf(item));
    }

    [Fact]
    public void EveryKnownMoteTierHasAFragmentCounterpart()
    {
        foreach (string tier in MoteValues.Tiers)
        {
            string fragment = MoteTiers.FragmentItemName(tier);

            Assert.Equal(fragment, MoteTiers.TrackedTierOf(fragment));
            Assert.Equal(fragment, MoteTiers.ItemName(fragment));
        }
    }

    [Fact]
    public void CountsFragmentLootSeparatelyFromSameTierMotes()
    {
        var engine = new LogEngine(motes: new MoteStore()) { CharacterName = "Quethrosar" };
        engine.ProcessLines([
            L(0, "You have entered Plane of Hate."),
            L(10, "You have slain Master Yael!"),
            L(11, "--You have looted a Mote of Major Potential from Master Yael's corpse.--"),
            L(12, "--You have looted a Fragment of Major Potential from Master Yael's corpse.--"),
            L(20, "--You have looted 4 Fragment of Major Potential from Reward Chest.--"),
        ]);

        var motes = engine.Snapshot(T0.AddMinutes(1)).Motes;
        Assert.Equal(1, Assert.Single(motes.Session, count => count.Tier == "Major").Count);
        Assert.Equal(5, Assert.Single(motes.Session, count => count.Tier == "Fragment of Major Potential").Count);
        Assert.Equal(6, motes.SessionTotal);
        Assert.Equal(1, Assert.Single(motes.ZoneRates, rate => rate.Mob == "Master Yael")
            .Tiers.Single(count => count.Tier == "Major").Count);
        Assert.Equal(1, Assert.Single(motes.ZoneRates, rate => rate.Mob == "Master Yael")
            .Tiers.Single(count => count.Tier == "Fragment of Major Potential").Count);
    }

    [Fact]
    public void UnknownTiersAreCountedAndSortedByCount()
    {
        var sorted = MoteTiers.Sort(new Dictionary<string, int> { ["Major"] = 2, ["Transcendent"] = 5, ["Minor"] = 0 });
        Assert.Equal(["Transcendent", "Major"], sorted.Select(c => c.Tier));
    }
}