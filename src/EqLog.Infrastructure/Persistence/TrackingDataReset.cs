using System.Text.Json;

namespace EqLog.Infrastructure.Persistence;

/// <summary>Schedules a backed-up tracking reset to run before stores load on the next launch.</summary>
public static class TrackingDataReset
{
    private const string Marker = "tracking-reset.json";
    private const string LogCutoff = "tracking-log-cutoff.json";
    private static readonly string[] Files =
    [
        "spell-durations.json", "spawn-timers.json", "motes.json", "mob-resistances.json", "mob-resistances.json.journal",
        "mob-resistances.json.checkpoint",
        "quest-inventory.json", "npc-compendium.json", "npc-compendium.json.migrated",
        "leveling-history.json", "leveling-history.jsonl",
    ];

    /// <summary>Persists a reset request in <paramref name="folder"/> without changing loaded stores.</summary>
    /// <param name="ignoreLogLinesBefore">
    /// When set, tracking ignores log lines stamped before this local time after the reset, so existing logs
    /// are not reprocessed. When null, any earlier cutoff is removed and logs are rebuilt from their start.
    /// </param>
    public static Task RequestAsync(string folder, DateTime? ignoreLogLinesBefore = null)
    {
        return AtomicFile.WriteJsonAsync(Path.Combine(folder, Marker),
            new ResetRequest(Guid.NewGuid().ToString("N"), ignoreLogLinesBefore));
    }

    /// <summary>The local time before which log lines are ignored for tracking, or null to process whole logs.</summary>
    public static async Task<DateTime?> LoadLogCutoffAsync(string folder, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(folder, LogCutoff);
        if (!File.Exists(path))
        {
            return null;
        }
        return await AtomicFile.ReadJsonAsync<DateTime?>(path, ct: cancellationToken).ConfigureAwait(false);
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
        var request = await ReadRequestAsync(marker).ConfigureAwait(false);
        if (!Guid.TryParseExact(request?.Id, "N", out var resetId))
        {
            throw new IOException("The tracking reset request is invalid; no data was moved.");
        }
        string backup = Path.Combine(folder, "tracking-backups", resetId.ToString("N"));
        Directory.CreateDirectory(backup);
        // Interrupted atomic writes leave ".tmp" copies that still hold tracked data.
        foreach (string name in Files.SelectMany(file => new[] { file, file + ".tmp" }))
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
        // Handled outside the move list so a resumed reset never collides with a cutoff it already backed up.
        string cutoff = Path.Combine(folder, LogCutoff);
        if (request!.IgnoreLogLinesBefore is DateTime ignoreBefore)
        {
            await AtomicFile.WriteJsonAsync(cutoff, ignoreBefore).ConfigureAwait(false);
        }
        else if (File.Exists(cutoff))
        {
            File.Move(cutoff, Path.Combine(backup, LogCutoff), overwrite: true);
        }
        File.Delete(marker);
        return backup;
    }

    private static async Task<ResetRequest?> ReadRequestAsync(string marker)
    {
        try
        {
            var json = await AtomicFile.ReadJsonAsync<JsonElement>(marker).ConfigureAwait(false);
            // Requests from older versions are a bare id and always rebuild from the start of the logs.
            if (json.ValueKind == JsonValueKind.String)
            {
                return new(json.GetString(), null);
            }
            return json.Deserialize<ResetRequest>(AtomicFile.Json);
        }
        catch (JsonException ex)
        {
            throw new IOException("The tracking reset request is invalid; no data was moved.", ex);
        }
    }

    private sealed record ResetRequest(string? Id, DateTime? IgnoreLogLinesBefore);
}
