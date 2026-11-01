using EqLog.Core;

namespace EqLog.Application.Wiki;

/// <summary>Application-owned priority and abandonment identity for one shared enrichment operation.</summary>
public sealed class WikiRequestContext(WikiPriority priority, string name = "Wiki request")
{
    public string Name { get; } = name;
    private int _priority = (int)priority;
    private readonly TaskCompletionSource _abandoned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Abandoned
    {
        get
        {
            return _abandoned.Task;
        }
    }
    public bool IsAbandoned
    {
        get
        {
            return _abandoned.Task.IsCompleted;
        }
    }
    public WikiPriority Priority
    {
        get
        {
            return (WikiPriority)Volatile.Read(ref _priority);
        }
    }
    public void Abandon()
    {
        _abandoned.TrySetResult();
    }
    public void Promote(WikiPriority priority)
    {
        int previous;
        do
        {
            previous = Volatile.Read(ref _priority);
            if (previous <= (int)priority)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _priority, (int)priority, previous) != previous);
    }
}

/// <summary>Network failures crossing the transport boundary without an Application dependency on HTTP.</summary>
public sealed class WikiTransportException(string message, Exception innerException) : Exception(message, innerException);

public interface IWikiTransport : IAsyncDisposable
{
    WikiActivitySnapshot Snapshot();
    int PendingCount
    {
        get;
    }
    void RecordFailure(string name, Exception error);
    void RecordCompletion(string name, bool cacheHit);
    Task<MobWikiInfo> FindMobAsync(string mob, WikiRequestContext context, CancellationToken cancellationToken);
    Task<SpellResistanceInfo> FindSpellAsync(string spell, WikiRequestContext context, CancellationToken cancellationToken);
    Task<QuestLookupResult> FindQuestAsync(string item, WikiRequestContext context, CancellationToken cancellationToken);
    Task<ItemEffectInfo> FindItemEffectsAsync(string item, WikiRequestContext context, CancellationToken cancellationToken);
    Task<SkyQuestCatalog> FindSkyAsync(WikiRequestContext context, CancellationToken cancellationToken);
}

public interface IWikiCacheRepository
{
    IReadOnlyList<string> LoadWarnings
    {
        get;
    }
    Task<IReadOnlyList<QuestLookupResult>> LoadQuestsAsync(CancellationToken cancellationToken = default);
    Task<SkyQuestCatalog?> LoadSkyAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ItemEffectInfo>> LoadItemEffectsAsync(CancellationToken cancellationToken = default);
    Task SaveMobMetadataAsync(MobResistanceStore mobs, CancellationToken cancellationToken = default);
    Task SaveQuestAsync(QuestLookupResult result, CancellationToken cancellationToken = default);
    Task SaveSkyAsync(SkyQuestCatalog catalog, CancellationToken cancellationToken = default);
    Task SaveItemEffectsAsync(ItemEffectInfo result, CancellationToken cancellationToken = default);
    Task FlushAsync(CancellationToken cancellationToken = default);
    Task ResetAsync(MobResistanceStore mobs, CancellationToken cancellationToken = default);
}
