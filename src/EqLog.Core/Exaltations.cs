namespace EqLog.Core;

/// <summary>One exaltation occurrence in the latest export, with its host and physical location.</summary>
/// <param name="Name">The exported exaltation name.</param>
/// <param name="BaseItem">The original item used for wiki lookups.</param>
/// <param name="Count">Quantity at this location.</param>
/// <param name="Installed">Whether this copy is socketed.</param>
/// <param name="Host">The host's full name, including upgrade rank, or null if loose or unresolved.</param>
/// <param name="Where">Readable location of the host or loose exaltation.</param>
/// <param name="Socket">Socket number for installed copies.</param>
/// <param name="InExaltStorage">Whether the export lists this copy in Augmentation storage.</param>
/// <param name="WikiDefault">Whether this is an unverified default inferred from a stored original item.</param>
public sealed record ExaltationLocation(string Name, string BaseItem, int Count, bool Installed,
    string? Host, string Where, string? Socket, bool InExaltStorage = false, bool WikiDefault = false);

/// <summary>Maps exported exaltations to hosts without merging duplicate worn slot names.</summary>
public static class Exaltations
{
    private const string Suffix = " (Exaltation)";

    /// <summary>Lists installed, loose, and Augmentation-storage exaltations, excluding ordinary stored equipment.</summary>
    /// <param name="items">All rows from one character's inventory export, in export order.</param>
    /// <returns>Physical exaltations and their exact host locations.</returns>
    public static IReadOnlyList<ExaltationLocation> Locate(IReadOnlyList<InventoryExportItem> items)
    {
        var descriptions = InventoryExport.DescribeAll(items);
        var result = new List<ExaltationLocation>();
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            bool exaltStorage = item.Collection &&
                item.Location.Equals("Augmentation", StringComparison.OrdinalIgnoreCase);
            if ((item.Collection && !exaltStorage) || !item.Name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int hostIndex = -1;
            int socketStart = item.Location.LastIndexOf("-Slot", StringComparison.OrdinalIgnoreCase);
            if (item.Installed && socketStart >= 0)
            {
                string hostLocation = item.Location[..socketStart];
                for (int j = i - 1; j >= 0; j--)
                {
                    if (!items[j].Collection && items[j].WornSlot == item.WornSlot &&
                        items[j].Location.Equals(hostLocation, StringComparison.OrdinalIgnoreCase))
                    {
                        hostIndex = j;
                        break;
                    }
                }
            }

            result.Add(new(item.Name, ItemNames.Normalize(item.Name[..^Suffix.Length]), item.Count,
                item.Installed, hostIndex >= 0 ? items[hostIndex].Name : null,
                exaltStorage ? "Exalt storage (Augmentation)" :
                    hostIndex >= 0 ? descriptions[hostIndex] : descriptions[i],
                item.Installed && socketStart >= 0 ? item.Location[(socketStart + 5)..] : null, exaltStorage));
        }
        return result.OrderBy(row => row.BaseItem, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Where, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Lists original equipment-storage items whose default effects can be researched, not confirmed augments.</summary>
    /// <param name="items">Inventory export rows in their original order.</param>
    /// <returns>Unverified candidates, with storage entry numbers and original item names.</returns>
    public static IReadOnlyList<ExaltationLocation> WikiDefaults(IReadOnlyList<InventoryExportItem> items)
    {
        var result = new List<ExaltationLocation>();
        int entry = 0;
        foreach (var item in items)
        {
            if (!item.Collection || !item.Location.Equals("Equipment", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            entry++;
            if (item.Name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string original = ItemNames.Normalize(item.Name);
            result.Add(new(original + Suffix, original, item.Count, false, item.Name,
                $"Equipment storage, export entry {entry} (in-game slot unverified)", null, WikiDefault: true));
        }
        return result;
    }

    /// <summary>Matches every search word against effects, names, hosts, and locations.</summary>
    /// <param name="location">The physical exaltation to search.</param>
    /// <param name="effects">Cached effect information, when available.</param>
    /// <param name="search">Space-separated, case-insensitive search terms.</param>
    /// <returns>True when all terms match the combined searchable information.</returns>
    public static bool Matches(ExaltationLocation location, ItemEffectInfo? effects, string search)
    {
        string text = $"{location.Name} {location.Host} {location.Where} " +
            (location.WikiDefault ? "Expected wiki default unverified equipment storage " :
                location.Installed ? "Installed " : location.InExaltStorage ? "Exalt storage not installed " : "Loose not installed ") +
            string.Join(" ", effects?.Effects.Select(effect => $"{effect.Kind} {effect.Name} {effect.Description}") ?? []) +
            $" {effects?.SearchTerms}";
        return search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}
