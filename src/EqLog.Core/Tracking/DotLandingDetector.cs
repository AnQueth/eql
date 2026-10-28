using EqLog.Core.Parsing;

namespace EqLog.Core.Tracking;

/// <summary>
/// Decides when your damage-over-time tick proves a cast landed: the first tick of a recently cast spell on each
/// target. DoTs land fully or are fully resisted, so one landing per cast and target is a fair outcome. Targets the
/// spell was already ticking on when cast are skipped, since their ticks could come from the previous cast.
/// </summary>
public sealed class DotLandingDetector
{
    private static readonly TimeSpan CastWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AlreadyTicking = TimeSpan.FromSeconds(8);

    private readonly Dictionary<string, RecentCast> _casts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Spell, string Target), DateTime> _lastTick = [];
    private string? _lastCastBase;

    public void OnCast(CastBeginEvent cast)
    {
        string spell = SpellNames.BaseName(cast.Spell);
        var ticking = _lastTick
            .Where(kv => string.Equals(kv.Key.Spell, spell, StringComparison.OrdinalIgnoreCase) && cast.Time - kv.Value <= AlreadyTicking)
            .Select(kv => kv.Key.Target);
        _casts[spell] = new RecentCast(cast.Spell, cast.Time, new HashSet<string>(ticking, StringComparer.OrdinalIgnoreCase));
        _lastCastBase = spell;
    }

    /// <summary>An interrupted or fizzled cast can't land; null means the latest cast.</summary>
    public void OnCastFailed(string? spell)
    {
        string? key = spell is null ? _lastCastBase : SpellNames.BaseName(spell);
        if (key is not null)
        {
            _casts.Remove(key);
        }
    }

    /// <summary>True when this resist is the assessable outcome of a recent cast on that target.</summary>
    public bool OnResisted(SpellResistedEvent resist)
    {
        return resist.Target is not null && Claim(resist.Spell, resist.Target, resist.Time) is not null;
    }

    /// <summary>Returns the cast spell when this tick is the first proof that a recent cast landed on the target.</summary>
    public string? OnTick(DamageEvent tick)
    {
        if (tick is not { Attacker: Names.You, Kind: DamageKind.DamageOverTime, Source: not null, Amount: > 0 } || tick.Target == Names.You)
        {
            return null;
        }

        string? spell = Claim(tick.Source, tick.Target, tick.Time);
        _lastTick[(SpellNames.BaseName(tick.Source), tick.Target)] = tick.Time;
        if (_lastTick.Count > 512)
        {
            foreach (var stale in _lastTick.Where(kv => tick.Time - kv.Value > CastWindow).Select(kv => kv.Key).ToList())
            {
                _lastTick.Remove(stale);
            }
        }

        return spell;
    }

    public void Clear()
    {
        _casts.Clear();
        _lastTick.Clear();
        _lastCastBase = null;
    }

    private string? Claim(string spell, string target, DateTime time)
    {
        if (!_casts.TryGetValue(SpellNames.BaseName(spell), out var cast) ||
            time < cast.Time || time - cast.Time > CastWindow ||
            cast.Skipped.Contains(target))
        {
            return null;
        }

        // Once a target has an outcome for this cast, later ticks or resists aren't new outcomes.
        cast.Skipped.Add(target);
        return cast.Spell;
    }

    private sealed record RecentCast(string Spell, DateTime Time, HashSet<string> Skipped);
}
