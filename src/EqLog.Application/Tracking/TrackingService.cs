using System.Text.Json;
using EqLog.Core;
using EqLog.Core.Tracking;

namespace EqLog.Application.Tracking;

public sealed record TrackingStorageLocations(string Folder, string Durations, string Spawns, string Motes,
    string Mobs, string Npcs, string LegacyNpcs);
public sealed record TrackingLoadFailure(string Store, Exception Error);
public sealed record TrackingStartup(SpellDurationStore Durations, SpawnStore Spawns, MoteStore Motes,
    MobResistanceStore? Mobs, NpcCompendium? Npcs, IReadOnlyList<TrackingLoadFailure> Failures);

public interface ITrackingResetRepository
{
    Task RequestAsync(string folder);
    Task<string?> ApplyPendingAsync(string folder);
}

/// <summary>Tracking startup, persistence, lazy history and reset requests without exposing storage paths to views.</summary>
public sealed class TrackingService(ISpellDurationRepository durations, ISpawnRepository spawns, IMoteRepository motes,
    IMobResistanceRepository mobs, INpcCompendiumRepository npcs, ITrackingResetRepository reset,
    TrackingStorageLocations locations)
{
    private readonly TrackingPersistenceCoordinator _saves = new(durations, spawns, motes);
    private readonly Dictionary<string, Exception> _loadErrors = new();

    public async Task<TrackingStartup> LoadAsync(CancellationToken cancellationToken = default)
    {
        var durationLoad = AttemptAsync("durations", () => durations.LoadAsync(locations.Durations, cancellationToken));
        var spawnLoad = AttemptAsync("spawns", () => spawns.LoadAsync(locations.Spawns, cancellationToken));
        var moteLoad = AttemptAsync("motes", () => motes.LoadAsync(locations.Motes, cancellationToken));
        var mobLoad = AttemptAsync("mobs", () => mobs.LoadAsync(locations.Mobs, cancellationToken));
        var npcLoad = AttemptAsync("npcs", () => npcs.LoadAsync(locations.Npcs, locations.LegacyNpcs, cancellationToken));
        await Task.WhenAll(durationLoad, spawnLoad, moteLoad, mobLoad, npcLoad).ConfigureAwait(false);
        TrackingLoadFailure[] failures;
        lock (_loadErrors)
        {
            failures = _loadErrors.Select(pair => new TrackingLoadFailure(pair.Key, pair.Value)).ToArray();
        }
        return new(durationLoad.Result ?? new(), spawnLoad.Result ?? new(), moteLoad.Result ?? new(),
            mobLoad.Result, npcLoad.Result, failures);
    }

    private async Task<T?> AttemptAsync<T>(string store, Func<Task<T>> load) where T : class
    {
        try
        {
            return await load().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            lock (_loadErrors)
            {
                _loadErrors[store] = ex;
            }
            return null;
        }
    }

    public async Task SaveSpellDurationsAsync(LogEngine engine, CancellationToken cancellationToken = default)
    {
        Protect("durations");
        await _saves.SaveSpellDurationsAsync(engine, locations.Durations, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
    public async Task SaveSpawnsAsync(LogEngine engine, CancellationToken cancellationToken = default)
    {
        Protect("spawns");
        await _saves.SaveSpawnsAsync(engine, locations.Spawns, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
    public async Task SaveMotesAsync(LogEngine engine, CancellationToken cancellationToken = default)
    {
        Protect("motes");
        await _saves.SaveMotesAsync(engine, locations.Motes, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
    public async Task SaveMobsAsync(MobResistanceStore store, CancellationToken cancellationToken = default)
    {
        Protect("mobs");
        await mobs.SaveAsync(locations.Mobs, store, cancellationToken).ConfigureAwait(false);
    }
    public async Task SaveNpcsAsync(NpcCompendium compendium, CancellationToken cancellationToken = default)
    {
        Protect("npcs");
        await npcs.SaveAsync(locations.Npcs, compendium, cancellationToken).ConfigureAwait(false);
    }
    public Task<NpcEntry?> GetNpcAsync(NpcCompendium compendium, string mob, string zone, CancellationToken cancellationToken = default)
    {
        return npcs.GetAsync(compendium, mob, zone, cancellationToken);
    }
    public Task RequestResetAsync()
    {
        return reset.RequestAsync(locations.Folder);
    }
    public Task<string?> ApplyPendingResetAsync()
    {
        return reset.ApplyPendingAsync(locations.Folder);
    }
    private void Protect(string store)
    {
        lock (_loadErrors)
        {
            if (_loadErrors.TryGetValue(store, out var error))
            {
                throw new InvalidOperationException($"{store} data could not be loaded; the original document is protected until repair and restart.", error);
            }
        }
    }
}
