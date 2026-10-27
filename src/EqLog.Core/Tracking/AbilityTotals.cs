namespace EqLog.Core.Tracking;

public sealed record AbilityStats(string Name, long Total, double PerSecond, double Percent,
    int Hits, int Crits, long MaxHit, long Overheal);

public sealed class AbilityTotals
{
    private readonly Dictionary<string, Totals> _abilities = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string name, long amount, bool critical, long overheal = 0)
    {
        if (!_abilities.TryGetValue(name, out var totals))
        {
            _abilities[name] = totals = new Totals();
        }

        totals.Total += amount;
        totals.Hits++;
        if (critical)
        {
            totals.Crits++;
        }

        totals.MaxHit = Math.Max(totals.MaxHit, amount);
        totals.Overheal += overheal;
    }

    public IReadOnlyList<AbilityStats> Snapshot(double seconds)
    {
        long total = _abilities.Values.Sum(a => a.Total);
        return _abilities.OrderByDescending(a => a.Value.Total).ThenBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
            .Select(a => new AbilityStats(a.Key, a.Value.Total, a.Value.Total / Math.Max(1, seconds),
                total == 0 ? 0 : 100.0 * a.Value.Total / total, a.Value.Hits, a.Value.Crits,
                a.Value.MaxHit, a.Value.Overheal)).ToArray();
    }

    private sealed class Totals
    {
        public long Total;
        public int Hits;
        public int Crits;
        public long MaxHit;
        public long Overheal;
    }
}
