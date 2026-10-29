namespace EqLog.Core;

/// <summary>Schedules a backed-up tracking reset to run before stores load on the next launch.</summary>
public static class TrackingDataReset
{
    private const string Marker = "tracking-reset.json";
    private static readonly string[] Files =
    [
        "spell-durations.json", "spawn-timers.json", "motes.json", "mob-resistances.json",
        "quest-inventory.json", "npc-compendium.json",
    ];

    /// <summary>Persists a reset request in <paramref name="folder"/> without changing loaded stores.</summary>
    public static Task RequestAsync(string folder)
    {
        return AtomicFile.WriteJsonAsync(Path.Combine(folder, Marker), Guid.NewGuid().ToString("N"));
    }

    /// <summary>Moves tracking stores into a backup, preserving settings, logs, and lookup caches.</summary>
    /// <returns>The backup path, or null when no reset was requested.</returns>
    public static async Task<string?> ApplyPendingAsync(string folder)
    {
        string marker = Path.Combine(folder, Marker);
        if (!File.Exists(marker))
        {
            return null;
        }
        string? id = await AtomicFile.ReadJsonAsync<string>(marker).ConfigureAwait(false);
        if (!Guid.TryParseExact(id, "N", out var resetId))
        {
            throw new IOException("The tracking reset request is invalid; no data was moved.");
        }
        string backup = Path.Combine(folder, "tracking-backups", resetId.ToString("N"));
        Directory.CreateDirectory(backup);
        foreach (string name in Files)
        {
            string source = Path.Combine(folder, name);
            if (File.Exists(source))
            {
                File.Move(source, Path.Combine(backup, name));
            }
        }
        string history = Path.Combine(folder, "npc-compendium");
        if (Directory.Exists(history))
        {
            Directory.Move(history, Path.Combine(backup, "npc-compendium"));
        }
        File.Delete(marker);
        return backup;
    }
}
