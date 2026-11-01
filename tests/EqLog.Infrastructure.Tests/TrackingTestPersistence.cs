using EqLog.Application.Tracking;
using EqLog.Core;
using EqLog.Core.Tracking;
using EqLog.Infrastructure.Persistence;

namespace EqLog.Core.Tests;

internal static class TrackingTestPersistence
{
    private static readonly SpellDurationRepository Durations = new();
    private static readonly SpawnRepository Spawns = new();
    private static readonly MoteRepository Motes = new();
    private static readonly MobResistanceRepository Resistances = new();
    private static readonly NpcCompendiumRepository Npcs = new();
    private static readonly TrackingPersistenceCoordinator Coordinator = new(Durations, Spawns, Motes);

    public static Task<SpellDurationStore> LoadSpellDurationsAsync(string path)
    {
        return Durations.LoadAsync(path);
    }

    public static Task<SpawnStore> LoadSpawnsAsync(string path)
    {
        return Spawns.LoadAsync(path);
    }

    public static Task<MoteStore> LoadMotesAsync(string path)
    {
        return Motes.LoadAsync(path);
    }

    public static Task<MobResistanceStore> LoadResistancesAsync(string path)
    {
        return Resistances.LoadAsync(path);
    }

    public static Task<NpcCompendium> LoadNpcCompendiumAsync(string folder, string? legacyPath = null)
    {
        return Npcs.LoadAsync(folder, legacyPath);
    }

    public static async Task<NpcEntry?> GetNpcAsync(NpcCompendium compendium, string mob, string zone)
    {
        try
        {
            return await Npcs.GetAsync(compendium, mob, zone);
        }
        catch (InvalidOperationException)
        {
            return await compendium.GetAsync(mob, zone);
        }
    }

    public static Task SaveAsync(this SpellDurationStore store, string path)
    {
        var data = store.ExportData(onlyIfDirty: false)!;
        return Durations.SaveAsync(path, data);
    }

    public static Task SaveAsync(this SpawnStore store, string path)
    {
        var data = store.ExportData(onlyIfDirty: false)!;
        return Spawns.SaveAsync(path, data);
    }

    public static Task SaveAsync(this MoteStore store, string path)
    {
        var data = store.ExportData(onlyIfDirty: false)!;
        return Motes.SaveAsync(path, data);
    }

    public static Task SaveAsync(this MobResistanceStore store, string path)
    {
        return Resistances.SaveAsync(path, store);
    }

    public static Task SaveAsync(this NpcCompendium store, string folder)
    {
        return Npcs.SaveAsync(folder, store);
    }

    public static Task ResetWikiCachesAsync(this MobResistanceStore store, string path)
    {
        return Resistances.ResetWikiCachesAsync(path, store);
    }

    public static Task SaveSpellDurationsAsync(
        this LogEngine engine,
        string path,
        bool onlyIfDirty = true,
        CancellationToken cancellationToken = default)
    {
        return Coordinator.SaveSpellDurationsAsync(engine, path, onlyIfDirty, cancellationToken);
    }

    public static Task SaveSpawnsAsync(
        this LogEngine engine,
        string path,
        bool onlyIfDirty = true,
        CancellationToken cancellationToken = default)
    {
        return Coordinator.SaveSpawnsAsync(engine, path, onlyIfDirty, cancellationToken);
    }

    public static Task SaveMotesAsync(
        this LogEngine engine,
        string path,
        bool onlyIfDirty = true,
        CancellationToken cancellationToken = default)
    {
        return Coordinator.SaveMotesAsync(engine, path, onlyIfDirty, cancellationToken);
    }
}
