using EqLog.Core;
using EqLog.Core.Parsing;

namespace EqLog.Overlay.ViewModels;

/// <summary>A registered wiki request that can be cancelled and awaited during cache reset.</summary>
public interface IOverlayWikiLookupLease : IDisposable
{
    /// <summary>The request cancellation token.</summary>
    CancellationToken Token { get; }

    /// <summary>Completes when the request lease is disposed.</summary>
    Task Completion { get; }

    /// <summary>Cancels the request.</summary>
    void Cancel();
}

/// <summary>Wiki/cache services used by the tradeskills presentation.</summary>
public interface ITradeskillsOverlayServices
{
    /// <summary>Tradeskill pages loaded from the local cache at startup.</summary>
    IReadOnlyList<TradeskillWikiPage> InitialPages { get; }

    /// <summary>A startup cache-load error, if one occurred.</summary>
    Exception? InitialError { get; }

    /// <summary>Whether wiki lookup is paused for cache reset or shutdown.</summary>
    bool WikiLookupPaused { get; }

    /// <summary>Starts a lookup registered with the shell's cache-reset workflow.</summary>
    IOverlayWikiLookupLease BeginLookup();

    /// <summary>Loads one skill, recipe, or ingredient merchant page.</summary>
    Task<TradeskillWikiPage> GetTradeskillAsync(TradeskillPageKind kind, string title, bool force, CancellationToken token);

    /// <summary>Returns a cache-write error for the specified operation, when present.</summary>
    string? GetCacheError(string operation);

    /// <summary>Reports a wiki lookup failure to application telemetry.</summary>
    void TrackWikiError(Exception error);
}

/// <summary>Inventory and wiki services used by the exaltations presentation.</summary>
public interface IExaltationsOverlayServices
{
    /// <summary>Item-effect data loaded from the local cache at startup.</summary>
    IReadOnlyList<ItemEffectInfo> InitialEffects { get; }

    /// <summary>A startup effect-cache load error, if one occurred.</summary>
    Exception? InitialError { get; }

    /// <summary>The current character name, if known.</summary>
    string? CharacterName { get; }

    /// <summary>The exaltations from the latest inventory export for the current character.</summary>
    IReadOnlyList<ExaltationLocation> ExaltationLocations { get; }

    /// <summary>Whether the current character has a nonempty matching inventory export.</summary>
    bool HasCurrentInventory { get; }

    /// <summary>The write time of the inventory export, if available.</summary>
    DateTime? InventoryWritten { get; }

    /// <summary>A revision that changes when inventory/session state changes.</summary>
    long InventoryRevision { get; }

    /// <summary>Whether wiki lookup is paused for cache reset or shutdown.</summary>
    bool WikiLookupPaused { get; }

    /// <summary>Starts a lookup registered with the shell's cache-reset workflow.</summary>
    IOverlayWikiLookupLease BeginLookup();

    /// <summary>Loads base-item effects and descriptions from the shared wiki cache.</summary>
    Task<ItemEffectInfo> GetItemEffectsAsync(string item, WikiPriority priority, bool force, CancellationToken token);

    /// <summary>Returns a cache-write error for the specified operation, when present.</summary>
    string? GetCacheError(string operation);

    /// <summary>Reports a wiki lookup failure to application telemetry.</summary>
    void TrackWikiError(Exception error);
}
