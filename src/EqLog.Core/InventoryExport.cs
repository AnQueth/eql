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

/// <param name="WornSlot">EverQuest equipment slot number (0 charm .. 22 ammo) for worn items and their augments.</param>
public sealed record InventoryExportItem(string Location, string Name, int Id, int Count, InventoryStorage Storage,
    bool Collection = false, bool Installed = false, int? WornSlot = null);

/// <summary>One copy of an upgradeable item; <paramref name="Rank"/> is the "+N" suffix (0 when absent).</summary>
public sealed record InventoryMergeItem(string Name, int Rank, int Count, string Where, InventoryStorage Storage);

/// <summary>Copies of the same item at different "+N" ranks, best first.</summary>
public sealed record InventoryMergeGroup(string BaseName, IReadOnlyList<InventoryMergeItem> Items)
{
    public int Total
    {
        get
        {
            return Items.Sum(i => i.Count);
        }
    }
}

public static class InventoryExport
{
    /// <summary>Worn slots (including the "Any Slot" charm slot) and the cursor.</summary>
    private static readonly HashSet<string> CarriedRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "Any Slot", "Charm", "Ear", "Head", "Face", "Neck", "Shoulders", "Arms", "Back", "Wrist", "Range", "Hands",
        "Primary", "Secondary", "Fingers", "Chest", "Legs", "Feet", "Waist", "Power Source", "Ammo", "Held", "Cursor",
    };

    /// <summary>
    /// Worn slot numbers in the order the export lists each root. Roots that occur twice (two ears, wrists, rings,
    /// and "Any Slot" for the charm and power source) can only be told apart by their order in the file.
    /// </summary>
    private static readonly Dictionary<string, int[]> WornSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Any Slot"] = [0, 21],
        ["Charm"] = [0],
        ["Ear"] = [1, 4],
        ["Head"] = [2],
        ["Face"] = [3],
        ["Neck"] = [5],
        ["Shoulders"] = [6],
        ["Arms"] = [7],
        ["Back"] = [8],
        ["Wrist"] = [9, 10],
        ["Range"] = [11],
        ["Hands"] = [12],
        ["Primary"] = [13],
        ["Secondary"] = [14],
        ["Fingers"] = [15, 16],
        ["Chest"] = [17],
        ["Legs"] = [18],
        ["Feet"] = [19],
        ["Waist"] = [20],
        ["Power Source"] = [21],
        ["Ammo"] = [22],
    };

    private static readonly System.Text.RegularExpressions.Regex RankSuffix =
        new(@"^(?<name>.+?) \+(?<rank>\d+)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Splits "Bracelet of Quiescence +4" into its base name and rank; names without a suffix are rank 0.</summary>
    public static (string BaseName, int Rank) SplitRank(string name)
    {
        var match = RankSuffix.Match(name);
        if (match.Success && int.TryParse(match.Groups["rank"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int rank))
        {
            return (match.Groups["name"].Value, rank);
        }

        return (name, 0);
    }

    /// <summary>
    /// Loose items (not collections or installed augments) that exist more than once under the same base name with
    /// at least one "+N" copy, so they can be combined. Two copies that were never upgraded have no suffix and look
    /// like stackable items, so they are not listed.
    /// </summary>
    public static IReadOnlyList<InventoryMergeGroup> MergeGroups(IReadOnlyList<InventoryExportItem> items)
    {
        var containers = ContainerNames(items);
        return items
            .Where(i => !i.Collection && !i.Installed)
            .Select(i => (Item: i, Split: SplitRank(i.Name)))
            .GroupBy(x => x.Split.BaseName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Sum(x => x.Item.Count) >= 2 && g.Any(x => x.Split.Rank > 0))
            .Select(g => new InventoryMergeGroup(g.First().Split.BaseName, g
                .OrderByDescending(x => x.Split.Rank)
                .ThenBy(x => x.Item.Storage)
                .Select(x => new InventoryMergeItem(x.Item.Name, x.Split.Rank, x.Item.Count,
                    Describe(x.Item, containers), x.Item.Storage))
                .ToArray()))
            .OrderBy(g => g.BaseName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>A readable location for every row, e.g. "Bank slot 10 (Storage Trunk), bag slot 6".</summary>
    public static IReadOnlyList<string> DescribeAll(IReadOnlyList<InventoryExportItem> items)
    {
        var containers = ContainerNames(items);
        return items.Select(i => Describe(i, containers)).ToArray();
    }

    private static Dictionary<string, string> ContainerNames(IEnumerable<InventoryExportItem> items)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (!item.Collection && !item.Location.Contains("-Slot", StringComparison.OrdinalIgnoreCase))
            {
                names.TryAdd(item.Location, item.Name);
            }
        }
        return names;
    }

    private static string Describe(InventoryExportItem item, IReadOnlyDictionary<string, string> containers)
    {
        if (item.Collection)
        {
            return $"Collection ({item.Location})";
        }

        string[] parts = item.Location.Split("-Slot", StringSplitOptions.None);
        string root = parts[0].Trim();
        string place;
        int nested;
        if (item.WornSlot is int worn)
        {
            place = $"Worn {root}, equipment slot {worn}";
            nested = 1;
        }
        else if (Numbered(root, "General ") is int general)
        {
            place = $"Inventory slot {general}";
            nested = 1;
        }
        else if (Numbered(root, "SharedBank") is int shared)
        {
            place = $"Shared bank slot {shared}";
            nested = 1;
        }
        else if (Numbered(root, "Bank") is int bank)
        {
            place = $"Bank slot {bank}";
            nested = 1;
        }
        else if (Numbered(root, "Hoard ") is int hoard)
        {
            place = $"Dragon's Hoard slot {hoard}";
            nested = 1;
        }
        else if (Numbered(root, "Personal-Depot") is int depot)
        {
            place = $"Tradeskill depot slot {depot}";
            nested = 1;
        }
        else if (root.Equals("Held", StringComparison.OrdinalIgnoreCase) || root.Equals("Cursor", StringComparison.OrdinalIgnoreCase))
        {
            place = "On cursor";
            nested = 1;
        }
        else
        {
            return item.Location;
        }

        // Inventory and bank slots hold bags; worn, hoard and depot slots hold the item itself, so their first
        // "-SlotN" is an augment slot.
        bool bagHolder = item.WornSlot is null && Numbered(root, "Hoard ") is null && Numbered(root, "Personal-Depot") is null;
        for (int i = nested; i < parts.Length; i++)
        {
            string container = string.Join("-Slot", parts, 0, i);
            string slot = parts[i].Trim();
            if (bagHolder && i == 1)
            {
                place += containers.TryGetValue(container, out string? bag)
                    ? $" ({bag}), bag slot {slot}"
                    : $", bag slot {slot}";
            }
            else
            {
                place += containers.TryGetValue(container, out string? host)
                    ? $", augment slot {slot} of {host}"
                    : $", augment slot {slot}";
            }
        }
        return place;
    }

    private static int? Numbered(string root, string prefix)
    {
        return IsNumbered(root, prefix) &&
            int.TryParse(root.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
            ? n
            : null;
    }

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
        var wornSeen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var wornCurrent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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

            int? wornSlot = null;
            string wornRoot = Root(cells[0]);
            if (!collection && WornSlots.TryGetValue(wornRoot, out int[]? slots))
            {
                // Empty slots still count toward the order, so this runs before empty rows are skipped.
                if (!cells[0].Contains("-Slot", StringComparison.OrdinalIgnoreCase))
                {
                    int seen = wornSeen.GetValueOrDefault(wornRoot);
                    wornSeen[wornRoot] = seen + 1;
                    if (seen < slots.Length)
                    {
                        wornCurrent[wornRoot] = slots[seen];
                    }
                    else
                    {
                        wornCurrent.Remove(wornRoot);
                    }
                }
                if (wornCurrent.TryGetValue(wornRoot, out int current))
                {
                    wornSlot = current;
                }
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
            items.Add(new(cells[0], cells[1], id, count, storage, collection, installed, wornSlot));
        }
        return items;
    }
}
