using EqLog.Core.Parsing;

namespace EqLog.Core.Tracking;

public sealed record CombatantStats(string Name, long Total, double Dps, double Percent, int Hits, int Crits, long MaxHit)
{
    public IReadOnlyList<AbilityStats> Abilities { get; init; } = [];

    public IReadOnlyList<DamageTimelinePoint> Timeline { get; init; } = [];

    public IReadOnlyList<AbilityTimeline> AbilityTimelines { get; init; } = [];
}

/// <summary>Damage landed during one second of a fight, offset from the fight start.</summary>
public sealed record DamageTimelinePoint(int Second, long Amount);

/// <summary>When one damage type (ability, spell, or Melee) landed during a fight.</summary>
public sealed record AbilityTimeline(string Name, IReadOnlyList<DamageTimelinePoint> Points);

/// <summary>A continuous stretch of one damage type, covering fight seconds [StartSecond, EndSecond].</summary>
public sealed record DamageSegment(int StartSecond, int EndSecond, long Amount, int ActiveSeconds);

/// <summary>Damage summed over a span of fight seconds, [StartSecond, EndSecond).</summary>
public sealed record DamageTimelineBucket(int StartSecond, int EndSecond, long Amount)
{
    public double PerSecond
    {
        get
        {
            return (double)Amount / Math.Max(1, EndSecond - StartSecond);
        }
    }
}

public static class DamageTimeline
{
    /// <summary>Groups per-second points into at most <paramref name="maxBuckets"/> equal-width buckets covering the whole fight, including idle seconds.</summary>
    public static IReadOnlyList<DamageTimelineBucket> Bucket(IReadOnlyList<DamageTimelinePoint> points, double durationSeconds, int maxBuckets)
    {
        int seconds = Math.Max(1, (int)Math.Ceiling(durationSeconds));
        if (points.Count > 0)
        {
            seconds = Math.Max(seconds, points.Max(p => p.Second) + 1);
        }

        int width = Math.Max(1, (int)Math.Ceiling((double)seconds / Math.Max(1, maxBuckets)));
        int count = (seconds + width - 1) / width;
        var totals = new long[count];
        foreach (var p in points)
        {
            totals[Math.Clamp(p.Second / width, 0, count - 1)] += p.Amount;
        }

        var buckets = new List<DamageTimelineBucket>(count);
        for (int i = 0; i < count; i++)
        {
            buckets.Add(new DamageTimelineBucket(i * width, Math.Min(seconds, (i + 1) * width), totals[i]));
        }

        return buckets;
    }

    /// <summary>Longest silence still treated as one continuous stretch; covers 6-second DoT ticks and slow melee rounds.</summary>
    public const int SegmentGapSeconds = 7;

    /// <summary>Splits one damage type's per-second points into start/finish stretches separated by gaps over <paramref name="maxGapSeconds"/>.</summary>
    public static IReadOnlyList<DamageSegment> Segments(IReadOnlyList<DamageTimelinePoint> points, int maxGapSeconds = SegmentGapSeconds)
    {
        var segments = new List<DamageSegment>();
        DamageTimelinePoint? first = null;
        DamageTimelinePoint? last = null;
        long amount = 0;
        int active = 0;
        foreach (var p in points.OrderBy(p => p.Second))
        {
            if (first is not null && last is not null && p.Second - last.Second > maxGapSeconds)
            {
                segments.Add(new DamageSegment(first.Second, last.Second, amount, active));
                first = null;
                amount = 0;
                active = 0;
            }

            first ??= p;
            last = p;
            amount += p.Amount;
            active++;
        }

        if (first is not null && last is not null)
        {
            segments.Add(new DamageSegment(first.Second, last.Second, amount, active));
        }

        return segments;
    }

    /// <summary>Combines damage types with the same name (e.g. across attackers), ordered by when each first landed.</summary>
    public static IReadOnlyList<AbilityTimeline> Merge(IEnumerable<AbilityTimeline> timelines)
    {
        return timelines
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AbilityTimeline(g.First().Name, g.SelectMany(t => t.Points)
                .GroupBy(p => p.Second)
                .OrderBy(s => s.Key)
                .Select(s => new DamageTimelinePoint(s.Key, s.Sum(p => p.Amount)))
                .ToArray()))
            .Where(t => t.Points.Count > 0)
            .OrderBy(t => t.Points[0].Second)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

public sealed record FightSnapshot(
    string Target,
    DateTime Start,
    DateTime LastActivity,
    double DurationSeconds,
    long Total,
    double Dps,
    bool IsActive,
    bool Killed,
    IReadOnlyList<CombatantStats> Attackers)
{
    public CombatantStats? You
    {
        get
        {
            return Attackers.FirstOrDefault(a => a.Name == Names.You);
        }
    }

    public IReadOnlyList<DamageTimelinePoint> Timeline { get; init; } = [];
}

/// <summary>Groups damage dealt to each creature into fights and computes per-attacker DPS.</summary>
public sealed class FightTracker
{
    private const int MaxFights = 60;
    private readonly Dictionary<string, Fight> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Fight> _fights = [];

    public TimeSpan FightTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public long SessionDamageByYou
    {
        get; private set;
    }

    public long SessionMaxHitByYou
    {
        get; private set;
    }

    public string? SessionMaxHitSource
    {
        get; private set;
    }

