using EqLog.Application.Wiki;
using EqLog.Core;

namespace EqLog.Application.Tests;

public sealed class WikiServiceTests
{
    [Fact]
    public async Task CacheWriteFailureIsVisibleAndCachedResultRetriesWithoutAnotherFetch()
    {
        var transport = new Transport();
        var caches = new Caches { Fail = true };
        await using var service = new WikiService(transport, new(), caches);
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CacheWriteFailed += (_, error) => reported.TrySetResult(error);
        var first = await service.GetQuestAsync("Item", WikiPriority.Interactive, false, default);
        Assert.IsType<IOException>(await reported.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(service.GetCacheError("quest:Item"));
        Assert.Contains(service.GetActivity().Recent, result => result.IsError);
        caches.Fail = false;
        var second = await service.GetQuestAsync("Item", WikiPriority.Interactive, false, default);
        Assert.Same(first, second);
        Assert.Null(service.GetCacheError("quest:Item"));
        Assert.Equal(1, transport.Fetches);
        Assert.Equal(2, caches.Writes);
    }

    [Fact]
    public async Task FailedForcedRefreshPreservesLastGoodResult()
    {
        var transport = new Transport();
        await using var service = new WikiService(transport, new(), new Caches());
        var first = await service.GetQuestAsync("Item", WikiPriority.Interactive, false, default);
        transport.Fail = true;
        await Assert.ThrowsAsync<WikiTransportException>(() => service.GetQuestAsync("Item", WikiPriority.Interactive, true, default));
        Assert.Same(first, await service.GetQuestAsync("Item", WikiPriority.Interactive, false, default));
        Assert.Equal(2, transport.Fetches);
    }

    [Fact]
    public async Task TradeskillCacheFailureRetriesWithoutRefetchAndForceFailureKeepsLastGoodPage()
    {
        var transport = new Transport();
        var caches = new Caches { Fail = true };
        await using var service = new WikiService(transport, new(), caches);
        string key = TradeskillQueries.CacheKey(TradeskillPageKind.Skill, "Alchemy");

        var first = await service.GetTradeskillAsync(TradeskillPageKind.Skill, "Alchemy", WikiPriority.Interactive, false, default);
        Assert.NotNull(service.GetCacheError(key));
        caches.Fail = false;
        Assert.Same(first, await service.GetTradeskillAsync(TradeskillPageKind.Skill, "Alchemy", WikiPriority.Interactive, false, default));
        Assert.Equal(1, transport.Fetches);
        Assert.Equal(2, caches.Writes);
        Assert.Null(service.GetCacheError(key));
        transport.Fail = true;
        await Assert.ThrowsAsync<WikiTransportException>(() =>
            service.GetTradeskillAsync(TradeskillPageKind.Skill, "Alchemy", WikiPriority.Interactive, true, default));
        Assert.Same(first, await service.GetTradeskillAsync(TradeskillPageKind.Skill, "Alchemy", WikiPriority.Interactive, false, default));
    }

    [Fact]
    public async Task TradeskillKindsHaveSeparateCachesAndExpiredDataRefreshes()
    {
        var transport = new Transport();
        await using var service = new WikiService(transport, new(), new Caches());
        service.SeedTradeskill(new(TradeskillPageKind.Recipe, "Test_Item", DateTime.UtcNow.AddDays(-2), [], []));

        await service.GetTradeskillAsync(TradeskillPageKind.Recipe, "Test Item", WikiPriority.Interactive, false, default);
        await service.GetTradeskillAsync(TradeskillPageKind.Merchants, "Test_Item", WikiPriority.Interactive, false, default);
        await service.GetTradeskillAsync(TradeskillPageKind.Recipe, "Test_Item", WikiPriority.Interactive, false, default);

        Assert.Equal(2, transport.Fetches);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task TradeskillCatalogRefreshesOldParserDataEvenWhenRecentlyCached(bool currentVersion, int expectedFetches)
    {
        var transport = new Transport();
        await using var service = new WikiService(transport, new(), new Caches());
        service.SeedTradeskill(new(TradeskillPageKind.Skill, "Tailoring", DateTime.UtcNow, [], [])
        {
            CatalogVersion = currentVersion ? Tradeskills.CatalogVersion : 0,
        });

        var first = await service.GetTradeskillAsync(TradeskillPageKind.Skill, "Tailoring", WikiPriority.Interactive, false, default);
        var second = await service.GetTradeskillAsync(TradeskillPageKind.Skill, "Tailoring", WikiPriority.Interactive, false, default);

        Assert.Equal(expectedFetches, transport.Fetches);
        Assert.Same(first, second);
    }

    private sealed class Caches : IWikiCacheRepository
    {
        public IReadOnlyList<string> LoadWarnings { get; } = [];
        public bool Fail
        {
            get; set;
        }
        public int Writes
        {
            get; private set;
        }
        public Task SaveQuestAsync(QuestLookupResult result, CancellationToken cancellationToken = default)
        {
            Writes++;
            return Fail ? Task.FromException(new IOException("Cache write failed.")) : Task.CompletedTask;
        }
        public Task<IReadOnlyList<QuestLookupResult>> LoadQuestsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<QuestLookupResult>>([]);
        }
        public Task<IReadOnlyList<ItemEffectInfo>> LoadItemEffectsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ItemEffectInfo>>([]);
        }
        public Task<SkyQuestCatalog?> LoadSkyAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SkyQuestCatalog?>(null);
        }
        public Task SaveMobMetadataAsync(MobResistanceStore mobs, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
        public Task SaveItemEffectsAsync(ItemEffectInfo result, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
        public Task SaveSkyAsync(SkyQuestCatalog catalog, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<TradeskillWikiPage>> LoadTradeskillsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<TradeskillWikiPage>>([]);
        }
        public Task SaveTradeskillAsync(TradeskillWikiPage result, CancellationToken cancellationToken = default)
        {
            Writes++;
            return Fail ? Task.FromException(new IOException("Cache write failed.")) : Task.CompletedTask;
        }
        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
        public Task ResetAsync(MobResistanceStore mobs, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class Transport : IWikiTransport
    {
        private readonly List<WikiActivityEvent> _events = [];
        public int Fetches
        {
            get; private set;
        }
        public bool Fail
        {
            get; set;
        }
        public int PendingCount
        {
            get
            {
                return 0;
            }
        }
        public Task<QuestLookupResult> FindQuestAsync(string item, WikiRequestContext context, CancellationToken cancellationToken)
        {
            Fetches++;
            return Fail ? Task.FromException<QuestLookupResult>(new WikiTransportException("Offline", new Exception("Offline"))) :
                Task.FromResult(new QuestLookupResult(item, DateTime.UtcNow, []));
        }
        public void RecordFailure(string name, Exception error)
        {
            _events.Add(new(DateTime.UtcNow, name, error.Message, true));
        }
        public void RecordCompletion(string name, bool cacheHit)
        {
            _events.Add(new(DateTime.UtcNow, name, "Completed", false));
        }
        public WikiActivitySnapshot Snapshot()
        {
            return new([], _events.ToArray(), []);
        }
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
        public Task<MobWikiInfo> FindMobAsync(string mob, WikiRequestContext context, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
        public Task<SpellResistanceInfo> FindSpellAsync(string spell, WikiRequestContext context, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
        public Task<ItemEffectInfo> FindItemEffectsAsync(string item, WikiRequestContext context, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
        public Task<SkyQuestCatalog> FindSkyAsync(WikiRequestContext context, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
        public Task<TradeskillWikiPage> FindTradeskillAsync(TradeskillPageKind kind, string title,
            WikiRequestContext context, CancellationToken cancellationToken)
        {
            Fetches++;
            return Fail ? Task.FromException<TradeskillWikiPage>(new WikiTransportException("Offline", new Exception("Offline"))) :
                Task.FromResult(new TradeskillWikiPage(kind, title, DateTime.UtcNow, [], [])
                {
                    CatalogVersion = Tradeskills.CatalogVersion,
                });
        }
    }
}
