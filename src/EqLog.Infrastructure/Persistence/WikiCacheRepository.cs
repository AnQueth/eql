using System.Text.Json;
using EqLog.Application.Wiki;
using EqLog.Application.Tracking;
using EqLog.Core;

namespace EqLog.Infrastructure.Persistence;

/// <summary>Existing wiki documents, with independently ordered writes, dirty retries and explicit damaged-cache protection.</summary>
public sealed class WikiCacheRepository(string folder, bool mobWritesAllowed = true,
    IMobResistanceRepository? mobRepository = null) : IWikiCacheRepository
{
    private readonly IMobResistanceRepository _mobs = mobRepository ?? new MobResistanceRepository();
    private bool _mobWritesAllowed = mobWritesAllowed;
    private readonly SemaphoreSlim _questGate = new(1, 1);
    private readonly SemaphoreSlim _effectGate = new(1, 1);
    private readonly SemaphoreSlim _skyGate = new(1, 1);
    private readonly Dictionary<string, QuestLookupResult> _quests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QuestLookupResult> _savedQuests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ItemEffectInfo> _effects = new(StringComparer.OrdinalIgnoreCase);
    private byte[]? _savedEffectsHash;
    private SkyQuestCatalog? _sky;
    private SkyQuestCatalog? _savedSky;
    private Exception? _questError;
    private Exception? _effectError;
    private Exception? _skyError;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _loadWarnings = new();
    public IReadOnlyList<string> LoadWarnings
    {
        get
        {
            return _loadWarnings.ToArray();
        }
    }
    private string QuestPath { get; } = Path.Combine(folder, "quest-lookups.jsonl");
    private string LegacyQuestPath { get; } = Path.Combine(folder, "quest-lookups.json");
    private string EffectPath { get; } = Path.Combine(folder, "item-effects.json");
    private string SkyPath { get; } = Path.Combine(folder, "sky-quests.json");
    private string MobPath { get; } = Path.Combine(folder, "mob-resistances.json");
    public void ProtectMobWrites(bool writesAllowed)
    {
        _mobWritesAllowed = writesAllowed;
    }

    public async Task<IReadOnlyList<QuestLookupResult>> LoadQuestsAsync(CancellationToken cancellationToken = default)
    {
        await _questGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await QuestLookupStore.LoadAsync(QuestPath, LegacyQuestPath, cancellationToken,
                _loadWarnings.Enqueue).ConfigureAwait(false);
            foreach (var result in loaded)
            {
                _quests[result.Item] = result;
                _savedQuests[result.Item] = result;
            }
            return loaded;
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            _questError = ex;
            throw;
        }
        finally
        {
            _questGate.Release();
        }
    }

    public async Task<IReadOnlyList<ItemEffectInfo>> LoadItemEffectsAsync(CancellationToken cancellationToken = default)
    {
        await _effectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(EffectPath))
            {
                return [];
            }
            var loaded = await AtomicFile.ReadJsonAsync<List<ItemEffectInfo>>(EffectPath, ct: cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException("Empty item-effect cache.");
            if (loaded.Any(result => result is null || string.IsNullOrWhiteSpace(result.Item) ||
                result.Effects is null || result.Effects.Any(effect => effect is null)))
            {
                throw new JsonException("Invalid item-effect cache.");
            }
            foreach (var result in loaded)
            {
                _effects[result.Item] = result;
            }
            _savedEffectsHash = AtomicFile.HashJson(_effects.Values.ToArray());
            return loaded;
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            _effectError = ex;
            throw;
        }
        finally
        {
            _effectGate.Release();
        }
    }

    public async Task<SkyQuestCatalog?> LoadSkyAsync(CancellationToken cancellationToken = default)
    {
        await _skyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(SkyPath))
            {
                return null;
            }
            _sky = await AtomicFile.ReadJsonAsync<SkyQuestCatalog>(SkyPath, ct: cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException("Empty Sky quest cache.");
            if (_sky.Quests is null || _sky.Quests.Count == 0 || _sky.Quests.Any(quest => quest is null))
            {
                throw new JsonException("Sky cache contains no valid quests.");
            }
            _savedSky = _sky;
            return _sky;
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            _skyError = ex;
            throw;
        }
        finally
        {
            _skyGate.Release();
        }
    }

    public Task SaveMobMetadataAsync(MobResistanceStore mobs, CancellationToken cancellationToken = default)
    {
        if (!_mobWritesAllowed)
        {
            throw new InvalidOperationException("Mob tracking data could not be loaded; the existing file is protected.");
        }
        return _mobs.SaveAsync(MobPath, mobs, cancellationToken);
    }

    public async Task SaveQuestAsync(QuestLookupResult result, CancellationToken cancellationToken = default)
    {
        await _questGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_quests.TryGetValue(result.Item, out var newer) || newer.CheckedUtc <= result.CheckedUtc)
            {
                _quests[result.Item] = result;
            }
            await WriteQuestsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _questGate.Release();
        }
    }

    public async Task SaveItemEffectsAsync(ItemEffectInfo result, CancellationToken cancellationToken = default)
    {
        await _effectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_effects.TryGetValue(result.Item, out var newer) || newer.CheckedUtc <= result.CheckedUtc)
            {
                _effects[result.Item] = result;
            }
            await WriteEffectsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _effectGate.Release();
        }
    }

    public async Task SaveSkyAsync(SkyQuestCatalog catalog, CancellationToken cancellationToken = default)
    {
        await _skyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sky is null || _sky.CheckedUtc <= catalog.CheckedUtc)
            {
                _sky = catalog;
            }
            await WriteSkyAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _skyGate.Release();
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        return Task.WhenAll(FlushQuestsAsync(cancellationToken), FlushEffectsAsync(cancellationToken), FlushSkyAsync(cancellationToken));
    }

    private async Task FlushQuestsAsync(CancellationToken token)
    {
        await _questGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await WriteQuestsAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _questGate.Release();
        }
    }
    private async Task FlushEffectsAsync(CancellationToken token)
    {
        await _effectGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await WriteEffectsAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _effectGate.Release();
        }
    }
    private async Task FlushSkyAsync(CancellationToken token)
    {
        await _skyGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await WriteSkyAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _skyGate.Release();
        }
    }

    private async Task WriteQuestsAsync(CancellationToken token)
    {
        Protect(_questError, "Quest");
        foreach (var result in _quests.Values)
        {
            if (_savedQuests.GetValueOrDefault(result.Item) == result)
            {
                continue;
            }
            await QuestLookupStore.AppendAsync(QuestPath, result, token).ConfigureAwait(false);
            _savedQuests[result.Item] = result;
        }
    }
    private async Task WriteEffectsAsync(CancellationToken token)
    {
        Protect(_effectError, "Item-effect");
        if (_effects.Count == 0)
        {
            return;
        }
        var snapshot = _effects.Values.ToArray();
        byte[] hash = AtomicFile.HashJson(snapshot);
        if (_savedEffectsHash is not null && hash.AsSpan().SequenceEqual(_savedEffectsHash))
        {
            return;
        }
        await AtomicFile.WriteJsonAsync(EffectPath, snapshot, ct: token).ConfigureAwait(false);
        _savedEffectsHash = hash;
    }
    private async Task WriteSkyAsync(CancellationToken token)
    {
        Protect(_skyError, "Sky");
        if (_sky is not null && _sky != _savedSky)
        {
            await AtomicFile.WriteJsonAsync(SkyPath, _sky, ct: token).ConfigureAwait(false);
            _savedSky = _sky;
        }
    }

    public async Task ResetAsync(MobResistanceStore mobs, CancellationToken cancellationToken = default)
    {
        if (!_mobWritesAllowed)
        {
            throw new InvalidOperationException("Mob tracking data could not be loaded; reset is disabled.");
        }
        await _questGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _effectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _skyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await WikiCacheReset.ResetAsync(folder, mobs, _mobs).ConfigureAwait(false);
                    _quests.Clear();
                    _savedQuests.Clear();
                    _effects.Clear();
                    _savedEffectsHash = null;
                    _sky = null;
                    _savedSky = null;
                    _questError = null;
                    _effectError = null;
                    _skyError = null;
                }
                finally
                {
                    _skyGate.Release();
                }
            }
            finally
            {
                _effectGate.Release();
            }
        }
        finally
        {
            _questGate.Release();
        }
    }

    private static bool IsStorageFailure(Exception error)
    {
        return error is IOException or UnauthorizedAccessException or JsonException;
    }
    private static void Protect(Exception? error, string name)
    {
        if (error is not null)
        {
            throw new InvalidOperationException($"{name} cache could not be loaded; saving is disabled until reset or repair and restart.", error);
        }
    }
}
