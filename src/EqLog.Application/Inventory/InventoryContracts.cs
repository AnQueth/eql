using EqLog.Core;

namespace EqLog.Application.Inventory;

/// <summary>Identity of one version of a game-owned export; times are UTC.</summary>
public sealed record InventoryFileStamp(string Path, long Size, DateTime Written);

/// <summary>Read-only game input. Missing files return null; read/parse failures throw.</summary>
public interface IInventoryExportSource
{
    /// <summary>Resolves and inspects the export belonging to a log without reading its contents.</summary>
    Task<InventoryFileStamp?> InspectAsync(string logPath, CancellationToken cancellationToken);

    /// <summary>Reads only the expected version; null means it changed during the read.</summary>
    Task<IReadOnlyList<InventoryExportItem>?> ReadAsync(InventoryFileStamp expected, CancellationToken cancellationToken);
}

/// <summary>Loads compatible inventory state and atomically saves detached snapshots.</summary>
public interface IQuestInventoryRepository
{
    /// <summary>Returns empty state for a missing document; corrupt documents must throw.</summary>
    Task<QuestInventory> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes a snapshot that the caller will not mutate until completion.</summary>
    Task SaveAsync(QuestInventory snapshot, CancellationToken cancellationToken = default);
}

/// <summary>Typed observation, safe to enqueue from a log worker without touching presentation state.</summary>
public sealed record InventoryLootObservation(
    string Character, string Source, long Line, string Item, int Quantity, bool Live, DateTime? TimeUtc);

/// <summary>One committed, immutable export shared by inventory, merge and upgrade queries.</summary>
public sealed record InventoryExportSnapshot(
    string Character, InventoryFileStamp Stamp, IReadOnlyList<InventoryExportItem> Rows);

/// <summary>A detached domain snapshot and a stable export identity; never the service's live state.</summary>
public sealed record InventorySnapshot(long Revision, QuestInventory State, InventoryExportSnapshot? Export)
{
    /// <summary>Immutable summaries for binding, separate from the detached rule-engine compatibility snapshot.</summary>
    public IReadOnlyList<InventoryItemSummary> Items { get; } = Array.AsReadOnly(State.Items.Select(item =>
        new InventoryItemSummary(item.Character, item.Item, item.Owned, item.Historical, item.Carried, item.Bank,
            item.Hoard, item.Depot, item.OtherStorage, item.Collection, item.Installed, item.LastLootUtc)).ToArray());
}

/// <summary>One item summary; confirmed counts remain separate from collection, installation and recency.</summary>
public sealed record InventoryItemSummary(
    string Character, string Item, int Owned, int Historical, int Carried, int Bank, int Hoard, int Depot,
    int OtherStorage, int Collection, int Installed, DateTime? LastLootUtc);

/// <summary>One exported stack/copy, including collections and installed augments.</summary>
public sealed record InventoryItemLocation(string Item, int Rank, int Quantity, string Location, string Kind);

/// <summary>Import progress; errors propagate to the caller for logging and presentation.</summary>
public enum InventoryImportStatus
{
    Missing,
    Stabilizing,
    Unchanged,
    Superseded,
    Imported,
}

/// <summary>Outcome of a poll, with committed metadata only after a successful import.</summary>
public sealed record InventoryImportResult(InventoryImportStatus Status, InventoryExportSnapshot? Export = null);
