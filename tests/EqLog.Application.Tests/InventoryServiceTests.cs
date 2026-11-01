using EqLog.Application.Inventory;
using EqLog.Core;

namespace EqLog.Application.Tests;

public sealed class InventoryServiceTests
{
    private static readonly DateTime At = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task RequiresStableMetadataAndImportsHighestRankAndConfirmedCounts()
    {
        var source = new FakeSource
        {
            Rows =
            [
                new("Held", "Sword +3", 1, 2, InventoryStorage.Carried),
                new("Bank1", "SWORD +10", 1, 1, InventoryStorage.Bank),
                new("Equipment", "Sword +9", 1, 1, InventoryStorage.Other, Collection: true),
                new("Back-Slot1", "Sword +2", 1, 1, InventoryStorage.Carried, Installed: true),
            ],
        };
        var repository = new FakeRepository();
        var service = Create(source, repository);
        service.ObserveLoot(new("Que", "log", 1, "Sword", 5, true, At.AddMinutes(1)));
        Assert.Equal(InventoryImportStatus.Stabilizing, (await service.PollAsync()).Status);
        Assert.Equal(0, source.Reads);
        Assert.Equal(InventoryImportStatus.Imported, (await service.PollAsync()).Status);
        var snapshot = service.Snapshot();
        var item = Assert.Single(snapshot.State.Items);
        Assert.Equal("SWORD +10", item.Item);
        Assert.Equal(3, item.Owned);
        Assert.Equal(item.Carried + item.Bank + item.Hoard + item.Depot + item.OtherStorage, item.Owned);
        Assert.Equal(1, item.Collection);
        Assert.Equal(1, item.Installed);
        Assert.Equal(At.AddMinutes(1), item.LastLootUtc);
        Assert.Equal(5, InventoryQueries.CopyLocations(snapshot.Export!.Rows, "Sword").Sum(copy => copy.Quantity));
        Assert.Equal(InventoryImportStatus.Unchanged, (await service.PollAsync()).Status);
        Assert.Equal(1, source.Reads);
        await service.SaveAsync();
        Assert.Equal(3, Assert.Single(repository.Writes).Items.Single().Owned);
    }

    [Fact]
    public async Task ChangedMetadataAndParseFailureKeepLastGoodExportAndRetry()
    {
        var source = new FakeSource();
        var service = Create(source);
        await Import(service);
        var original = service.Snapshot();
        source.Stamp = source.Stamp! with { Written = At.AddMinutes(1) };
        Assert.Equal(InventoryImportStatus.Stabilizing, (await service.PollAsync()).Status);
        source.Error = new FormatException("Partial export.");
        await Assert.ThrowsAsync<FormatException>(() => service.PollAsync());
        Assert.Same(original.Export, service.Snapshot().Export);
        Assert.Equal(original.State.Items[0].Owned, service.Snapshot().State.Items[0].Owned);
        source.Error = null;
        source.Read = () =>
        {
            source.Stamp = source.Stamp! with { Size = 99 };
            return Task.FromResult<IReadOnlyList<InventoryExportItem>?>(source.Rows);
        };
        Assert.Equal(InventoryImportStatus.Stabilizing, (await service.PollAsync()).Status);
        Assert.Same(original.Export, service.Snapshot().Export);
        source.Read = null;
        await Import(service);
        Assert.NotSame(original.Export, service.Snapshot().Export);
    }

    [Theory]
    [InlineData("other-log", "Other")]
    [InlineData("other-log", "Que")]
    public async Task SwitchingSessionDuringReadCannotCommitOldExport(string nextLog, string nextCharacter)
    {
        var source = new FakeSource();
        var service = Create(source);
        var entered = Signal();
        var resume = new TaskCompletionSource<IReadOnlyList<InventoryExportItem>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Read = () =>
        {
            entered.SetResult();
            return resume.Task;
        };
        await service.PollAsync();
        var importing = service.PollAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.SelectSession(nextLog, nextCharacter);
        resume.SetResult(source.Rows);
        Assert.Equal(InventoryImportStatus.Superseded, (await importing).Status);
        Assert.Null(service.Snapshot().Export);
        Assert.Empty(service.Snapshot().State.Items);
        source.Read = null;
        await Import(service);
        Assert.Equal(nextCharacter, service.Snapshot().Export!.Character);
    }

