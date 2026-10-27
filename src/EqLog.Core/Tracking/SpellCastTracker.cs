using EqLog.Core.Parsing;

namespace EqLog.Core.Tracking;

public sealed record SpellStats(string Spell, int Casts, int Interrupts, int Resists, DateTime LastCast);

public sealed record SpellCastSnapshot(int TotalCasts, int TotalInterrupts, int TotalResists, int Fizzles, IReadOnlyList<SpellStats> Spells);

/// <summary>Counts spells you cast, with interrupts, resists and fizzles.</summary>
public sealed class SpellCastTracker
{
    private static readonly TimeSpan AttributionWindow = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, Entry> _spells = new(StringComparer.OrdinalIgnoreCase);
    private (string Spell, DateTime Time)? _lastCast;
    private int _casts, _interrupts, _resists, _fizzles;

    public void OnCast(CastBeginEvent e)
    {
        Get(e.Spell).Casts++;
        Get(e.Spell).LastCast = e.Time;
        _lastCast = (e.Spell, e.Time);
        _casts++;
    }

    public void OnInterrupted(CastInterruptedEvent e)
    {
        string? spell = e.Spell ?? Recent(e.Time);
        if (spell is not null)
        {
            Get(spell).Interrupts++;
        }

        _interrupts++;
    }

    public void OnFizzle(CastFizzleEvent e)
    {
        string? spell = Recent(e.Time);
        if (spell is not null)
        {
            Get(spell).Interrupts++;
        }

        _fizzles++;
    }

    public void OnResisted(SpellResistedEvent e)
    {
        string spell = _spells.ContainsKey(e.Spell) ? e.Spell : SpellNames.FindByBase(_spells.Keys, e.Spell) ?? e.Spell;
        Get(spell).Resists++;
        _resists++;
    }

    public void ResetSession()
    {
        _spells.Clear();
        _lastCast = null;
        _casts = _interrupts = _resists = _fizzles = 0;
    }

    public SpellCastSnapshot Snapshot(int top)
    {
        return new(
        _casts,
        _interrupts,
        _resists,
        _fizzles,
        _spells.Values.OrderByDescending(s => s.Casts).Take(top)
            .Select(s => new SpellStats(s.Spell, s.Casts, s.Interrupts, s.Resists, s.LastCast)).ToList());
    }

    private string? Recent(DateTime t)
    {
        return _lastCast is { } lc && t - lc.Time <= AttributionWindow ? lc.Spell : null;
    }

    private Entry Get(string spell)
    {
        if (!_spells.TryGetValue(spell, out var entry))
        {
            entry = new Entry(spell);
            _spells[spell] = entry;
        }

        return entry;
    }

    private sealed class Entry(string spell)
    {
        public string Spell { get; } = spell;

        public int Casts
        {
            get; set;
        }

        public int Interrupts
        {
            get; set;
        }

        public int Resists
        {
            get; set;
        }

        public DateTime LastCast
        {
            get; set;
        }
    }
}
