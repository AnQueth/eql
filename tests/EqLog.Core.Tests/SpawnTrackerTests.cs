using EqLog.Core.Tracking;

namespace EqLog.Core.Tests;

public class SpawnTrackerTests
{
    [Theory]
    [InlineData("6 minutes 40 seconds", 400)]
    [InlineData("6m 40s", 400)]
    [InlineData("16:06", 966)]
    [InlineData("1:02:03", 3723)]
    [InlineData("2 hours", 7200)]
    [InlineData("6.5 mins", 390)]
    [InlineData("7 days", 604800)]
    public void ParsesExplicitWikiRespawnDurations(string text, double expected)
    {
        Assert.True(SpawnStore.TryParseWikiRespawn(text, out double seconds));
        Assert.Equal(expected, seconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("6-12 hours")]
    [InlineData("6 minutes or 12 minutes")]
    [InlineData("16 minutes (placeholder)")]
    [InlineData("0 seconds")]
    [InlineData("NaN")]
    [InlineData("960")]
    public void DoesNotGuessAmbiguousWikiRespawnDurations(string? text)
    {
        Assert.False(SpawnStore.TryParseWikiRespawn(text, out _));
    }

    [Fact]
    public void WikiFallbackTimesExistingDeathButNeverReplacesLearningOrOverride()
    {
        var store = new SpawnStore();
        store.SetTracked("Kedge Keep", "Shellara Ebbhunter", true);
        var engine = new LogEngine(spawns: store);
        engine.ProcessLines([L(0, "You have entered Kedge Keep."), L(10, "You have slain Shellara Ebbhunter!")]);
        engine.SetSpawnWikiRespawn("Shellara Ebbhunter", 400);
        var timer = Assert.Single(engine.Snapshot(T0.AddSeconds(100)).Spawns.Timers);
        Assert.True(timer.UsesWiki);
        Assert.Equal(310, timer.RemainingSeconds);
        Assert.Equal(0, timer.Samples);
        Assert.Null(store.Get("Kedge Keep", "Shellara Ebbhunter")!.LearnedSeconds);
        store.AddSample("Kedge Keep", "Shellara Ebbhunter", T0, 500);
        timer = Assert.Single(engine.Snapshot(T0.AddSeconds(100)).Spawns.Timers);
        Assert.False(timer.UsesWiki);
        Assert.Equal(410, timer.RemainingSeconds);
        engine.SetSpawnOverride("Kedge Keep", "Shellara Ebbhunter", 600);
        Assert.Equal(600, Assert.Single(engine.Snapshot(T0).Spawns.Timers).RespawnSeconds);
        engine.SetSpawnOverride("Kedge Keep", "Shellara Ebbhunter", null);
        Assert.Equal(500, Assert.Single(engine.Snapshot(T0).Spawns.Timers).RespawnSeconds);
    }

    [Fact]
    public async Task WikiFallbackCanBeRemovedAndRehydratedWithoutBecomingLearnedData()
    {
        string path = Path.Combine(Path.GetTempPath(), $"spawn-wiki-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SpawnStore();
            store.SetTracked("Kedge Keep", "Shellara Ebbhunter", true);
            store.SetWikiRespawn("Shellara Ebbhunter", 400);
            await store.SaveAsync(path);
            var reloaded = await SpawnStore.LoadAsync(path);
            Assert.Null(reloaded.Get("Kedge Keep", "Shellara Ebbhunter")!.RespawnSeconds);
            reloaded.SetWikiRespawn("Shellara Ebbhunter", 400);
            Assert.Equal(400, reloaded.Get("Kedge Keep", "Shellara Ebbhunter")!.RespawnSeconds);
            reloaded.SetWikiRespawn("Shellara Ebbhunter", null);
            Assert.Null(reloaded.Get("Kedge Keep", "Shellara Ebbhunter")!.RespawnSeconds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RelearnClearsSamplesKeepsSettingsAndIgnoresOlderSightingsAfterReload()
    {
        string path = Path.Combine(Path.GetTempPath(), $"spawn-relearn-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SpawnStore();
            store.SetTracked("Kedge Keep", "Shellara Ebbhunter", true);
            store.SetOverride("Kedge Keep", "Shellara Ebbhunter", 600);
            store.AddSample("Kedge Keep", "Shellara Ebbhunter", T0, 900);
            store.AddSample("Kedge Keep", "Shellara Ebbhunter", T0.AddHours(1), 905);
            var cutoff = T0.AddHours(1).AddSeconds(905);
            store.Relearn("Kedge Keep 2 (Refined)", "Shellara Ebbhunter", cutoff);

            var info = store.Get("Kedge Keep", "Shellara Ebbhunter")!;
            Assert.Equal(0, info.Samples);
            Assert.Null(info.LearnedSeconds);
            Assert.Equal(600, info.OverrideSeconds);
            Assert.True(info.Tracked);

            await store.SaveAsync(path);
            var loaded = await SpawnStore.LoadAsync(path);
            loaded.AddSample("Kedge Keep", "Shellara Ebbhunter", T0, 900);
            loaded.AddSample("Kedge Keep", "Shellara Ebbhunter", T0.AddHours(1), 905);
            Assert.Equal(0, loaded.Get("Kedge Keep", "Shellara Ebbhunter")!.Samples);

            // Killed before relearning but first seen alive afterwards: a fresh measurement.
            loaded.AddSample("Kedge Keep", "Shellara Ebbhunter", cutoff.AddSeconds(-100), 400);
            var relearned = loaded.Get("Kedge Keep", "Shellara Ebbhunter")!;
            Assert.Equal(1, relearned.Samples);
            Assert.Equal(400, relearned.LearnedSeconds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static readonly DateTime T0 = new(2026, 10, 21, 18, 0, 0);

    private static string L(int seconds, string msg)
    {
        return $"[{T0.AddSeconds(seconds):ddd MMM dd HH:mm:ss yyyy}] {msg}";
    }

    private static string[] KillAndRespawn(int start, int respawn)
    {
        return [
        L(start, "You slash Lord Rikantus Everling for 100 points of damage."),
        L(start + 5, "You have slain Lord Rikantus Everling!"),
        L(start + 6, "Lord Rikantus Everling is burned by YOUR flames for 50 points of non-melee damage."),
        L(start + 5 + respawn, "Lord Rikantus Everling punches YOU for 200 points of damage."),
    ];
    }

    [Fact]
    public void SameNamedMobActiveRightAfterKillIsNotLearned()
    {
        var store = new SpawnStore();
        var engine = new LogEngine(spawns: store);
        static string[] Wave(int s)
        {
            return [
            L(s, "You have slain Guardian Golem!"),
            L(s + 10, "Guardian Golem hits YOU for 50 points of damage."),
            L(s + 40, "Guardian Golem hits YOU for 50 points of damage."),
        ];
        }

        engine.ProcessLines([L(0, "You have entered Nektropos Castle."), .. Wave(100), .. Wave(1000), .. Wave(2000)]);

        Assert.Equal(0, store.Get("Nektropos Castle", "Guardian Golem")!.Samples);
        Assert.False(engine.SpawnCandidates().Single(c => c.Mob == "Guardian Golem").LikelyNamed);
    }

    [Fact]
    public void AllyKilledByAMobIsNotLikelyNamed()
    {
        var engine = new LogEngine(spawns: new SpawnStore());
        engine.ProcessLines([
            L(0, "You have entered Nektropos Castle."),
            L(10, "Feelon has been slain by The Juggernaught!"),
            L(20, "The Juggernaught hits YOU for 500 points of damage."),
            L(30, "You have slain The Juggernaught!"),
        ]);

        var names = engine.SpawnCandidates().ToDictionary(c => c.Mob, c => c.LikelyNamed);
        Assert.False(names["Feelon"]);
        Assert.True(names["The Juggernaught"]);
    }
    [Fact]
    public async Task CountdownSurvivesRestartWithNewLogFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"spawns-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SpawnStore();
            var engine = new LogEngine(spawns: store);
            engine.ProcessLines([L(0, "You have entered Nektropos Castle 2 (Refined)."), L(10, "You have slain Lord Rikantus Everling!")]);
            store.SetTracked("Nektropos Castle", "Lord Rikantus Everling", true);
            store.SetOverride("Nektropos Castle", "Lord Rikantus Everling", 900);
            await store.SaveAsync(path);

            // A new log (e.g. after logging back in) only shows you entering the zone again.
            var restarted = new LogEngine(spawns: await SpawnStore.LoadAsync(path));
            restarted.ProcessLines([L(300, "You have entered Nektropos Castle 4 (Refined).")]);
            var timer = Assert.Single(restarted.Snapshot(T0.AddSeconds(310)).Spawns.Timers);

            Assert.Equal(SpawnState.Dead, timer.State);
            Assert.Equal(T0.AddSeconds(10), timer.KilledAt);
            Assert.Equal(600, timer.RemainingSeconds!.Value, 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LearnsRespawnFromKillToNextSightingInSameZone()
    {
        var store = new SpawnStore();
        var engine = new LogEngine(spawns: store) { CharacterName = "Quethrosar" };
        engine.ProcessLines([
            L(0, "You have entered Nektropos Castle 4 (Refined)."),
            .. KillAndRespawn(10, 1000),
            .. KillAndRespawn(2000, 966),
            .. KillAndRespawn(4000, 970),
        ]);

        var info = Assert.Single(store.List("Nektropos Castle"));
        Assert.Equal("Lord Rikantus Everling", info.Mob);
        Assert.Equal(3, info.Samples);
        Assert.Equal(966, info.LearnedSeconds);

        // Re-reading the same history adds nothing.
        var again = new LogEngine(spawns: store) { CharacterName = "Quethrosar" };
        again.ProcessLines([L(0, "You have entered Nektropos Castle 4 (Refined)."), .. KillAndRespawn(10, 1000)]);
        Assert.Equal(3, store.Get("Nektropos Castle", "Lord Rikantus Everling")!.Samples);
    }

    [Fact]
    public void ZoningBetweenKillAndSightingIsNotLearned()
    {
        var store = new SpawnStore();
        var engine = new LogEngine(spawns: store);
        engine.ProcessLines([
            L(0, "You have entered Kedge Keep."),
            L(10, "You have slain Shellara Ebbhunter!"),
            L(20, "You have entered Lake Nerius."),
            L(300, "You have entered Kedge Keep."),
            L(400, "Shellara Ebbhunter hits YOU for 100 points of damage."),
        ]);

        Assert.Equal(0, store.Get("Kedge Keep", "Shellara Ebbhunter")!.Samples);
    }

    [Fact]
    public void TimesTrackedMobsInCurrentZone()
    {
        var store = new SpawnStore();
        store.SetTracked("Nektropos Castle", "Lord Rikantus Everling", true);
        store.SetOverride("Nektropos Castle", "Lord Rikantus Everling", 960);
        var engine = new LogEngine(spawns: store);
        engine.ProcessLines([
            L(0, "You have entered Nektropos Castle 2 (Adaptive)."),
            L(100, "You have slain Lord Rikantus Everling!"),
            L(101, "You have slain a reanimated servant!"),
        ]);

        var t = Assert.Single(engine.Snapshot(T0.AddSeconds(400)).Spawns.Timers);
        Assert.Equal(SpawnState.Dead, t.State);
        Assert.Equal(660, t.RemainingSeconds);
        Assert.Equal(SpawnState.Due, engine.Snapshot(T0.AddSeconds(1100)).Spawns.Timers[0].State);

        engine.ProcessLines([L(1200, "Lord Rikantus Everling says, 'You will not leave here alive!'")]);
        Assert.Equal(SpawnState.Up, engine.Snapshot(T0.AddSeconds(1200)).Spawns.Timers[0].State);

        var candidates = engine.SpawnCandidates();
        Assert.Contains(candidates, c => c.Mob == "Lord Rikantus Everling" && c.LikelyNamed && c.Kills == 1);
        Assert.Contains(candidates, c => c.Mob == "A reanimated servant" && !c.LikelyNamed);
    }

    [Fact]
    public void PlayersAreNotLikelyNamed()
    {
        var engine = new LogEngine();
        engine.ProcessLines([
            L(0, "You have entered Nektropos Castle."),
            L(1, "Zasanab tells the group, 'inc'"),
            L(5, "Zasanab has been slain by Lord Rikantus Everling!"),
        ]);

        Assert.Contains(engine.SpawnCandidates(), c => c.Mob == "Zasanab" && !c.LikelyNamed);
    }

    [Theory]
    [InlineData("Nektropos Castle 4 (Refined)", "Nektropos Castle")]
    [InlineData("The Plane of Fear", "The Plane of Fear")]
    public void BaseZoneStripsInstanceSuffix(string zone, string expected)
    {
        Assert.Equal(expected, SpawnStore.BaseZone(zone));
    }

    [Theory]
    [InlineData("966", 966)]
    [InlineData("16:06", 966)]
    [InlineData("1:00:00", 3600)]
    public void ParsesDurations(string text, double expected)
    {
        Assert.True(SpawnStore.TryParseDuration(text, out double s));
        Assert.Equal(expected, s);
    }

    [Fact]
    public void EstimateIgnoresSingleLowOutlier()
    {
        Assert.Equal(966, SpawnStore.Estimate([300, 966, 970, 1500]));
    }
}
