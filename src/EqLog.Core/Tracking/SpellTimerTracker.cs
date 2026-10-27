using EqLog.Core.Parsing;

namespace EqLog.Core.Tracking;

public enum TimerState
{
    Running,
    Expired,
    WornOff,
    Broken,
}

public sealed record SpellTimerSnapshot(
    string Spell,
    string Target,
    bool TargetConfirmed,
    bool IsCharm,
    DateTime Start,
    double? DurationSeconds,
    double ElapsedSeconds,
    double? RemainingSeconds,
    TimerState State);

/// <summary>
/// Tracks debuffs, mez, charm (and other spells with a "worn off of" message) you have on targets,
/// estimating when they will wear off using <see cref="SpellDurationStore"/>.
/// </summary>
public sealed class SpellTimerTracker(SpellDurationStore store)
{
    private static readonly TimeSpan LandingWindow = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan EndedDisplayTime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ExpiredGrace = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UnknownDurationLimit = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MinLearn = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxLearn = TimeSpan.FromHours(3);
    private const string UnknownTarget = "(target)";

    private readonly List<ActiveTimer> _timers = [];
    private readonly Dictionary<string, DateTime> _lastCastOf = new(StringComparer.OrdinalIgnoreCase);
    private Pending? _pending;
    private string? _lastEngaged;
    private (string Spell, DateTime Time)? _lastCast;

    public SpellDurationStore Store { get; } = store;

    public void OnCast(CastBeginEvent e)
    {
        FinalizePending(e.Time, force: true);
        Prune(e.Time);
        _lastCast = (e.Spell, e.Time);
        _lastCastOf[e.Spell] = e.Time;
        if (Store.IsKnown(e.Spell))
        {
            _pending = new Pending(e.Spell, e.Time);
        }
    }

    public void OnInterruptedOrFizzled(string? spell)
    {
        if (_pending is { Bound: false } p && (spell is null || SpellNames.SameBase(spell, p.Spell)))
        {
            _pending = null;
        }
    }

    public void OnResisted(SpellResistedEvent e)
    {
        if (_pending is { Bound: false } p && SpellNames.SameBase(e.Spell, p.Spell))
        {
            _pending = null;
        }
    }

    public void OnEffectLanded(EffectLandedEvent e)
    {
        if (_pending is { } p && e.Time - p.Time <= LandingWindow)
        {
            Start(p.Spell, e.Target, e.Time, confirmed: true, charm: e.IsCharm);
            p.Bound = true;
        }
        else if (e.IsCharm)
        {
            string spell = _lastCast is { } lc && e.Time - lc.Time <= LandingWindow ? lc.Spell : "Charm";
            Start(spell, e.Target, e.Time, confirmed: true, charm: true);
        }
    }

    public void OnDamage(DamageEvent e)
    {
        if (e.Attacker != Names.You || e.Target == Names.You || e.Kind == DamageKind.DamageShield)
        {
            return;
        }

        _lastEngaged = e.Target;
        if (e.Source is null)
        {
            return;
        }

        if (_pending is { Bound: false } p && e.Time - p.Time <= LandingWindow && SpellNames.SameBase(e.Source, p.Spell))
        {
            Start(p.Spell, e.Target, p.Time, confirmed: true, charm: false);
            p.Bound = true;
            return;
        }

        if (e.Kind == DamageKind.DamageOverTime)
        {
            // A DoT tick confirms which target a guessed timer belongs to.
            var guess = _timers.FirstOrDefault(t => !t.TargetConfirmed && t.State == TimerState.Running && SpellNames.SameBase(t.Spell, e.Source));
            if (guess is not null && !_timers.Any(t => t != guess && SameTimer(t, guess.Spell, e.Target)))
            {
                guess.Target = e.Target;
                guess.TargetConfirmed = true;
            }
        }
    }

    public void OnWornOff(SpellWornOffEvent e)
    {
        FinalizePending(e.Time, force: false);
        var timer = _timers.FirstOrDefault(t => t.State is TimerState.Running or TimerState.Expired && SameTimer(t, e.Spell, e.Target))
            ?? _timers.FirstOrDefault(t => t.State is TimerState.Running or TimerState.Expired && !t.TargetConfirmed && SpellNames.SameBase(t.Spell, e.Spell));

        DateTime? start = timer?.Start ?? (_lastCastOf.TryGetValue(e.Spell, out var lc) ? lc : null);
        if (start is DateTime s && e.Time - s >= MinLearn && e.Time - s <= MaxLearn)
        {
            Store.AddSample(e.Spell, (e.Time - s).TotalSeconds, e.Time);
        }

        if (timer is null)
        {
            // Show the wear-off even if we never saw it land.
            timer = new ActiveTimer(e.Spell, e.Target, start ?? e.Time, confirmed: true, charm: false);
            _timers.Add(timer);
        }

        timer.Target = e.Target;
        timer.TargetConfirmed = true;
        timer.State = TimerState.WornOff;
        timer.EndedAt = e.Time;
    }

