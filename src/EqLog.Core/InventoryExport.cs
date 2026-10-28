using System.Globalization;

namespace EqLog.Core;

/// <summary>Where a loose item lives. Collection and installed entries are flagged separately on the row.</summary>
public enum InventoryStorage
{
    /// <summary>Worn, in general inventory or its bags, or on the cursor (Held).</summary>
    Carried,

    /// <summary>Bank and shared bank slots and their bags.</summary>
    Bank,

    /// <summary>Dragon's Hoard ("Hoard N").</summary>
    Hoard,

    /// <summary>Personal tradeskill depot ("Personal-DepotN").</summary>
    Depot,

    /// <summary>A location this parser doesn't recognise. Still owned, but kept out of the other buckets.</summary>
    Other,
}

public sealed record InventoryExportItem(string Location, string Name, int Id, int Count, InventoryStorage Storage,
    bool Collection = false, bool Installed = false);

public static class InventoryExport
{
    /// <summary>Worn slots (including the "Any Slot" charm slot) and the cursor.</summary>
    private static readonly HashSet<string> CarriedRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "Any Slot", "Charm", "Ear", "Head", "Face", "Neck", "Shoulders", "Arms", "Back", "Wrist", "Range", "Hands",
        "Primary", "Secondary", "Fingers", "Chest", "Legs", "Feet", "Waist", "Power Source", "Ammo", "Held", "Cursor",
    };

    /// <summary>
    /// Storage for a location such as "Bank3-Slot2", "Hoard 12", "Personal-Depot7" or "General 1-Slot4".
    /// Unrecognised roots are <see cref="InventoryStorage.Other"/> rather than guessed, so carried counts stay accurate.
    /// </summary>
    public static InventoryStorage StorageOf(string location)
    {
        string root = Root(location);
        if (CarriedRoots.Contains(root) || IsNumbered(root, "General "))
        {
            return InventoryStorage.Carried;
        }

        if (IsNumbered(root, "Bank") || IsNumbered(root, "SharedBank"))
        {
            return InventoryStorage.Bank;
        }

        if (IsNumbered(root, "Hoard "))
        {
            return InventoryStorage.Hoard;
        }

        if (IsNumbered(root, "Personal-Depot"))
        {
            return InventoryStorage.Depot;
        }

        return InventoryStorage.Other;
    }

    /// <summary>Distinct location roots that were stored as <see cref="InventoryStorage.Other"/>.</summary>
    public static IReadOnlyList<string> UnknownLocations(IEnumerable<InventoryExportItem> items)
    {
        return items
            .Where(i => i.Storage == InventoryStorage.Other)
            .Select(i => Root(i.Location))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>The location without its "-SlotN" suffixes, e.g. "Bank3-Slot1-Slot2" becomes "Bank3".</summary>
    private static string Root(string location)
    {
        int slot = location.IndexOf("-Slot", StringComparison.OrdinalIgnoreCase);
        return (slot >= 0 ? location[..slot] : location).Trim();
    }

    private static bool IsNumbered(string root, string prefix)
    {
        return root.Length > prefix.Length &&
            root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            root.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    public static string PathForLog(string logPath)
    {
        string name = Path.GetFileNameWithoutExtension(logPath);
        if (!name.StartsWith("eqlog_", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Expected an eqlog_ log filename.", nameof(logPath));
        }

        string folder = Path.GetDirectoryName(Path.GetFullPath(logPath))!;
        return Path.Combine(Directory.GetParent(folder)!.FullName, name[6..] + "-Inventory.txt");
    }

    public static IReadOnlyList<InventoryExportItem> Parse(string text)
    {
        using var reader = new StringReader(text);
        if (reader.ReadLine()?.TrimStart('\uFEFF') != "Location\tName\tID\tCount\tSlots")
        {
            throw new FormatException("Inventory export must start with Location, Name, ID, Count, Slots (tab-separated).");
        }

        var items = new List<InventoryExportItem>();
        int lineNumber = 1;
        bool collection = false;
        while (reader.ReadLine() is string line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] cells = line.Split('\t');
            if (cells[0] == "KeyRing" && cells.Length >= 3 && cells[1] == "Name")
            {
                collection = true;
                continue;
            }
            int count = 1;
            if ((collection ? cells.Length != 3 : cells.Length != 5) ||
                !int.TryParse(cells[2], NumberStyles.None, CultureInfo.InvariantCulture, out int id) ||
                (!collection && (!int.TryParse(cells[3], NumberStyles.None, CultureInfo.InvariantCulture, out count) ||
                !int.TryParse(cells[4], NumberStyles.None, CultureInfo.InvariantCulture, out _))))
            {
                throw new FormatException($"Invalid inventory row at line {lineNumber}.");
            }

            if (id == 0 || count == 0 || cells[1] == "Empty")
            {
                continue;
            }

            var storage = collection ? InventoryStorage.Carried : StorageOf(cells[0]);
            // General and bank slots hold bags, so "-SlotN" is a bag slot and only a second "-SlotN" is an augment.
            // Worn, hoard and depot slots hold items directly, so their first "-SlotN" is already an augment.
            // Unknown locations are treated like bags so their contents are counted rather than hidden as augments.
            bool bag = cells[0].StartsWith("General ", StringComparison.OrdinalIgnoreCase) ||
                storage is InventoryStorage.Bank or InventoryStorage.Other;
            int slot = cells[0].IndexOf("-Slot", StringComparison.OrdinalIgnoreCase);
            bool installed = !collection && slot >= 0 &&
                (!bag || cells[0].IndexOf("-Slot", slot + 5, StringComparison.OrdinalIgnoreCase) >= 0);
            items.Add(new(cells[0], cells[1], id, count, storage, collection, installed));
        }
        return items;
    }
}
