using EqLog.Core;
using EqLog.Application.Tracking;

namespace EqLog.Application.Wiki;

/// <summary>Application-wide wiki access, shared caches and in-flight work, with priority-aware HTTP scheduling.</summary>
public sealed class WikiService : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Operation> _operations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QuestLookupResult> _quests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ItemEffectInfo> _itemEffects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _retryAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _cacheErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly MobResistanceStore _mobs;
    private readonly IWikiCacheRepository _caches;
    private readonly CancellationTokenSource _stop = new();
    private readonly IWikiTransport _transport;
    private SkyQuestCatalog? _sky;
    private bool _disposed;
    private bool _resetting;
    private long _cacheGeneration;

    public event Action<string, Exception>? BackgroundFailed;
    public event Action<string, Exception>? CacheWriteFailed;

    public string? GetCacheError(string operation)
    {
        lock (_gate)
        {
            return _cacheErrors.GetValueOrDefault(operation);
        }
    }

    private async Task PersistCacheAsync(string operation, Func<Task> save)
    {
        try
        {
            await save().ConfigureAwait(false);
            lock (_gate)
            {
                _cacheErrors.Remove(operation);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            lock (_gate)
            {
                _cacheErrors[operation] = ex.Message;
            }
            _transport.RecordFailure(operation, ex);
            CacheWriteFailed?.Invoke(operation, ex);
        }
    }

    /// <summary>Returns live requests and recent outcomes without exposing mutable scheduler state.</summary>
    public WikiActivitySnapshot GetActivity()
    {
        string[] operations;
        lock (_gate)
        {
            operations = _operations.Keys.ToArray();
        }
        return _transport.Snapshot() with
        {
            Operations = operations
        };
    }

    internal int PendingRequestCount
    {
        get
        {
            return _transport.PendingCount;
        }
    }

    public WikiService(IWikiTransport transport, MobResistanceStore mobs, IWikiCacheRepository caches)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(mobs);
        ArgumentNullException.ThrowIfNull(caches);
        _mobs = mobs;
        _caches = caches;
        _transport = transport;
    }

    public Task<MobWikiInfo> GetMobAsync(string mob, WikiPriority priority, bool force, CancellationToken token)
    {
        return GetAsync("mob:" + MobResistanceStore.MobKey(mob), priority, force, async operation =>
        {
            var ct = operation.Cancellation.Token;
            var cached = _mobs.Get("", mob).Wiki;
            lock (_gate)
            {
                if (!operation.Force && cached is not null && !(cached.Found && (cached.Stats is null || cached.Quests is null)) &&
                    DateTime.UtcNow - cached.CheckedUtc < TimeSpan.FromDays(1))
                {
                    operation.CacheHit = true;
                    return cached;
                }
            }
            var result = await _transport.FindMobAsync(mob, operation.Context, ct).ConfigureAwait(false);
            _mobs.SetWiki(mob, result);
            await _caches.SaveMobMetadataAsync(_mobs, ct).ConfigureAwait(false);
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
            var result = await _transport.FindSpellAsync(spell, operation.Context, ct).ConfigureAwait(false);
            _mobs.SetSpell(result);
            await _caches.SaveMobMetadataAsync(_mobs, ct).ConfigureAwait(false);
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
            QuestLookupResult? cached = null;
            lock (_gate)
            {
                if (!operation.Force && _quests.TryGetValue(item, out var entry) &&
                    !entry.NeedsRefresh)
                {
                    operation.CacheHit = true;
                    cached = entry;
                }
            }
            var result = cached ?? await _transport.FindQuestAsync(item, operation.Context, ct).ConfigureAwait(false);
            SeedQuest(result);
            await PersistCacheAsync("quest:" + item, () => _caches.SaveQuestAsync(result, ct)).ConfigureAwait(false);
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

    /// <summary>Seeds an item-effect cache entry loaded from local storage.</summary>
    /// <param name="result">Previously fetched base-item effects.</param>
    public void SeedItemEffects(ItemEffectInfo result)
    {
        lock (_gate)
        {
            _itemEffects[result.Item] = result;
        }
    }

    /// <summary>Gets cached or scheduled base-item effects through the shared wiki HTTP lane.</summary>
    /// <param name="item">Base item name without exaltation or upgrade suffixes.</param>
    /// <param name="priority">Scheduler priority.</param>
    /// <param name="force">Bypasses completed cache entries.</param>
    /// <param name="token">Cancels this caller's wait.</param>
    /// <returns>Explicit effect data and linked descriptions.</returns>
    public Task<ItemEffectInfo> GetItemEffectsAsync(string item, WikiPriority priority, bool force, CancellationToken token)
    {
        item = ItemNames.Normalize(item);
        return GetAsync("item-effects:" + item, priority, force, async operation =>
        {
            ItemEffectInfo? cached = null;
            lock (_gate)
            {
                if (!operation.Force && _itemEffects.TryGetValue(item, out var entry) && !entry.NeedsRefresh)
                {
                    operation.CacheHit = true;
                    cached = entry;
                }
            }
            var result = cached ?? await _transport.FindItemEffectsAsync(item, operation.Context, operation.Cancellation.Token).ConfigureAwait(false);
            SeedItemEffects(result);
            await PersistCacheAsync("item-effects:" + item,
                () => _caches.SaveItemEffectsAsync(result, operation.Cancellation.Token)).ConfigureAwait(false);
            return result;
        }, token);
    }

    public Task<SkyQuestCatalog> GetSkyAsync(WikiPriority priority, bool force, CancellationToken token)
    {
        return GetAsync("sky", priority, force, async operation =>
        {
            var ct = operation.Cancellation.Token;
            SkyQuestCatalog? cached = null;
            lock (_gate)
            {
                if (!operation.Force && _sky is not null && _sky.Quests.Count > 0 && _sky.DropSources is not null &&
                    _sky.Quests.All(q => q.Requirements is not null && q.RuneLinks is not null && q.ItemLinks is not null) &&
                    DateTime.UtcNow - _sky.CheckedUtc < TimeSpan.FromDays(1))
                {
                    operation.CacheHit = true;
                    cached = _sky;
                }
            }
            var result = cached ?? await _transport.FindSkyAsync(operation.Context, ct).ConfigureAwait(false);
            SeedSky(result);
            await PersistCacheAsync("sky", () => _caches.SaveSkyAsync(result, ct)).ConfigureAwait(false);
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

    /// <summary>Retries failed cache writes without refetching successfully parsed data.</summary>
    public Task SaveCachesAsync(CancellationToken cancellationToken = default)
    {
        return _caches.FlushAsync(cancellationToken);
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
            if (_disposed || _resetting || _retryAfter.GetValueOrDefault(key) > DateTime.UtcNow ||
                _operations.ContainsKey("spell:" + key))
            {
                return;
            }
            // Start the repository operation while holding the same gate used by duplicate observations.
            _ = ObserveSpellAsync(spell, key, _cacheGeneration);
        }
    }

    private async Task ObserveSpellAsync(string spell, string key, long generation)
    {
        try
        {
            await GetSpellAsync(spell, WikiPriority.Background, false, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposed || generation != Volatile.Read(ref _cacheGeneration))
        {
        }
        catch (Exception ex) when (ex is WikiTransportException or IOException or UnauthorizedAccessException or
            System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException)
        {
            lock (_gate)
            {
                if (generation != _cacheGeneration)
                {
                    return;
                }
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
            if (_resetting)
            {
                throw new OperationCanceledException("Wiki caches are being reset.");
            }
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
        try
        {
            if (operation.Cancellation.IsCancellationRequested)
            {
                operation.Completion.TrySetCanceled(operation.Cancellation.Token);
                return;
            }
            // Inspect the outcome instead of rethrowing, so cancellation does not raise another exception here.
            var fetching = fetch(operation);
            await ((Task)fetching).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (fetching.IsCompletedSuccessfully)
            {
                _transport.RecordCompletion(key, operation.CacheHit);
                operation.Completion.TrySetResult(fetching.Result!);
            }
            else if (fetching.IsCanceled)
            {
                operation.Completion.TrySetCanceled(operation.Cancellation.Token);
            }
            else
            {
                var error = fetching.Exception!.InnerException ?? fetching.Exception;
                if (error is OperationCanceledException canceled)
                {
                    operation.Completion.TrySetCanceled(canceled.CancellationToken);
                }
                else
                {
                    _transport.RecordFailure(key, error);
                    operation.Completion.TrySetException(error);
                }
            }
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
        }
    }

    /// <summary>Cancels and drains old requests before clearing caches and their application-owned persistence.</summary>
    /// <param name="updateView">Refreshes display-only state while new requests are paused.</param>
    /// <param name="engine">Optional engine whose derived wiki timers must be reset.</param>
    /// <param name="tracking">Persists reset derived timers through the tracking workflow.</param>
    /// <returns>A task completing after caches are cleared and new lookups can start.</returns>
    public async Task ResetCachesAsync(Func<Task> updateView, LogEngine? engine = null, TrackingService? tracking = null)
    {
        ArgumentNullException.ThrowIfNull(updateView);
        Operation[] operations;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_resetting)
            {
                throw new InvalidOperationException("A wiki cache reset is already running.");
            }
            _resetting = true;
            _cacheGeneration++;
            operations = _operations.Values.ToArray();
            foreach (var operation in operations)
            {
                if (!operation.Finished)
                {
                    operation.Cancellation.Cancel();
                }
            }
        }
        try
        {
            await Task.WhenAll(operations.Select(async operation =>
            {
                var finished = await Task.WhenAny(operation.Worker, operation.Context.Abandoned).ConfigureAwait(false);
                await finished.ConfigureAwait(false);
            })).ConfigureAwait(false);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _quests.Clear();
                _itemEffects.Clear();
                _sky = null;
                _retryAfter.Clear();
                _cacheErrors.Clear();
            }
            await _caches.ResetAsync(_mobs).ConfigureAwait(false);
            if (engine is not null)
            {
                engine.ResetWikiCaches();
                if (tracking is not null)
                {
                    await tracking.SaveSpellDurationsAsync(engine).ConfigureAwait(false);
                }
            }
            await updateView().ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _resetting = false;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Operation[] operations;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            operations = _operations.Values.ToArray();
        }
        // Release callers immediately. Queued and in-flight HTTP is abandoned rather than canceled, because a
        // cancellation would be rethrown through every lookup layer for each pending request.
        foreach (var operation in operations)
        {
            operation.Completion.TrySetCanceled();
        }
        await _transport.DisposeAsync().ConfigureAwait(false);
        await _stop.CancelAsync().ConfigureAwait(false);
        foreach (var operation in operations)
        {
            // Abandoned work never resumes, so only finish workers that are parsing or saving.
            await Task.WhenAny(operation.Worker, operation.Context.Abandoned).ConfigureAwait(false);
        }
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

}
