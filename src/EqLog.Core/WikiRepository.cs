namespace EqLog.Core;

/// <summary>Application-wide wiki access, shared caches and in-flight work, with priority-aware HTTP scheduling.</summary>
public sealed class WikiRepository : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Operation> _operations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QuestLookupResult> _quests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _retryAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly MobResistanceStore _mobs;
    private readonly Action _saveMobs;
    private readonly CancellationTokenSource _stop = new();
    private readonly WikiRequestScheduler _scheduler;
    private readonly HttpClient _client;
    private readonly AsyncLocal<WikiRequestContext?> _context = new();
    private SkyQuestCatalog? _sky;
    private bool _disposed;

    public event Action<string, Exception>? BackgroundFailed;

    /// <summary>Returns live requests and recent outcomes without exposing mutable scheduler state.</summary>
    public WikiActivitySnapshot GetActivity()
    {
        string[] operations;
        lock (_gate)
        {
            operations = _operations.Keys.ToArray();
        }
        return _scheduler.Snapshot() with
        {
            Operations = operations
        };
    }

    internal int PendingRequestCount
    {
        get
        {
            return _scheduler.PendingCount;
        }
    }

    public WikiRepository(HttpClient http, MobResistanceStore mobs, Action saveMobs,
        TimeSpan? interval = null, TimeSpan? maximumWait = null)
    {
        _mobs = mobs;
        _saveMobs = saveMobs;
        _scheduler = new(http, interval ?? TimeSpan.FromSeconds(1), maximumWait ?? TimeSpan.FromSeconds(30));
        _client = new HttpClient(new ScheduledHandler(_scheduler, _context)) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<MobWikiInfo> GetMobAsync(string mob, WikiPriority priority, bool force, CancellationToken token)
    {
        return GetAsync("mob:" + MobResistanceStore.MobKey(mob), priority, force, async operation =>
        {
            var ct = operation.Cancellation.Token;
            var cached = _mobs.Get("", mob).Wiki;
            lock (_gate)
            {
                if (!operation.Force && cached is not null && !(cached.Found && cached.Stats is null) &&
                    DateTime.UtcNow - cached.CheckedUtc < TimeSpan.FromDays(1))
                {
                    operation.CacheHit = true;
                    return cached;
                }
            }
            var result = await new MobWikiLookup(_client).FindMobAsync(mob, ct).ConfigureAwait(false);
            _mobs.SetWiki(mob, result);
            _saveMobs();
            return result;
        }, token);
    }

    public Task<SpellResistanceInfo> GetSpellAsync(string spell, WikiPriority priority, bool force, CancellationToken token)
    {
        return GetAsync("spell:" + MobResistanceStore.MobKey(spell), priority, force, async operation =>
        {
            var ct = operation.Cancellation.Token;
            var cached = _mobs.GetSpell(spell);
            lock (_gate)
            {
                if (!operation.Force && cached is { NeedsRefresh: false })
                {
                    operation.CacheHit = true;
                    return cached;
                }
            }
            var result = await new MobWikiLookup(_client).FindSpellAsync(spell, ct).ConfigureAwait(false);
            _mobs.SetSpell(result);
            _saveMobs();
            return result;
        }, token);
    }

    public void SeedQuest(QuestLookupResult result)
    {
        lock (_gate)
        {
            _quests[QuestLookup.ItemTitle(result.Item)] = result;
        }
    }

    public Task<QuestLookupResult> GetQuestAsync(string item, WikiPriority priority, bool force, CancellationToken token)
    {
        item = QuestLookup.ItemTitle(item);
        return GetAsync("quest:" + item, priority, force, async operation =>
        {
            var ct = operation.Cancellation.Token;
            lock (_gate)
            {
                if (!operation.Force && _quests.TryGetValue(item, out var cached) &&
                    !cached.NeedsRefresh)
                {
                    operation.CacheHit = true;
                    return cached;
                }
            }
            var result = await new QuestLookup(_client).FindAsync(item, ct).ConfigureAwait(false);
            SeedQuest(result);
            return result;
        }, token);
    }

    public void SeedSky(SkyQuestCatalog catalog)
    {
        lock (_gate)
        {
            _sky = catalog;
        }
    }

    public Task<SkyQuestCatalog> GetSkyAsync(WikiPriority priority, bool force, CancellationToken token)
    {
        return GetAsync("sky", priority, force, async operation =>
        {
            var ct = operation.Cancellation.Token;
            lock (_gate)
            {
                if (!operation.Force && _sky is not null && _sky.Quests.Count > 0 &&
                    _sky.Quests.All(q => q.Requirements is not null && q.RuneLinks is not null && q.ItemLinks is not null) &&
                    DateTime.UtcNow - _sky.CheckedUtc < TimeSpan.FromDays(1))
                {
                    operation.CacheHit = true;
                    return _sky;
                }
            }
            var result = await SkyQuests.FetchAsync(_client, ct).ConfigureAwait(false);
            SeedSky(result);
            return result;
        }, token);
    }

    public void PromoteSky(WikiPriority priority)
    {
        lock (_gate)
        {
            if (_operations.TryGetValue("sky", out var operation))
            {
                operation.Context.Promote(priority);
            }
        }
    }

    public void ObserveSpell(string spell)
    {
        if (string.IsNullOrWhiteSpace(spell) || _mobs.GetSpell(spell) is { NeedsRefresh: false })
        {
            return;
        }
        string key = MobResistanceStore.MobKey(spell);
        lock (_gate)
        {
            if (_disposed || _retryAfter.GetValueOrDefault(key) > DateTime.UtcNow ||
                _operations.ContainsKey("spell:" + key))
            {
                return;
            }
            // Start the repository operation while holding the same gate used by duplicate observations.
            _ = ObserveSpellAsync(spell, key);
        }
    }

    private async Task ObserveSpellAsync(string spell, string key)
    {
        try
        {
            await GetSpellAsync(spell, WikiPriority.Background, false, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or
            System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException)
        {
            lock (_gate)
            {
                _retryAfter[key] = DateTime.UtcNow.AddMinutes(5);
            }
            BackgroundFailed?.Invoke(spell, ex);
        }
    }

    private async Task<T> GetAsync<T>(string key, WikiPriority priority, bool force,
        Func<Operation, Task<T>> fetch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Operation operation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_operations.TryGetValue(key, out operation!) || operation.Cancellation.IsCancellationRequested ||
                (force && operation.CacheHit))
            {
                operation = new(key, priority, force, _stop.Token);
                _operations[key] = operation;
                operation.Worker = Task.Run(() => ExecuteAsync(key, operation, fetch));
            }
            else
            {
                operation.Context.Promote(priority);
                operation.Force |= force;
            }
            operation.Waiters++;
        }
        try
        {
            return (T)await operation.Completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                operation.Waiters--;
                if (operation.Waiters == 0)
                {
                    if (operation.Finished)
                    {
                        operation.Cancellation.Dispose();
                    }
                    else if (!operation.Completion.Task.IsCompleted)
                    {
                        operation.Cancellation.Cancel();
                    }
                }
            }
        }
    }

    private async Task ExecuteAsync<T>(string key, Operation operation, Func<Operation, Task<T>> fetch)
    {
        _context.Value = operation.Context;
        try
        {
            operation.Cancellation.Token.ThrowIfCancellationRequested();
            var result = await fetch(operation).ConfigureAwait(false);
            _scheduler.RecordCompletion(key, operation.CacheHit);
            operation.Completion.TrySetResult(result!);
        }
        catch (OperationCanceledException ex)
        {
            operation.Completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            _scheduler.RecordFailure(key, ex);
            operation.Completion.TrySetException(ex);
        }
        finally
        {
            lock (_gate)
            {
                operation.Finished = true;
                if (_operations.GetValueOrDefault(key) == operation)
                {
                    _operations.Remove(key);
                }
                if (operation.Waiters == 0)
                {
                    operation.Cancellation.Dispose();
                }
            }
            _context.Value = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] workers;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            workers = _operations.Values.Select(o => o.Worker).ToArray();
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(workers).ConfigureAwait(false);
        await _scheduler.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
    }

    private sealed class Operation(string name, WikiPriority priority, bool force, CancellationToken token)
    {
        public WikiRequestContext Context { get; } = new(priority, name);
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(token);
        public TaskCompletionSource<object> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Worker { get; set; } = Task.CompletedTask;
        public int Waiters
        {
            get; set;
        }
        public bool Finished
        {
            get; set;
        }
        public bool Force { get; set; } = force;
        public bool CacheHit
        {
            get; set;
        }
    }

    private sealed class ScheduledHandler(WikiRequestScheduler scheduler, AsyncLocal<WikiRequestContext?> context) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return scheduler.SendAsync(request,
                context.Value ?? throw new InvalidOperationException("Wiki HTTP access requires a repository operation."),
                cancellationToken);
        }
    }
}
