using System.Text.Json;
using EqLog.Application.Inventory;
using EqLog.Application.Tracking;
using EqLog.Application.Wiki;
using EqLog.Application.Sessions;
using EqLog.Core;

namespace EqLog.Application.Settings;

public enum PersistedFeature
{
    Settings, SpellDurations, Spawns, Motes, Inventory, Mobs, Npcs, Wiki,
}
public sealed record PersistenceOutcome(PersistedFeature Feature, Exception? Error);
public sealed record PersistenceResult(IReadOnlyList<PersistenceOutcome> Outcomes)
{
    public IEnumerable<PersistenceOutcome> Failures
    {
        get
        {
            return Outcomes.Where(outcome => outcome.Error is not null);
        }
    }
}

/// <summary>Starts independent saves together and returns explicit per-feature outcomes for periodic, shutdown and update flows.</summary>
public sealed class ApplicationPersistenceService(SettingsService settings, TrackingService tracking,
    InventoryService inventory, WikiService wiki)
{
    public Task<PersistenceResult> SaveAsync(AppSettings state, GameSession session)
    {
        return SaveAsync(state, session.Engine, session.Mobs, session.Npcs, session.MobsWritable, session.NpcsWritable);
    }

    public async Task<PersistenceResult> SaveAsync(AppSettings state, LogEngine engine, MobResistanceStore mobs,
        NpcCompendium npcs, bool mobsWritable, bool npcsWritable)
    {
        Task<PersistenceOutcome>[] saves =
        [
            AttemptAsync(PersistedFeature.Settings, settings.SaveAsync(state)),
            AttemptAsync(PersistedFeature.SpellDurations, tracking.SaveSpellDurationsAsync(engine)),
            AttemptAsync(PersistedFeature.Spawns, tracking.SaveSpawnsAsync(engine)),
            AttemptAsync(PersistedFeature.Motes, tracking.SaveMotesAsync(engine)),
            AttemptAsync(PersistedFeature.Inventory, inventory.SaveAsync()),
            AttemptAsync(PersistedFeature.Wiki, wiki.SaveCachesAsync()),
            AttemptAsync(PersistedFeature.Mobs, mobsWritable ? tracking.SaveMobsAsync(mobs) :
                Task.FromException(new IOException("Mob data could not load; the original file is protected."))),
            AttemptAsync(PersistedFeature.Npcs, npcsWritable ? tracking.SaveNpcsAsync(npcs) :
                Task.FromException(new IOException("NPC data could not load; the original file is protected."))),
        ];
        return new(Array.AsReadOnly(await Task.WhenAll(saves).ConfigureAwait(false)));
    }

    private static async Task<PersistenceOutcome> AttemptAsync(PersistedFeature feature, Task save)
    {
        try
        {
            await save.ConfigureAwait(false);
            return new(feature, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return new(feature, ex);
        }
    }
}