    public void OnDamage(DamageEvent e)
    {
        if (e.Target == Names.You || e.Attacker == e.Target)
        {
            return;
        }

        if (_active.TryGetValue(e.Target, out var fight) && e.Time - fight.LastActivity > FightTimeout)
        {
            End(fight);
            fight = null;
        }

        if (fight is null)
        {
            if (e.Attacker != Names.You && !LooksLikeNpc(e.Target))
            {
                return;
            }

            fight = new Fight(e.Target, e.Time);
            _active[e.Target] = fight;
            _fights.Add(fight);
            if (_fights.Count > MaxFights)
            {
                _fights.RemoveAt(0);
            }
        }

        fight.Add(e);
        if (e.Attacker == Names.You)
        {
            SessionDamageByYou += e.Amount;
            if (e.Amount > SessionMaxHitByYou)
            {
                SessionMaxHitByYou = e.Amount;
                SessionMaxHitSource = $"{e.Source ?? "melee"} on {e.Target}";
            }
        }
    }

    public void OnKill(KillEvent e)
    {
        if (_active.TryGetValue(e.Victim, out var fight))
        {
            fight.Killed = true;
            fight.LastActivity = e.Time > fight.LastActivity ? e.Time : fight.LastActivity;
            End(fight);
        }
    }

    public void Expire(DateTime now)
    {
        foreach (var f in _active.Values.Where(f => now - f.LastActivity > FightTimeout).ToList())
        {
            End(f);
        }
    }

    public void Clear()
    {
        _active.Clear();
        _fights.Clear();
        ResetSession();
    }

    public void ResetSession()
    {
        SessionDamageByYou = 0;
        SessionMaxHitByYou = 0;
        SessionMaxHitSource = null;
    }

    /// <summary>The fight to feature: the most recently active fight you took part in, else the most recent fight.</summary>
    public FightSnapshot? Current()
    {
        var fight = _fights.Where(f => f.YouInvolved).MaxBy(f => f.LastActivity) ?? _fights.MaxBy(f => f.LastActivity);
        return fight?.ToSnapshot();
    }

    public IReadOnlyList<FightSnapshot> Recent(int count)
    {
        return _fights.Where(f => f.YouInvolved).OrderByDescending(f => f.LastActivity).Take(count).Select(f => f.ToSnapshot()).ToList();
    }

    private void End(Fight f)
    {
        f.IsActive = false;
        _active.Remove(f.Target);
    }

    private static bool LooksLikeNpc(string name)
    {
        return name.Contains(' ', StringComparison.Ordinal);
    }

    private sealed class Fight(string target, DateTime start)
    {
        private readonly Dictionary<string, Combatant> _attackers = new(StringComparer.OrdinalIgnoreCase);
        private readonly SortedDictionary<int, long> _timeline = [];

        public string Target { get; } = target;

        public DateTime Start { get; } = start;

        public DateTime LastActivity { get; set; } = start;

        public long Total
        {
            get; private set;
        }

        public bool IsActive { get; set; } = true;

        public bool Killed
        {
            get; set;
        }

        public bool YouInvolved
        {
            get; private set;
        }

        public void Add(DamageEvent e)
        {
            if (!_attackers.TryGetValue(e.Attacker, out var c))
            {
                c = new Combatant(e.Attacker);
                _attackers[e.Attacker] = c;
            }

            int second = Math.Max(0, (int)(e.Time - Start).TotalSeconds);
            AddToTimeline(_timeline, second, e.Amount);
            AddToTimeline(c.Timeline, second, e.Amount);
            string ability = e.Source ?? (e.Kind == DamageKind.Melee ? "Melee" : e.Kind.ToString());
            if (!c.AbilityTimelines.TryGetValue(ability, out var abilityTimeline))
            {
                abilityTimeline = [];
                c.AbilityTimelines[ability] = abilityTimeline;
            }

            AddToTimeline(abilityTimeline, second, e.Amount);
            c.Total += e.Amount;
            c.Abilities.Add(ability, e.Amount, e.IsCritical);
            c.Hits++;
            if (e.IsCritical)
            {
                c.Crits++;
            }

            if (e.Amount > c.MaxHit)
            {
                c.MaxHit = e.Amount;
            }

            Total += e.Amount;
            if (e.Time > LastActivity)
            {
                LastActivity = e.Time;
            }

            if (e.Attacker == Names.You)
            {
                YouInvolved = true;
            }
        }

        public FightSnapshot ToSnapshot()
        {
            double seconds = Math.Max(1, (LastActivity - Start).TotalSeconds + 1);
            var attackers = _attackers.Values
                .OrderByDescending(a => a.Total)
                .Select(a => new CombatantStats(a.Name, a.Total, a.Total / seconds, Total == 0 ? 0 : (double)a.Total / Total, a.Hits, a.Crits, a.MaxHit)
                {
                    Abilities = a.Abilities.Snapshot(seconds),
                    Timeline = ToPoints(a.Timeline),
                    AbilityTimelines = DamageTimeline.Merge(a.AbilityTimelines.Select(t => new AbilityTimeline(t.Key, ToPoints(t.Value)))),
                })
                .ToList();
            return new FightSnapshot(Target, Start, LastActivity, seconds, Total, Total / seconds, IsActive, Killed, attackers)
            {
                Timeline = ToPoints(_timeline),
            };
        }

        private static void AddToTimeline(SortedDictionary<int, long> timeline, int second, long amount)
        {
            timeline.TryGetValue(second, out long current);
            timeline[second] = current + amount;
        }

        private static DamageTimelinePoint[] ToPoints(SortedDictionary<int, long> timeline)
        {
            return timeline.Select(p => new DamageTimelinePoint(p.Key, p.Value)).ToArray();
        }
    }

    private sealed class Combatant(string name)
    {
        public AbilityTotals Abilities { get; } = new();
        public SortedDictionary<int, long> Timeline { get; } = [];
        public Dictionary<string, SortedDictionary<int, long>> AbilityTimelines { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string Name { get; } = name;

        public long Total
        {
            get; set;
        }

        public int Hits
        {
            get; set;
        }

        public int Crits
        {
            get; set;
        }

        public long MaxHit
        {
            get; set;
        }
    }
}
