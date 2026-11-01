using EqLog.Application.Inventory;
using EqLog.Application.Sessions;
using EqLog.Core;
using EqLog.Core.Parsing;

namespace EqLog.Application.Tests;

public sealed class LogSessionServiceTests
{
    [Fact]
    public async Task ApplicationSessionOwnsEngineReplacementAndRejectsLateSourceCallbacks()
    {
        var input = new FakeInput();
        var inventory = new InventoryService(new NoExport(), new NoStorage(), new());
        await using var source = new LogSessionService(input, input, inventory);
        var session = new GameSession(new(new(), new(), new(), new(), new(), []));
        session.Start(source, File("one", "Que"), false);
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        long first = session.Revision;
        var late = input.Lines!;
        late(["[Tue Oct 27 12:00:00 2026] You have entered Blackburrow."]);
        var oldFrame = await session.CaptureFrameAsync(source, false, []);
        Assert.Equal("Que", oldFrame.Snapshot.CharacterName);
        Assert.Throws<InvalidOperationException>(() => session.Start(source, File("two", "Other"), true));
        Assert.Equal(first, session.Revision);
        await source.StopAsync();
        input.Reset();
        session.Start(source, File("two", "Other"), true);
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        late(["[Tue Oct 27 12:01:00 2026] You have entered Qeynos."]);
        var current = await session.CaptureFrameAsync(source, false, []);
        Assert.Equal(first + 1, session.Revision);
        Assert.Equal("Other", current.Snapshot.CharacterName);
        Assert.Null(current.Snapshot.LastLogTime);
        Assert.Equal("Que", oldFrame.Snapshot.CharacterName);
        Assert.Equal("Blackburrow", oldFrame.Snapshot.Session.Zone);
    }

    [Fact]
    public async Task ReplayAndLiveBatchesRetainSourceIdentityAndOnlyLiveInputRaisesAlerts()
    {
        var input = new FakeInput();
        var inventory = new InventoryService(new NoExport(), new NoStorage(), new());
        await using var sessions = new LogSessionService(input, input, inventory);
        var batches = new List<LogSessionBatch>();
        sessions.BatchProcessed += batch => batches.Add(batch);
        sessions.Start(File("one", "Que"), new LogEngine { CharacterName = "Que" });
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        input.Lines!(
        [
            "[Tue Oct 27 12:00:00 2026] You have entered Blackburrow.",
            "[Tue Oct 27 12:00:01 2026] --You have looted a Gnoll Fang from a gnoll's corpse.--",
        ]);
        input.Progress!(new(10, 10, 2, true));
        input.Lines!(["[Tue Oct 27 12:00:02 2026] --You have looted a Gnoll Fang from a gnoll's corpse.--"]);
        Assert.Equal([false, true], batches.Select(batch => batch.Live));
        Assert.All(batches, batch => Assert.Equal("one", batch.Source));
        await sessions.StopAsync();
        await inventory.SaveAsync();
        var item = Assert.Single(inventory.Snapshot().State.Items);
        Assert.Equal(0, item.Owned);
        Assert.Equal(1, item.Historical);
        Assert.Equal(3, Assert.Single(inventory.Snapshot().State.LootWatermarks).Value.Line);
        Assert.Null(sessions.Completion);
    }

    [Fact]
    public async Task SlowPreviousSessionMustFinishBeforeANewerOneCanStart()
    {
        var input = new FakeInput { CompleteOnCancellation = false };
        var inventory = new InventoryService(new NoExport(), new NoStorage(), new());
        await using var sessions = new LogSessionService(input, input, inventory);
        sessions.Start(File("one", "Que"), new LogEngine());
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = sessions.StopAsync();
        Assert.Throws<InvalidOperationException>(() => sessions.Start(File("two", "Other"), new LogEngine()));
        input.Finished.SetResult();
        await stopping;
        input.Reset();
        input.CompleteOnCancellation = true;
        sessions.Start(File("two", "Other"), new LogEngine { CharacterName = "Other" });
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        input.Lines!(["[Tue Oct 27 12:00:01 2026] --You have looted a Gnoll Fang from a gnoll's corpse.--"]);
        await sessions.StopAsync();
        inventory.ProcessPending(TimeSpan.FromMilliseconds(5));
        Assert.Equal("Other", Assert.Single(inventory.Snapshot().State.Items).Character);
    }

    [Fact]
    public async Task SourceFailureIsExplicitAndDoesNotPreventRestart()
    {
        var input = new FakeInput { Error = new IOException("Reader failed.") };
        var inventory = new InventoryService(new NoExport(), new NoStorage(), new());
        await using var sessions = new LogSessionService(input, input, inventory);
        sessions.Start(File("one", "Que"), new LogEngine());
        await Assert.ThrowsAsync<IOException>(() => sessions.Completion!);
        await Assert.ThrowsAsync<IOException>(() => sessions.StopAsync());
        Assert.Null(sessions.Completion);
        input.Error = null;
        input.Reset();
        sessions.Start(File("two", "Other"), new LogEngine());
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sessions.StopAsync();
    }

