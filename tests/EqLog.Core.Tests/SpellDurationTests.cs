using EqLog.Core.Tracking;

namespace EqLog.Core.Tests;

public class SpellDurationTests
{
    private static readonly DateTime T0 = new(2026, 10, 28, 12, 0, 0);

    [Theory]
    [InlineData("Clarity", 1)]
    [InlineData("Clarity V", 5)]
    [InlineData("Clarity IX", 9)]
    [InlineData("Clarity X", 10)]
    [InlineData("Clarity Rk. IV", 4)]
    [InlineData("Clarity XL", 40)]
    public void ReadsSpellRank(string spell, int expected)
    {
        Assert.Equal(expected, SpellNames.Rank(spell));
    }

    [Fact]
    public void HighestRankSelectionKeepsOtherRanksSaved()
    {
        var store = new SpellDurationStore();
        store.SetOverride("Clarity", 1200);
        store.SetOverride("Clarity V", 1620);
        store.SetOverride("Envenomed Bolt IX", 30);
        store.SetOverride("Envenomed Bolt X", 36);
        store.SetOverride("Camouflage", 1200);
        var engine = new LogEngine(durations: store);

        Assert.Equal(
            new[] { "Camouflage", "Clarity V", "Envenomed Bolt X" },
            engine.SpellDurations(highestRanksOnly: true).Select(duration => duration.Spell));
        Assert.Equal(5, engine.SpellDurations().Count);
        Assert.Equal(1200, store.GetDuration("Clarity"));
    }

    [Theory]
    [InlineData("Up to 2 ticks", 12)]
    [InlineData("2 ticks", 12)]
    [InlineData("1 tick", 6)]
    [InlineData("  up TO 1.5 minutes  ", 90)]
    [InlineData("2.5 seconds", 2.5)]
    [InlineData("1 hour 12 minutes", 4320)]
    [InlineData("00:12", 12)]
    public void ParsesExplicitWikiDurations(string text, double expected)
    {
        Assert.True(SpellDurationStore.TryParseWikiDuration(text, out double seconds));
        Assert.Equal(expected, seconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Instant")]
    [InlineData("Permanent")]
    [InlineData("0 seconds")]
    [InlineData("2-4 ticks")]
    [InlineData("2 ticks per level")]
    [InlineData("12")]
    public void DoesNotGuessAmbiguousOrNonTimedWikiDurations(string? text)
    {
        Assert.False(SpellDurationStore.TryParseWikiDuration(text, out _));
    }

    [Fact]
    public void OverrideAndLearnedDurationTakePriorityOverWikiIncludingOtherRanks()
    {
        var store = new SpellDurationStore();
        store.SetWikiDuration("Whirl Till You Hurl X", 12);
        Assert.Equal(12, store.GetDuration("Whirl Till You Hurl X"));
        Assert.Equal(12, store.GetDuration("Whirl Till You Hurl IX"));

        store.AddSample("Whirl Till You Hurl IX", 10, T0);
        Assert.Equal(10, store.GetDuration("Whirl Till You Hurl X"));

        store.SetOverride("Whirl Till You Hurl X", 15);
        Assert.Equal(15, store.GetDuration("Whirl Till You Hurl X"));
        store.SetOverride("Whirl Till You Hurl X", null);
        Assert.Equal(10, store.GetDuration("Whirl Till You Hurl X"));

        var info = Assert.Single(store.List(), d => d.Spell == "Whirl Till You Hurl X");
        Assert.Equal(12, info.WikiSeconds);
        Assert.Equal(0, info.Samples);
    }

    [Fact]
    public void WikiFallbackDoesNotDirtyAnUnchangedStore()
    {
        var store = new SpellDurationStore();
        store.SetWikiDuration("Whirl Till You Hurl X", 12);
        var snapshotStore = (ISnapshotStore<SpellDurationStore.Dto>)store;
        snapshotStore.TakeSnapshot(onlyIfDirty: true);

        store.SetWikiDuration("Whirl Till You Hurl X", 12);
        Assert.False(store.IsDirty);
        store.SetWikiDuration("Whirl Till You Hurl X", null);
        Assert.True(store.IsDirty);
        Assert.Empty(store.List());
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.MaxValue)]
    public void InvalidOverrideDoesNotReplaceExistingDuration(double seconds)
    {
        var store = new SpellDurationStore();
        store.SetOverride("Whirl Till You Hurl X", 12);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetOverride("Whirl Till You Hurl X", seconds));
        Assert.Equal(12, store.GetDuration("Whirl Till You Hurl X"));
    }

