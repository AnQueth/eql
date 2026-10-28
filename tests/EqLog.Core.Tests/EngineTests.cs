using EqLog.Core.Parsing;
using EqLog.Core.Tracking;

namespace EqLog.Core.Tests;

public class EngineTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 18, 0, 0);

    private static string L(int seconds, string msg)
    {
        return $"[{T0.AddSeconds(seconds):ddd MMM dd HH:mm:ss yyyy}] {msg}";
    }

    [Fact]
    public void BuildsFightWithPerAttackerDps()
    {
        var engine = new LogEngine { CharacterName = "Quethrosar" };
        engine.ProcessLines([
            L(0, "You slash Guardian Golem for 100 points of damage."),
            L(4, "Gibarn slashes Guardian Golem for 50 points of damage."),
            L(9, "You hit Guardian Golem for 850 points of fire damage by Conflagration X."),
            L(9, "You have slain Guardian Golem!"),
        ]);

        var snap = engine.Snapshot(T0.AddSeconds(10));
        var fight = Assert.IsType<FightSnapshot>(snap.CurrentFight);
        Assert.Equal("Guardian Golem", fight.Target);
        Assert.True(fight.Killed);
        Assert.Equal(1000, fight.Total);
        Assert.Equal(10, fight.DurationSeconds);
        Assert.Equal(95, fight.You!.Dps);
        Assert.Equal(1, snap.Session.YourKills);
    }

    [Fact]
    public void LearnsDurationFromWearOffAndTimesNextCast()
    {
        var store = new SpellDurationStore();
        var engine = new LogEngine(store);
        engine.ProcessLines([
            L(0, "You begin casting Allure X."),
            L(2, "heart harpie has been charmed."),
            L(122, "Your Allure X spell has worn off of heart harpie."),
            L(130, "You begin casting Allure X."),
            L(132, "heart harpie has been charmed."),
        ]);

        // Measured from the charm landing (t=2) to wearing off (t=122).
        Assert.Equal(120, store.GetDuration("Allure X"));
        var timer = Assert.Single(engine.Snapshot(T0.AddSeconds(140)).Timers);
        Assert.True(timer.IsCharm);
        Assert.Equal("Heart harpie", timer.Target);
        Assert.Equal(TimerState.Running, timer.State);
        Assert.Equal(112, timer.RemainingSeconds!.Value, 3);
    }

    [Fact]
    public void BindsDotTimerToTargetFromTickAndClearsOnKill()
    {
        var store = new SpellDurationStore();
        store.SetOverride("Ignite Blood IX", 60);
        var engine = new LogEngine(store);
        engine.ProcessLines([
            L(0, "You begin casting Ignite Blood IX."),
            L(3, "Guardian Golem has taken 300 damage from your Ignite Blood IX."),
        ]);

        var timer = Assert.Single(engine.Snapshot(T0.AddSeconds(10)).Timers);
        Assert.Equal("Guardian Golem", timer.Target);
        Assert.Equal(50, timer.RemainingSeconds!.Value, 3);

        engine.ProcessLines([L(12, "You have slain Guardian Golem!")]);
        Assert.Empty(engine.Snapshot(T0.AddSeconds(13)).Timers);
    }

    [Fact]
    public void InterruptedCastDoesNotStartTimer()
    {
        var store = new SpellDurationStore();
        store.SetOverride("Mesmerization X", 24);
        var engine = new LogEngine(store);
        engine.ProcessLines([
            L(0, "You slash an azarack for 10 points of damage."),
            L(1, "You begin casting Mesmerization X."),
            L(2, "Your Mesmerization X spell is interrupted."),
        ]);

        Assert.Empty(engine.Snapshot(T0.AddSeconds(30)).Timers);
    }

    [Theory]
    [InlineData("You have slain Guardian Golem!")]
    [InlineData("Guardian Golem has been slain by Gibarn!")]
    [InlineData("Guardian Golem died.")]
    public void KillClearsPendingTimerWithoutRecreatingIt(string death)
    {
        var store = new SpellDurationStore();
        store.SetOverride("Mesmerization X", 60);
        var engine = new LogEngine(store);
        engine.ProcessLines([
            L(0, "You slash Guardian Golem for 10 points of damage."),
            L(1, "You begin casting Mesmerization X."),
            L(3, death),
        ]);

        Assert.Empty(engine.Snapshot(T0.AddSeconds(4)).Timers);
        Assert.Empty(engine.Snapshot(T0.AddSeconds(20)).Timers);
    }

    [Fact]
    public void KillClearsAllVictimSpellsButPreservesOtherTargets()
    {
        var store = new SpellDurationStore();
        store.SetOverride("Mesmerization X", 60);
        store.SetOverride("Ignite Blood IX", 60);
        var engine = new LogEngine(store);
        engine.ProcessLines([
            L(0, "You begin casting Mesmerization X."),
            L(1, "Guardian Golem has been mesmerized."),
            L(2, "You begin casting Ignite Blood IX."),
            L(3, "Guardian Golem has taken 300 damage from your Ignite Blood IX."),
            L(4, "You begin casting Mesmerization X."),
            L(5, "an azarack has been mesmerized."),
            L(6, "Guardian Golem has been slain by Gibarn!"),
        ]);

        var timer = Assert.Single(engine.Snapshot(T0.AddSeconds(20)).Timers);
        Assert.Equal("An azarack", timer.Target);
        Assert.Equal(TimerState.Running, timer.State);
    }

    [Fact]
    public void CastAfterKillDoesNotGuessDeadTarget()
    {
        var store = new SpellDurationStore();
        store.SetOverride("Mesmerization X", 60);
        var engine = new LogEngine(store);
        engine.ProcessLines([
            L(0, "You slash Guardian Golem for 10 points of damage."),
            L(1, "You have slain Guardian Golem!"),
            L(2, "You begin casting Mesmerization X."),
        ]);

        var timer = Assert.Single(engine.Snapshot(T0.AddSeconds(20)).Timers);
        Assert.Equal("(target)", timer.Target);
        Assert.False(timer.TargetConfirmed);
    }

    [Fact]
    public void WornOffShowsThenDisappears()
    {
        var store = new SpellDurationStore();
        store.SetOverride("Mesmerization X", 24);
        var engine = new LogEngine(store);
        engine.ProcessLines([
            L(0, "You begin casting Mesmerization X."),
            L(1, "an azarack has been mesmerized."),
            L(5, "An azarack has been awakened by Quethrosar."),
        ]);

        Assert.Equal(TimerState.Broken, Assert.Single(engine.Snapshot(T0.AddSeconds(6)).Timers).State);
        Assert.Empty(engine.Snapshot(T0.AddSeconds(30)).Timers);
    }

    [Fact]
    public void DotOnYouWithoutCasterIsLabelledBySpell()
    {
        var engine = new LogEngine();
        engine.ProcessLines([L(0, "You have taken 345 damage from Envenomed Bolt.")]);
        var taken = engine.Snapshot(T0.AddSeconds(1)).DamageTaken;
        Assert.Equal("Envenomed Bolt (DoT)", Assert.Single(taken.EncounterByAttacker).Name);
        Assert.Equal("Envenomed Bolt (DoT)", Assert.Single(taken.SessionByAttacker).Name);
        Assert.Equal(345, taken.SessionTotal);
    }

    [Fact]
    public async Task ReReadingHistoryDoesNotDuplicateLearnedSamples()
    {
        string[] lines =
        [
            L(0, "You begin casting Mesmerization X."),
            L(1, "an azarack has been mesmerized."),
            L(1, "a spiroc banisher has been mesmerized."),
            L(31, "Your Mesmerization X spell has worn off of an azarack."),
            L(31, "Your Mesmerization X spell has worn off of a spiroc banisher."),
        ];
        var store = new SpellDurationStore();
        new LogEngine(store).ProcessLines(lines);
        int firstCount = Assert.Single(store.List()).Samples;
        double? firstEstimate = store.GetDuration("Mesmerization X");
        new LogEngine(store).ProcessLines(lines);

        string path = Path.Combine(Path.GetTempPath(), $"durations_{Guid.NewGuid():N}.json");
        try
        {
            await store.SaveAsync(path);
            var reloaded = await SpellDurationStore.LoadAsync(path);
            new LogEngine(reloaded).ProcessLines(lines);

            var info = Assert.Single(reloaded.List());
            Assert.InRange(firstCount, 1, 2);
            Assert.Equal(firstCount, info.Samples);
            Assert.Equal(firstEstimate, info.LearnedSeconds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoginResetsSessionStats()
    {
        var engine = new LogEngine();
        engine.ProcessLines([
            L(0, "You have slain a rat!"),
            L(1, "You receive 5 platinum from the corpse."),
            L(100, "Welcome to EverQuest Legends!"),
            L(101, "You have entered Surefall Glade."),
        ]);

        var snap = engine.Snapshot(T0.AddSeconds(102));
        Assert.Equal(0, snap.Session.YourKills);
        Assert.Equal(0, snap.Loot.CoinFromCorpses.Copper);
        Assert.Equal("Surefall Glade", snap.Session.Zone);
    }

    [Fact]
    public void ParsesFullSampleLog()
    {
        string? path = FindSample();
        if (path is null)
        {
            return;
        }

        var store = new SpellDurationStore();
        var engine = new LogEngine(store) { CharacterName = "Quethrosar", ResetOnLogin = false };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int batch = 5000;
        var buffer = new List<string>(batch);
        foreach (string line in File.ReadLines(path, System.Text.Encoding.Latin1))
        {
            buffer.Add(line);
            if (buffer.Count == batch)
            {
                engine.ProcessLines(buffer);
                buffer.Clear();
            }
        }

        engine.ProcessLines(buffer);
        sw.Stop();

        var snap = engine.Snapshot(DateTime.Now);
        Assert.True(snap.LinesProcessed > 700_000);
        Assert.True(snap.Session.YourKills > 3000);
        Assert.True(snap.SessionDamageByYou > 0);
        Assert.True(snap.Loot.ItemsLooted > 100);
        Assert.Contains(store.List(), d => d.Spell == "Mesmerization X");
        Assert.Contains(store.List(), d => d.Spell == "Allure X");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"Parsing took {sw.Elapsed}");
    }

    private static string? FindSample()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var match = dir.EnumerateFiles("eqlog_*.txt").FirstOrDefault();
            if (match is not null)
            {
                return match.FullName;
            }
        }

        return null;
    }
}
