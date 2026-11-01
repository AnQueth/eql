using System.Collections.Concurrent;
using System.Diagnostics;
using EqLog.Core;

namespace EqLog.Application.Inventory;

/// <summary>
/// Owns inventory mutations, stable import policy and ordered saves. No callbacks execute under its state lock.
/// Application lifetime; callers cancel/await outstanding operations before abandoning the instance.
/// </summary>
public sealed class InventoryService
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly ConcurrentQueue<InventoryLootObservation> _loot = new();
    private readonly IInventoryExportSource _source;
    private readonly IQuestInventoryRepository _repository;
    private readonly QuestInventory _state;
    private readonly Exception? _loadError;
    private string? _log;
    private string? _character;
    private long _session;
    private long _revision;
    private long _stateVersion;
    private long _savedVersion;
    private InventoryFileStamp? _observed;
    private InventoryExportSnapshot? _export;
    private bool _polling;

    /// <summary>Adopts a clone of loaded state; a load failure permanently prevents overwriting that document.</summary>
    public InventoryService(IInventoryExportSource source, IQuestInventoryRepository repository,
        QuestInventory initialState, Exception? loadError = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(initialState);
        _source = source;
        _repository = repository;
        _state = initialState.Clone();
        _loadError = loadError;
    }

    /// <summary>Changes whenever query state changes; allows views to avoid cloning unchanged inventories.</summary>
    public long Revision
    {
        get
        {
            lock (_gate)
            {
                return _revision;
            }
        }
    }

    /// <summary>Invalidates in-flight imports, even when switching between logs for the same character.</summary>
    public void SelectSession(string log, string character)
    {
        lock (_gate)
        {
            if (_log == log && _character == character)
            {
                return;
            }
            _log = log;
            _character = character;
            _session++;
            _observed = null;
            _export = null;
            _revision++;
        }
    }

    /// <summary>Enqueues immutable observations from any thread without mutating the current snapshot.</summary>
    public void ObserveLoot(InventoryLootObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        _loot.Enqueue(observation);
    }

    /// <summary>Applies queued loot in budgeted slices, checking time between observations; returns whether anything changed.</summary>
    public bool ProcessPending(TimeSpan budget)
    {
        if (budget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), "The processing budget must be positive.");
        }
        bool changed = false;
        lock (_gate)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < budget && _loot.TryDequeue(out var note))
            {
                _state.NoteLoot(note.Character, note.Source, note.Line, note.Item, note.Quantity, note.Live, note.TimeUtc);
                changed = true;
            }
            if (changed)
            {
                MarkChanged();
            }
        }
        return changed;
    }

    /// <summary>Returns detached rule/query state while retaining immutable export identity between revisions.</summary>
    public InventorySnapshot Snapshot()
    {
        lock (_gate)
        {
            return new(_revision, _state.Clone(), _export);
        }
    }

    /// <summary>Adds a lookup-only item without claiming ownership.</summary>
    public void TrackItem(string character, string item)
    {
        lock (_gate)
        {
            if (_state.Items.Any(entry => entry.Character.Equals(character, StringComparison.OrdinalIgnoreCase) &&
                ItemNames.Comparer.Equals(entry.Item, item)))
            {
                return;
            }
            _state.Items.Add(new QuestInventoryItem { Character = character, Item = item });
            MarkChanged();
        }
    }

    /// <summary>Toggles a character-specific quest completion and returns the new value.</summary>
    public bool ToggleCompletion(string character, string quest)
    {
        lock (_gate)
        {
            string key = QuestInventory.CompletionKey(character, quest);
            bool completed = _state.Completed.Add(key);
            if (!completed)
            {
                _state.Completed.Remove(key);
            }
            MarkChanged();
            return completed;
        }
    }

    /// <summary>
    /// Requires matching metadata on successive polls and before committing. Failures retain last-good state.
    /// Cancellation propagates; superseded sessions never publish their export.
    /// </summary>
    public async Task<InventoryImportResult> PollAsync(CancellationToken cancellationToken = default)
    {
        string log;
        string character;
        long session;
        lock (_gate)
        {
            if (_polling || _log is null || _character is null)
            {
                return new(InventoryImportStatus.Superseded);
            }
            _polling = true;
            log = _log;
            character = _character;
            session = _session;
        }
        try
        {
            var stamp = await _source.InspectAsync(log, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (session != _session)
                {
                    return new(InventoryImportStatus.Superseded);
                }
                if (stamp is null)
                {
                    _observed = null;
                    if (_export is not null)
                    {
                        _export = null;
                        _revision++;
                    }
                    return new(InventoryImportStatus.Missing);
                }
                if (_export?.Stamp == stamp)
                {
                    return new(InventoryImportStatus.Unchanged, _export);
                }
                if (_observed != stamp)
                {
                    _observed = stamp;
                    return new(InventoryImportStatus.Stabilizing);
                }
            }
            var rows = await _source.ReadAsync(stamp, cancellationToken).ConfigureAwait(false);
            if (rows is null)
            {
                return new(InventoryImportStatus.Stabilizing);
            }

            // Reconciliation drains on a worker, not on a WPF dispatcher; recheck the file after draining.
            await Task.Run(DrainPending, cancellationToken).ConfigureAwait(false);
            var current = await _source.InspectAsync(log, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (session != _session)
                {
                    return new(InventoryImportStatus.Superseded);
                }
                if (current != stamp)
                {
                    return new(InventoryImportStatus.Stabilizing);
                }
                var immutableRows = Array.AsReadOnly(rows.ToArray());
                _state.ApplyExport(character, immutableRows, stamp.Written);
                _export = new(character, stamp, immutableRows);
                MarkChanged();
                return new(InventoryImportStatus.Imported, _export);
            }
        }
        finally
        {
            lock (_gate)
            {
                _polling = false;
            }
        }
    }

    /// <summary>
    /// Captures and writes on workers in gate order. Failed/canceled writes remain dirty;
    /// mutations during a write belong to the next save, so an older snapshot cannot win.
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (_loadError is not null)
        {
            throw new InvalidOperationException("Quest inventory was not loaded successfully; saving is disabled to preserve the existing file.", _loadError);
        }
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var captured = await Task.Run(() =>
            {
                DrainPending();
                lock (_gate)
                {
                    return _stateVersion == _savedVersion
                        ? (State: (QuestInventory?)null, Version: _stateVersion)
                        : (State: (QuestInventory?)_state.Clone(), Version: _stateVersion);
                }
            }, cancellationToken).ConfigureAwait(false);
            if (captured.State is null)
            {
                return;
            }
            await _repository.SaveAsync(captured.State, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _savedVersion = captured.Version;
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void DrainPending()
    {
        while (!_loot.IsEmpty)
        {
            ProcessPending(TimeSpan.FromMilliseconds(5));
            Thread.Yield();
        }
    }

    private void MarkChanged()
    {
        _stateVersion++;
        _revision++;
    }
}
