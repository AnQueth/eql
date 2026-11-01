using EqLog.Application.Inventory;
using EqLog.Application.Settings;
using EqLog.Application.Tracking;
using EqLog.Application.Wiki;
using EqLog.Infrastructure.GameFiles;

namespace EqLog.Infrastructure.Tests;

public sealed class ApplicationPersistenceTests
{
    [Fact]
    public async Task FailedSettingsWriteDoesNotSkipOtherStores()
    {
        await using var fixture = new Fixture(blockSettings: true);
        var result = await fixture.Service.SaveAsync(new(), fixture.Engine, new(), new(), true, true);
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
        var result = await fixture.Service.SaveAsync(new(), fixture.Engine, new(), new(), false, false);
        Assert.Equal([PersistedFeature.Mobs, PersistedFeature.Npcs], result.Failures.Select(failure => failure.Feature));
        Assert.True(File.Exists(Path.Combine(fixture.Folder, "settings.json")));
        Assert.False(File.Exists(Path.Combine(fixture.Folder, "mob-resistances.json")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Folder, "npc-compendium")));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "eql-persistence-" + Guid.NewGuid().ToString("N"));
        public LogEngine Engine { get; } = new();
        public ApplicationPersistenceService Service
        {
            get;
        }
        private readonly WikiService _wiki;

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
