using EqLog.Core.Parsing;
using EqLog.Core.Tracking;

namespace EqLog.Core.Tests;

public sealed class LevelingHistoryTests
{
    private static readonly DateTime At = new(2026, 11, 9, 18, 0, 0);

    [Fact]
    public void ReplayReconstructsContextWithoutDuplicatingMinutesOrSameSecondGains()
    {
        var history = new LevelingHistory();
        Replay(history);
        Replay(history);
        var rows = history.Snapshot("Que", At, At.AddHours(1));
        Assert.Equal(2, rows.Sum(row => row.Gains));
        Assert.Equal(3, rows.Sum(row => row.Experience));
        Assert.Equal(60, rows.Sum(row => row.Seconds));
        var xp = Assert.Single(rows, row => row.Gains > 0);
        Assert.Equal(("Halas", 42, "Wizard / Enchanter"), (xp.Context.Zone, xp.Context.Level, xp.Context.Build));

        static void Replay(LevelingHistory target)
        {
            var tracker = new LevelingTracker(target, "log");
            tracker.OnLine(At, new ZoneEvent(At, "Halas"), "Que");
            tracker.OnLine(At, new WhoLoadoutEvent(At, "Que", 42, "Wizard / Enchanter"), "Que");
            tracker.OnLine(At.AddMinutes(1), new ExperienceEvent(At.AddMinutes(1), ExperienceKind.Party, 1), "Que");
            tracker.OnLine(At.AddMinutes(1), new ExperienceEvent(At.AddMinutes(1), ExperienceKind.Party, 2), "Que");
        }
    }

    [Fact]
    public void BoundariesPreserveOldContextAndDoNotCountOfflineGapsOrReuseLoginBuild()
    {
        var history = new LevelingHistory();
        var tracker = new LevelingTracker(history, "log");
        tracker.OnLine(At, new WhoLoadoutEvent(At, "Que", 42, "Wizard"), "Que");
        tracker.OnLine(At.AddMinutes(5), new LevelEvent(At.AddMinutes(5), 43), "Que");
        tracker.OnLine(At.AddMinutes(6), new ZoneEvent(At.AddMinutes(6), "Halas"), "Que");
        tracker.OnLine(At.AddDays(1), new LoginEvent(At.AddDays(1)), "Que");
        tracker.OnLine(At.AddDays(1).AddMinutes(1),
            new ExperienceEvent(At.AddDays(1).AddMinutes(1), ExperienceKind.Solo, 2), "Que");
        var rows = history.Snapshot("Que", At, At.AddDays(2));
        Assert.Equal(300, rows.Where(row => row.Context.Level == 42).Sum(row => row.Seconds));
        Assert.Equal(60, rows.Where(row => row.Context.Level == 43).Sum(row => row.Seconds));
        Assert.All(rows.Where(row => row.Context.Level == 42), row => Assert.Null(row.LevelStarted));
        Assert.Equal(At.AddMinutes(5), Assert.Single(rows, row => row.LevelStarted.HasValue).LevelStarted);
        var nextDay = rows.Where(row => row.Minute.Date > At.Date).ToArray();
        Assert.Equal(60, nextDay.Sum(row => row.Seconds));
        Assert.All(nextDay, row =>
        {
            Assert.Null(row.Context.Level);
            Assert.Null(row.Context.Build);
            Assert.Null(row.Context.Zone);
        });
    }

    [Fact]
    public void ReplayBackfillsLevelStartsInOlderHistoryWithoutDuplicatingObservations()
    {
        var history = new LevelingHistory();
        Replay(history);
        var old = history.Capture()!.Value.Data;
        history.Import(old with
        {
            Minutes = old.Minutes.Select(row => row with { LevelStarted = null }).ToList(),
        });
        Replay(history);
        var rows = history.Snapshot("Que", At, At.AddHours(1));
        Assert.Equal(At, Assert.Single(rows, row => row.LevelStarted.HasValue).LevelStarted);
        Assert.Equal(1, rows.Sum(row => row.Gains));
        Assert.Equal(2, rows.Sum(row => row.Experience));
        Assert.Equal(60, rows.Sum(row => row.Seconds));
        history.CompleteSave(history.Capture()!.Value.Version);
        Replay(history);
        Assert.Null(history.Capture());

        static void Replay(LevelingHistory target)
        {
            var tracker = new LevelingTracker(target, "log");
            tracker.OnLine(At, new LevelEvent(At, 43), "Que");
            tracker.OnLine(At.AddMinutes(1), new ExperienceEvent(At.AddMinutes(1), ExperienceKind.Solo, 2), "Que");
        }
    }

    [Fact]
    public void EngineBackfillsUnknownXpRewardsDeathsAndKeepsHistoryAcrossSessionReset()
    {
        var history = new LevelingHistory();
        var engine = new LogEngine(logIdentity: "log", leveling: history) { CharacterName = "Que" };
        engine.ProcessLines(
        [
            Line(0, "You have entered Halas."),
            Line(60, "You gain experience! (2.5%)"),
            Line(90, "You gain party experience!"),
            Line(120, "You gained reward experience!"),
            Line(180, "You have been slain by a rat!"),
        ]);
        engine.ResetSession();
        var rows = history.Snapshot("Que", At, At.AddHours(1));
        Assert.Equal(2.5, rows.Sum(row => row.Experience));
        Assert.Equal(1, rows.Sum(row => row.UnknownGains));
        Assert.Equal(1, rows.Sum(row => row.Rewards));
        Assert.Equal(1, rows.Sum(row => row.Deaths));
        Assert.Equal(180, rows.Sum(row => row.Seconds));
    }

    private static string Line(int seconds, string message)
    {
        return $"[{At.AddSeconds(seconds):ddd MMM dd HH:mm:ss yyyy}] {message}";
    }
}
