using System.Globalization;

namespace EqLog.Core;

public sealed record InventoryExportItem(string Location, string Name, int Id, int Count, bool Bank,
    bool Collection = false, bool Installed = false);

public static class InventoryExport
{
    public static string PathForLog(string logPath)
    {
        string name = Path.GetFileNameWithoutExtension(logPath);
        if (!name.StartsWith("eqlog_", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected an eqlog_ log filename.", nameof(logPath));
        string folder = Path.GetDirectoryName(Path.GetFullPath(logPath))!;
        return Path.Combine(Directory.GetParent(folder)!.FullName, name[6..] + "-Inventory.txt");
    }

    public static IReadOnlyList<InventoryExportItem> Parse(string text)
    {
        using var reader = new StringReader(text);
        if (reader.ReadLine()?.TrimStart('\uFEFF') != "Location\tName\tID\tCount\tSlots")
            throw new FormatException("Inventory export must start with Location, Name, ID, Count, Slots (tab-separated).");
        var items = new List<InventoryExportItem>();
        int lineNumber = 1;
        bool collection = false;
        while (reader.ReadLine() is string line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
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
                throw new FormatException($"Invalid inventory row at line {lineNumber}.");
            if (id == 0 || count == 0 || cells[1] == "Empty") continue;
            bool bank = cells[0].StartsWith("Bank", StringComparison.OrdinalIgnoreCase) ||
                cells[0].StartsWith("SharedBank", StringComparison.OrdinalIgnoreCase);
            bool bag = cells[0].StartsWith("General ", StringComparison.OrdinalIgnoreCase) || bank;
            int slot = cells[0].IndexOf("-Slot", StringComparison.OrdinalIgnoreCase);
            bool installed = !collection && slot >= 0 &&
                (!bag || cells[0].IndexOf("-Slot", slot + 5, StringComparison.OrdinalIgnoreCase) >= 0);
            items.Add(new(cells[0], cells[1], id, count, bank, collection, installed));
        }
        return items;
    }
}
