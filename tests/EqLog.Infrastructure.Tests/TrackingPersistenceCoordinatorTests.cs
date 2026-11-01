using EqLog.Application.Tracking;
using EqLog.Core;
using EqLog.Core.Tracking;
using EqLog.Infrastructure.Persistence;

namespace EqLog.Core.Tests;

public sealed class TrackingPersistenceCoordinatorTests
{
    [Fact]
    public async Task FailedDurationWrite_RestoresDirtyStateForRetry()
    {
        var durations = new FailOnceDurationRepository();
        var coordinator = new TrackingPersistenceCoordinator(
            durations,
            new NoOpSpawnRepository(),
            new NoOpMoteRepository());
        var engine = new LogEngine();
        engine.SetSpellDurationOverride("Clarity", 123);

        await Assert.ThrowsAsync<IOException>(() => coordinator.SaveSpellDurationsAsync(engine, "ignored.json"));
        await coordinator.SaveSpellDurationsAsync(engine, "ignored.json");

        Assert.Equal(2, durations.SaveCount);
        Assert.Equal(123, durations.SavedData!.Overrides!["Clarity"]);
        Assert.Null(engine.CaptureSpellDurationData());
    }

    [Fact]
    public async Task DurationRepositoryWritesOnlyDetachedEngineSnapshots()
    {
        string path = Path.Combine(Path.GetTempPath(), $"eql-duration-{Guid.NewGuid():N}.json");
        try
        {
            var engine = new LogEngine();
            engine.SetSpellDurationOverride("Clarity", 123);
            var coordinator = new TrackingPersistenceCoordinator(
                new SpellDurationRepository(),
                new NoOpSpawnRepository(),
                new NoOpMoteRepository());

            await coordinator.SaveSpellDurationsAsync(engine, path);

            var loaded = await new SpellDurationRepository().LoadAsync(path);
            Assert.Equal(123d, Assert.Single(loaded.List()).OverrideSeconds);
            Assert.Null(engine.CaptureSpellDurationData());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class FailOnceDurationRepository : ISpellDurationRepository
    {
        public int SaveCount
        {
            get; private set;
        }
        public SpellDurationStore.Dto? SavedData
        {
            get; private set;
        }

        public Task<SpellDurationStore> LoadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task SaveAsync(
            string path,
            SpellDurationStore.Dto data,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            if (SaveCount == 1)
            {
                throw new IOException("Synthetic write failure.");
            }

            SavedData = data;
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpSpawnRepository : ISpawnRepository
    {
        public Task<SpawnStore> LoadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task SaveAsync(
            string path,
            Dictionary<string, Dictionary<string, SpawnStore.Entry>> data,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpMoteRepository : IMoteRepository
    {
        public Task<MoteStore> LoadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task SaveAsync(
            string path,
            MoteStore.Data data,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
