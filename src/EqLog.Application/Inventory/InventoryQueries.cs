using EqLog.Core;

namespace EqLog.Application.Inventory;

/// <summary>Pure read-model queries; filtering and selecting never access adapters.</summary>
public sealed record InventoryMergeSummary(string BaseName, IReadOnlyList<InventoryMergeItem> Items, int Total);

public static class InventoryQueries
{
    public static IReadOnlyList<InventoryMergeSummary> MergeGroups(IReadOnlyList<InventoryExportItem> items)
    {
        return InventoryExport.MergeGroups(items).Select(group => new InventoryMergeSummary(group.BaseName, group.Items, group.Total)).ToArray();
    }

    public static IReadOnlyList<string> UnknownLocations(IReadOnlyList<InventoryExportItem> items)
    {
        return InventoryExport.UnknownLocations(items);
    }

    public static IReadOnlyList<ExaltationLocation> ExaltationLocations(IReadOnlyList<InventoryExportItem> items)
    {
        return Exaltations.Locate(items).Concat(Exaltations.WikiDefaults(items)).ToArray();
    }

    public static bool MatchesExaltation(ExaltationLocation location, ItemEffectInfo? effects, string filter)
    {
        return Exaltations.Matches(location, effects, filter);
    }

    /// <summary>Finds every matching rank while retaining container names from the complete export.</summary>
    public static IReadOnlyList<InventoryItemLocation> CopyLocations(IReadOnlyList<InventoryExportItem> items, string item)
    {
        var locations = InventoryExport.DescribeAll(items);
        return items.Select((row, index) => (Row: row, Location: locations[index]))
            .Where(copy => ItemNames.Comparer.Equals(copy.Row.Name, item))
            .Select(copy => new InventoryItemLocation(copy.Row.Name, InventoryExport.SplitRank(copy.Row.Name).Rank,
                copy.Row.Count, copy.Location, copy.Row.Installed ? "Installed" : copy.Row.Collection ? "Collection" : "Loose / worn"))
            .OrderByDescending(copy => copy.Rank)
            .ThenBy(copy => copy.Location, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
