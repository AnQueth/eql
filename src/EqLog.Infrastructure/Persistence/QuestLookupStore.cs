using System.Text.Json;
using EqLog.Core;

namespace EqLog.Infrastructure.Persistence;

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
        CancellationToken ct = default, Action<string>? reportRecovery = null)
    {
        if (!File.Exists(path) && legacyPath is not null && File.Exists(legacyPath))
        {
            var legacy = await AtomicFile.ReadJsonAsync<List<QuestLookupResult>>(legacyPath, ct: ct).ConfigureAwait(false) ?? [];
            if (legacy.Any(result => !Valid(result)))
            {
                throw new JsonException("Invalid quest lookup in the legacy cache.");
            }
            var newest = legacy
                .Where(Valid)
                .GroupBy(r => QuestLookup.ItemTitle(r.Item), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.MaxBy(r => r.CheckedUtc)!)
                .ToArray();
            await JsonLines.RewriteAsync(path, newest, ct: ct).ConfigureAwait(false);
            File.Delete(legacyPath);
        }

        if (!File.Exists(path))
        {
            return [];
        }
        using var reader = File.OpenText(path);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            QuestLookupResult? result;
            try
            {
                result = JsonSerializer.Deserialize<QuestLookupResult>(line);
            }
            catch (JsonException) when (line.TrimStart().StartsWith('{') && !line.TrimEnd().EndsWith('}'))
            {
                const string message = "An interrupted quest-cache record was skipped; completed cache records are retained.";
                if (reportRecovery is not null)
                {
                    reportRecovery(message);
                }
                else
                {
                    System.Diagnostics.Trace.TraceWarning(message);
                }
                continue;
            }
            if (!Valid(result))
            {
                throw new JsonException("Invalid quest lookup in the cache.");
            }
        }
        reader.Dispose();
        var latest = await JsonLines.LoadLatestAsync<QuestLookupResult>(path,
            result => QuestLookup.ItemTitle(result.Item), StringComparer.OrdinalIgnoreCase, ct: ct).ConfigureAwait(false);
        return latest.Values.ToArray();
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
