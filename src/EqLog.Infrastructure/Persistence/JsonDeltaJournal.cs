using System.Text.Json;
using System.Text.Json.Nodes;

namespace EqLog.Infrastructure.Persistence;

/// <summary>Persists object-property changes as idempotent batches over an atomic JSON baseline.</summary>
internal sealed class JsonDeltaJournal(string path, JsonObject saved, JsonSerializerOptions options)
{
    private JsonObject _saved = saved;

    internal sealed record Change(string[] Path, bool Remove, JsonNode? Value);
    internal sealed record Batch(List<Change> Changes);

    internal static string JournalPath(string path)
    {
        return path + ".journal";
    }

    internal bool IsForPath(string otherPath)
    {
        return string.Equals(Path.GetFullPath(path), Path.GetFullPath(otherPath), StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<(JsonDeltaJournal Journal, T Data)> OpenAsync<T>(
        string path, T empty, JsonSerializerOptions options, Action<T> validate, CancellationToken cancellationToken)
        where T : class
    {
        string checkpoint = path + ".checkpoint";
        if (File.Exists(checkpoint))
        {
            var pending = await AtomicFile.ReadJsonAsync<T>(checkpoint, options, cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException("Persistence checkpoint is empty.");
            validate(pending);
            await FinishCheckpointAsync(path, options, cancellationToken).ConfigureAwait(false);
        }
        T data = empty;
        if (File.Exists(path))
        {
            data = await AtomicFile.ReadJsonAsync<T>(path, options, cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException("Persistence baseline is empty.");
        }
        validate(data);
        var current = JsonSerializer.SerializeToNode(data, options) as JsonObject
            ?? throw new JsonException("Persistence baseline must be an object.");
        string journalPath = JournalPath(path);
        var batches = await PersistenceJournal.ReadAsync<Batch>(journalPath, cancellationToken).ConfigureAwait(false);
        foreach (var batch in batches)
        {
            Apply(current, batch);
            data = current.Deserialize<T>(options) ?? throw new JsonException("Persistence journal produced empty data.");
            validate(data);
        }
        if (File.Exists(journalPath) && new FileInfo(journalPath).Length >= 1024 * 1024)
        {
            var journal = new JsonDeltaJournal(path, current, options);
            await journal.CheckpointAsync(cancellationToken).ConfigureAwait(false);
        }
        return (new JsonDeltaJournal(path, current, options), data);
    }

    internal async Task SaveAsync<T>(T data, CancellationToken cancellationToken)
    {
        if (File.Exists(path + ".checkpoint"))
        {
            await FinishCheckpointAsync(path, options, cancellationToken).ConfigureAwait(false);
        }
        var current = JsonSerializer.SerializeToNode(data, options) as JsonObject
            ?? throw new JsonException("Persistence snapshot must be an object.");
        if (!File.Exists(path) && !File.Exists(JournalPath(path)))
        {
            await AtomicFile.WriteJsonAsync(path, current, options, cancellationToken).ConfigureAwait(false);
            _saved = current;
            return;
        }
        var changes = new List<Change>();
        Compare(_saved, current, [], changes);
        if (changes.Count == 0)
        {
            return;
        }
        await PersistenceJournal.AppendAsync(JournalPath(path), new Batch(changes), cancellationToken).ConfigureAwait(false);
        _saved = current;
    }

    internal async Task CheckpointAsync(CancellationToken cancellationToken)
    {
        await AtomicFile.WriteJsonAsync(path + ".checkpoint", _saved, options, cancellationToken).ConfigureAwait(false);
        await FinishCheckpointAsync(path, options, cancellationToken).ConfigureAwait(false);
    }

    private static async Task FinishCheckpointAsync(
        string path, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        string checkpoint = path + ".checkpoint";
        var saved = await AtomicFile.ReadJsonAsync<JsonObject>(checkpoint, options, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("Persistence checkpoint is empty.");
        await AtomicFile.WriteJsonAsync(path, saved, options, cancellationToken).ConfigureAwait(false);
        File.Delete(JournalPath(path));
        File.Delete(checkpoint);
    }

    private static void Compare(JsonObject previous, JsonObject current, string[] path, List<Change> changes)
    {
        foreach (var (key, oldValue) in previous)
        {
            if (!current.ContainsKey(key))
            {
                changes.Add(new Change([.. path, key], true, null));
            }
        }
        foreach (var (key, value) in current)
        {
            string[] childPath = [.. path, key];
            if (!previous.TryGetPropertyValue(key, out var oldValue))
            {
                changes.Add(new Change(childPath, false, value?.DeepClone()));
            }
            else if (oldValue is JsonObject oldObject && value is JsonObject newObject)
            {
                Compare(oldObject, newObject, childPath, changes);
            }
            else if (!JsonNode.DeepEquals(oldValue, value))
            {
                changes.Add(new Change(childPath, false, value?.DeepClone()));
            }
        }
    }

    private static void Apply(JsonObject root, Batch batch)
    {
        if (batch.Changes is null)
        {
            throw new JsonException("Persistence journal batch has no changes.");
        }
        foreach (var change in batch.Changes)
        {
            if (change?.Path is null || change.Path.Length == 0 ||
                change.Path.Any(part => part is null) || (change.Remove && change.Value is not null))
            {
                throw new JsonException("Persistence journal contains an invalid change.");
            }
            JsonObject target = root;
            foreach (string part in change.Path[..^1])
            {
                if (target[part] is not JsonObject nested)
                {
                    throw new JsonException("Persistence journal references a missing object.");
                }
                target = nested;
            }
            string key = change.Path[^1];
            if (change.Remove)
            {
                target.Remove(key);
            }
            else
            {
                target[key] = change.Value?.DeepClone();
            }
        }
    }
}
