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

    [Fact]
    public async Task SavesAndLoads()
    {
        string path = Path.Combine(Path.GetTempPath(), $"motes-{Guid.NewGuid():N}.json");
        try
        {
            var store = new MoteStore();
            new LogEngine(motes: store) { CharacterName = "Quethrosar" }.ProcessLines(Log);
            await store.SaveAsync(path);

            var loaded = await MoteStore.LoadAsync(path);
            Assert.Equal(13, loaded.Totals("quethrosar").Single(c => c.Tier == "Major").Count);
            Assert.Single(loaded.ZoneRates("Nektropos Castle 2 (Adaptive)"), r => r.Mob == "Guardian Golem");

            new LogEngine(motes: loaded) { CharacterName = "Quethrosar" }.ProcessLines(Log);
            Assert.Equal(14, loaded.Totals("Quethrosar").Sum(c => c.Count));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("Mote of Major Potential", "Major")]
    [InlineData("Mote of Infinitesimal Potential", "Infinitesimal")]
    [InlineData("Motes of Transcendent Potential", "Transcendent")]
    [InlineData("Mote of Very Small Potential", "Very Small")]
    [InlineData("Swirling Mist", null)]
    public void TierOf(string item, string? tier)
    {
        Assert.Equal(tier, MoteTiers.TierOf(item));
    }

    [Fact]
    public void UnknownTiersAreCountedAndSortedByCount()
    {
        var sorted = MoteTiers.Sort(new Dictionary<string, int> { ["Major"] = 2, ["Transcendent"] = 5, ["Minor"] = 0 });
        Assert.Equal(["Transcendent", "Major"], sorted.Select(c => c.Tier));
    }
}