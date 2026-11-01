using EqLog.Core;

namespace EqLog.Application.Inventory;

/// <summary>Pure read-model queries; filtering and selecting never access adapters.</summary>
public static class InventoryQueries
{
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
