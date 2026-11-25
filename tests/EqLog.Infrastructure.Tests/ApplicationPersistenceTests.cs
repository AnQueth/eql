using EqLog.Application.Inventory;
using EqLog.Application.Settings;
using EqLog.Application.Tracking;
using EqLog.Application.Wiki;
using EqLog.Application.Sessions;
using EqLog.Infrastructure.GameFiles;
using EqLog.Core.Tracking;
using System.Text.Json;

namespace EqLog.Infrastructure.Tests;

public sealed class ApplicationPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentTrackingAppearsBeforeResistanceHistoryFinishes(bool damagedResistanceHistory)
    {
        var mobs = new PendingMobRepository();
        await using var fixture = new Fixture(blockSettings: false, mobs);
        string path = Path.Combine(fixture.Folder, "spell-durations.json");
        var repository = new SpellDurationRepository();
        var durations = await repository.LoadAsync(path);
        durations.AddSample("Fire +2", 75, DateTime.UtcNow);
        await repository.SaveAsync(path, durations.ExportData()!);
        var available = new TaskCompletionSource<TrackingStartup>(TaskCreationOptions.RunContinuationsAsynchronously);

        var loading = fixture.Tracking.LoadAsync(coreLoaded: state =>
        {
            available.SetResult(state);
            return Task.CompletedTask;
        });
        var partial = await available.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(loading.IsCompleted);
        Assert.Equal(75, Assert.Single(partial.Durations.List()).LearnedSeconds);
        Assert.NotNull(partial.Npcs);
        Assert.Null(partial.Mobs);

        var mobHistory = new MobResistanceStore();
        if (damagedResistanceHistory)
        {
            mobs.Loaded.SetException(new JsonException("Damaged resistance journal."));
        }
        else
        {
            mobs.Loaded.SetResult(mobHistory);
        }
        var complete = await loading.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(partial.Durations, complete.Durations);
        Assert.Same(partial.Npcs, complete.Npcs);
        if (damagedResistanceHistory)
        {
            Assert.Equal("mobs", Assert.Single(complete.Failures).Store);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Tracking.SaveMobsAsync(mobHistory));
        }
        else
        {
            Assert.Same(mobHistory, complete.Mobs);
            Assert.Empty(complete.Failures);
        }
    }

    [Theory]
    [InlineData(true, false, PersistedFeature.Npcs)]
    [InlineData(false, true, PersistedFeature.Mobs)]
    public async Task ApplicationSessionPreservesIndependentStoreProtection(bool mobsWritable, bool npcsWritable, PersistedFeature expected)
    {
        await using var fixture = new Fixture(blockSettings: false);
        var session = fixture.Session(mobsWritable, npcsWritable);
        session.Mobs.Record("log", 1, "Zone", "Mob", "Poison", true, DateTime.UtcNow);
        var result = await fixture.Service.SaveAsync(new(), session);
        Assert.Equal(expected, Assert.Single(result.Failures).Feature);
        Assert.Equal(mobsWritable, File.Exists(Path.Combine(fixture.Folder, "mob-resistances.json")));
        Assert.Equal(npcsWritable, Directory.Exists(Path.Combine(fixture.Folder, "npc-compendium")));
    }

    [Fact]
    public async Task ApplicationSessionSavesAllLoadedTrackingStateBeforeSelectingALog()
    {
        await using var fixture = new Fixture(blockSettings: false);
        var durations = new EqLog.Core.Tracking.SpellDurationStore();
        durations.AddSample("Fire +2", 75, DateTime.UtcNow);
        var spawns = new EqLog.Core.Tracking.SpawnStore();
        spawns.AddSample("Zone", "Mob", DateTime.UtcNow, 300);
        var motes = new EqLog.Core.Tracking.MoteStore();
        motes.AddMote("Que", "Minor", 3);
        var session = new GameSession(new(durations, spawns, motes, new(), new(), []));
        var result = await fixture.Service.SaveAsync(new(), session);
        Assert.Empty(result.Failures);
        var storedDurations = await new SpellDurationRepository().LoadAsync(Path.Combine(fixture.Folder, "spell-durations.json"));
        var storedSpawns = await new SpawnRepository().LoadAsync(Path.Combine(fixture.Folder, "spawn-timers.json"));
        var storedMotes = await new MoteRepository().LoadAsync(Path.Combine(fixture.Folder, "motes.json"));
        Assert.Equal(75, Assert.Single(storedDurations.List()).LearnedSeconds);
        Assert.Equal(300, Assert.Single(storedSpawns.List("Zone")).LearnedSeconds);
        Assert.Equal(3, Assert.Single(storedMotes.Totals("Que")).Count);
    }

    [Fact]
    public async Task FailedSettingsWriteDoesNotSkipOtherStores()
    {
        await using var fixture = new Fixture(blockSettings: true);
        var result = await fixture.Service.SaveAsync(new(), fixture.Session(true, true));
        var failure = Assert.Single(result.Failures);
        Assert.Equal(PersistedFeature.Settings, failure.Feature);
        Assert.IsAssignableFrom<IOException>(failure.Error);
        Assert.Equal(9, result.Outcomes.Count);
        Assert.True(File.Exists(Path.Combine(fixture.Folder, "quest-inventory.json")));
    }

    [Fact]
    public async Task ProtectedTrackingStoresReturnExplicitFailuresWithoutBeingWritten()
    {
        await using var fixture = new Fixture(blockSettings: false);
        var result = await fixture.Service.SaveAsync(new(), fixture.Session(false, false));
        Assert.Equal([PersistedFeature.Mobs, PersistedFeature.Npcs], result.Failures.Select(failure => failure.Feature));
        Assert.True(File.Exists(Path.Combine(fixture.Folder, "settings.json")));
        Assert.False(File.Exists(Path.Combine(fixture.Folder, "mob-resistances.json")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Folder, "npc-compendium")));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "eql-persistence-" + Guid.NewGuid().ToString("N"));
        public ApplicationPersistenceService Service
        {
            get;
        }
        public TrackingService Tracking { get; }
        private readonly WikiService _wiki;

        public GameSession Session(bool mobsWritable, bool npcsWritable)
        {
            return new(new(new(), new(), new(), mobsWritable ? new() : null, npcsWritable ? new() : null, []));
        }

        public Fixture(bool blockSettings, IMobResistanceRepository? mobLoader = null)
        {
            Directory.CreateDirectory(Folder);
            string settings = Path.Combine(Folder, "settings.json");
            if (blockSettings)
            {
                string blocked = Path.Combine(Folder, "blocked");
                File.WriteAllText(blocked, "not a directory");
                settings = Path.Combine(blocked, "settings.json");
            }
            var mobs = new MobResistanceRepository();
            Tracking = new TrackingService(new SpellDurationRepository(), new SpawnRepository(), new MoteRepository(),
                mobLoader ?? mobs, new NpcCompendiumRepository(), new TrackingResetRepository(),
                new(Folder, Path.Combine(Folder, "spell-durations.json"), Path.Combine(Folder, "spawn-timers.json"),
                    Path.Combine(Folder, "motes.json"), Path.Combine(Folder, "mob-resistances.json"),
                    Path.Combine(Folder, "npc-compendium"), Path.Combine(Folder, "npc-compendium.json")),
                new LevelingHistoryRepository());
            var inventory = new InventoryService(new InventoryExportSource(),
                new QuestInventoryRepository(Path.Combine(Folder, "quest-inventory.json")), new());
            inventory.SelectSession("log", "Que");
            inventory.ObserveLoot(new("Que", "log", 1, "Item", 1, true, DateTime.UtcNow));
            _wiki = new(new WikiTransport(new HttpClient(new NoNetwork()), TimeSpan.Zero, ownsHttp: true), new(),
                new WikiCacheRepository(Folder, mobRepository: mobs));
            Service = new(new SettingsService(new SettingsRepository(settings)), Tracking, inventory, _wiki);
        }

        public async ValueTask DisposeAsync()
        {
            await _wiki.DisposeAsync();
            Directory.Delete(Folder, true);
        }
    }

    private sealed class PendingMobRepository : IMobResistanceRepository
    {
        public TaskCompletionSource<MobResistanceStore> Loaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MobResistanceStore> LoadAsync(string path, CancellationToken cancellationToken = default)
        {
            return Loaded.Task.WaitAsync(cancellationToken);
        }

        public Task SaveAsync(string path, MobResistanceStore store, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("No writes should occur while checking startup.");
        }

        public Task ResetWikiCachesAsync(string path, MobResistanceStore store, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("No reset should occur while checking startup.");
        }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Persistence must not submit HTTP requests.");
        }
    }
}
