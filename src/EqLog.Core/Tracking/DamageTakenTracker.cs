using EqLog.Core.Parsing;

namespace EqLog.Core.Tracking;

public sealed record NamedTotal(string Name, long Total, double Percent, int Count = 0);

public sealed record DamageTakenSnapshot(
    DateTime? EncounterStart,
    double EncounterSeconds,
    long EncounterTotal,
    double EncounterDtps,
    IReadOnlyList<NamedTotal> EncounterByAttacker,
    long SessionTotal,
    long SessionMaxHit,
    string? SessionMaxHitFrom,
    IReadOnlyList<NamedTotal> SessionByAttacker);

/// <summary>Tracks damage dealt to you. An "encounter" is a burst of incoming damage with no gap longer than the timeout.</summary>
public sealed class DamageTakenTracker
{
    private readonly Dictionary<string, (long Total, int Count)> _encounter = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Total, int Count)> _session = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _encounterStart;
    private DateTime _encounterLast;
    private long _encounterTotal;
    private long _sessionTotal;
    private long _sessionMax;
    private string? _sessionMaxFrom;

    public TimeSpan EncounterTimeout { get; set; } = TimeSpan.FromSeconds(20);

    public void OnDamage(DamageEvent e)
    {
        if (e.Target != Names.You || e.Attacker == Names.You)
        {
            return;
        }

        if (_encounterStart is null || e.Time - _encounterLast > EncounterTimeout)
        {
            _encounter.Clear();
            _encounterTotal = 0;
            _encounterStart = e.Time;
        }

        _encounterLast = e.Time;
        string key = e.Kind == DamageKind.Melee ? e.Attacker : Label(e);
        Add(_encounter, key, e.Amount);
        Add(_session, e.Attacker == Names.Unknown && e.Source is not null ? Label(e) : e.Attacker, e.Amount);
        _encounterTotal += e.Amount;
        _sessionTotal += e.Amount;
        if (e.Amount > _sessionMax)
        {
            _sessionMax = e.Amount;
            _sessionMaxFrom = e.Source is null ? e.Attacker : Label(e);
        }
    }

    // "You have taken N damage from Envenomed Bolt." names no caster, so label it by the spell.
    private static string Label(DamageEvent e) =>
        e.Source is null ? e.Attacker
        : e.Attacker == Names.Unknown ? $"{e.Source} (DoT)"
        : $"{e.Attacker} ({e.Source})";

    public void ResetSession()
    {
        _encounter.Clear();
        _session.Clear();
        _encounterStart = null;
        _encounterTotal = 0;
        _sessionTotal = 0;
        _sessionMax = 0;
        _sessionMaxFrom = null;
    }

    public DamageTakenSnapshot Snapshot(int top)
    {
        double seconds = _encounterStart is null ? 0 : Math.Max(1, (_encounterLast - _encounterStart.Value).TotalSeconds + 1);
        return new DamageTakenSnapshot(
            _encounterStart,
            seconds,
            _encounterTotal,
            seconds == 0 ? 0 : _encounterTotal / seconds,
            Top(_encounter, _encounterTotal, top),
            _sessionTotal,
            _sessionMax,
            _sessionMaxFrom,
            Top(_session, _sessionTotal, top));
    }

    private static void Add(Dictionary<string, (long Total, int Count)> d, string key, long amount)
    {
        d.TryGetValue(key, out var v);
        d[key] = (v.Total + amount, v.Count + 1);
    }

    internal static IReadOnlyList<NamedTotal> Top(Dictionary<string, (long Total, int Count)> d, long total, int top) =>
        d.OrderByDescending(kv => kv.Value.Total)
            .Take(top)
            .Select(kv => new NamedTotal(kv.Key, kv.Value.Total, total == 0 ? 0 : (double)kv.Value.Total / total, kv.Value.Count))
            .ToList();
}
