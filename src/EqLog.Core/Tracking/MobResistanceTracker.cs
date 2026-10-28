using EqLog.Core.Parsing;

namespace EqLog.Core.Tracking;

/// <summary>Counts explicit personal resists and confirmed cast outcomes; never treats each DoT tick as a cast.</summary>
public sealed class MobResistanceTracker(MobResistanceStore store, string logIdentity)
{
    private string _zone = "(unknown zone)";
    private CastBeginEvent? _cast;
    private readonly HashSet<string> _outcomes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PendingOutcome> _pending = [];
    private static readonly TimeSpan DeathWindow = TimeSpan.FromSeconds(3);

    public string CharacterName { get; set; } = "";
    /// <summary>Separate learning context; empty disables learning until the current loadout is known.</summary>
    public string? DamageProfile
    {
        get; set;
    }
    private string? _castProfile;

    public void OnUnparsedLine(DateTime time, string message)
    {
        // The general parser avoids ambiguous single-word "X died." lines; a pending target provides context here.
        if (message.EndsWith(" died.", StringComparison.Ordinal))
        {
            string victim = message[..^" died.".Length];
            OnEvent(new KillEvent(time, null, victim), 0);
            return;
        }
        Advance(time);
    }

    public void OnEvent(LogEvent ev, long line)
    {
        if (ev is KillEvent kill)
        {
            var finishing = _pending.LastOrDefault(p => p.Damage is not null &&
                string.Equals(p.Target, kill.Victim, StringComparison.OrdinalIgnoreCase) &&
                kill.Time >= p.Time && kill.Time - p.Time <= DeathWindow);
            if (finishing is not null)
            {
                _pending.Remove(finishing);
            }
        }
        Advance(ev.Time);
        switch (ev)
        {
            case ZoneEvent zone:
                _zone = zone.Zone;
                _cast = null;
                _outcomes.Clear();
                break;
            case LoginEvent:
            case LevelEvent:
                _cast = null;
                _outcomes.Clear();
                break;
            case WhoLoadoutEvent when _castProfile != (DamageProfile ?? CharacterName):
                _cast = null;
                _outcomes.Clear();
                break;
            case CastBeginEvent cast:
                _cast = cast;
                _castProfile = DamageProfile ?? CharacterName;
                _outcomes.Clear();
                break;
            case CastInterruptedEvent:
            case CastFizzleEvent:
                _cast = null;
                break;
            case SpellResistedEvent { Target: not null } resist when resist.Target != Names.You:
                _pending.Add(new(line, _zone, resist.Target, resist.Spell, resist.Time, true, null, CharacterName));
                if (_cast is not null && SpellNames.SameBase(_cast.Spell, resist.Spell))
                {
                    _outcomes.Add(resist.Target);
                }
                break;
            case DamageEvent { Attacker: Names.You, Source: not null } damage
                when damage.Target != Names.You && damage.Kind == DamageKind.DirectSpell:
                Landed(damage, line);
                break;
        }
    }

    public void Advance(DateTime time)
    {
        while (_pending.Count > 0 && time - _pending[0].Time > DeathWindow)
        {
            var outcome = _pending[0];
            _pending.RemoveAt(0);
            store.Record(logIdentity, outcome.Line, outcome.Zone, outcome.Target, outcome.Spell, outcome.Resisted,
                outcome.Time, outcome.Character, outcome.Damage);
        }
    }

    private void Landed(DamageEvent damage, long line)
    {
        if (_cast is null || damage.Time < _cast.Time || damage.Time - _cast.Time > TimeSpan.FromSeconds(20) ||
            !SpellNames.SameBase(damage.Source!, _cast.Spell) || !_outcomes.Add(damage.Target) ||
            damage.IsCritical || damage.Modifier is not null || damage.Amount <= 0 || string.IsNullOrWhiteSpace(_castProfile))
        {
            return;
        }
        _pending.Add(new(line, _zone, damage.Target, _cast.Spell, damage.Time, false, damage.Amount, _castProfile));
    }

    private sealed record PendingOutcome(long Line, string Zone, string Target, string Spell, DateTime Time,
        bool Resisted, long? Damage, string Character);
}
