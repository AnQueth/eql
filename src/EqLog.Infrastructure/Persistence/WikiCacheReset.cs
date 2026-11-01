using EqLog.Core;
using EqLog.Application.Tracking;

namespace EqLog.Infrastructure.Persistence;

/// <summary>Clears persisted wiki lookups while preserving tracking stores and settings.</summary>
public static class WikiCacheReset
{
    /// <summary>Resets NPC/spell metadata and removes standalone wiki caches, including legacy formats.</summary>
    /// <param name="folder">The application's data folder.</param>
    /// <param name="mobs">The loaded resistance store; callers must first drain wiki writers.</param>
    /// <returns>A task completing when all cache files have been reset.</returns>
    public static async Task ResetAsync(string folder, MobResistanceStore mobs, IMobResistanceRepository? repository = null)
    {
        ArgumentNullException.ThrowIfNull(mobs);
        await (repository ?? new MobResistanceRepository()).ResetWikiCachesAsync(Path.Combine(folder, "mob-resistances.json"), mobs).ConfigureAwait(false);
        foreach (string name in new[]
        {
            "quest-lookups.jsonl", "quest-lookups.json", "sky-quests.json", "item-effects.json",
        })
        {
            File.Delete(Path.Combine(folder, name));
        }
    }
}
