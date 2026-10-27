using EqLog.Core.Parsing;

namespace EqLog.Core.Tracking;

public sealed record HealSnapshot(
    long OutgoingTotal,
    long OutgoingOverheal,
    double OutgoingRecentHps,
    IReadOnlyList<NamedTotal> OutgoingBySpell,
    IReadOnlyList<NamedTotal> OutgoingByTarget,
    long IncomingTotal,
    double IncomingRecentHps,
    IReadOnlyList<NamedTotal> IncomingByHealer)
{
    public IReadOnlyList<HealingCombatant> Healers { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<AbilityStats>> OutgoingTargetAbilities { get; init; }
        = new Dictionary<string, IReadOnlyList<AbilityStats>>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, IReadOnlyList<AbilityStats>> IncomingHealerAbilities { get; init; }
        = new Dictionary<string, IReadOnlyList<AbilityStats>>(StringComparer.OrdinalIgnoreCase);
    public double DurationSeconds { get; init; }
}

public sealed record HealingCombatant(string Name, IReadOnlyList<AbilityStats> Abilities);

/// <summary>Tracks heals you cast (by spell and target) and heals you receive (by healer).</summary>
public sealed class HealTracker
{
    private readonly Dictionary<string, (long Total, int Count)> _bySpell = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Total, int Count)> _byTarget = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Total, int Count)> _byHealer = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(DateTime Time, long Amount, bool Outgoing)> _recent = new();
    private long _outTotal;
    private long _overheal;
    private long _inTotal;
    private readonly Dictionary<string, AbilityTotals> _healers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AbilityTotals> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AbilityTotals> _incomingAbilities = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _start;
    private DateTime? _last;

    public TimeSpan RecentWindow { get; set; } = TimeSpan.FromSeconds(30);

    public void OnHeal(HealEvent e)
    {
        _start = _start is { } start && start < e.Time ? start : e.Time;
        _last = _last is { } last && last > e.Time ? last : e.Time;
        string ability = e.Spell ?? "(unknown)";
        bool critical = e.Modifier?.Contains("Critical", StringComparison.OrdinalIgnoreCase) == true;
        long overheal = Math.Max(0, (e.FullAmount ?? e.Amount) - e.Amount);
        Get(_healers, e.Healer).Add(ability, e.Amount, critical, overheal);
        if (e.Healer == Names.You)
        {
            Get(_targets, e.Target).Add(ability, e.Amount, critical, overheal);
            _outTotal += e.Amount;
            if (e.FullAmount is long full && full > e.Amount)
            {
                _overheal += full - e.Amount;
            }

            Add(_bySpell, e.Spell ?? "(unknown)", e.Amount);
            Add(_byTarget, e.Target, e.Amount);
            _recent.Enqueue((e.Time, e.Amount, true));
        }

        if (e.Target == Names.You)
        {
            string healer = e.Healer == Names.You ? $"You ({e.Spell})" : e.Healer;
            Get(_incomingAbilities, healer).Add(ability, e.Amount, critical, overheal);
            _inTotal += e.Amount;
            Add(_byHealer, e.Healer == Names.You ? $"You ({e.Spell})" : e.Healer, e.Amount);
            _recent.Enqueue((e.Time, e.Amount, false));
        }

        while (_recent.Count > 0 && e.Time - _recent.Peek().Time > RecentWindow)
        {
            _recent.Dequeue();
        }
    }

    public void ResetSession()
    {
        _bySpell.Clear();
        _byTarget.Clear();
        _byHealer.Clear();
        _recent.Clear();
        _healers.Clear();
        _targets.Clear();
        _incomingAbilities.Clear();
        _start = _last = null;
        _outTotal = _overheal = _inTotal = 0;
    }

    public HealSnapshot Snapshot(DateTime now, int top)
    {
        double window = RecentWindow.TotalSeconds;
        long recentOut = 0, recentIn = 0;
        foreach (var r in _recent)
        {
            if (now - r.Time > RecentWindow) continue;
            if (r.Outgoing) recentOut += r.Amount; else recentIn += r.Amount;
        }

        double seconds = _start is { } start && _last is { } last ? Math.Max(1, (last - start).TotalSeconds + 1) : 1;
        return new HealSnapshot(
            _outTotal,
            _overheal,
            recentOut / window,
            DamageTakenTracker.Top(_bySpell, _outTotal, top),
            DamageTakenTracker.Top(_byTarget, _outTotal, top),
            _inTotal,
            recentIn / window,
            DamageTakenTracker.Top(_byHealer, _inTotal, top))
        {
            DurationSeconds = seconds,
            Healers = _healers.Select(h => new HealingCombatant(h.Key, h.Value.Snapshot(seconds)))
                .OrderByDescending(h => h.Abilities.Sum(a => a.Total)).ToArray(),
            OutgoingTargetAbilities = _targets.ToDictionary(h => h.Key, h => h.Value.Snapshot(seconds), StringComparer.OrdinalIgnoreCase),
            IncomingHealerAbilities = _incomingAbilities.ToDictionary(h => h.Key, h => h.Value.Snapshot(seconds), StringComparer.OrdinalIgnoreCase),
        };
    }

    private static AbilityTotals Get(Dictionary<string, AbilityTotals> dictionary, string name)
    {
        if (!dictionary.TryGetValue(name, out var totals)) dictionary[name] = totals = new AbilityTotals();
        return totals;
    }

    private static void Add(Dictionary<string, (long Total, int Count)> d, string key, long amount)
    {
        d.TryGetValue(key, out var v);
        d[key] = (v.Total + amount, v.Count + 1);
    }
}
