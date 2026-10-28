namespace EqLog.Core.Tracking;

/// <summary>A store whose state is guarded by its owner's lock and saved from a detached copy.</summary>
internal interface ISnapshotStore<TSnapshot> where TSnapshot : class
{
    /// <summary>Serializes saves of this store so an older snapshot can never replace a newer file.</summary>
    SemaphoreSlim SaveGate
    {
        get;
    }

    /// <summary>
    /// Copies the current state into objects nothing else will mutate (sharing immutable values), and marks it clean;
    /// returns null when there is nothing to save. The copy is streamed to disk outside the lock.
    /// </summary>
    TSnapshot? TakeSnapshot(bool onlyIfDirty);

    void MarkDirty();
}

internal static class SnapshotStore
{
    /// <summary>
    /// Snapshots the store under <paramref name="stateGate"/> (null when the caller guarantees no concurrent changes),
    /// then streams it as JSON outside that lock. A failed write marks the store dirty again.
    /// </summary>
    public static async Task SaveAsync<TSnapshot>(ISnapshotStore<TSnapshot> store, object? stateGate, string path, bool onlyIfDirty, CancellationToken ct)
        where TSnapshot : class
    {
        await store.SaveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            TSnapshot? snapshot;
            if (stateGate is null)
            {
                snapshot = store.TakeSnapshot(onlyIfDirty);
            }
            else
            {
                lock (stateGate)
                {
                    snapshot = store.TakeSnapshot(onlyIfDirty);
                }
            }
            if (snapshot is null)
            {
                return;
            }
            try
            {
                await AtomicFile.WriteJsonAsync(path, snapshot, ct: ct).ConfigureAwait(false);
            }
            catch
            {
                if (stateGate is null)
                {
                    store.MarkDirty();
                }
                else
                {
                    lock (stateGate)
                    {
                        store.MarkDirty();
                    }
                }
                throw;
            }
        }
        finally
        {
            store.SaveGate.Release();
        }
    }
}