using EqLog.Application.Wiki;
using EqLog.Core;

namespace EqLog.Infrastructure.Wiki;

/// <summary>Owns the paced/retrying HTTP lane and clients, but not shared request deduplication or cache policy.</summary>
public sealed class WikiTransport : IWikiTransport
{
    private readonly WikiRequestScheduler _scheduler;
    private readonly HttpClient _client;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly AsyncLocal<WikiRequestContext?> _context = new();

    public WikiTransport(HttpClient http, TimeSpan? interval = null, TimeSpan? maximumWait = null, bool ownsHttp = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _ownsHttp = ownsHttp;
        _scheduler = new(http, interval ?? TimeSpan.FromSeconds(1), maximumWait ?? TimeSpan.FromSeconds(30));
        _client = new(new ScheduledHandler(_scheduler, _context))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public int PendingCount
    {
        get
        {
            return _scheduler.PendingCount;
        }
    }
    public WikiActivitySnapshot Snapshot()
    {
        return _scheduler.Snapshot();
    }
    public void RecordFailure(string name, Exception error)
    {
        _scheduler.RecordFailure(name, error);
    }
    public void RecordCompletion(string name, bool cacheHit)
    {
        _scheduler.RecordCompletion(name, cacheHit);
    }
    public Task<MobWikiInfo> FindMobAsync(string mob, WikiRequestContext context, CancellationToken cancellationToken)
    {
        return FetchAsync(context, () => new MobWikiClient(_client).FindMobAsync(mob, cancellationToken));
    }
    public Task<SpellResistanceInfo> FindSpellAsync(string spell, WikiRequestContext context, CancellationToken cancellationToken)
    {
        return FetchAsync(context, () => new MobWikiClient(_client).FindSpellAsync(spell, cancellationToken));
    }
    public Task<QuestLookupResult> FindQuestAsync(string item, WikiRequestContext context, CancellationToken cancellationToken)
    {
        return FetchAsync(context, () => new QuestWikiClient(_client).FindAsync(item, cancellationToken));
    }
    public Task<ItemEffectInfo> FindItemEffectsAsync(string item, WikiRequestContext context, CancellationToken cancellationToken)
    {
        return FetchAsync(context, () => new ItemEffectClient(_client).FindAsync(item, cancellationToken));
    }
    public Task<SkyQuestCatalog> FindSkyAsync(WikiRequestContext context, CancellationToken cancellationToken)
    {
        return FetchAsync(context, () =>
        {
            var client = new SkyWikiClient(_client);
            client.DropSourcesFailed += error => RecordFailure("Sky drop sources", error);
            return client.FetchAsync(cancellationToken);
        });
    }

    private async Task<T> FetchAsync<T>(WikiRequestContext context, Func<Task<T>> fetch)
    {
        _context.Value = context;
        try
        {
            return await fetch().ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new WikiTransportException(ex.Message, ex);
        }
        finally
        {
            _context.Value = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _scheduler.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private sealed class ScheduledHandler(WikiRequestScheduler scheduler, AsyncLocal<WikiRequestContext?> context) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return scheduler.SendAsync(request,
                context.Value ?? throw new InvalidOperationException("Wiki HTTP access requires an application operation."),
                cancellationToken);
        }
    }
}
