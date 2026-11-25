using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using EqLog.Application.Tracking;
using EqLog.Core;
using EqLog.Core.Tracking;

namespace EqLog.Infrastructure.Persistence;

public sealed class SpellDurationRepository : ISpellDurationRepository
{
    public async Task<SpellDurationStore> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var store = new SpellDurationStore();
        SpellDurationStore.Dto? data;
        try
        {
            data = await AtomicFile.ReadJsonAsync<SpellDurationStore.Dto>(
                path,
                ct: cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return store;
        }
        catch (DirectoryNotFoundException)
        {
            return store;
        }

        if (data is null)
        {
            throw new JsonException("Spell duration data is empty.");
        }

        TrackingDataValidation.Validate(data);
        store.ImportData(data);
        return store;
    }

    public Task SaveAsync(
        string path,
        SpellDurationStore.Dto data,
        CancellationToken cancellationToken = default)
    {
        return AtomicFile.WriteJsonAsync(path, data, ct: cancellationToken);
    }
}

public sealed class SpawnRepository : ISpawnRepository
{
    public async Task<SpawnStore> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var store = new SpawnStore();
        Dictionary<string, Dictionary<string, SpawnStore.Entry>>? data;
        try
        {
            data = await AtomicFile.ReadJsonAsync<Dictionary<string, Dictionary<string, SpawnStore.Entry>>>(
                path,
                ct: cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return store;
        }
        catch (DirectoryNotFoundException)
        {
            return store;
        }

        if (data is null)
        {
            throw new JsonException("Spawn data is empty.");
        }

        TrackingDataValidation.Validate(data);
        store.ImportData(data);
        return store;
    }

    public Task SaveAsync(
        string path,
        Dictionary<string, Dictionary<string, SpawnStore.Entry>> data,
        CancellationToken cancellationToken = default)
    {
        return AtomicFile.WriteJsonAsync(path, data, ct: cancellationToken);
    }
}

public sealed class MoteRepository : IMoteRepository
{
    public async Task<MoteStore> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var store = new MoteStore();
        MoteStore.Data? data;
        try
        {
            data = await AtomicFile.ReadJsonAsync<MoteStore.Data>(
                path,
                ct: cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return store;
        }
        catch (DirectoryNotFoundException)
        {
            return store;
        }

        if (data is null)
        {
            throw new JsonException("Mote data is empty.");
        }

        TrackingDataValidation.Validate(data);
        store.ImportData(data);
        return store;
    }

    public Task SaveAsync(
        string path,
        MoteStore.Data data,
        CancellationToken cancellationToken = default)
    {
        return AtomicFile.WriteJsonAsync(path, data, ct: cancellationToken);
    }

}

internal static class TrackingDataValidation
{
    public static void Validate(SpellDurationStore.Dto data)
    {
        if (data.Samples?.Values.Any(samples => samples is null || samples.Any(sample => sample is null)) == true)
        {
            throw new JsonException("Spell duration data contains invalid or null samples.");
        }
    }

    public static void Validate(Dictionary<string, Dictionary<string, SpawnStore.Entry>> data)
    {
        if (data.Values.Any(mobs => mobs is null || mobs.Values.Any(entry =>
            entry is null || entry.Samples is null || entry.Samples.Any(sample => sample is null))))
        {
            throw new JsonException("Spawn data contains invalid or null entries.");
        }
    }

    public static void Validate(MoteStore.Data data)
    {
        if (data.Characters is null || data.Zones is null ||
            data.Characters.Values.Any(character => character is null || character.Tiers is null) ||
            data.Zones.Values.Any(mobs => mobs is null || mobs.Values.Any(mob => mob is null || mob.Tiers is null)))
        {
            throw new JsonException("Mote data contains invalid or null entries.");
        }
    }
}

public sealed class MobResistanceRepository : IMobResistanceRepository
{
    private static readonly JsonSerializerOptions ResistanceJson = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly ConditionalWeakTable<MobResistanceStore, JsonDeltaJournal> _journals = new();

    public async Task<MobResistanceStore> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var store = new MobResistanceStore();
        var empty = JsonSerializer.SerializeToNode(new MobResistanceStore.Data(), ResistanceJson)!.AsObject();
        var (journal, document) = await JsonDeltaJournal.OpenAsync(
            path, empty, ResistanceJson, ValidateIndex, cancellationToken, ValidateChanges,
            ValidateCompleteDocument).ConfigureAwait(false);
        var data = ReadMetadata(document);
        var history = new LazyResistanceHistory(journal, document);
        _journals.Add(store, journal);

