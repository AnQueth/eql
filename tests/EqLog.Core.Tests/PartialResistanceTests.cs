using EqLog.Core.Parsing;
using EqLog.Core.Tracking;

namespace EqLog.Core.Tests;

public class PartialResistanceTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 19, 0, 0);

    [Theory]
    [InlineData(1000, 4, 0)]
    [InlineData(900, 4, 0)]
    [InlineData(899, 4, 1)]
    [InlineData(100, 4, 1)]
    public void LearnedDamage_UsesExactNinetyPercentCutoff(long hit, long landed, long partial)
    {
        var store = CreateStore();
        Learn(store, "Tester", "Conflagration X", 1000);
        store.Record("trial", 1, "Sky", "Target", "Conflagration X", false, T0, "Tester", hit);
        var fire = Fire(store);
        Assert.Equal(landed, fire.Landed);
        Assert.Equal(partial, fire.EstimatedPartial);
        Assert.Equal(0, fire.Resisted);
        Assert.Equal("Not Resistant", fire.Status);
    }

    [Fact]
    public void LearnedDamage_RequiresThreeHits_ThenAssessesLowDamageSeparately()
    {
        var store = CreateStore();
        store.Record("trial", 1, "Sky", "Target", "Conflagration X", false, T0, "Tester", 100);
        store.Record("trial", 2, "Sky", "Target", "Conflagration X", false, T0, "Tester", 1000);
        Assert.Equal(2, Fire(store).UnassessedDamage);
        Assert.Equal(2, Fire(store).Landed);
        Assert.Equal("Not Resistant", Fire(store).Status);
        store.Record("trial", 3, "Sky", "Target", "Conflagration X", false, T0, "Tester", 1000);
        Assert.Equal(0, Fire(store).UnassessedDamage);
        Assert.Equal(1, Fire(store).EstimatedPartial);
        Assert.Equal("Not Resistant", Fire(store).Status);
    }

    [Fact]
    public void LearnedDamage_UnassessedHits_DoNotMakeExplicitResistsLookLikeFullImmunity()
    {
        var store = CreateStore();
        store.Record("trial", 1, "Sky", "Target", "Conflagration X", true, T0);
        store.Record("trial", 2, "Sky", "Target", "Conflagration X", false, T0, "Tester", 500);
        Assert.Equal("Partially Resistant", Fire(store).Status);
        Assert.Equal(1, Fire(store).Landed);
        Assert.Equal(1, Fire(store).Resisted);
        Learn(store, "Tester", "Conflagration X", 1000);
        Assert.Equal(0, Fire(store).UnassessedDamage);
        Assert.Equal(1, Fire(store).EstimatedPartial);
        Assert.Equal("Partially Resistant", Fire(store).Status);
    }

    [Fact]
    public void LearnedDamage_ThunderSpiritNormalHits_AreNotResistsDespiteHigherHistoricalPeaks()
    {
        var store = CreateStore();
        for (int line = 1; line <= 11; line++)
        {
            store.Record("training", line, "Castle", "Training mob", "Conflagration X", false, T0,
                "Tester", line <= 8 ? 1694 : 2080);
        }
        var tracker = new MobResistanceTracker(store, "fight") { CharacterName = "Tester" };
        tracker.OnEvent(new ZoneEvent(T0, "Sky"), 1);
        tracker.OnEvent(new CastBeginEvent(T0, "Conflagration X"), 2);
        tracker.OnEvent(Hit(T0.AddSeconds(2), 4836) with
        {
            Modifier = "Critical"
        }, 3);
        int lineNumber = 4;
        foreach (var (second, damage) in new[] { (5, 1755), (8, 1555), (12, 1708) })
        {
            tracker.OnEvent(new CastBeginEvent(T0.AddSeconds(second - 2), "Conflagration X"), lineNumber++);
            tracker.OnEvent(Hit(T0.AddSeconds(second), damage), lineNumber++);
        }
        tracker.OnEvent(new CastBeginEvent(T0.AddSeconds(13), "Conflagration X"), lineNumber++);
        tracker.OnEvent(Hit(T0.AddSeconds(15), 4683) with
        {
            Modifier = "Critical"
        }, lineNumber++);
        tracker.OnEvent(new KillEvent(T0.AddSeconds(16), Names.You, "Target"), lineNumber);
        tracker.Advance(T0.AddSeconds(20));

        var fire = Fire(store);
        Assert.Equal(3, fire.Landed);
        Assert.Equal(0, fire.Resisted);
        Assert.Equal(0, fire.EstimatedPartial);
        Assert.Equal("Not Resistant \u2014 0% (0/3)", fire.DisplayText);
    }

    [Fact]
    public void LearnedDamage_EvenSampleCount_AveragesMiddleValuesWithoutRounding()
    {
        var store = CreateStore();
        long[] damage = [900, 1000, 1000, 1001, 1001, 2000];
        for (int index = 0; index < damage.Length; index++)
        {
            store.Record("trial", index + 1, "Sky", "Target", "Conflagration X", false, T0, "Tester", damage[index]);
        }

        Assert.Equal(1, Fire(store).EstimatedPartial);
        Assert.Equal(6, Fire(store).Landed);
        Assert.Equal(0, Fire(store).Resisted);
    }

    [Fact]
    public void LearnedDamage_SingleHighOutlier_DoesNotPreventLearningTypicalDamage()
    {
        var store = CreateStore();
        Learn(store, "Tester", "Conflagration X", 1800);
        store.Record("outlier", 1, "Sky", "Target", "Conflagration X", false, T0, "Tester", 9000);

        Assert.Equal(0, Fire(store).UnassessedDamage);
        Assert.Equal(0, Fire(store).EstimatedPartial);
        Assert.Equal(4, Fire(store).Landed);
    }

    [Fact]
    public void Tracker_KillOfDifferentMob_DoesNotDiscardHit()
    {
        var store = CreateStore();
        Learn(store, "Tester", "Conflagration X", 1000);
        var tracker = new MobResistanceTracker(store, "trial") { CharacterName = "Tester" };
        tracker.OnEvent(new ZoneEvent(T0, "Sky"), 1);
        tracker.OnEvent(new CastBeginEvent(T0, "Conflagration X"), 2);
        tracker.OnEvent(Hit(T0.AddSeconds(1), 500), 3);
        tracker.OnEvent(new KillEvent(T0.AddSeconds(2), null, "Another mob"), 4);
        tracker.Advance(T0.AddSeconds(5));
        Assert.Equal(1, Fire(store).EstimatedPartial);
    }

    [Fact]
    public void LearnedDamage_SeparatesCharacterAndExactSpellRank()
    {
        var store = CreateStore();
        Learn(store, "High", "Conflagration X", 1000);
        Learn(store, "Low", "Conflagration X", 100);
        Learn(store, "High", "Conflagration IX", 100);
        Assert.Equal(0, Fire(store).EstimatedPartial);
        Assert.Equal(0, Fire(store).UnassessedDamage);
        Assert.Equal(9, Fire(store).Landed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Other player")]
    [InlineData("You")]
    public void Tracker_LoggedDeath_ExcludesFinishingHitFromBaselineAndOutcomes(string? killer)
    {
        var store = CreateStore();
        Learn(store, "Tester", "Conflagration X", 1000);
        var tracker = new MobResistanceTracker(store, "finisher") { CharacterName = "Tester" };
        tracker.OnEvent(new CastBeginEvent(T0, "Conflagration X"), 1);
        tracker.OnEvent(Hit(T0.AddSeconds(1), 50), 2);
        tracker.OnEvent(new KillEvent(T0.AddSeconds(2), killer, "Target"), 3);
        tracker.Advance(T0.AddSeconds(10));
        Assert.Equal(3, Fire(store).Landed);
        Assert.Equal(0, Fire(store).EstimatedPartial);
    }

    [Fact]
    public void Tracker_FullDamageFinishingHit_CannotBecomeBaseline()
    {
        var store = CreateStore();
        var tracker = new MobResistanceTracker(store, "log") { CharacterName = "Tester" };
        for (int i = 0; i < 3; i++)
        {
            tracker.OnEvent(new CastBeginEvent(T0.AddSeconds(i * 5), "Conflagration X"), i * 3 + 1);
            tracker.OnEvent(Hit(T0.AddSeconds(i * 5 + 1), 1000), i * 3 + 2);
            tracker.OnEvent(new KillEvent(T0.AddSeconds(i * 5 + 2), null, "Target"), i * 3 + 3);
        }
        tracker.Advance(T0.AddMinutes(1));
        Assert.Empty(store.Get("Sky", "Target").Spells);
        Assert.Empty(store.Get("(unknown zone)", "Target").Spells);
    }

    [Fact]
    public void Tracker_CriticalAndModifiedHits_DoNotDistortLearnedDamage()
    {
        var store = CreateStore();
        Learn(store, "Tester", "Conflagration X", 1000);
        var tracker = new MobResistanceTracker(store, "log") { CharacterName = "Tester" };
        tracker.OnEvent(new CastBeginEvent(T0, "Conflagration X"), 1);
        tracker.OnEvent(Hit(T0.AddSeconds(1), 5000) with
        {
            Modifier = "Critical"
        }, 2);
        tracker.OnEvent(new CastBeginEvent(T0.AddSeconds(5), "Conflagration X"), 3);
        tracker.OnEvent(Hit(T0.AddSeconds(6), 5000) with
        {
            Modifier = "Lucky"
        }, 4);
        tracker.Advance(T0.AddSeconds(10));
        Assert.Equal(3, Fire(store).Landed);
        Assert.Equal(0, Fire(store).UnassessedDamage);
    }

    [Fact]
    public void Engine_DamageThenDeathAcrossBatches_DoesNotMisclassifyFinisherAndReplaysOnce()
    {
        var store = CreateStore();
        Learn(store, "Tester\nDRU/WIZ/ENC\n50", "Conflagration X", 1000);
        for (int replay = 0; replay < 2; replay++)
        {
            var engine = new LogEngine(resistances: store, logIdentity: "live") { CharacterName = "Tester" };
            engine.Process(new(T0, "You have entered Sky."));
            engine.Process(new(T0, "[50 DRU/WIZ/ENC] Tester (Erudite)"));
            engine.Process(new(T0, "You begin casting Conflagration X."));
            engine.Process(new(T0.AddSeconds(1), "You hit Target for 50 points of fire damage by Conflagration X."));
            engine.Snapshot(T0.AddSeconds(2));
            engine.Process(new(T0.AddSeconds(2), "Target died."));
            engine.Process(new(T0.AddSeconds(10), "You begin casting Conflagration X."));
            engine.Process(new(T0.AddSeconds(11), "You hit Target for 500 points of fire damage by Conflagration X."));
            engine.Snapshot(T0.AddSeconds(15));
        }
        Assert.Equal(4, Fire(store).Landed);
        Assert.Equal(1, Fire(store).EstimatedPartial);
        Assert.Equal("Not Resistant", Fire(store).Status);
    }

    [Fact]
    public async Task Store_DamageHistoryAndBaseline_PersistAcrossRestart()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "mob-resistances.json");
        Directory.CreateDirectory(folder);
        try
        {
            var store = CreateStore();
            Learn(store, "Tester", "Conflagration X", 1000);
            store.Record("trial", 1, "Sky", "Target", "Conflagration X", false, T0, "Tester", 500);
            await store.SaveAsync(path);
            var loaded = await MobResistanceStore.LoadAsync(path);
            loaded.Record("trial", 1, "Sky", "Target", "Conflagration X", false, T0, "Tester", 500);
            Assert.Equal(1, Fire(loaded).EstimatedPartial);
            Assert.Equal(4, Fire(loaded).Landed);
            Assert.Equal(0, Fire(loaded).Resisted);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static MobResistanceStore CreateStore()
    {
        var store = new MobResistanceStore();
        store.SetSpell(new("Conflagration X", ResistanceType.Fire, "Fire", T0, true));
        store.SetSpell(new("Conflagration IX", ResistanceType.Fire, "Fire", T0, true));
        return store;
    }

    private static void Learn(MobResistanceStore store, string character, string spell, long damage)
    {
        for (int i = 1; i <= 3; i++)
        {
            store.Record(character + spell, i, "Sky", "Target", spell, false, T0, character, damage);
        }
    }

    private static ResistanceObservation Fire(MobResistanceStore store)
    {
        return store.Get("Sky", "Target").Observations.Single(o => o.Type == ResistanceType.Fire);
    }

    private static DamageEvent Hit(DateTime time, long damage)
    {
        return new(time, Names.You, "Target", damage, DamageKind.DirectSpell, "Conflagration X", null);
    }
}