    [Fact]
    public async Task FollowNewestRejectsStaleDiscoveryAfterSessionChanges()
    {
        var input = new FakeInput();
        var inventory = new InventoryService(new NoExport(), new NoStorage(), new());
        await using var sessions = new LogSessionService(input, input, inventory);
        var found = new TaskCompletionSource<LogFileInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        input.Find = found.Task;
        var discovering = sessions.FindNewerAsync("folder", false);
        sessions.Start(File("one", "Que"), new LogEngine());
        found.SetResult(File("two", "Other") with
        {
            LastWriteTime = DateTime.Now.AddHours(1)
        });
        Assert.Null(await discovering);
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sessions.StopAsync();
        sessions.ForgetSource();
        input.Find = Task.FromResult<LogFileInfo?>(File("new", "New"));
        Assert.Equal("new", (await sessions.FindNewerAsync("folder", false))!.Path);
    }

    [Fact]
    public async Task ReplayFramesUseLogTimeWithoutAccessingSourceFiles()
    {
        var input = new FakeInput();
        var inventory = new InventoryService(new NoExport(), new NoStorage(), new());
        await using var sessions = new LogSessionService(input, input, inventory,
            new FixedTime(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero)));
        var engine = new LogEngine();
        engine.ProcessLines(["[Tue Oct 27 12:00:00 2026] You have entered Blackburrow."]);
        var frame = await sessions.CaptureFrameAsync(engine, true, [ChatChannel.Say]);
        Assert.True(frame.Replay);
        Assert.Equal(frame.Snapshot.LastLogTime, frame.Snapshot.Now);
        Assert.Equal(0, input.Reads);
    }

    [Fact]
    public async Task OldSourceCallbacksCannotMutateAStoppedSession()
    {
        var input = new FakeInput();
        var inventory = new InventoryService(new NoExport(), new NoStorage(), new());
        await using var sessions = new LogSessionService(input, input, inventory);
        var engine = new LogEngine();
        int batches = 0;
        sessions.BatchProcessed += _ => batches++;
        sessions.Start(File("one", "Que"), engine);
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var late = input.Lines!;
        await sessions.StopAsync();
        late(["[Tue Oct 27 12:00:00 2026] You have entered Blackburrow."]);
        Assert.Equal(0, batches);
        Assert.Null(engine.LastLogTime);
    }

    [Fact]
    public async Task DisposedServiceRejectsNewSources()
    {
        var input = new FakeInput();
        var inventory = new InventoryService(new NoExport(), new NoStorage(), new());
        var sessions = new LogSessionService(input, input, inventory);
        await sessions.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => sessions.Start(File("one", "Que"), new LogEngine()));
        await sessions.DisposeAsync();
    }

    [Fact]
    public void PresenterQueriesRemainDetachedAfterTrackingChanges()
    {
        var durations = new EqLog.Core.Tracking.SpellDurationStore();
        durations.AddSample("Fire", 60, DateTime.Now);
        var engine = new LogEngine(durations);
        var query = EngineQuerySnapshot.Capture(engine);
        engine.SetSpellDurationOverride("Fire", 99);
        Assert.Null(Assert.Single(query.SpellDurations).OverrideSeconds);
        Assert.Equal(99, Assert.Single(EngineQuerySnapshot.Capture(engine).SpellDurations).OverrideSeconds);
    }

    private static LogFileInfo File(string path, string character)
    {
        return new(path, character, "server", DateTime.Now, 10);
    }

    private sealed class FakeInput : ILogSource, ILogDiscovery
    {
        public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<IReadOnlyList<string>>? Lines
        {
            get; private set;
        }
        public Action<TailProgress>? Progress
        {
            get; private set;
        }
        public bool CompleteOnCancellation { get; set; } = true;
        public Exception? Error
        {
            get; set;
        }
        public Task<LogFileInfo?> Find { get; set; } = Task.FromResult<LogFileInfo?>(null);
        public int Reads
        {
            get; private set;
        }

        public void Reset()
        {
            Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async Task RunAsync(string path, bool fromStart, Action<IReadOnlyList<string>> lines, Action<TailProgress> progress, CancellationToken cancellationToken)
        {
            Reads++;
            if (Error is not null)
            {
                throw Error;
            }
            Lines = lines;
            Progress = progress;
            using var registration = cancellationToken.Register(() =>
            {
                if (CompleteOnCancellation)
                {
                    Finished.TrySetResult();
                }
            });
            Started.TrySetResult();
            await Finished.Task;
        }

        public Task<LogFileInfo> DescribeAsync(string path, CancellationToken cancellationToken)
        {
            return Task.FromResult(File(path, "Que"));
        }

        public Task<LogFileInfo?> FindNewestAsync(string directory, CancellationToken cancellationToken)
        {
            return Find;
        }

        public Task<LogStartupSelection> FindInitialAsync(string[] arguments, string? savedPath, string? savedDirectory, bool followNewest, CancellationToken cancellationToken)
        {
            return Task.FromResult(new LogStartupSelection(savedPath, savedDirectory));
        }

        public Task<string> ResolveDirectoryAsync(string directory, CancellationToken cancellationToken)
        {
            return Task.FromResult(directory);
        }

        public Task<bool> DirectoryExistsAsync(string directory, CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }
    }

    private sealed class NoExport : IInventoryExportSource
    {
        public Task<InventoryFileStamp?> InspectAsync(string logPath, CancellationToken cancellationToken)
        {
            return Task.FromResult<InventoryFileStamp?>(null);
        }

        public Task<IReadOnlyList<InventoryExportItem>?> ReadAsync(InventoryFileStamp expected, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<InventoryExportItem>?>(null);
        }
    }

    private sealed class NoStorage : IQuestInventoryRepository
    {
        public Task<QuestInventory> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new QuestInventory());
        }

        public Task SaveAsync(QuestInventory snapshot, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }
}
