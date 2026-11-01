using EqLog.Application.Wiki;
using EqLog.Core;

namespace EqLog.Infrastructure.Tests;

/// <summary>Composes production workflow/transport with a test-only cache port for the existing HTTP regression suite.</summary>
internal sealed class WikiTestComposition : IAsyncDisposable
{
    private readonly WikiService _service;
    public WikiTestComposition(HttpClient http, MobResistanceStore mobs, Func<Task> saveMobs,
        TimeSpan? interval = null, TimeSpan? maximumWait = null)
    {
        _service = new(new WikiTransport(http, interval, maximumWait), mobs, new TestCaches(saveMobs));
    }
    public event Action<string, Exception>? BackgroundFailed
    {
        add
        {
            _service.BackgroundFailed += value;
        }
        remove
        {
            _service.BackgroundFailed -= value;
        }
    }
    public int PendingRequestCount
    {
        get
        {
            return _service.PendingRequestCount;
        }
    }
    public WikiActivitySnapshot GetActivity()
    {
        return _service.GetActivity();
    }
    public void SeedQuest(QuestLookupResult result)
    {
        _service.SeedQuest(result);
    }
    public void SeedSky(SkyQuestCatalog catalog)
    {
        _service.SeedSky(catalog);
    }
    public void SeedItemEffects(ItemEffectInfo result)
    {
        _service.SeedItemEffects(result);
    }
    public Task<MobWikiInfo> GetMobAsync(string mob, WikiPriority priority, bool force, CancellationToken token)
    {
        return _service.GetMobAsync(mob, priority, force, token);
    }
    public Task<SpellResistanceInfo> GetSpellAsync(string spell, WikiPriority priority, bool force, CancellationToken token)
    {
        return _service.GetSpellAsync(spell, priority, force, token);
    }
    public Task<QuestLookupResult> GetQuestAsync(string item, WikiPriority priority, bool force, CancellationToken token)
    {
        return _service.GetQuestAsync(item, priority, force, token);
    }
    public Task<ItemEffectInfo> GetItemEffectsAsync(string item, WikiPriority priority, bool force, CancellationToken token)
    {
        return _service.GetItemEffectsAsync(item, priority, force, token);
    }
    public Task<SkyQuestCatalog> GetSkyAsync(WikiPriority priority, bool force, CancellationToken token)
    {
        return _service.GetSkyAsync(priority, force, token);
    }
    public void PromoteSky(WikiPriority priority)
    {
        _service.PromoteSky(priority);
    }
    public void ObserveSpell(string spell)
    {
        _service.ObserveSpell(spell);
    }
    public Task ResetCachesAsync(Func<Task> resetStorage)
    {
        return _service.ResetCachesAsync(resetStorage);
    }
    public ValueTask DisposeAsync()
    {
        return _service.DisposeAsync();
    }

    private sealed class TestCaches(Func<Task> saveMobs) : IWikiCacheRepository
    {
        public IReadOnlyList<string> LoadWarnings { get; } = [];
        public Task<IReadOnlyList<QuestLookupResult>> LoadQuestsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<QuestLookupResult>>([]);
        }
        public Task<SkyQuestCatalog?> LoadSkyAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SkyQuestCatalog?>(null);
        }
        public Task<IReadOnlyList<ItemEffectInfo>> LoadItemEffectsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ItemEffectInfo>>([]);
        }
        public Task SaveMobMetadataAsync(MobResistanceStore mobs, CancellationToken cancellationToken = default)
        {
            return saveMobs();
        }
        public Task SaveQuestAsync(QuestLookupResult result, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
        public Task SaveSkyAsync(SkyQuestCatalog catalog, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
        public Task SaveItemEffectsAsync(ItemEffectInfo result, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
        public Task ResetAsync(MobResistanceStore mobs, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