    [Fact]
    public async Task CancellationBeforeCommitDoesNotPublishAndNextPollCanRetry()
    {
        var source = new FakeSource();
        var service = Create(source);
        using var cancellation = new CancellationTokenSource();
        source.Read = () =>
        {
            cancellation.Cancel();
            return Task.FromResult<IReadOnlyList<InventoryExportItem>?>(source.Rows);
        };
        await service.PollAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PollAsync(cancellation.Token));
        Assert.Null(service.Snapshot().Export);
        source.Read = null;
        Assert.Equal(InventoryImportStatus.Imported, (await service.PollAsync()).Status);
    }

    [Fact]
    public async Task MissingExportClearsLocationsButKeepsConfirmedCounts()
    {
        var source = new FakeSource();
        var service = Create(source);
        await Import(service);
        source.Stamp = null;
        Assert.Equal(InventoryImportStatus.Missing, (await service.PollAsync()).Status);
        Assert.Null(service.Snapshot().Export);
        Assert.Equal(2, service.Snapshot().State.Items[0].Owned);
    }

    [Fact]
    public async Task SnapshotsAndRepositoryWritesNeverShareLiveMutableState()
    {
        var source = new FakeSource();
        var initial = new QuestInventory();
        var repository = new FakeRepository();
        var service = new InventoryService(source, repository, initial);
        service.SelectSession("log", "Que");
        initial.Items.Add(new() { Item = "Outside mutation" });
        await Import(service);
        var detached = service.Snapshot();
        detached.State.Items[0].Owned = 99;
        detached.State.Items.Clear();
        Assert.Equal(2, service.Snapshot().State.Items[0].Owned);
        var rows = Assert.IsAssignableFrom<IList<InventoryExportItem>>(detached.Export!.Rows);
        Assert.True(rows.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => rows.Clear());
        service.TrackItem("Que", "Other");
        Assert.Same(detached.Export, service.Snapshot().Export);
        await service.SaveAsync();
        repository.Writes[0].Items.Clear();
        Assert.Equal(2, service.Snapshot().State.Items.Count);
    }

    [Fact]
    public async Task SavesRemainOrderedAndMutationDuringWriteRequiresANewerSnapshot()
    {
        var entered = Signal();
        var resume = Signal();
        var repository = new FakeRepository
        {
            BeforeWrite = async index =>
            {
                if (index == 0)
                {
                    entered.SetResult();
                    await resume.Task;
                }
            },
        };
        var service = Create(new(), repository);
        service.ObserveLoot(new("Que", "log", 1, "Sword", 1, false, At));
        var first = service.SaveAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.ObserveLoot(new("Que", "log", 2, "Sword", 2, false, At.AddMinutes(1)));
        bool completed = service.ToggleCompletion("Que", "A quest");
        Assert.True(completed);
        var second = service.SaveAsync();
        resume.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, repository.Writes.Count);
        Assert.Equal(1, repository.Writes[0].Items.Single().Historical);
        Assert.Equal(3, repository.Writes[1].Items.Single().Historical);
        Assert.Empty(repository.Writes[0].Completed);
        Assert.Single(repository.Writes[1].Completed);
        await service.SaveAsync();
        Assert.Equal(2, repository.Writes.Count);
        Assert.False(service.ToggleCompletion("Que", "A quest"));
    }

    [Fact]
    public async Task FailedSaveRemainsDirtyAndBlockedLoadNeverOverwritesExistingData()
    {
        var repository = new FakeRepository { Error = new IOException("Disk full.") };
        var service = Create(new(), repository);
        service.TrackItem("Que", "Sword");
        await Assert.ThrowsAsync<IOException>(() => service.SaveAsync());
        repository.Error = null;
        await service.SaveAsync();
        Assert.Single(repository.Writes);
        var blocked = new InventoryService(new FakeSource(), repository, new(), new FormatException("Corrupt document."));
        blocked.TrackItem("Que", "Do not save");
        await Assert.ThrowsAsync<InvalidOperationException>(() => blocked.SaveAsync());
        Assert.Single(repository.Writes);
    }

    [Fact]
    public async Task CanceledSaveRemainsDirtyAndWorkerSaveCanDrainWithoutADispatcher()
    {
        using var cancellation = new CancellationTokenSource();
        var repository = new FakeRepository
        {
            BeforeWrite = _ =>
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
        };
        var service = Create(new(), repository);
        service.ObserveLoot(new("Que", "log", 1, "Sword", 2, false, At));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() => service.SaveAsync(cancellation.Token)));
        Assert.Empty(repository.Writes);
        repository.BeforeWrite = null;
        await Task.Run(() => service.SaveAsync());
        Assert.Equal(2, Assert.Single(repository.Writes).Items.Single().Historical);
    }

    [Fact]
    public async Task ReadQueriesAndLookupOnlyTrackingNeverReadFilesOrClaimOwnership()
    {
        var source = new FakeSource();
        var service = Create(source);
        service.TrackItem("Que", "Sword +1");
        service.TrackItem("QUE", "sword +3");
        var snapshot = service.Snapshot();
        Assert.Equal(0, Assert.Single(snapshot.State.Items).Owned);
        Assert.Empty(InventoryQueries.CopyLocations([], "Sword"));
        Assert.Equal(0, source.Inspections);
        Assert.Equal(0, source.Reads);
        await service.SaveAsync();
        Assert.Equal(0, source.Inspections);
    }

    [Fact]
    public void ProcessingRequiresAPositiveBudgetAndPreservesQueuedLootOnInvalidInput()
    {
        var service = Create(new());
        service.ObserveLoot(new("Que", "log", 1, "Sword", 2, false, At));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.ProcessPending(TimeSpan.Zero));
        Assert.Empty(service.Snapshot().State.Items);
        Assert.True(service.ProcessPending(TimeSpan.FromMilliseconds(5)));
        Assert.Equal(2, Assert.Single(service.Snapshot().State.Items).Historical);
        Assert.False(service.ProcessPending(TimeSpan.FromMilliseconds(5)));
    }

    private static InventoryService Create(FakeSource source, FakeRepository? repository = null)
    {
        var service = new InventoryService(source, repository ?? new(), new());
        service.SelectSession("log", "Que");
        return service;
    }

    private static async Task Import(InventoryService service)
    {
        Assert.Equal(InventoryImportStatus.Stabilizing, (await service.PollAsync()).Status);
        Assert.Equal(InventoryImportStatus.Imported, (await service.PollAsync()).Status);
    }

    private static TaskCompletionSource Signal()
    {
        return new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FakeSource : IInventoryExportSource
    {
        public InventoryFileStamp? Stamp { get; set; } = new("export", 10, At);
        public IReadOnlyList<InventoryExportItem> Rows { get; set; } =
            [new("Held", "Sword +2", 1, 2, InventoryStorage.Carried)];
        public Exception? Error { get; set; }
        public Func<Task<IReadOnlyList<InventoryExportItem>?>>? Read { get; set; }
        public int Inspections { get; private set; }
        public int Reads { get; private set; }

        public Task<InventoryFileStamp?> InspectAsync(string logPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Inspections++;
            return Task.FromResult(Stamp);
        }

        public Task<IReadOnlyList<InventoryExportItem>?> ReadAsync(InventoryFileStamp expected, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            if (Error is not null)
            {
                throw Error;
            }
            return Read?.Invoke() ?? Task.FromResult<IReadOnlyList<InventoryExportItem>?>(Rows);
        }
    }

    private sealed class FakeRepository : IQuestInventoryRepository
    {
        public List<QuestInventory> Writes { get; } = [];
        public Exception? Error { get; set; }
        public Func<int, Task>? BeforeWrite { get; set; }

        public Task<QuestInventory> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new QuestInventory());
        }

        public async Task SaveAsync(QuestInventory snapshot, CancellationToken cancellationToken = default)
        {
            if (Error is not null)
            {
                throw Error;
            }
            if (BeforeWrite is not null)
            {
                await BeforeWrite(Writes.Count);
            }
            Writes.Add(snapshot);
        }
    }
}
