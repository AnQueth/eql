using System.Text.Json.Serialization;

namespace EqLog.Core;

public sealed class QuestInventory : IJsonOnDeserialized
{
    public List<QuestInventoryItem> Items { get; set; } = [];

    /// <summary>Highest loot line counted per character and log, so replaying a log never counts loot twice.</summary>
    public Dictionary<string, ResistanceWatermark> LootWatermarks { get; set; } = [];

    /// <summary>Legacy per-line keys ("character\tlog\tline"); converted to <see cref="LootWatermarks"/> when loaded.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HashSet<string>? ImportedLines
    {
        get; set;
    }

    public HashSet<string> Completed { get; set; } = [];
    public Dictionary<string, DateTime> ExportTimes { get; set; } = [];
    public List<InventoryLootDelta> RecentLoot { get; set; } = [];

    /// <summary>A detached copy that can be serialized on another thread while this one keeps changing.</summary>
    public QuestInventory Clone()
    {
        return new QuestInventory
        {
            Items = Items.ConvertAll(i => i.Clone()),
            LootWatermarks = new(LootWatermarks, LootWatermarks.Comparer),
            Completed = new(Completed, Completed.Comparer),
            ExportTimes = new(ExportTimes, ExportTimes.Comparer),
            RecentLoot = [.. RecentLoot],
        };
    }

    void IJsonOnDeserialized.OnDeserialized()
    {
        LootWatermarks ??= [];
        if (ImportedLines is null)
        {
            return;
        }

        // The old keys carry no time, so stamp them "now": any replayed line is no newer, and new lines have higher numbers.
        var now = DateTime.UtcNow;
        foreach (string imported in ImportedLines)
        {
            int last = imported.LastIndexOf('\t');
            if (last <= 0 || !long.TryParse(imported.AsSpan(last + 1), out long line))
            {
                continue;
            }
            string key = imported[..last].ToUpperInvariant();
            if (!LootWatermarks.TryGetValue(key, out var watermark) || line > watermark.Line)
            {
                LootWatermarks[key] = new(now, line);
            }
        }
        ImportedLines = null;
    }

    /// <summary>
    /// Records that a log line was counted. A line is new when it is further into the log than the watermark,
    /// or newer in time (a rotated log restarts its line numbers).
    /// </summary>
    private bool MarkCounted(string character, string source, long line, DateTime? timeUtc)
    {
        string key = (character + "\t" + source).ToUpperInvariant();
        var time = timeUtc ?? DateTime.MinValue;
        if (LootWatermarks.TryGetValue(key, out var watermark) && line <= watermark.Line && time <= watermark.Time)
        {
            return false;
        }
        LootWatermarks[key] = new(watermark is null || time > watermark.Time ? time : watermark.Time, line);
        return true;
    }

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

        if (!MarkCounted(character, source, line, timeUtc))
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
            item.Hoard = 0;
            item.Depot = 0;
            item.OtherStorage = 0;
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
            var loose = group.Where(r => !r.Collection && !r.Installed).ToArray();
            item.Carried = loose.Where(r => r.Storage == InventoryStorage.Carried).Sum(r => r.Count);
            item.Bank = loose.Where(r => r.Storage == InventoryStorage.Bank).Sum(r => r.Count);
            item.Hoard = loose.Where(r => r.Storage == InventoryStorage.Hoard).Sum(r => r.Count);
            item.Depot = loose.Where(r => r.Storage == InventoryStorage.Depot).Sum(r => r.Count);
            item.OtherStorage = loose.Where(r => r.Storage == InventoryStorage.Other).Sum(r => r.Count);
            item.Collection = group.Where(r => r.Collection).Sum(r => r.Count);
            item.Installed = group.Where(r => r.Installed).Sum(r => r.Count);
            item.Owned = item.Carried + item.Bank + item.Hoard + item.Depot + item.OtherStorage;
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Owned
    {
        get; set;
    }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Historical
    {
        get; set;
    }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Carried
    {
        get; set;
    }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Bank
    {
        get; set;
    }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Hoard
    {
        get; set;
    }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Depot
    {
        get; set;
    }
    /// <summary>Loose items in an export location the parser doesn't recognise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int OtherStorage
    {
        get; set;
    }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Collection
    {
        get; set;
    }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Installed
    {
        get; set;
    }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTime? LastLootUtc
    {
        get; set;
    }

    public QuestInventoryItem Clone()
    {
        return (QuestInventoryItem)MemberwiseClone();
    }
}

public sealed record InventoryLootDelta(string Character, string Item, int Quantity, DateTime TimeUtc);
