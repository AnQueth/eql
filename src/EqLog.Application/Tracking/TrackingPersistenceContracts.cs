using EqLog.Core;
using EqLog.Core.Tracking;
using System.Runtime.CompilerServices;

namespace EqLog.Application.Tracking;

public interface ISpellDurationRepository
{
    Task<SpellDurationStore> LoadAsync(string path, CancellationToken cancellationToken = default);

    Task SaveAsync(
        string path,
        SpellDurationStore.Dto data,
        CancellationToken cancellationToken = default);
}

public interface ISpawnRepository
{
    Task<SpawnStore> LoadAsync(string path, CancellationToken cancellationToken = default);

    Task SaveAsync(
        string path,
        Dictionary<string, Dictionary<string, SpawnStore.Entry>> data,
        CancellationToken cancellationToken = default);
}

public interface IMoteRepository
{
    Task<MoteStore> LoadAsync(string path, CancellationToken cancellationToken = default);

    Task SaveAsync(
        string path,
        MoteStore.Data data,
        CancellationToken cancellationToken = default);
}

public interface IMobResistanceRepository
{
    Task<MobResistanceStore> LoadAsync(string path, CancellationToken cancellationToken = default);

    Task SaveAsync(string path, MobResistanceStore store, CancellationToken cancellationToken = default);

    Task ResetWikiCachesAsync(string path, MobResistanceStore store, CancellationToken cancellationToken = default);
}

public interface INpcCompendiumRepository
{
    Task<NpcCompendium> LoadAsync(
        string folder,
        string? legacyPath = null,
        CancellationToken cancellationToken = default);

    Task SaveAsync(string folder, NpcCompendium compendium, CancellationToken cancellationToken = default);

    Task<NpcEntry?> GetAsync(
        NpcCompendium compendium,
        string mob,
        string zone,
        CancellationToken cancellationToken = default);
}

/// <summary>Serializes engine-owned tracking snapshots and delegates all file operations to repositories.</summary>
public sealed class TrackingPersistenceCoordinator
{
    private readonly ISpellDurationRepository _durations;
    private readonly ISpawnRepository _spawns;
    private readonly IMoteRepository _motes;
    private static readonly ConditionalWeakTable<LogEngine, EngineSaveGates> SaveGates = new();

    public TrackingPersistenceCoordinator(
        ISpellDurationRepository durations,
        ISpawnRepository spawns,
        IMoteRepository motes)
    {
        _durations = durations;
        _spawns = spawns;
        _motes = motes;
    }

    public async Task SaveSpellDurationsAsync(
        LogEngine engine,
        string path,
        bool onlyIfDirty = true,
        CancellationToken cancellationToken = default)
    {
        SemaphoreSlim gate = SaveGates.GetValue(engine, _ => new EngineSaveGates()).Durations;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = engine.CaptureSpellDurationData(onlyIfDirty);
            if (snapshot is null)
            {
                return;
            }

            try
            {
                await _durations.SaveAsync(path, snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                engine.MarkSpellDurationDataDirty();
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveSpawnsAsync(
        LogEngine engine,
        string path,
        bool onlyIfDirty = true,
        CancellationToken cancellationToken = default)
    {
        SemaphoreSlim gate = SaveGates.GetValue(engine, _ => new EngineSaveGates()).Spawns;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = engine.CaptureSpawnData(onlyIfDirty);
            if (snapshot is null)
            {
                return;
            }

            try
            {
                await _spawns.SaveAsync(path, snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                engine.MarkSpawnDataDirty();
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveMotesAsync(
        LogEngine engine,
        string path,
        bool onlyIfDirty = true,
        CancellationToken cancellationToken = default)
    {
        SemaphoreSlim gate = SaveGates.GetValue(engine, _ => new EngineSaveGates()).Motes;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = engine.CaptureMoteData(onlyIfDirty);
            if (snapshot is null)
            {
                return;
            }

            try
            {
                await _motes.SaveAsync(path, snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                engine.MarkMoteDataDirty();
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed class EngineSaveGates
    {
        public SemaphoreSlim Durations { get; } = new(1, 1);
        public SemaphoreSlim Spawns { get; } = new(1, 1);
        public SemaphoreSlim Motes { get; } = new(1, 1);
    }
}
