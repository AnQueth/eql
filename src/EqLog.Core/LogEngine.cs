using EqLog.Core.Parsing;
using EqLog.Core.Tracking;

namespace EqLog.Core;

public sealed record EngineSnapshot(
    DateTime Now,
    string CharacterName,
    long LinesProcessed,
    DateTime? LastLogTime,
    FightSnapshot? CurrentFight,
    IReadOnlyList<FightSnapshot> RecentFights,
    long SessionDamageByYou,
    long SessionMaxHitByYou,
    string? SessionMaxHitSource,
    DamageTakenSnapshot DamageTaken,
    HealSnapshot Heals,
    SessionSnapshot Session,
    LootSnapshot Loot,
    SpellCastSnapshot Spells,
    IReadOnlyList<SpellTimerSnapshot> Timers,
    SpawnSnapshot Spawns,
    MoteSnapshot Motes,
    long ChatVersion);

/// <summary>
/// Parses log lines and feeds every tracker. All public members are thread-safe:
/// the tailer thread calls <see cref="ProcessLines"/> while the UI calls <see cref="Snapshot"/>.
/// </summary>
public sealed class LogEngine
{
    public event Action<LootEvent, long>? LootObserved;
    private readonly object _gate = new();
    private readonly LogEventParser _parser = new();
    private readonly FightTracker _fights = new();
    private readonly DamageTakenTracker _taken = new();
    private readonly HealTracker _heals = new();
    private readonly SessionTracker _session = new();
    private readonly LootTracker _loot = new();
    private readonly ChatTracker _chat = new();
    private readonly SpellCastTracker _spells = new();
    private readonly SpellTimerTracker _timers;
    private readonly SpawnTracker _spawns;
    private readonly MoteTracker _motes;
    private long _lines;
    private DateTime? _lastLogTime;

    public LogEngine(SpellDurationStore? durations = null, SpawnStore? spawns = null, MoteStore? motes = null)
    {
        _motes = new MoteTracker(motes ?? new MoteStore());
        _timers = new SpellTimerTracker(durations ?? new SpellDurationStore());
        _spawns = new SpawnTracker(spawns ?? new SpawnStore());
    }

    /// <summary>When true, session statistics reset each time the log shows a new login.</summary>
    public bool ResetOnLogin { get; set; } = true;

    public string CharacterName
    {
        get { lock (_gate) return _parser.CharacterName; }
        set { lock (_gate) _parser.CharacterName = value; }
    }

    public TimeSpan FightTimeout
    {
        get { lock (_gate) return _fights.FightTimeout; }
        set { lock (_gate) _fights.FightTimeout = value; }
    }

    public DateTime? LastLogTime
    {
        get { lock (_gate) return _lastLogTime; }
    }

    public void ProcessLines(IReadOnlyList<string> rawLines)
    {
        lock (_gate)
        {
            foreach (string raw in rawLines)
            {
                if (LogLine.TryParse(raw, out var line))
                {
                    ProcessCore(line);
                }
            }
        }
    }

    public void Process(LogLine line)
    {
        lock (_gate)
        {
            ProcessCore(line);
        }
    }

    private void ProcessCore(LogLine line)
    {
        _lines++;
        _lastLogTime = line.Timestamp;
        _session.OnFirstLine(line.Timestamp);
        _motes.OnFirstLine(line.Timestamp);

        var ev = _parser.Parse(line);
        if (ev is not null)
        {
            _spawns.OnEvent(ev);
            _motes.OnEvent(ev, MoteCharacter);
        }

        switch (ev)
        {
            case DamageEvent d:
                _fights.OnDamage(d);
                _taken.OnDamage(d);
                _timers.OnDamage(d);
                break;
            case HealEvent h:
                _heals.OnHeal(h);
                break;
            case CastBeginEvent c:
                _spells.OnCast(c);
                _timers.OnCast(c);
                break;
            case CastInterruptedEvent i:
                _spells.OnInterrupted(i);
                _timers.OnInterruptedOrFizzled(i.Spell);
                break;
            case CastFizzleEvent f:
                _spells.OnFizzle(f);
                _timers.OnInterruptedOrFizzled(null);
                break;
            case SpellResistedEvent r:
                _spells.OnResisted(r);
                _timers.OnResisted(r);
                break;
            case SpellWornOffEvent w:
                _timers.OnWornOff(w);
                break;
            case EffectLandedEvent el:
                _timers.OnEffectLanded(el);
                break;
            case AwakenedEvent a:
                _timers.OnAwakened(a);
                break;
            case KillEvent k:
                _fights.OnKill(k);
                _session.OnKill(k);
                _timers.OnKill(k);
                break;
            case ExperienceEvent x:
                _session.OnExperience(x);
                break;
            case AaCapEvent cap:
                _session.OnAaCap(cap);
                break;
            case LevelEvent lv:
                _session.OnLevel(lv);
                break;
            case LootEvent l:
                _loot.OnLoot(l);
                LootObserved?.Invoke(l, _lines);
                break;
            case CoinEvent coin:
                _loot.OnCoin(coin);
                break;
            case ChatEvent chat:
                _chat.OnChat(chat);
                break;
            case ZoneEvent z:
                _session.OnZone(z);
                _timers.Clear();
                break;
            case LoginEvent login:
                _timers.Clear();
                if (ResetOnLogin)
                {
                    ResetSessionCore(login.Time);
                }

                break;
        }
    }

