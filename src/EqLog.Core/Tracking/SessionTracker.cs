using EqLog.Core.Parsing;

namespace EqLog.Core.Tracking;

public sealed record KillRecord(DateTime Time, string Victim, string? Killer);

public sealed record SessionSnapshot(
    string? Zone,
    DateTime? ZoneEnteredAt,
    DateTime? SessionStart,
    int YourKills,
    int Deaths,
    string? LastKilledBy,
    int ExperienceGains,
    double ExperiencePercent,
    int PartyExperienceGains,
    int? Level,
    int LevelsGained,
    bool AaCapped,
    IReadOnlyList<KillRecord> RecentKills)
{
    public double ExperiencePercentPerHour(DateTime now) =>
        SessionStart is DateTime s && now - s > TimeSpan.FromMinutes(1) ? ExperiencePercent / (now - s).TotalHours : 0;
}

/// <summary>Zone, kills, deaths, experience and levels for the current play session.</summary>
public sealed class SessionTracker
{
    private const int MaxKills = 40;
    private readonly List<KillRecord> _kills = [];
    private DateTime? _lastAaCap;
    private string? _zone;
    private DateTime? _zoneAt;
    private DateTime? _sessionStart;
    private int _yourKills;
    private int _deaths;
    private string? _lastKilledBy;
    private int _xpGains;
    private double _xpPercent;
    private int _partyXp;
    private int? _level;
    private int _levelsGained;

    public void OnFirstLine(DateTime t) => _sessionStart ??= t;

    public void OnZone(ZoneEvent e)
    {
        _zone = e.Zone;
        _zoneAt = e.Time;
    }

    public void OnKill(KillEvent e)
    {
        if (e.Victim == Names.You)
        {
            _deaths++;
            _lastKilledBy = e.Killer;
            return;
        }

        if (e.Killer == Names.You)
        {
            _yourKills++;
        }

        _kills.Add(new KillRecord(e.Time, e.Victim, e.Killer));
        if (_kills.Count > MaxKills)
        {
            _kills.RemoveAt(0);
        }
    }

    public void OnExperience(ExperienceEvent e)
    {
        _xpGains++;
        if (e.Kind is ExperienceKind.Party or ExperienceKind.Raid) _partyXp++;
        if (e.Percent is double p) _xpPercent += p;
    }

    public void OnAaCap(AaCapEvent e) => _lastAaCap = e.Time;

    public void OnLevel(LevelEvent e)
    {
        _level = e.Level;
        _levelsGained++;
    }

    /// <summary>Starts a new session at <paramref name="t"/>; zone and level are kept.</summary>
    public void ResetSession(DateTime t)
    {
        _kills.Clear();
        _sessionStart = t;
        _yourKills = _deaths = _xpGains = _partyXp = _levelsGained = 0;
        _xpPercent = 0;
        _lastKilledBy = null;
        _lastAaCap = null;
    }

    public SessionSnapshot Snapshot(DateTime now, int recentKills) => new(
        _zone,
        _zoneAt,
        _sessionStart,
        _yourKills,
        _deaths,
        _lastKilledBy,
        _xpGains,
        _xpPercent,
        _partyXp,
        _level,
        _levelsGained,
        _lastAaCap is DateTime cap && now - cap < TimeSpan.FromMinutes(30),
        _kills.AsEnumerable().Reverse().Take(recentKills).ToList());
}
