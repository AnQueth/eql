using System.Text.RegularExpressions;
using EqLog.Core.Parsing;

namespace EqLog.Core.Tracking;

public sealed record MoteDrop(DateTime Time, string Looter, string Tier, int Quantity, string Source);

public sealed record MoteSnapshot(
    string? Zone,
    DateTime? SessionStart,
    IReadOnlyList<MoteCount> Session,
    int SessionTotal,
    IReadOnlyList<MoteCount> AllTime,
    int AllTimeTotal,
    IReadOnlyList<MoteDrop> Recent,
    IReadOnlyList<MoteMobRate> ZoneRates)
{
    public double PerHour(DateTime now)
    {
        return SessionStart is DateTime s && now - s > TimeSpan.FromMinutes(1) ? SessionTotal / (now - s).TotalHours : 0;
    }
}

/// <summary>Mote tiers, taken from the item name ("Mote of X Potential"); no fixed list of tiers is assumed.</summary>
public static partial class MoteTiers
{
    /// <summary>"Mote of Major Potential" gives "Major".</summary>
    public static string? TierOf(string item)
    {
        var m = MoteRegex().Match(item);
        return m.Success ? m.Groups["t"].Value : null;
    }

    /// <summary>Tiers with a count, most collected first.</summary>
    public static IReadOnlyList<MoteCount> Sort(IReadOnlyDictionary<string, int> counts)
    {
        return counts.Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new MoteCount(kv.Key, kv.Value))
            .ToList();
    }

    [GeneratedRegex(@"^Motes? of (?<t>.+?) Potential$", RegexOptions.IgnoreCase)]
    private static partial Regex MoteRegex();
}

/// <summary>
/// Mote loot: per-tier counts for the session and all time, recent drops, and which mobs in the current zone drop them.
/// </summary>
public sealed class MoteTracker
{
    private const int MaxRecent = 30;
    private readonly Dictionary<string, int> _session = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MoteDrop> _recent = [];
    private string? _zone;
    private DateTime? _sessionStart;
    private DateTime _claimTime;
    private int _claimIndex;
    private (string Victim, DateTime Time)? _lastKill;

    public MoteTracker(MoteStore store)
    {
        Store = store;
    }

    public MoteStore Store
    {
        get;
    }

    public void OnFirstLine(DateTime t)
    {
        _sessionStart ??= t;
    }

    public void OnEvent(LogEvent e, string character)
    {
        switch (e)
        {
            case ZoneEvent z:
                _zone = z.Zone;
                break;
            case KillEvent k:
                OnKill(k, character);
                break;
            case LootEvent l when MoteTiers.TierOf(l.Item) is string tier:
                OnMote(l, tier, character);
                break;
        }
    }

    private void OnKill(KillEvent k, string character)
    {
        if (_zone is null || !IsCreature(k.Victim) || _lastKill == (k.Victim, k.Time))
        {
            return;
        }

        _lastKill = (k.Victim, k.Time);
        if (Claim(k.Time, character))
        {
            Store.AddKill(_zone, k.Victim);
        }
    }

    private void OnMote(LootEvent l, string tier, string character)
    {
        string source = l.Corpse ?? "?";
        if (l.Looter == Names.You)
        {
            _session[tier] = _session.GetValueOrDefault(tier) + l.Quantity;
        }

        _recent.Add(new MoteDrop(l.Time, l.Looter, tier, l.Quantity, source));
        if (_recent.Count > MaxRecent)
        {
            _recent.RemoveAt(0);
        }

        if (!Claim(l.Time, character))
        {
            return;
        }

        if (l.Looter == Names.You)
        {
            Store.AddMote(character, tier, l.Quantity);
        }

        // Only corpses of mobs killed here count toward drop rates (not reward chests).
        if (_zone is not null && Store.HasKills(_zone, source))
        {
            Store.AddDrop(_zone, source, tier, l.Quantity);
        }
    }

    private bool Claim(DateTime t, string character)
    {
        if (t != _claimTime)
        {
            _claimTime = t;
            _claimIndex = 0;
        }

        _claimIndex++;
        return Store.TryClaim(character, t, _claimIndex);
    }

    private static bool IsCreature(string name)
    {
        return name is not (Names.You or Names.Self or Names.Unknown)
        && !name.EndsWith(" pet", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("`s ", StringComparison.Ordinal)
        && !name.Contains("'s ", StringComparison.Ordinal);
    }

    public void ResetSession(DateTime at)
    {
        _session.Clear();
        _sessionStart = at;
    }

    public MoteSnapshot Snapshot(string character, int recent)
    {
        var session = MoteTiers.Sort(_session);
        var allTime = Store.Totals(character);
        var rates = _zone is null
            ? []
            : Store.ZoneRates(_zone).OrderByDescending(r => r.Drops).ThenBy(r => r.Mob, StringComparer.OrdinalIgnoreCase).ToList();
        return new MoteSnapshot(
            _zone,
            _sessionStart,
            session,
            session.Sum(c => c.Count),
            allTime,
            allTime.Sum(c => c.Count),
            _recent.AsEnumerable().Reverse().Take(recent).ToList(),
            rates);
    }
}
