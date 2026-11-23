using System.Text.Json;
using System.Text.Json.Nodes;
using EqLog.Application.Tracking;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;
using EqLog.Infrastructure.Persistence;

namespace EqLog.Infrastructure.Tests;

public sealed class LevelingHistoryRepositoryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "eql-leveling-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime At = new(2026, 11, 9, 18, 0, 0);

    [Fact]
    public async Task LegacyHistoryLoadsAndLevelStartMetadataSurvivesReplayAndSave()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "leveling-history.json");
        var history = new LevelingHistory();
        ReplayLevel(history);
        var document = Assert.IsType<JsonObject>(JsonSerializer.SerializeToNode(history.Capture()!.Value.Data));
        foreach (var row in Assert.IsType<JsonArray>(document["Minutes"]))
        {
            Assert.IsType<JsonObject>(row).Remove("LevelStarted");
        }
        await File.WriteAllTextAsync(path, document.ToJsonString());
        var repository = new LevelingHistoryRepository();
        var restored = await repository.LoadAsync(path);
        Assert.All(restored.Snapshot("Que", At, At.AddHours(1)), row => Assert.Null(row.LevelStarted));
        ReplayLevel(restored);
        await repository.SaveAsync(path, restored);
        var saved = (await repository.LoadAsync(path)).Snapshot("Que", At, At.AddHours(1));
        Assert.Equal(At, Assert.Single(saved, row => row.LevelStarted.HasValue).LevelStarted);
        Assert.Equal(1, saved.Sum(row => row.Gains));
        Assert.Equal(2, saved.Sum(row => row.Experience));

        static void ReplayLevel(LevelingHistory target)
        {
            var tracker = new LevelingTracker(target, "log");
            tracker.OnLine(At, new LevelEvent(At, 43), "Que");
            tracker.OnLine(At.AddMinutes(1), new ExperienceEvent(At.AddMinutes(1), ExperienceKind.Solo, 2), "Que");
        }
    }

    [Fact]
    public async Task RoundTripRetainsHistoricalBuildAndReplayWatermarksAndAppendsOnlyNewActivity()
    {
        string path = Path.Combine(_folder, "leveling-history.json");
        var repository = new LevelingHistoryRepository();
        var history = await repository.LoadAsync(path);
        Replay(history, extended: false);
        await repository.SaveAsync(path, history);
        Assert.Null(history.Capture());

        var restored = await repository.LoadAsync(path);
        Replay(restored, extended: true);
        await repository.SaveAsync(path, restored);
        var rows = (await repository.LoadAsync(path)).Snapshot("Que", At, At.AddHours(1));
        Assert.Equal(2, rows.Sum(row => row.Gains));
        Assert.Equal(3, rows.Sum(row => row.Experience));
        Assert.Equal(120, rows.Sum(row => row.Seconds));
        Assert.All(rows.Where(row => row.Gains > 0), row => Assert.Equal("Wizard", row.Context.Build));
    }

    [Fact]
    public async Task IncrementalSavesAppendOnlyChangedMinutesAndRecoverAfterRestart()
    {
        string path = Path.Combine(_folder, "leveling-history.json");
        var repository = new LevelingHistoryRepository();
        var history = new LevelingHistory();
        var tracker = new LevelingTracker(history, "log");
        tracker.OnLine(At, new ZoneEvent(At, "Halas"), "Que");
        for (int minute = 1; minute <= 500; minute++)
        {
            tracker.OnLine(At.AddMinutes(minute), new ExperienceEvent(At.AddMinutes(minute), ExperienceKind.Solo, 1), "Que");
        }
        await repository.SaveAsync(path, history);
        string journal = LevelingHistoryRepository.JournalPath(path);
        long before = new FileInfo(journal).Length;
        tracker.OnLine(At.AddMinutes(501), new ExperienceEvent(At.AddMinutes(501), ExperienceKind.Solo, 2), "Que");
        await repository.SaveAsync(path, history);
        long appended = new FileInfo(journal).Length - before;
        Assert.True(appended < 2048, $"Expected less than 2 KiB appended, got {appended} bytes.");
        Assert.True(appended * 20 < before, $"Expected a small delta: {appended} versus {before} bytes.");
        var restored = await repository.LoadAsync(path);
        var rows = restored.Snapshot("Que", At, At.AddDays(1));
        Assert.Equal(501, rows.Sum(row => row.Gains));
        Assert.Equal(502, rows.Sum(row => row.Experience));
    }

    [Fact]
    public async Task InterruptedFinalRecordIsRemovedBeforeTheNextAppend()
    {
        string path = Path.Combine(_folder, "leveling-history.json");
        var repository = new LevelingHistoryRepository();
        var history = new LevelingHistory();
        Replay(history, extended: false);
        await repository.SaveAsync(path, history);
        string journal = LevelingHistoryRepository.JournalPath(path);
        await File.AppendAllTextAsync(journal, "{\"Minutes\":[");
        var restored = await repository.LoadAsync(path);
        Replay(restored, extended: true);
        await repository.SaveAsync(path, restored);
        var reloaded = await repository.LoadAsync(path);
        Assert.Equal(2, reloaded.Snapshot("Que", At, At.AddHours(1)).Sum(row => row.Gains));
    }

    [Fact]
    public async Task CompleteMalformedJournalRecordFailsInsteadOfLosingHistory()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "leveling-history.json");
        await File.WriteAllTextAsync(LevelingHistoryRepository.JournalPath(path), "{broken}\n");
        await Assert.ThrowsAsync<JsonException>(() => new LevelingHistoryRepository().LoadAsync(path));
    }

    [Fact]
    public async Task LargeJournalIsCheckpointedWithoutChangingHistory()
    {
        string path = Path.Combine(_folder, "leveling-history.json");
        var repository = new LevelingHistoryRepository();
        var history = new LevelingHistory();
        var tracker = new LevelingTracker(history, "log");
        tracker.OnLine(At, new ZoneEvent(At, "Halas"), "Que");
        for (int minute = 1; minute <= 4000; minute++)
        {
            tracker.OnLine(At.AddMinutes(minute), new ExperienceEvent(At.AddMinutes(minute), ExperienceKind.Solo, 1), "Que");
        }
        await repository.SaveAsync(path, history);
        string journal = LevelingHistoryRepository.JournalPath(path);
        Assert.True(new FileInfo(journal).Length >= 1024 * 1024);
        var restored = await repository.LoadAsync(path);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(journal));
        Assert.Equal(4000, restored.Snapshot("Que", At, At.AddDays(4)).Sum(row => row.Gains));
    }

    [Fact]
    public async Task FailedWriteLeavesHistoryDirtyForSuccessfulRetry()
    {
        Directory.CreateDirectory(_folder);
        string blocked = Path.Combine(_folder, "blocked");
        await File.WriteAllTextAsync(blocked, "not a directory");
        var history = new LevelingHistory();
        Replay(history, extended: false);
        var repository = new LevelingHistoryRepository();
        await Assert.ThrowsAnyAsync<IOException>(() => repository.SaveAsync(Path.Combine(blocked, "history.json"), history));
        Assert.NotNull(history.Capture());
        await repository.SaveAsync(Path.Combine(_folder, "history.json"), history);
        Assert.Null(history.Capture());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"Minutes\":null,\"Watermarks\":{}}")]
    [InlineData("{\"Minutes\":[null],\"Watermarks\":{}}")]
    public async Task CorruptHistoryIsReportedAndProtectedDuringApplicationSave(string text)
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "leveling-history.json");
        await File.WriteAllTextAsync(path, text);
        var service = new TrackingService(new SpellDurationRepository(), new SpawnRepository(), new MoteRepository(),
            new MobResistanceRepository(), new NpcCompendiumRepository(), new TrackingResetRepository(),
            new(_folder, Path.Combine(_folder, "durations.json"), Path.Combine(_folder, "spawns.json"),
                Path.Combine(_folder, "motes.json"), Path.Combine(_folder, "mobs.json"),
                Path.Combine(_folder, "npcs"), Path.Combine(_folder, "legacy-npcs.json")),
            new LevelingHistoryRepository());
        var startup = await service.LoadAsync();
        var failure = Assert.Single(startup.Failures);
        Assert.Equal("leveling", failure.Store);
        Assert.IsType<JsonException>(failure.Error);
        Assert.Null(startup.Leveling);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveLevelingAsync(new()));
        Assert.Equal(text, await File.ReadAllTextAsync(path));
    }

    private static void Replay(LevelingHistory history, bool extended)
    {
        var tracker = new LevelingTracker(history, "log");
        tracker.OnLine(At, new ZoneEvent(At, "Halas"), "Que");
        tracker.OnLine(At, new WhoLoadoutEvent(At, "Que", 42, "Wizard"), "Que");
        tracker.OnLine(At.AddMinutes(1), new ExperienceEvent(At.AddMinutes(1), ExperienceKind.Party, 1), "Que");
        if (extended)
        {
            tracker.OnLine(At.AddMinutes(2), new ExperienceEvent(At.AddMinutes(2), ExperienceKind.Party, 2), "Que");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