    [Fact]
    public void RecheckingAnotherRankEnablesTheWholeSpellFamily()
    {
        var store = new SpellDurationStore();
        store.SetWikiDuration("Whirl Till You Hurl X", 12);
        store.SetWikiDuration("Whirl Till You Hurl IX", 12);
        store.SetTracked("Whirl Till You Hurl IX", false);
        Assert.All(store.List(), d => Assert.False(d.Tracked));

        store.SetTracked("Whirl Till You Hurl X", true);
        Assert.All(store.List(), d => Assert.True(d.Tracked));
    }

    [Fact]
    public async Task WikiAndRemovedSpellsPersistAndOnlyManualAdditionRevivesRemovedSpell()
    {
        string path = Path.Combine(Path.GetTempPath(), $"eql-durations-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SpellDurationStore();
            store.SetWikiDuration("Whirl Till You Hurl X", 12);
            store.SetWikiDuration("Mesmerization X", 24);
            store.SetOverride("Whirl Till You Hurl X", 15);
            store.AddSample("Whirl Till You Hurl X", 10, T0);
            store.Remove("Whirl Till You Hurl X");
            await store.SaveAsync(path);

            var loaded = await SpellDurationStore.LoadAsync(path);
            loaded.SetWikiDuration("Whirl Till You Hurl X", 12);
            loaded.AddSample("Whirl Till You Hurl X", 10, T0.AddMinutes(1));
            loaded.SetTracked("Whirl Till You Hurl X", true);
            Assert.False(loaded.IsTracked("Whirl Till You Hurl X"));
            Assert.Null(loaded.GetDuration("Whirl Till You Hurl X"));
            Assert.Equal(24, Assert.Single(loaded.List()).WikiSeconds);

            loaded.SetOverride("whirl till you hurl x", 14);
            Assert.True(loaded.IsTracked("Whirl Till You Hurl X"));
            Assert.Equal(14, loaded.GetDuration("Whirl Till You Hurl X"));
            var revived = Assert.Single(loaded.List(), d => d.Spell.Equals("Whirl Till You Hurl X", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(0, revived.Samples);
            await loaded.SaveAsync(path);
            var reloaded = await SpellDurationStore.LoadAsync(path);
            Assert.Equal(14, reloaded.GetDuration("Whirl Till You Hurl X"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CachedWikiDurationBindsWhirlTimerToSpinningTargetAndDeathClearsIt()
    {
        var metadata = new MobResistanceStore();
        metadata.SetSpell(new("Whirl Till You Hurl X", ResistanceType.Magic, "Magic", DateTime.UtcNow,
            Duration: "Up to 2 ticks"));
        var engine = new LogEngine(resistances: metadata);
        engine.ProcessLines([
            L(0, "You begin casting Whirl Till You Hurl X."),
            L(3, "a heartsbane drake begins to spin."),
        ]);

        var timer = Assert.Single(engine.Snapshot(T0.AddSeconds(5)).Timers);
        Assert.Equal("A heartsbane drake", timer.Target);
        Assert.True(timer.TargetConfirmed);
        Assert.Equal(12, timer.DurationSeconds);
        Assert.Equal(10, timer.RemainingSeconds);
        Assert.Equal(12, Assert.Single(engine.SpellDurations()).WikiSeconds);

        engine.ProcessLines([L(6, "You have slain a heartsbane drake!")]);
        Assert.Empty(engine.Snapshot(T0.AddSeconds(7)).Timers);
    }

    [Fact]
    public void WikiResultArrivingAfterCastIsAvailableForLaterCasts()
    {
        var metadata = new MobResistanceStore();
        var engine = new LogEngine(resistances: metadata);
        engine.ProcessLines([L(0, "You begin casting Whirl Till You Hurl X.")]);
        Assert.Empty(engine.SpellDurations());
        metadata.SetSpell(new("Whirl Till You Hurl X", ResistanceType.Magic, "Magic", DateTime.UtcNow,
            Duration: "Up to 2 ticks"));
        Assert.Equal(12, Assert.Single(engine.SpellDurations()).WikiSeconds);

        engine.ProcessLines([
            L(20, "You begin casting Whirl Till You Hurl X."),
            L(23, "a heartsbane drake begins to spin."),
        ]);
        Assert.Equal(10, Assert.Single(engine.Snapshot(T0.AddSeconds(25)).Timers).RemainingSeconds);
    }

    [Fact]
    public void InstantWikiSpellsDoNotBecomeDurationTimers()
    {
        var metadata = new MobResistanceStore();
        metadata.SetSpell(new("Conflagration X", ResistanceType.Fire, "Fire", DateTime.UtcNow, Duration: "Instant"));
        var engine = new LogEngine(resistances: metadata);
        engine.ProcessLines([L(0, "You begin casting Conflagration X.")]);
        Assert.Empty(engine.SpellDurations());
        Assert.Empty(engine.Snapshot(T0.AddSeconds(20)).Timers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingSpellClearsPendingAndActiveTimersAndBlocksReplay(bool landed)
    {
        var store = new SpellDurationStore();
        store.SetOverride("Whirl Till You Hurl X", 12);
        var engine = new LogEngine(store);
        engine.ProcessLines([L(0, "You begin casting Whirl Till You Hurl X.")]);
        if (landed)
        {
            engine.ProcessLines([L(3, "a heartsbane drake begins to spin.")]);
        }

        engine.RemoveSpellDuration("Whirl Till You Hurl X");
        Assert.Empty(engine.Snapshot(T0.AddSeconds(4)).Timers);
        engine.ProcessLines([
            L(10, "You begin casting Whirl Till You Hurl X."),
            L(13, "a heartsbane drake begins to spin."),
            L(25, "Your Whirl Till You Hurl X spell has worn off of a heartsbane drake."),
        ]);
        Assert.Empty(engine.SpellDurations());
        Assert.Empty(engine.Snapshot(T0.AddSeconds(26)).Timers);

        engine.SetSpellDurationOverride("Whirl Till You Hurl X", 12);
        Assert.Empty(engine.Snapshot(T0.AddSeconds(27)).Timers);
    }

    [Fact]
    public async Task RelearnClearsSamplesKeepsOverrideAndIgnoresOlderWearOffsAfterReload()
    {
        string path = Path.Combine(Path.GetTempPath(), $"eql-durations-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SpellDurationStore();
            store.AddSample("Mesmerize", 30, T0);
            store.AddSample("Mesmerize", 31, T0.AddMinutes(1));
            store.SetOverride("Mesmerize", 25);
            store.Relearn("Mesmerize", T0.AddMinutes(1));

            var info = Assert.Single(store.List());
            Assert.Equal(0, info.Samples);
            Assert.Null(info.LearnedSeconds);
            Assert.Equal(25, info.OverrideSeconds);

            await store.SaveAsync(path);
            var loaded = await SpellDurationStore.LoadAsync(path);
            loaded.AddSample("Mesmerize", 30, T0);
            loaded.AddSample("Mesmerize", 31, T0.AddMinutes(1));
            Assert.Equal(0, Assert.Single(loaded.List()).Samples);

            loaded.AddSample("Mesmerize", 18, T0.AddMinutes(2));
            Assert.Equal(18, Assert.Single(loaded.List()).LearnedSeconds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EngineRelearnUsesLatestLogLineAsCutoff()
    {
        string[] firstCharm = [
            L(0, "You begin casting Allure X."),
            L(2, "heart harpie has been charmed."),
            L(122, "Your Allure X spell has worn off of heart harpie."),
        ];
        var engine = new LogEngine();
        engine.ProcessLines(firstCharm);
        Assert.Equal(120, Assert.Single(engine.SpellDurations()).LearnedSeconds);

        engine.RelearnSpellDuration("Allure X");
        engine.ProcessLines(firstCharm);
        Assert.Equal(0, Assert.Single(engine.SpellDurations()).Samples);

        engine.ProcessLines([
            L(200, "You begin casting Allure X."),
            L(202, "heart harpie has been charmed."),
            L(302, "Your Allure X spell has worn off of heart harpie."),
        ]);
        var relearned = Assert.Single(engine.SpellDurations());
        Assert.Equal(1, relearned.Samples);
        Assert.Equal(100, relearned.LearnedSeconds);
    }

    private static string L(int seconds, string message)
    {
        return $"[{T0.AddSeconds(seconds):ddd MMM dd HH:mm:ss yyyy}] {message}";
    }
}