    public void OnAwakened(AwakenedEvent e)
    {
        foreach (var t in _timers.Where(t => t.State == TimerState.Running && !t.IsCharm && string.Equals(t.Target, e.Target, StringComparison.OrdinalIgnoreCase)))
        {
            t.State = TimerState.Broken;
            t.EndedAt = e.Time;
        }
    }

    public void OnKill(KillEvent e)
    {
        _timers.RemoveAll(t => string.Equals(t.Target, e.Victim, StringComparison.OrdinalIgnoreCase));
    }

    public void Clear()
    {
        _timers.Clear();
        _pending = null;
        _lastEngaged = null;
    }

    public IReadOnlyList<SpellTimerSnapshot> Snapshot(DateTime now)
    {
        FinalizePending(now, force: false);
        Prune(now);
        return _timers
            .Select(t =>
            {
                double? duration = Store.GetDuration(t.Spell);
                double elapsed = ((t.EndedAt ?? now) - t.Start).TotalSeconds;
                double? remaining = duration is double d ? d - (now - t.Start).TotalSeconds : null;
                var state = t.State == TimerState.Running && remaining < 0 ? TimerState.Expired : t.State;
                return new SpellTimerSnapshot(t.Spell, t.Target, t.TargetConfirmed, t.IsCharm, t.Start, duration, elapsed, remaining, state);
            })
            .OrderBy(s => s.State is TimerState.WornOff or TimerState.Broken ? 0 : 1)
            .ThenBy(s => s.IsCharm ? 0 : 1)
            .ThenBy(s => s.RemainingSeconds ?? double.MaxValue)
            .ToList();
    }

    private void FinalizePending(DateTime now, bool force)
    {
        if (_pending is not { } p)
        {
            return;
        }

        if (p.Bound)
        {
            if (force || now - p.Time > LandingWindow)
            {
                _pending = null;
            }

            return;
        }

        if (force || now - p.Time > LandingWindow)
        {
            // No landing message seen; assume it landed on whatever we were last fighting.
            Start(p.Spell, _lastEngaged ?? UnknownTarget, p.Time, confirmed: false, charm: false);
            _pending = null;
        }
    }

    private void Start(string spell, string target, DateTime start, bool confirmed, bool charm)
    {
        var existing = _timers.FirstOrDefault(t => SameTimer(t, spell, target));
        if (existing is null)
        {
            _timers.Add(new ActiveTimer(spell, target, start, confirmed, charm));
            return;
        }

        existing.Spell = spell;
        existing.Start = start;
        existing.State = TimerState.Running;
        existing.EndedAt = null;
        existing.TargetConfirmed |= confirmed;
        existing.IsCharm |= charm;
    }

    private void Prune(DateTime now)
    {
        _timers.RemoveAll(t =>
        {
            if (t.EndedAt is DateTime ended)
            {
                return now - ended > EndedDisplayTime;
            }

            double? duration = Store.GetDuration(t.Spell);
            return duration is double d
                ? now - t.Start > TimeSpan.FromSeconds(d) + ExpiredGrace
                : now - t.Start > UnknownDurationLimit;
        });
    }

    private static bool SameTimer(ActiveTimer t, string spell, string target)
    {
        return string.Equals(t.Target, target, StringComparison.OrdinalIgnoreCase) && SpellNames.SameBase(t.Spell, spell);
    }

    private sealed class Pending(string spell, DateTime time)
    {
        public string Spell { get; } = spell;

        public DateTime Time { get; } = time;

        public bool Bound
        {
            get; set;
        }
    }

    private sealed class ActiveTimer(string spell, string target, DateTime start, bool confirmed, bool charm)
    {
        public string Spell { get; set; } = spell;

        public string Target { get; set; } = target;

        public DateTime Start { get; set; } = start;

        public bool TargetConfirmed { get; set; } = confirmed;

        public bool IsCharm { get; set; } = charm;

        public TimerState State { get; set; } = TimerState.Running;

        public DateTime? EndedAt
        {
            get; set;
        }
    }
}