    public void ResetSession()
    {
        lock (_gate)
        {
            ResetSessionCore(_lastLogTime ?? DateTime.Now);
        }
    }

    private void ResetSessionCore(DateTime at)
    {
        _fights.ResetSession();
        _taken.ResetSession();
        _heals.ResetSession();
        _session.ResetSession(at);
        _motes.ResetSession(at);
        _loot.ResetSession();
        _spells.ResetSession();
    }

    public EngineSnapshot Snapshot(DateTime now, int topRows = 12)
    {
        lock (_gate)
        {
            _fights.Expire(now);
            return new EngineSnapshot(
                now,
                _parser.CharacterName,
                _lines,
                _lastLogTime,
                _fights.Current(),
                _fights.Recent(8),
                _fights.SessionDamageByYou,
                _fights.SessionMaxHitByYou,
                _fights.SessionMaxHitSource,
                _taken.Snapshot(topRows),
                _heals.Snapshot(now, topRows),
                _session.Snapshot(now, topRows),
                _loot.Snapshot(30, topRows),
                _spells.Snapshot(topRows),
                _timers.Snapshot(now),
                _spawns.Snapshot(now),
                _motes.Snapshot(MoteCharacter, 10),
                _chat.Version);
        }
    }

    public IReadOnlyList<ChatEvent> RecentChat(int count, ISet<ChatChannel>? channels)
    {
        lock (_gate)
        {
            return _chat.Recent(count, channels);
        }
    }

    public IReadOnlyList<SpellDurationInfo> SpellDurations()
    {
        lock (_gate)
        {
            return _timers.Store.List();
        }
    }

    public void SetSpellDurationOverride(string spell, double? seconds)
    {
        lock (_gate)
        {
            _timers.Store.SetOverride(spell, seconds);
        }
    }

    public void SaveSpellDurations(string path, bool onlyIfDirty = true)
    {
        lock (_gate)
        {
            if (!onlyIfDirty || _timers.Store.IsDirty)
            {
                _timers.Store.Save(path);
            }
        }
    }
    public IReadOnlyList<SpawnCandidate> SpawnCandidates()
    {
        lock (_gate)
        {
            return _spawns.Candidates();
        }
    }

    public string? SpawnZone
    {
        get { lock (_gate) return _spawns.Zone; }
    }

    public void SetSpawnTracked(string zone, string mob, bool tracked)
    {
        lock (_gate)
        {
            _spawns.Store.SetTracked(zone, mob, tracked);
        }
    }

    public void SetSpawnOverride(string zone, string mob, double? seconds)
    {
        lock (_gate)
        {
            _spawns.Store.SetOverride(zone, mob, seconds);
        }
    }

    public void SaveSpawns(string path, bool onlyIfDirty = true)
    {
        lock (_gate)
        {
            if (!onlyIfDirty || _spawns.Store.IsDirty)
            {
                _spawns.Store.Save(path);
            }
        }
    }

    private string MoteCharacter => _parser.CharacterName.Length > 0 ? _parser.CharacterName : "Unknown";

    public void SaveMotes(string path, bool onlyIfDirty = true)
    {
        lock (_gate)
        {
            if (!onlyIfDirty || _motes.Store.IsDirty)
            {
                _motes.Store.Save(path);
            }
        }
    }
}