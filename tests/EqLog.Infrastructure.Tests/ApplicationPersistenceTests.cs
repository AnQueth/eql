using EqLog.Application.Inventory;
using EqLog.Application.Settings;
using EqLog.Application.Tracking;
using EqLog.Application.Wiki;
using EqLog.Application.Sessions;
using EqLog.Infrastructure.GameFiles;

namespace EqLog.Infrastructure.Tests;

public sealed class ApplicationPersistenceTests
{
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
        Assert.Equal(8, result.Outcomes.Count);
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
        private readonly WikiService _wiki;

        public GameSession Session(bool mobsWritable, bool npcsWritable)
        {
            return new(new(new(), new(), new(), mobsWritable ? new() : null, npcsWritable ? new() : null, []));
        }

        public Fixture(bool blockSettings)
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
            var tracking = new TrackingService(new SpellDurationRepository(), new SpawnRepository(), new MoteRepository(),
                mobs, new NpcCompendiumRepository(), new TrackingResetRepository(),
                new(Folder, Path.Combine(Folder, "spell-durations.json"), Path.Combine(Folder, "spawn-timers.json"),
                    Path.Combine(Folder, "motes.json"), Path.Combine(Folder, "mob-resistances.json"),
                    Path.Combine(Folder, "npc-compendium"), Path.Combine(Folder, "npc-compendium.json")));
            var inventory = new InventoryService(new InventoryExportSource(),
                new QuestInventoryRepository(Path.Combine(Folder, "quest-inventory.json")), new());
            inventory.SelectSession("log", "Que");
            inventory.ObserveLoot(new("Que", "log", 1, "Item", 1, true, DateTime.UtcNow));
            _wiki = new(new WikiTransport(new HttpClient(new NoNetwork()), TimeSpan.Zero, ownsHttp: true), new(),
                new WikiCacheRepository(Folder, mobRepository: mobs));
            Service = new(new SettingsService(new SettingsRepository(settings)), tracking, inventory, _wiki);
        }

        public async ValueTask DisposeAsync()
        {
            await _wiki.DisposeAsync();
            Directory.Delete(Folder, true);
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
