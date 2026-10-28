using EqLog.Core.Parsing;

namespace EqLog.Core.Tracking;

/// <summary>Supplies parsing context to persistent encounters without counting DoT ticks as spell landings.</summary>
public sealed class NpcInteractionTracker(NpcCompendium store, string source)
{
    private string _zone = "(unknown zone)";
    private CastBeginEvent? _cast;
    private string? _castProfile;
    private readonly HashSet<string> _targets = new(StringComparer.OrdinalIgnoreCase);

    public void OnEvent(LogEvent ev, long line, string character, WhoLoadoutEvent? loadout)
    {
        bool landed = false;
        string profile = $"{character}\n{loadout?.Classes}\n{loadout?.Level}";
        switch (ev)
        {
            case ZoneEvent zone:
                _zone = zone.Zone;
                _cast = null;
                break;
            case LoginEvent:
            case LevelEvent:
            case CastInterruptedEvent:
            case CastFizzleEvent:
                _cast = null;
                break;
            case WhoLoadoutEvent when _castProfile != profile:
                _cast = null;
                break;
            case CastBeginEvent cast:
                _cast = cast;
                _castProfile = profile;
                _targets.Clear();
                break;
            case SpellResistedEvent { Target: not null } resist:
                if (_cast is not null && SpellNames.SameBase(_cast.Spell, resist.Spell))
                {
                    _targets.Add(resist.Target);
                }
                break;
            case DamageEvent { Attacker: Names.You, Kind: DamageKind.DirectSpell, Source: not null, Amount: > 0 } damage:
                landed = _cast is not null && damage.Time >= _cast.Time &&
                    damage.Time - _cast.Time <= TimeSpan.FromSeconds(20) &&
                    SpellNames.SameBase(_cast.Spell, damage.Source) && _targets.Add(damage.Target);
                break;
        }
        store.Process(source, line, ev, character, loadout, _zone, landed);
    }

    public void OnUnparsedLine(LogLine line, long index, string character, WhoLoadoutEvent? loadout)
    {
        if (line.Message.EndsWith(" died.", StringComparison.Ordinal))
        {
            string victim = line.Message[..^" died.".Length];
            if (store.HasActiveEncounter(victim, _zone))
            {
                OnEvent(new KillEvent(line.Timestamp, null, victim), index, character, loadout);
                return;
            }
        }
        Advance(line.Timestamp);
    }

    public void Advance(DateTime time)
    {
        store.Advance(source, time);
    }
}
