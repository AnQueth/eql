namespace EqLog.Core;

public sealed class QuestInventory
{
    public List<QuestInventoryItem> Items { get; set; } = [];
    public HashSet<string> ImportedLines { get; set; } = [];
    public HashSet<string> Completed { get; set; } = [];
    public Dictionary<string, DateTime> ExportTimes { get; set; } = [];
    public List<InventoryLootDelta> RecentLoot { get; set; } = [];

    public void NoteLoot(string character, string source, long line, string item, int quantity, bool live, DateTime? timeUtc = null)
    {
        var entry = Items.FirstOrDefault(i => i.Character.Equals(character, StringComparison.OrdinalIgnoreCase) &&
            ItemNames.Comparer.Equals(i.Item, item));
        if (entry is null)
        {
            entry = new()
            {
                Character = character,
                Item = item
            };
            Items.Add(entry);
        }

        if (timeUtc is DateTime observed && (entry.LastLootUtc is null || observed > entry.LastLootUtc))
        {
            entry.LastLootUtc = observed;
        }

        if (!ImportedLines.Add($"{character}\t{source}\t{line}"))
        {
            return;
        }

        string key = character.ToUpperInvariant();
        bool afterExport = timeUtc is DateTime time && ExportTimes.TryGetValue(key, out var exported) && time > exported;
        if (live || afterExport)
        {
            if (timeUtc is DateTime at)
            {
                RecentLoot.Add(new(character, item, quantity, at));
            }

            if (!ExportTimes.TryGetValue(key, out var cutoff) || timeUtc is null || timeUtc > cutoff)
            {
                entry.Owned += quantity;
            }
        }
        else
        {
            entry.Historical += quantity;
        }
    }

    public void ApplyExport(string character, IReadOnlyList<InventoryExportItem> rows, DateTime writtenUtc)
    {
        string key = character.ToUpperInvariant();
        foreach (var item in Items.Where(i => i.Character.Equals(character, StringComparison.OrdinalIgnoreCase)))
        {
            item.Owned = 0;
            item.Carried = 0;
            item.Bank = 0;
            item.Collection = 0;
            item.Installed = 0;
        }
        foreach (var group in rows.GroupBy(r => r.Name, ItemNames.Comparer))
        {
            var item = Items.FirstOrDefault(i => i.Character.Equals(character, StringComparison.OrdinalIgnoreCase) &&
                ItemNames.Comparer.Equals(i.Item, group.Key));
            if (item is null)
            {
                item = new()
                {
                    Character = character,
                    Item = group.Key
                };
                Items.Add(item);
            }
            item.Carried = group.Where(r => !r.Bank && !r.Collection && !r.Installed).Sum(r => r.Count);
            item.Bank = group.Where(r => r.Bank && !r.Collection && !r.Installed).Sum(r => r.Count);
            item.Collection = group.Where(r => r.Collection).Sum(r => r.Count);
            item.Installed = group.Where(r => r.Installed).Sum(r => r.Count);
            item.Owned = item.Carried + item.Bank;
        }
        RecentLoot.RemoveAll(d => d.Character.Equals(character, StringComparison.OrdinalIgnoreCase) && d.TimeUtc <= writtenUtc);
        foreach (var delta in RecentLoot.Where(d => d.Character.Equals(character, StringComparison.OrdinalIgnoreCase)))
        {
            Items.First(i => i.Character.Equals(character, StringComparison.OrdinalIgnoreCase) &&
                ItemNames.Comparer.Equals(i.Item, delta.Item)).Owned += delta.Quantity;
        }

        ExportTimes[key] = writtenUtc;
    }

    public static string CompletionKey(string character, string quest)
    {
        return character.ToUpperInvariant() + "\t" + quest;
    }

    public IEnumerable<QuestOpportunity> Opportunities(string character, IReadOnlyDictionary<string, QuestLookupResult> cache,
        string filter, bool recentFirst)
    {
        filter = filter.Trim();
        var groups = Items.Where(i => i.Character.Equals(character, StringComparison.OrdinalIgnoreCase) &&
                cache.ContainsKey(QuestLookup.ItemTitle(i.Item)))
            .SelectMany(i => cache[QuestLookup.ItemTitle(i.Item)].Quests.Select(q => (Item: i, Quest: q)))
            .GroupBy(x => x.Quest.Title)
            .Where(g => !Completed.Contains(CompletionKey(character, g.Key)))
            .Where(g => filter.Length == 0 || g.Key.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                g.Any(x => ItemNames.Normalize(x.Item.Item).Contains(ItemNames.Normalize(filter), StringComparison.OrdinalIgnoreCase)))
            .Select(g => new QuestOpportunity(g.Key, g.Select(x => x.Item).ToArray()));
        return recentFirst
            ? groups.OrderByDescending(g => g.Items.Max(i => i.LastLootUtc)).ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
            : groups.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record QuestOpportunity(string Title, IReadOnlyList<QuestInventoryItem> Items);

public sealed class QuestInventoryItem
{
    public string Character { get; set; } = "";
    public string Item { get; set; } = "";
    public int Owned
    {
        get; set;
    }
    public int Historical
    {
        get; set;
    }
    public int Carried
    {
        get; set;
    }
    public int Bank
    {
        get; set;
    }
    public int Collection
    {
        get; set;
    }
    public int Installed
    {
        get; set;
    }
    public DateTime? LastLootUtc
    {
        get; set;
    }
}

public sealed record InventoryLootDelta(string Character, string Item, int Quantity, DateTime TimeUtc);
