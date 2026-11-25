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
    private readonly SemaphoreSlim _tradeskillGate = new(1, 1);
    private readonly Dictionary<string, TradeskillWikiPage> _tradeskills = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _savedTradeskillsHashes = new(StringComparer.OrdinalIgnoreCase);
    private Exception? _tradeskillError;
    private readonly Dictionary<string, QuestLookupResult> _quests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QuestLookupResult> _savedQuests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ItemEffectInfo> _effects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _savedEffectsHashes = new(StringComparer.OrdinalIgnoreCase);
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
    private string EffectJournalPath { get; } = Path.Combine(folder, "item-effects.jsonl");
    private string SkyPath { get; } = Path.Combine(folder, "sky-quests.json");
    private string TradeskillPath { get; } = Path.Combine(folder, "tradeskills.json");
    private string TradeskillJournalPath { get; } = Path.Combine(folder, "tradeskills.jsonl");
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
            var loaded = File.Exists(EffectPath)
                ? await AtomicFile.ReadJsonAsync<List<ItemEffectInfo>>(EffectPath, ct: cancellationToken).ConfigureAwait(false)
                    ?? throw new JsonException("Empty item-effect cache.")
                : [];
            await foreach (var result in PersistenceJournal.ReadAsync<ItemEffectInfo>(EffectJournalPath, cancellationToken).ConfigureAwait(false))
            {
                loaded.Add(result);
            }
            if (loaded.Any(result => result is null || string.IsNullOrWhiteSpace(result.Item) ||
                result.Effects is null || result.Effects.Any(effect => effect is null)))
            {
                throw new JsonException("Invalid item-effect cache.");
            }
            foreach (var result in loaded)
            {
                _effects[result.Item] = result;
            }
            foreach (var (key, result) in _effects)
            {
                _savedEffectsHashes[key] = AtomicFile.HashJson(result);
            }
            await CompactCacheAsync(EffectPath, EffectJournalPath, _effects.Values.ToArray(), cancellationToken).ConfigureAwait(false);
            return _effects.Values.ToArray();
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

    public async Task<IReadOnlyList<TradeskillWikiPage>> LoadTradeskillsAsync(CancellationToken cancellationToken = default)
    {
        await _tradeskillGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pages = File.Exists(TradeskillPath)
                ? await AtomicFile.ReadJsonAsync<List<TradeskillWikiPage>>(TradeskillPath, ct: cancellationToken).ConfigureAwait(false)
                    ?? throw new JsonException("Empty tradeskill cache.")
                : [];
            await foreach (var page in PersistenceJournal.ReadAsync<TradeskillWikiPage>(TradeskillJournalPath, cancellationToken).ConfigureAwait(false))
            {
                pages.Add(page);
            }
            if (pages.Any(page => page is null || !Enum.IsDefined(page.Kind) || string.IsNullOrWhiteSpace(page.Title) ||
                page.Recipes is null || page.Merchants is null ||
                page.Kind == TradeskillPageKind.Skill && page.Recipes.Count == 0 ||
                page.Recipes.Any(recipe => recipe is null || string.IsNullOrWhiteSpace(recipe.Key) ||
                    string.IsNullOrWhiteSpace(recipe.PageTitle) || recipe.Name is null || recipe.Outcome is null ||
                    recipe.Skill is null || recipe.Trivial is null || recipe.Container is null || recipe.Ingredients is null ||
                    recipe.Ingredients.Any(ingredient => ingredient is null || ingredient.Quantity <= 0 ||
                        string.IsNullOrWhiteSpace(ingredient.PageTitle) || ingredient.Item is null || ingredient.Source is null)) ||
                page.Merchants.Any(merchant => merchant is null || string.IsNullOrWhiteSpace(merchant.Name) ||
                    string.IsNullOrWhiteSpace(merchant.PageTitle) || string.IsNullOrWhiteSpace(merchant.Zone) ||
                    merchant.ZonePageTitle is null || merchant.Area is null || merchant.Location is null)))
            {
                throw new JsonException("Invalid tradeskill cache.");
            }
            foreach (var page in pages)
            {
                _tradeskills[TradeskillQueries.CacheKey(page.Kind, page.Title)] = page;
            }
            foreach (var (key, page) in _tradeskills)
            {
                _savedTradeskillsHashes[key] = AtomicFile.HashJson(page);
            }
            await CompactCacheAsync(TradeskillPath, TradeskillJournalPath, _tradeskills.Values.ToArray(), cancellationToken).ConfigureAwait(false);
            return _tradeskills.Values.ToArray();
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            _tradeskillError = ex;
            throw;
        }
        finally
        {
            _tradeskillGate.Release();
        }
    }

    public async Task SaveTradeskillAsync(TradeskillWikiPage result, CancellationToken cancellationToken = default)
    {
        await _tradeskillGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string key = TradeskillQueries.CacheKey(result.Kind, result.Title);
            if (!_tradeskills.TryGetValue(key, out var newer) || newer.CheckedUtc <= result.CheckedUtc)
            {
                _tradeskills[key] = result;
            }
            await WriteTradeskillsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _tradeskillGate.Release();
        }
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
        return Task.WhenAll(FlushQuestsAsync(cancellationToken), FlushEffectsAsync(cancellationToken),
            FlushSkyAsync(cancellationToken), FlushTradeskillsAsync(cancellationToken));
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

    private async Task FlushTradeskillsAsync(CancellationToken token)
    {
        await _tradeskillGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await WriteTradeskillsAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _tradeskillGate.Release();
        }
    }

    private async Task WriteTradeskillsAsync(CancellationToken token)
    {
        Protect(_tradeskillError, "Tradeskill");
        foreach (var (key, page) in _tradeskills)
        {
            byte[] hash = AtomicFile.HashJson(page);
            if (_savedTradeskillsHashes.TryGetValue(key, out var saved) && hash.AsSpan().SequenceEqual(saved))
            {
                continue;
            }
            await PersistenceJournal.AppendAsync(TradeskillJournalPath, page, token).ConfigureAwait(false);
            _savedTradeskillsHashes[key] = hash;
        }
    }

    private async Task WriteEffectsAsync(CancellationToken token)
    {
        Protect(_effectError, "Item-effect");
        foreach (var (key, result) in _effects)
        {
            byte[] hash = AtomicFile.HashJson(result);
            if (_savedEffectsHashes.TryGetValue(key, out var saved) && hash.AsSpan().SequenceEqual(saved))
            {
                continue;
            }
            await PersistenceJournal.AppendAsync(EffectJournalPath, result, token).ConfigureAwait(false);
            _savedEffectsHashes[key] = hash;
        }
    }

    private static async Task CompactCacheAsync<T>(string path, string journal, T[] records, CancellationToken token)
    {
        if (File.Exists(journal) && new FileInfo(journal).Length >= 1024 * 1024)
        {
            await AtomicFile.WriteJsonAsync(path, records, ct: token).ConfigureAwait(false);
            File.Delete(journal);
        }
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
                    await _tradeskillGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await WikiCacheReset.ResetAsync(folder, mobs, _mobs).ConfigureAwait(false);
                        _quests.Clear();
                        _savedQuests.Clear();
                        _effects.Clear();
                        _savedEffectsHashes.Clear();
                        _sky = null;
                        _savedSky = null;
                        _questError = null;
                        _effectError = null;
                        _skyError = null;
                        _tradeskills.Clear();
                        _savedTradeskillsHashes.Clear();
                        _tradeskillError = null;
                    }
                    finally
                    {
                        _tradeskillGate.Release();
                    }
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
