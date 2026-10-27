using EqLog.Core.Parsing;

namespace EqLog.Core.Tracking;

public enum SpawnState
{
    /// <summary>Not killed while we were watching this zone.</summary>
    Unknown,

    /// <summary>Seen alive since it was last killed.</summary>
    Up,

    /// <summary>Killed and the respawn time has not passed yet (or is not known).</summary>
    Dead,

    /// <summary>Killed and the learned respawn time has passed.</summary>
    Due,
}

public sealed record SpawnTimerSnapshot(
    string Mob,
    SpawnState State,
    DateTime? KilledAt,
    DateTime? SeenAt,
    double? RespawnSeconds,
    double? RemainingSeconds,
    int Samples);

public sealed record SpawnSnapshot(string? Zone, IReadOnlyList<SpawnTimerSnapshot> Timers);

/// <summary>A mob that can be tracked in the current zone.</summary>
public sealed record SpawnCandidate(string Mob, bool LikelyNamed, int Kills, DateTime? LastKilled, SpawnInfo? Info);

/// <summary>
/// Learns respawn times by measuring the time from a mob's death to the next time it shows up alive in the log
/// (fighting, being hit, speaking, ...) while you stayed in the zone, and times the respawn of tracked mobs.
/// </summary>
public sealed class SpawnTracker
{
    /// <summary>Activity this soon after a kill is a same-named mob or the death itself, not a respawn.</summary>
    public static readonly TimeSpan MinRespawn = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan LateDeathMessages = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExperienceWindow = TimeSpan.FromSeconds(1);

    private readonly Dictionary<string, Dictionary<string, MobState>> _zones = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hostiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _npcs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _killedBy = new(StringComparer.OrdinalIgnoreCase);
    private KillEvent? _lastKill;
    private DateTime? _lastExperience;
    private string? _zone;

    public SpawnTracker(SpawnStore store)
    {
        Store = store;
    }

    public SpawnStore Store
    {
        get;
    }

    public string? Zone
    {
        get
        {
            return _zone;
        }
    }

    public void OnEvent(LogEvent e)
    {
        switch (e)
        {
            case ZoneEvent z:
                _zone = z.Zone;
                _pending.Clear();
                break;
            case LoginEvent:
                _pending.Clear();
                break;
            case KillEvent k:
                OnKill(k);
                break;
            case ExperienceEvent x:
                // Only NPC deaths give experience; the message can come just before or after the death message.
                _lastExperience = x.Time;
                if (_lastKill is not null && x.Time - _lastKill.Time <= ExperienceWindow)
                {
                    MarkNpc(_lastKill.Victim);
                }

                break;
            case DamageEvent d:
                if (d.Target == Names.You && d.Attacker != Names.You)
                {
                    _hostiles.Add(d.Attacker);
                }

                Seen(d.Attacker, d.Time);
                Seen(d.Target, d.Time);
                break;
            case HealEvent h:
                Seen(h.Healer, h.Time);
                Seen(h.Target, h.Time);
                break;
            case ChatEvent c when c.Channel == ChatChannel.Say:
                Seen(c.From, c.Time);
                break;
            case ChatEvent c:
                _players.Add(c.From);
                break;
            case EffectLandedEvent el:
                Seen(el.Target, el.Time);
                break;
            case AwakenedEvent a:
                Seen(a.Target, a.Time);
                break;
            case SpellResistedEvent r when r.Target is not null:
                Seen(r.Target, r.Time);
                break;
        }
    }

    /// <summary>Heuristic: proper-noun mobs ("Lord Rikantus Everling") rather than "a skeleton", pets or players.</summary>
    public bool IsLikelyNamed(string mob)
    {
        return mob.Length > 0
        && char.IsUpper(mob[0])
        && !mob.StartsWith("A ", StringComparison.Ordinal)
        && !mob.StartsWith("An ", StringComparison.Ordinal)
        && !_players.Contains(mob)
        && !KilledByHostile(mob);
    }

    // Killed by something that attacks you (or that you kill), so it was on your side: a player, mercenary or pet.
    private bool KilledByHostile(string mob)
    {
        return !_npcs.Contains(mob)
        && _killedBy.TryGetValue(mob, out var killer)
        && (_hostiles.Contains(killer) || _npcs.Contains(killer));
    }