        await MoveLegacySpellCacheAsync(path, cancellationToken).ConfigureAwait(false);
        var cached = await JsonLines.LoadLatestAsync<SpellResistanceInfo>(
            SpellCachePath(path),
            spell => MobResistanceStore.MobKey(spell.Spell ?? ""),
            options: ResistanceJson,
            ct: cancellationToken).ConfigureAwait(false);
        store.ImportData(data, cached.Values, history);
        return store;
    }

    public async Task SaveAsync(
        string path,
        MobResistanceStore store,
        CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = store.CapturePersistenceSnapshot(onlyChangedEntries: true);
            if (snapshot is null)
            {
                return;
            }

            var journal = await GetJournalAsync(path, store, cancellationToken, snapshot.HistorySource).ConfigureAwait(false);
            if (snapshot.PendingSpells.Count > 0)
            {
                await JsonLines.AppendAsync(
                    SpellCachePath(path),
                    snapshot.PendingSpells,
                    ResistanceJson,
                    cancellationToken).ConfigureAwait(false);
                store.CompleteSpellCacheAppend(snapshot.PendingSpells.Count);
            }

            await SaveSnapshotAsync(journal, snapshot, cancellationToken).ConfigureAwait(false);
            store.CompletePersistence(snapshot.Version);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task ResetWikiCachesAsync(
        string path,
        MobResistanceStore store,
        CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            store.ResetWikiCaches();
            var snapshot = store.CapturePersistenceSnapshot(onlyChangedEntries: true);
            if (snapshot is null)
            {
                return;
            }

            var journal = await GetJournalAsync(path, store, cancellationToken, snapshot.HistorySource).ConfigureAwait(false);
            File.Delete(SpellCachePath(path));
            File.Delete(LegacySpellCachePath(path));
            await SaveSnapshotAsync(journal, snapshot, cancellationToken).ConfigureAwait(false);
            await journal.CheckpointAsync(cancellationToken).ConfigureAwait(false);
            store.CompletePersistence(snapshot.Version);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public static string SpellCachePath(string path)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return Path.Combine(folder, "spell-info.jsonl");
    }

    private async Task<JsonDeltaJournal> GetJournalAsync(string path, MobResistanceStore store, CancellationToken token,
        IMobResistanceHistorySource? historySource = null)
    {
        if (historySource is LazyResistanceHistory history)
        {
            history.Protect();
            if (!history.Journal.IsForPath(path))
            {
                throw new InvalidOperationException("The mob resistance store is already stored at another path.");
            }
            return history.Journal;
        }
        if (historySource is not null)
        {
            throw new InvalidOperationException("The resistance history source does not support persistence.");
        }
        if (_journals.TryGetValue(store, out var journal))
        {
            if (!journal.IsForPath(path))
            {
                throw new InvalidOperationException("The mob resistance store is already stored at another path.");
            }
            return journal;
        }
        (journal, _) = await JsonDeltaJournal.OpenAsync(
            path, new MobResistanceStore.Data(), ResistanceJson, Validate, token).ConfigureAwait(false);
        _journals.Add(store, journal);
        return journal;
    }

    private static Task SaveSnapshotAsync(JsonDeltaJournal journal, MobResistancePersistenceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.IsDelta)
        {
            if (snapshot.HistorySource is LazyResistanceHistory lazy)
            {
                lazy.Protect();
            }
            string[] entryProperties = snapshot.WikiCachesReset ? ["Mobs"] : ["Mobs", "Wiki"];
            var removals = (snapshot.RemovedMobs ?? [])
                .Select(key => new JsonDeltaJournal.Change(["Mobs", key], true, null)).ToArray();
            return journal.SavePartialAsync(snapshot.Data, entryProperties, removals, cancellationToken);
        }
        return journal.SaveAsync(snapshot.Data, cancellationToken);
    }

    private static MobResistanceStore.Data ReadMetadata(JsonObject document)
    {
        var metadata = new JsonObject();
        foreach (string property in new[] { "Spells", "Watermarks", "ResistanceResetTimes" })
        {
            if (document.TryGetPropertyValue(property, out var value))
            {
                metadata[property] = value?.DeepClone();
            }
        }
        var data = metadata.Deserialize<MobResistanceStore.Data>(ResistanceJson)
            ?? throw new JsonException("Mob resistance metadata is empty.");
        Validate(data);
        return data;
    }

    private static void ValidateIndex(JsonObject document)
    {
        foreach (string property in new[] { "Mobs", "Wiki", "Spells", "Watermarks", "ResistanceResetTimes" })
        {
            if (document.TryGetPropertyValue(property, out var value) && value is not JsonObject)
            {
                throw new JsonException("Mob resistance data contains invalid or null dictionaries.");
            }
        }
        if (document["Mobs"] is JsonObject mobs && mobs.Any(entry => entry.Value is not JsonObject))
        {
            throw new JsonException("Mob resistance data contains invalid or null mob buckets.");
        }
        if (document["Wiki"] is JsonObject wiki &&
            wiki.Any(entry => entry.Value is not JsonObject fields || fields["Resistances"] is not JsonObject))
        {
            throw new JsonException("Mob resistance data contains invalid or null wiki entries.");
        }
        ReadMetadata(document);
    }

    private static void ValidateCompleteDocument(JsonObject document)
    {
        Validate(document.Deserialize<MobResistanceStore.Data>(ResistanceJson)
            ?? throw new JsonException("Mob resistance data is empty."));
    }

    private static void ValidateChanges(JsonObject document, JsonDeltaJournal.Batch batch)
    {
        // Entries are independent: validate changed entries after every batch, not the entire history.
        var changed = new JsonObject();
        foreach (var group in batch.Changes.GroupBy(change => change.Path[0]))
        {
            if (document[group.Key] is JsonObject entries && group.All(change => change.Path.Length > 1))
            {
                var selected = new JsonObject();
                foreach (string key in group.Select(change => change.Path[1]).Distinct())
                {
                    if (entries.TryGetPropertyValue(key, out var value))
                    {
                        selected[key] = value?.DeepClone();
                    }
                }
                changed[group.Key] = selected;
            }
            else if (document.TryGetPropertyValue(group.Key, out var value))
            {
                changed[group.Key] = value?.DeepClone();
            }
        }
        ValidateCompleteDocument(changed);
    }

    private sealed class LazyResistanceHistory(JsonDeltaJournal journal, JsonObject document) : IMobResistanceHistorySource
    {
        private JsonException? _error;
        private readonly Dictionary<string, List<(string Mob, SpellOutcome Outcome)>> _spellOutcomes = [];
        private Dictionary<string, List<(string Mob, string Key, JsonNode? Outcome)>>? _spellIndex;

        internal JsonDeltaJournal Journal { get; } = journal;

        public Dictionary<string, SpellOutcome>? LoadMob(string key)
        {
            var entry = SelectEntry("Mobs", key);
            if (entry is null)
            {
                return null;
            }
            return entry.Mobs[key];
        }

        public MobWikiInfo? LoadWiki(string mob)
        {
            var entry = SelectEntry("Wiki", mob);
            if (entry is null)
            {
                return null;
            }
            return entry.Wiki[mob];
        }

        public IEnumerable<SpellOutcome> SpellOutcomes(string spell, IReadOnlySet<string> excludedMobs)
        {
            Protect();
            if (!_spellOutcomes.TryGetValue(spell, out var outcomes))
            {
                outcomes = ReadSpellOutcomes(spell, excludedMobs);
                _spellOutcomes.Add(spell, outcomes);
            }
            foreach (var (mob, outcome) in outcomes)
            {
                if (!excludedMobs.Contains(mob))
                {
                    yield return outcome;
                }
            }
        }

        private List<(string Mob, SpellOutcome Outcome)> ReadSpellOutcomes(string spell, IReadOnlySet<string> excludedMobs)
        {
            _spellIndex ??= BuildSpellIndex(excludedMobs);
            var outcomes = new List<(string Mob, SpellOutcome Outcome)>();
            if (_spellIndex.TryGetValue(spell, out var entries))
            {
                foreach (var (mob, key, outcome) in entries)
                {
                    if (!excludedMobs.Contains(mob))
                    {
                        outcomes.Add((mob, ReadOutcome(mob, key, outcome)));
                    }
                }
            }
            return outcomes;
        }

        private Dictionary<string, List<(string Mob, string Key, JsonNode? Outcome)>> BuildSpellIndex(
            IReadOnlySet<string> excludedMobs)
        {
            lock (document)
            {
                return BuildSpellIndexCore(excludedMobs);
            }
        }

        private Dictionary<string, List<(string Mob, string Key, JsonNode? Outcome)>> BuildSpellIndexCore(
            IReadOnlySet<string> excludedMobs)
        {
            var index = new Dictionary<string, List<(string Mob, string Key, JsonNode? Outcome)>>();
            if (document["Mobs"] is not JsonObject mobs)
            {
                return index;
            }
            foreach (var (mob, value) in mobs)
            {
                if (excludedMobs.Contains(mob))
                {
                    continue;
                }
                if (value is not JsonObject entries)
                {
                    // A malformed bucket cannot safely be excluded from the shared damage baseline.
                    SelectEntry("Mobs", mob);
                    continue;
                }
                foreach (var (key, outcome) in entries)
                {
                    if (outcome is JsonObject fields && fields["Spell"] is JsonValue spellValue &&
                        spellValue.TryGetValue<string>(out var name) && !string.IsNullOrWhiteSpace(name))
                    {
                        string spell = MobResistanceStore.MobKey(name);
                        if (!index.TryGetValue(spell, out var entriesForSpell))
                        {
                            entriesForSpell = [];
                            index.Add(spell, entriesForSpell);
                        }
                        entriesForSpell.Add((mob, key, outcome));
                        continue;
                    }
                    ReadOutcome(mob, key, outcome);
                }
            }
            return index;
        }

        private SpellOutcome ReadOutcome(string mob, string key, JsonNode? outcome)
        {
            lock (document)
            {
                var selected = new JsonObject { [key] = outcome?.DeepClone() };
                var projection = new JsonObject { ["Mobs"] = new JsonObject { [mob] = selected } };
                return ReadValidated(projection).Mobs[mob][key];
            }
        }

        private MobResistanceStore.Data? SelectEntry(string property, string key)
        {
            lock (document)
            {
                Protect();
                if (document[property] is not JsonObject entries || !entries.TryGetPropertyValue(key, out var value))
                {
                    return null;
                }
                return ReadValidated(new JsonObject
                {
                    [property] = new JsonObject { [key] = value?.DeepClone() }
                });
            }
        }

        private MobResistanceStore.Data ReadValidated(JsonObject selected)
        {
            try
            {
                var data = selected.Deserialize<MobResistanceStore.Data>(ResistanceJson)
                    ?? throw new JsonException("Mob resistance entry is empty.");
                Validate(data);
                return data;
            }
            catch (JsonException error)
            {
                Interlocked.CompareExchange(ref _error, error, null);
                throw;
            }
        }

        internal void Protect()
        {
            if (Volatile.Read(ref _error) is JsonException error)
            {
                throw new JsonException("Resistance history could not be loaded; the original document is protected until repair and restart.", error);
            }
        }

    }
    private static string LegacySpellCachePath(string path)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + ".spells.jsonl");
    }

    private static Task MoveLegacySpellCacheAsync(string path, CancellationToken cancellationToken)
    {
        string legacy = LegacySpellCachePath(path);
        string current = SpellCachePath(path);
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(legacy) && !File.Exists(current))
                {
                    File.Move(legacy, current);
                }
            },
            cancellationToken);
    }

    private static void Validate(MobResistanceStore.Data data)
    {
        if (data.Mobs is null || data.Spells is null || data.Wiki is null || data.Watermarks is null ||
            data.ResistanceResetTimes is null ||
            data.Mobs.Values.Any(spells => spells is null || spells.Values.Any(spell => spell is null ||
                string.IsNullOrWhiteSpace(spell.Spell) || spell.Landed < 0 || spell.Resisted < 0 ||
                spell.DamageSamples?.Values.Any(histogram =>
                    histogram is null || histogram.Any(pair => pair.Key <= 0 || pair.Value <= 0)) == true)) ||
            data.Wiki.Values.Any(wiki => wiki is null || wiki.Resistances is null ||
                wiki.Quests?.Any(quest => quest is null || string.IsNullOrWhiteSpace(quest.Title) ||
                    string.IsNullOrWhiteSpace(quest.Url)) == true) ||
            data.Spells.Values.Any(spell => spell is null || string.IsNullOrWhiteSpace(spell.Spell)) ||
            data.Watermarks.Values.Any(watermark => watermark is null))
        {
            throw new JsonException("Mob resistance data contains invalid or null entries.");
        }
    }
}
