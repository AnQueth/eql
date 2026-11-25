using System.Text.Json;
using System.Text.Json.Nodes;

namespace EqLog.Infrastructure.Persistence;

/// <summary>Persists object-property changes as idempotent batches over an atomic JSON baseline.</summary>
internal sealed class JsonDeltaJournal(string path, JsonObject saved, JsonSerializerOptions options,
    Action<JsonObject>? validateSaved = null)
{
    private const long CheckpointThreshold = 1024 * 1024;
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
        string path, T empty, JsonSerializerOptions options, Action<T> validate, CancellationToken cancellationToken,
        Action<JsonObject, Batch>? validateChanges = null, Action<T>? validateCheckpoint = null)
        where T : class
    {
        string checkpoint = path + ".checkpoint";
        if (File.Exists(checkpoint))
        {
            var pending = await AtomicFile.ReadJsonAsync<T>(checkpoint, options, cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException("Persistence checkpoint is empty.");
            (validateCheckpoint ?? validate)(pending);
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
        await foreach (var batch in PersistenceJournal.ReadAsync<Batch>(journalPath, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Apply(current, batch);
            if (validateChanges is null)
            {
                data = current.Deserialize<T>(options) ?? throw new JsonException("Persistence journal produced empty data.");
                validate(data);
            }
            else
            {
                validateChanges(current, batch);
            }
        }
        if (validateChanges is not null)
        {
            data = current.Deserialize<T>(options) ?? throw new JsonException("Persistence journal produced empty data.");
            validate(data);
        }
        var journal = new JsonDeltaJournal(path, current, options, document =>
        {
            var checkpointData = document.Deserialize<T>(options)
                ?? throw new JsonException("Persistence checkpoint is empty.");
            (validateCheckpoint ?? validate)(checkpointData);
        });
        await journal.CheckpointIfNeededAsync(cancellationToken).ConfigureAwait(false);
        return (journal, data);
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
            await CheckpointIfNeededAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        await PersistenceJournal.AppendAsync(JournalPath(path), new Batch(changes), cancellationToken).ConfigureAwait(false);
        _saved = current;
        await CheckpointIfNeededAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task CheckpointAsync(CancellationToken cancellationToken)
    {
        JsonObject checkpoint;
        lock (_saved)
        {
            validateSaved?.Invoke(_saved);
            checkpoint = (JsonObject)_saved.DeepClone();
        }
        await AtomicFile.WriteJsonAsync(path + ".checkpoint", checkpoint, options, cancellationToken).ConfigureAwait(false);
        await FinishCheckpointAsync(path, options, cancellationToken).ConfigureAwait(false);
    }

    private Task CheckpointIfNeededAsync(CancellationToken cancellationToken)
    {
        string journalPath = JournalPath(path);
        return File.Exists(journalPath) && new FileInfo(journalPath).Length >= CheckpointThreshold
            ? CheckpointAsync(cancellationToken) : Task.CompletedTask;
    }

    internal async Task SavePartialAsync<T>(T data, IReadOnlyCollection<string> entryProperties,
        IReadOnlyList<Change> removals, CancellationToken cancellationToken)
    {
        if (File.Exists(path + ".checkpoint"))
        {
            await FinishCheckpointAsync(path, options, cancellationToken).ConfigureAwait(false);
        }
        var updates = JsonSerializer.SerializeToNode(data, options) as JsonObject
            ?? throw new JsonException("Persistence snapshot must be an object.");
        var changes = new List<Change>();
        lock (_saved)
        {
            foreach (var (property, value) in updates)
            {
                if (entryProperties.Contains(property) && _saved[property] is JsonObject previous && value is JsonObject entries)
                {
                    foreach (var (key, entry) in entries)
                    {
                        if (!previous.TryGetPropertyValue(key, out var oldEntry) || !JsonNode.DeepEquals(oldEntry, entry))
                        {
                            changes.Add(new Change([property, key], false, entry?.DeepClone()));
                        }
                    }
                }
                else if (!_saved.TryGetPropertyValue(property, out var oldValue) || !JsonNode.DeepEquals(oldValue, value))
                {
                    changes.Add(new Change([property], false, value?.DeepClone()));
                }
            }
            foreach (var removal in removals)
            {
                if (removal.Path.Length != 2 || !removal.Remove || removal.Value is not null)
                {
                    throw new ArgumentException("Partial snapshots require entry removals.", nameof(removals));
                }
                if (_saved[removal.Path[0]] is JsonObject entries && entries.ContainsKey(removal.Path[1]))
                {
                    changes.Add(removal);
                }
            }
        }
        if (changes.Count == 0 && (File.Exists(path) || File.Exists(JournalPath(path))))
        {
            await CheckpointIfNeededAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        var batch = new Batch(changes);
        if (!File.Exists(path) && !File.Exists(JournalPath(path)))
        {
            JsonObject initial;
            lock (_saved)
            {
                initial = (JsonObject)_saved.DeepClone();
            }
            Apply(initial, batch);
            await AtomicFile.WriteJsonAsync(path, initial, options, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await PersistenceJournal.AppendAsync(JournalPath(path), batch, cancellationToken).ConfigureAwait(false);
        }
        // Keep the lazy history document alive and update it only after the write succeeds.
        Apply(_saved, batch);
        await CheckpointIfNeededAsync(cancellationToken).ConfigureAwait(false);
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
        lock (root)
        {
            ApplyCore(root, batch);
        }
    }

    private static void ApplyCore(JsonObject root, Batch batch)
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
