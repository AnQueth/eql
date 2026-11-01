using System.IO;
using EqLog.Application.Tracking;
using EqLog.Infrastructure.Persistence;

namespace EqLog.Overlay;

internal static class TrackingComposition
{
    internal static IMobResistanceRepository MobRepository { get; } = new MobResistanceRepository();
    public static TrackingService CreateService()
    {
        var paths = SettingsComposition.Paths;
        return new(new SpellDurationRepository(), new SpawnRepository(), new MoteRepository(),
            MobRepository, new NpcCompendiumRepository(), new TrackingResetRepository(),
            new(paths.Folder, paths.Durations, paths.Spawns, paths.Motes,
                Path.Combine(paths.Folder, "mob-resistances.json"), Path.Combine(paths.Folder, "npc-compendium"),
                Path.Combine(paths.Folder, "npc-compendium.json")));
    }
}