    private static bool IsCreature(string name)
    {
        return name is not (Names.You or Names.Self or Names.Unknown)
        && !name.EndsWith(" pet", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("`s ", StringComparison.Ordinal)
        && !name.Contains("'s ", StringComparison.Ordinal);
    }

    private void OnKill(KillEvent k)
    {
        if (_zone is null || !IsCreature(k.Victim))
        {
            return;
        }

        _lastKill = k;
        if (k.Killer == Names.You || (_lastExperience is DateTime xp && k.Time - xp <= ExperienceWindow))
        {
            MarkNpc(k.Victim);
        }

        if (k.Killer is not null && k.Killer != Names.You)
        {
            _killedBy[k.Victim] = k.Killer;
        }

        var mobs = ZoneMobs(_zone);
        if (!mobs.TryGetValue(k.Victim, out var s))
        {
            s = new MobState();
            mobs[k.Victim] = s;
        }

        if (s.LastKill == k.Time)
        {
            return;
        }

        s.Kills++;
        s.LastKill = k.Time;
        _pending[k.Victim] = k.Time;
        if (IsLikelyNamed(k.Victim))
        {
            Store.Remember(_zone, k.Victim);
        }

        Store.NoteKill(_zone, k.Victim, k.Time);
    }

    private void Seen(string name, DateTime t)
    {
        if (_zone is null || !_zones.TryGetValue(_zone, out var mobs) || !mobs.TryGetValue(name, out var s))
        {
            return;
        }

        if (s.LastKill is DateTime killed && t - killed < MinRespawn)
        {
            // Activity a few seconds after the death means another mob has the same name, so this kill can't be timed.
            if (t - killed > LateDeathMessages && _pending.Remove(name))
            {
                s.Crowded++;
            }

            return;
        }

        s.LastSeen = t;
        Store.NoteSeen(_zone, name, t);
        if (_pending.Remove(name, out var kill) && !s.HasSeveral && Store.Get(_zone, name) is not null)
        {
            Store.AddSample(_zone, name, kill, (t - kill).TotalSeconds);
        }
    }

    private void MarkNpc(string name)
    {
        _npcs.Add(name);
        _players.Remove(name);
    }

    private Dictionary<string, MobState> ZoneMobs(string zone)
    {
        if (!_zones.TryGetValue(zone, out var mobs))
        {
            mobs = new(StringComparer.OrdinalIgnoreCase);
            _zones[zone] = mobs;
        }

        return mobs;
    }

    /// <summary>Mobs killed in the current zone (this log) plus mobs remembered for it from earlier logs.</summary>
    public IReadOnlyList<SpawnCandidate> Candidates()
    {
        if (_zone is null)
        {
            return [];
        }

        var mobs = _zones.GetValueOrDefault(_zone);
        var stored = Store.List(_zone).ToDictionary(i => i.Mob, StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(stored.Keys, StringComparer.OrdinalIgnoreCase);
        if (mobs is not null)
        {
            names.UnionWith(mobs.Keys);
        }

        return names
            .Select(n =>
            {
                var s = mobs?.GetValueOrDefault(n);
                var info = stored.GetValueOrDefault(n);
                return new SpawnCandidate(n, IsLikelyNamed(n) && s?.HasSeveral != true, s?.Kills ?? 0, Latest(s?.LastKill, info?.LastKill), info);
            })
            .ToList();
    }

    public SpawnSnapshot Snapshot(DateTime now)
    {
        if (_zone is null)
        {
            return new SpawnSnapshot(null, []);
        }

        var mobs = _zones.GetValueOrDefault(_zone);
        var timers = new List<SpawnTimerSnapshot>();
        foreach (var info in Store.List(_zone).Where(i => i.Tracked))
        {
            var s = mobs?.GetValueOrDefault(info.Mob);
            // The saved times cover kills from earlier sessions and other log files.
            var lastKill = Latest(s?.LastKill, info.LastKill);
            var lastSeen = Latest(s?.LastSeen, info.LastSeen);
            double? respawn = info.RespawnSeconds;
            var state = SpawnState.Unknown;
            double? remaining = null;
            if (lastKill is DateTime killed && !(lastSeen > killed))
            {
                if (respawn is double r)
                {
                    remaining = r - (now - killed).TotalSeconds;
                    state = remaining > 0 ? SpawnState.Dead : SpawnState.Due;
                }
                else
                {
                    state = SpawnState.Dead;
                }
            }
            else if (lastSeen is not null)
            {
                state = SpawnState.Up;
            }

            timers.Add(new SpawnTimerSnapshot(info.Mob, state, lastKill, lastSeen, respawn, remaining, info.Samples));
        }

        return new SpawnSnapshot(_zone, timers);
    }

    private static DateTime? Latest(DateTime? a, DateTime? b)
    {
        return a > b || b is null ? a : b;
    }

    private sealed class MobState
    {
        public int Kills
        {
            get; set;
        }

        public DateTime? LastKill
        {
            get; set;
        }

        public DateTime? LastSeen
        {
            get; set;
        }

        /// <summary>Kills followed within seconds by activity from a same-named mob.</summary>
        public int Crowded
        {
            get; set;
        }

        public bool HasSeveral
        {
            get
            {
                return Crowded >= 2 && Crowded * 4 >= Kills;
            }
        }
    }
}
