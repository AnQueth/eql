using EqLog.Core.Parsing;

namespace EqLog.Core.Tracking;

public sealed record ForageFind(DateTime Time, string Item, int Quantity, string Zone);

public sealed record ForageZone(string Zone, int Quantity);

public sealed record ForageItem(string Item, int Quantity, int Finds, DateTime LastFound, IReadOnlyList<ForageZone> Zones);

public sealed record ForageSnapshot(
    int Attempts,
    int Failures,
    DateTime? FirstAttempt,
    DateTime? LastAttempt,
    int? Skill,
    int SkillGains,
    IReadOnlyList<ForageItem> Items,
    IReadOnlyList<ForageFind> Recent);

/// <summary>Forage attempts, finds per item and zone, and Forage skill-ups observed this session.</summary>
public sealed class ForageTracker
{
    public const string UnknownZone = "(unknown zone)";
    private const int MaxRecent = 60;
    private readonly Dictionary<string, ItemTotals> _items = new(ItemNames.Comparer);
    private readonly List<ForageFind> _recent = [];
    private string _zone = UnknownZone;
    private int _attempts;
    private int _failures;
    private DateTime? _first;
    private DateTime? _last;
    private int? _skill;
    private int _skillGains;

    public void OnZone(ZoneEvent e)
    {
        _zone = e.Zone;
    }

    public void OnForage(ForageEvent e)
    {
        _attempts++;
        _first ??= e.Time;
        _last = e.Time;
        if (e.Item is null)
        {
            _failures++;
            return;
        }

        if (!_items.TryGetValue(e.Item, out var totals))
        {
            totals = new ItemTotals(e.Item);
            _items[e.Item] = totals;
        }

        totals.Quantity += e.Quantity;
        totals.Finds++;
        if (e.Time > totals.LastFound)
        {
            totals.LastFound = e.Time;
        }
        totals.Zones[_zone] = totals.Zones.GetValueOrDefault(_zone) + e.Quantity;

        _recent.Add(new ForageFind(e.Time, e.Item, e.Quantity, _zone));
        if (_recent.Count > MaxRecent)
        {
            _recent.RemoveAt(0);
        }
    }

    public void OnSkill(ForageSkillEvent e)
    {
        if (_skill is null || e.Skill > _skill)
        {
            _skillGains++;
        }

        _skill = e.Skill;
    }

    /// <summary>Clears session counts. The current zone and last known skill are kept because they are still accurate.</summary>
    public void ResetSession()
    {
        _items.Clear();
        _recent.Clear();
        _attempts = 0;
        _failures = 0;
        _first = null;
        _last = null;
        _skillGains = 0;
    }

    public ForageSnapshot Snapshot()
    {
        var items = _items.Values
            .Select(totals => new ForageItem(totals.Item, totals.Quantity, totals.Finds, totals.LastFound,
                totals.Zones
                    .OrderByDescending(zone => zone.Value)
                    .ThenBy(zone => zone.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(zone => new ForageZone(zone.Key, zone.Value))
                    .ToArray()))
            .OrderByDescending(item => item.Quantity)
            .ThenBy(item => item.Item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(_attempts, _failures, _first, _last, _skill, _skillGains, items,
            _recent.AsEnumerable().Reverse().ToArray());
    }

    private sealed class ItemTotals(string item)
    {
        public string Item { get; } = item;

        public int Quantity
        {
            get; set;
        }

        public int Finds
        {
            get; set;
        }

        public DateTime LastFound
        {
            get; set;
        }

        public Dictionary<string, int> Zones { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
