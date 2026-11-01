using EqLog.Application.Tracking;
using EqLog.Core.Tracking;
using EqLog.Infrastructure.Persistence;

namespace EqLog.Core.Tests;

public sealed class TrackingRepositoryRoundTripTests
{
    private static readonly DateTime T0 = new(2026, 10, 28, 12, 0, 0);

    [Fact]
    public async Task DurationRepository_RestoresStoreUsedByEngineAfterRestart()
    {
        string path = TemporaryFilePath();
        try
        {
            var store = new SpellDurationStore();
            store.SetOverride("Whirl Till You Hurl X", 12);
            await new SpellDurationRepository().SaveAsync(path, store.ExportData(onlyIfDirty: false)!);

            var loaded = await new SpellDurationRepository().LoadAsync(path);
            var engine = new LogEngine(loaded);
            engine.ProcessLines([
                L(0, "You begin casting Whirl Till You Hurl X."),
                L(3, "a heartsbane drake begins to spin.")
            ]);

            var timer = Assert.Single(engine.Snapshot(T0.AddSeconds(5)).Timers);
            Assert.Equal(12, timer.DurationSeconds);
            Assert.Equal(10, timer.RemainingSeconds);
        }
        finally
        {
            DeleteTemporaryFile(path);
        }
    }

    [Fact]
    public async Task SpawnRepository_RestoresLearnedOverrideAndTrackingState()
    {
        string path = TemporaryFilePath();
        try
        {
            var store = new SpawnStore();
            store.AddSample("Sky", "Heart harpie", T0, 3600);
            store.AddSample("Sky", "Heart harpie", T0.AddHours(1), 4200);
            store.SetOverride("Sky", "Heart harpie", 3900);
            store.SetTracked("Sky", "Heart harpie", false);
            await new SpawnRepository().SaveAsync(path, store.ExportData(onlyIfDirty: false)!);

            var loaded = await new SpawnRepository().LoadAsync(path);
            var entry = Assert.IsType<SpawnInfo>(loaded.Get("Sky", "Heart harpie"));
            Assert.Equal(3900, entry.OverrideSeconds);
            Assert.Equal(3600, entry.LearnedSeconds);
            Assert.Equal(2, entry.Samples);
            Assert.False(entry.Tracked);
        }
        finally
        {
            DeleteTemporaryFile(path);
        }
    }

    [Fact]
    public async Task SpawnRepository_RestoresRelearnCutoffAcrossRestart()
    {
        string path = TemporaryFilePath();
        try
        {
            var store = new SpawnStore();
            store.AddSample("Sky", "Heart harpie", T0, 3600);
            store.Relearn("Sky", "Heart harpie", T0.AddHours(2));
            await new SpawnRepository().SaveAsync(path, store.ExportData(onlyIfDirty: false)!);

            var loaded = await new SpawnRepository().LoadAsync(path);
            loaded.AddSample("Sky", "Heart harpie", T0, 3600);
            Assert.Equal(0, loaded.Get("Sky", "Heart harpie")!.Samples);

            loaded.AddSample("Sky", "Heart harpie", T0.AddHours(3), 4200);
            Assert.Equal(1, loaded.Get("Sky", "Heart harpie")!.Samples);
        }
        finally
        {
            DeleteTemporaryFile(path);
        }
    }

    [Fact]
    public async Task MoteRepository_RestoresCharacterTotalsAndZoneRates()
    {
        string path = TemporaryFilePath();
        try
        {
            var store = new MoteStore();
            store.AddKill("Sky", "Heart harpie");
            store.AddDrop("Sky", "Heart harpie", "Ancient", 2);
            store.AddMote("Tester", "Ancient", 2);
            await new MoteRepository().SaveAsync(path, store.ExportData(onlyIfDirty: false)!);

            var loaded = await new MoteRepository().LoadAsync(path);
            Assert.Equal(2, Assert.Single(loaded.Totals("Tester")).Count);
            var rate = Assert.Single(loaded.ZoneRates("Sky"));
            Assert.Equal("Heart harpie", rate.Mob);
            Assert.Equal(1, rate.Kills);
            Assert.Equal(1, rate.Drops);
            Assert.Equal(2, Assert.Single(rate.Tiers).Count);
        }
        finally
        {
            DeleteTemporaryFile(path);
        }
    }

    [Fact]
    public async Task MobResistanceRepository_RestoresPartialResistanceBaselineAndObservation()
    {
        string path = TemporaryFilePath();
        try
        {
            var store = new MobResistanceStore();
            store.SetSpell(new(
                "Conflagration X",
                ResistanceType.Fire,
                "Fire",
                T0,
                SingleTargetDirectDamage: true));
            for (int line = 1; line <= 3; line++)
            {
                store.Record("training", line, "Sky", "Training mob", "Conflagration X", false, T0, "Tester", 1000);
            }

            store.Record("trial", 1, "Sky", "Target", "Conflagration X", false, T0, "Tester", 400);
            await new MobResistanceRepository().SaveAsync(path, store);

            var loaded = await new MobResistanceRepository().LoadAsync(path);
            var fire = loaded.Get("Sky", "Target").Observations
                .Single(observation => observation.Type == ResistanceType.Fire);
            Assert.Equal(1, fire.Landed);
            Assert.Equal(1, fire.EstimatedPartial);
            Assert.Equal(ResistanceStatus.NotResistant, fire.Status);
        }
        finally
        {
            DeleteTemporaryFile(path);
        }
    }

    private static string TemporaryFilePath()
    {
        return Path.Combine(Path.GetTempPath(), $"eql-tracking-{Guid.NewGuid():N}.json");
    }

    private static void DeleteTemporaryFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        string directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory))
        {
            foreach (string temporaryFile in Directory.EnumerateFiles(directory, Path.GetFileName(path) + ".*"))
            {
                File.Delete(temporaryFile);
            }
        }
    }

    private static string L(int seconds, string message)
    {
        return $"[{T0.AddSeconds(seconds):ddd MMM dd HH:mm:ss yyyy}] {message}";
    }
}
