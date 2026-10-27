using EqLog.Core.Parsing;

namespace EqLog.Core.Tracking;

public sealed record CombatantStats(string Name, long Total, double Dps, double Percent, int Hits, int Crits, long MaxHit)
{
    public IReadOnlyList<AbilityStats> Abilities { get; init; } = [];
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

            c.Total += e.Amount;
            c.Abilities.Add(e.Source ?? (e.Kind == DamageKind.Melee ? "Melee" : e.Kind.ToString()),
                e.Amount, e.IsCritical);
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
                    Abilities = a.Abilities.Snapshot(seconds)
                })
                .ToList();
            return new FightSnapshot(Target, Start, LastActivity, seconds, Total, Total / seconds, IsActive, Killed, attackers);
        }
    }

    private sealed class Combatant(string name)
    {
        public AbilityTotals Abilities { get; } = new();
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
