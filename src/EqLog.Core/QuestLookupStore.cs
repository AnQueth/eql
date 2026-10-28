namespace EqLog.Core;

/// <summary>
/// Persists wiki quest lookups as JSON Lines, one result per line, so each lookup appends a line
/// instead of rewriting every cached result.
/// </summary>
public static class QuestLookupStore
{
    /// <summary>
    /// Loads the newest result per item. A legacy single-array JSON file is migrated into <paramref name="path"/>
    /// on first load and then deleted.
    /// </summary>
    public static async Task<IReadOnlyList<QuestLookupResult>> LoadAsync(string path, string? legacyPath = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(path) && legacyPath is not null && File.Exists(legacyPath))
        {
            var legacy = await AtomicFile.ReadJsonAsync<List<QuestLookupResult>>(legacyPath, ct: ct).ConfigureAwait(false) ?? [];
            var newest = legacy
                .Where(Valid)
                .GroupBy(r => QuestLookup.ItemTitle(r.Item), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.MaxBy(r => r.CheckedUtc)!)
                .ToArray();
            await JsonLines.RewriteAsync(path, newest, ct: ct).ConfigureAwait(false);
            try
            {
                File.Delete(legacyPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The JSONL file now exists and takes precedence; the old file is only left behind.
            }
        }

        var latest = await JsonLines.LoadLatestAsync<QuestLookupResult>(path, r => QuestLookup.ItemTitle(r.Item ?? ""),
            StringComparer.OrdinalIgnoreCase, ct: ct).ConfigureAwait(false);
        return latest.Values.Where(Valid).ToArray();
    }

    /// <summary>Appends one lookup result; on load it replaces any earlier result for the same item.</summary>
    public static Task AppendAsync(string path, QuestLookupResult result, CancellationToken ct = default)
    {
        return JsonLines.AppendAsync(path, [result], ct: ct);
    }

    private static bool Valid(QuestLookupResult? result)
    {
        return result is not null && !string.IsNullOrWhiteSpace(result.Item) && result.Quests is not null &&
            result.Quests.All(q => q is not null);
    }
}
