using System.Text.Json;
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

    public async Task<MobResistanceStore> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var store = new MobResistanceStore();
        var data = new MobResistanceStore.Data();
        if (File.Exists(path))
        {
            data = await AtomicFile.ReadJsonAsync<MobResistanceStore.Data>(
                path,
                ct: cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException("Mob resistance data is empty.");
            Validate(data);
        }

        await MoveLegacySpellCacheAsync(path, cancellationToken).ConfigureAwait(false);
        var cached = await JsonLines.LoadLatestAsync<SpellResistanceInfo>(
            SpellCachePath(path),
            spell => MobResistanceStore.MobKey(spell.Spell ?? ""),
            options: ResistanceJson,
            ct: cancellationToken).ConfigureAwait(false);
        store.ImportData(data, cached.Values);
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
            var snapshot = store.CapturePersistenceSnapshot();
            if (snapshot is null)
            {
                return;
            }

            if (snapshot.PendingSpells.Count > 0)
            {
                await JsonLines.AppendAsync(
                    SpellCachePath(path),
                    snapshot.PendingSpells,
                    ResistanceJson,
                    cancellationToken).ConfigureAwait(false);
                store.CompleteSpellCacheAppend(snapshot.PendingSpells.Count);
            }

            await AtomicFile.WriteJsonAsync(
                path,
                snapshot.Data,
                ct: cancellationToken).ConfigureAwait(false);
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
            var snapshot = store.CapturePersistenceSnapshot();
            if (snapshot is null)
            {
                return;
            }

            File.Delete(SpellCachePath(path));
            File.Delete(LegacySpellCachePath(path));
            await AtomicFile.WriteJsonAsync(
                path,
                snapshot.Data,
                ct: cancellationToken).ConfigureAwait(false);
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
