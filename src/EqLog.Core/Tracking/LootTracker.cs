using EqLog.Core.Parsing;

namespace EqLog.Core.Tracking;

public sealed record LootRecord(DateTime Time, string Looter, string Item, int Quantity, string? Corpse, Coin? SoldFor, bool Stored);
public sealed record LootGroup(string Looter, string Item, int Quantity, DateTime LastLoot);

public sealed record LootSnapshot(
    Coin CoinFromCorpses,
    Coin CoinFromAutoSell,
    int ItemsLooted,
    IReadOnlyList<LootRecord> Recent,
    IReadOnlyList<(string Item, int Quantity)> TopItems)
{
    public IReadOnlyList<LootGroup> Groups { get; init; } = [];

    public IEnumerable<LootGroup> OrderedGroups(bool recentFirst) => recentFirst
        ? Groups.OrderByDescending(g => g.LastLoot).ThenBy(g => ItemNames.Normalize(g.Item), StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Looter, StringComparer.OrdinalIgnoreCase)
        : Groups.OrderBy(g => ItemNames.Normalize(g.Item), StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Looter, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Items you (and others, when visible) looted, plus coin received.</summary>
public sealed class LootTracker
{
    private const int MaxRecent = 60;
    private readonly List<LootRecord> _recent = [];
    private readonly Dictionary<string, int> _itemCounts = new(ItemNames.Comparer);
    private readonly Dictionary<string, Dictionary<string, LootGroup>> _groups = new(StringComparer.OrdinalIgnoreCase);
    private Coin _coin;
    private Coin _sold;
    private int _items;

    public void OnLoot(LootEvent e)
    {
        if (!_groups.TryGetValue(e.Looter, out var items))
            _groups[e.Looter] = items = new Dictionary<string, LootGroup>(ItemNames.Comparer);
        items.TryGetValue(e.Item, out var previous);
        items[e.Item] = new(e.Looter, e.Item, (previous?.Quantity ?? 0) + e.Quantity,
            previous is not null && previous.LastLoot > e.Time ? previous.LastLoot : e.Time);
        _recent.Add(new LootRecord(e.Time, e.Looter, e.Item, e.Quantity, e.Corpse, e.SoldFor, e.Stored));
        if (_recent.Count > MaxRecent)
        {
            _recent.RemoveAt(0);
        }

        if (e.Looter != Names.You)
        {
            return;
        }

        _items += e.Quantity;
        _itemCounts.TryGetValue(e.Item, out int q);
        _itemCounts[e.Item] = q + e.Quantity;
        if (e.SoldFor is Coin c)
        {
            _sold += c;
        }
    }

    public void OnCoin(CoinEvent e) => _coin += e.Amount;

    public void ResetSession()
    {
        _itemCounts.Clear();
        _groups.Clear();
        _coin = Coin.Zero;
        _sold = Coin.Zero;
        _items = 0;
    }

    public LootSnapshot Snapshot(int recent, int topItems) => new(
        _coin,
        _sold,
        _items,
        _recent.AsEnumerable().Reverse().Take(recent).ToList(),
        _itemCounts.OrderByDescending(kv => kv.Value).Take(topItems).Select(kv => (kv.Key, kv.Value)).ToList())
    {
        Groups = _groups.Values.SelectMany(items => items.Values).ToArray(),
    };
}
