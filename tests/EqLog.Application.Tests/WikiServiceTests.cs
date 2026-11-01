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
    }
}
