using EqLog.Core.Tracking;

namespace EqLog.Core.Tests;

public sealed class MobResistanceResetTests
{
    private static readonly DateTime Start = new(2026, 10, 31, 13, 0, 0);

    [Fact]
    public void ResetResistances_ClearsOnlySelectedZoneAndMobWhilePreservingCaches()
    {
        var store = CreateStore();
        store.Record("log", 1, "Sky", "A rat", "Conflagration", true, Start);
        store.Record("log", 2, "Sky", "A rat", "Conflagration", false, Start, "Tester", 1000);
        store.Record("log", 3, "Sky", "A rat", "Unknown spell", true, Start);
        store.Record("log", 4, "Sky", "A rat", "Ignite Blood", false, Start, damageOverTime: true);
        store.Record("log", 5, "Fear", "A rat", "Conflagration", true, Start);
        store.Record("log", 6, "Sky", "A bat", "Conflagration", true, Start);
        var wiki = MobWikiLookup.ParseMob("A rat", "{{Namedmobpage\n| FR = 50\n}}", Start);
        store.SetWiki("A rat", wiki);

        store.ResetResistances(" sky ", " A  RAT ", Start.AddSeconds(1));

        var reset = store.Get("Sky", "A rat");
        Assert.Empty(reset.Spells);
        Assert.Equal(0, reset.Unclassified);
        Assert.All(reset.Observations, observation =>
        {
            Assert.Equal("Unknown", observation.Status);
            Assert.Equal(0, observation.OtherResisted);
            Assert.Equal(0, observation.UnassessedDamage);
        });
        Assert.Same(wiki, reset.Wiki);
        Assert.NotNull(store.GetSpell("Conflagration"));
        Assert.Equal(1, Fire(store, "Fear", "A rat").Resisted);
        Assert.Equal(1, Fire(store, "Sky", "A bat").Resisted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetResistances_PersistsCutoffAndRejectsOldCopiedLogs(bool dot)
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "mob-resistances.json");
        Directory.CreateDirectory(folder);
        try
        {
            var store = CreateStore();
            string spell = dot ? "Ignite Blood" : "Conflagration";
            store.Record("original", 1, "Sky", "A rat", spell, true, Start, damageOverTime: dot);
            var cutoff = Start.AddSeconds(10);
            store.ResetResistances("Sky", "A rat", cutoff);
            await store.SaveAsync(path);
            var loaded = await MobResistanceStore.LoadAsync(path);

            loaded.Record("copied", 1, "Sky", "A rat", spell, true, Start, damageOverTime: dot);
            loaded.Record("copied", 2, "Sky", "A rat", spell, true, cutoff, damageOverTime: dot);
            loaded.Record("copied", 3, "Fear", "A rat", spell, true, Start, damageOverTime: dot);
            Assert.Empty(loaded.Get("Sky", "A rat").Spells);
            Assert.Equal(1, Fire(loaded, "Fear", "A rat").Resisted);

            loaded.Record("copied", 4, "Sky", "A rat", spell, true, cutoff.AddSeconds(1), damageOverTime: dot);
            loaded.Record("COPIED", 4, "Sky", "A rat", spell, true, cutoff.AddSeconds(1), damageOverTime: dot);
            Assert.Equal(1, Fire(loaded, "Sky", "A rat").Resisted);
            await loaded.SaveAsync(path);
            Assert.Equal(1, Fire(await MobResistanceStore.LoadAsync(path), "Sky", "A rat").Resisted);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ResetResistances_PendingTrackerOutcomesFromBeforeResetRemainExcluded()
    {
        var store = CreateStore();
        var tracker = new MobResistanceTracker(store, "log");
        tracker.OnEvent(new EqLog.Core.Parsing.ZoneEvent(Start, "Sky"), 1);
        tracker.OnEvent(new EqLog.Core.Parsing.SpellResistedEvent(Start, "Conflagration", "A rat"), 2);

        store.ResetResistances("Sky", "A rat", Start.AddSeconds(1));
        tracker.Advance(Start.AddSeconds(4));

        Assert.Empty(store.Get("Sky", "A rat").Spells);
    }

    [Fact]
    public void ResetResistances_InvalidatesBaselineLearnedFromRemovedMobSamples()
    {
        var store = CreateStore();
        for (int line = 1; line <= 3; line++)
        {
            store.Record("log", line, "Sky", "A rat", "Conflagration", false, Start, "Tester", 1000);
        }
        store.Record("log", 4, "Sky", "A bat", "Conflagration", false, Start, "Tester", 500);
        Assert.Equal(1, Fire(store, "Sky", "A bat").EstimatedPartial);

        store.ResetResistances("Sky", "A rat", Start.AddSeconds(1));

        Assert.Equal(0, Fire(store, "Sky", "A bat").EstimatedPartial);
        Assert.Equal(1, Fire(store, "Sky", "A bat").UnassessedDamage);
        Assert.Single(store.Get("Sky", "A bat").Spells);
    }

    [Fact]
    public void ResetResistances_WithoutExistingHistoryStillPreventsOldEvents()
    {
        var store = CreateStore();
        store.ResetResistances("Sky", "A rat", Start.AddSeconds(10));
        store.ResetResistances("Sky", "A rat", Start.AddSeconds(5));

        store.Record("log", 1, "Sky", "A rat", "Conflagration", true, Start.AddSeconds(8));
        Assert.Empty(store.Get("Sky", "A rat").Spells);
        store.Record("log", 2, "Sky", "A rat", "Conflagration", true, Start.AddSeconds(11));
        Assert.Equal(1, Fire(store, "Sky", "A rat").Resisted);
    }

    private static MobResistanceStore CreateStore()
    {
        var store = new MobResistanceStore();
        store.SetSpell(new("Conflagration", ResistanceType.Fire, "Fire", Start, SingleTargetDirectDamage: true));
        store.SetSpell(new("Ignite Blood", ResistanceType.Fire, "Fire", Start, DamageOverTime: true));
        return store;
    }

    private static ResistanceObservation Fire(MobResistanceStore store, string zone, string mob)
    {
        return store.Get(zone, mob).Observations.Single(observation => observation.Type == ResistanceType.Fire);
    }
}
