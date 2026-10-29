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
    long ChatVersion,
    WhoLoadoutEvent? CurrentLoadout = null);

/// <summary>
/// Parses log lines and feeds every tracker. All public members are thread-safe:
/// the tailer thread calls <see cref="ProcessLines"/> while the UI calls <see cref="Snapshot"/>.
/// </summary>
public sealed class LogEngine
{
    public event Action<LootEvent, long>? LootObserved;
    public event Action<ConsiderEvent, string>? MobConsidered;
    public event Action<ConsiderAssessmentEvent>? ConsiderAssessment;
    public event Action<string>? SpellObserved;
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
    private readonly MobResistanceStore? _spellMetadata;
    private readonly HashSet<string> _durationSpells = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string? Text, double? Seconds)> _wikiDurationSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly SpawnTracker _spawns;
    private readonly MoteTracker _motes;
    private readonly MobResistanceTracker? _resistances;
    private readonly NpcInteractionTracker? _npcs;
    private long _lines;
    private DateTime? _lastLogTime;
    private WhoLoadoutEvent? _loadout;

    public LogEngine(SpellDurationStore? durations = null, SpawnStore? spawns = null, MoteStore? motes = null,
        MobResistanceStore? resistances = null, string? logIdentity = null, NpcCompendium? npcs = null)
    {
        _motes = new MoteTracker(motes ?? new MoteStore());
        _timers = new SpellTimerTracker(durations ?? new SpellDurationStore());
        _spellMetadata = resistances;
        _spawns = new SpawnTracker(spawns ?? new SpawnStore());
        if (resistances is not null && logIdentity is not null)
        {
            _resistances = new MobResistanceTracker(resistances, logIdentity);
        }
        if (npcs is not null && logIdentity is not null)
        {
            _npcs = new NpcInteractionTracker(npcs, logIdentity);
        }
    }

    /// <summary>When true, session statistics reset each time the log shows a new login.</summary>
    public bool ResetOnLogin { get; set; } = true;

    public string CharacterName
    {
        get
        {
            lock (_gate)
            {
                return _parser.CharacterName;
            }
        }
        set
        {
            lock (_gate)
            {
                if (!string.Equals(_parser.CharacterName, value, StringComparison.OrdinalIgnoreCase))
                {
                    _loadout = null;
                }
                _parser.CharacterName = value;
            }
        }
    }

    public TimeSpan FightTimeout
    {
        get
        {
            lock (_gate)
            {
                return _fights.FightTimeout;
            }
        }
        set
        {
            lock (_gate)
            {
                _fights.FightTimeout = value;
            }
        }
    }

    public DateTime? LastLogTime
    {
        get
        {
            lock (_gate)
            {
                return _lastLogTime;
            }
        }
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
        string? spell = ev switch
        {
            CastBeginEvent cast => cast.Spell,
            SpellResistedEvent resisted => resisted.Spell,
            SpellWornOffEvent worn => worn.Spell,
            DamageEvent { Kind: DamageKind.DirectSpell or DamageKind.DamageOverTime } damage => damage.Source,
            HealEvent heal => heal.Spell,
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(spell))
        {
            SpellObserved?.Invoke(spell);
        }
        switch (ev)
        {
            case WhoLoadoutEvent who:
                _loadout = who;
                break;
            case LevelEvent level when _loadout is not null:
                _loadout = _loadout with
                {
                    Level = level.Level
                };
                break;
            case LoginEvent:
                _loadout = null;
                break;
        }
        if (ev is not null)
        {
            _spawns.OnEvent(ev);
            _motes.OnEvent(ev, MoteCharacter);
            if (_resistances is not null)
            {
                _resistances.CharacterName = _parser.CharacterName;
                _resistances.DamageProfile = _loadout is null ? "" :
                    $"{_parser.CharacterName}\n{_loadout.Classes}\n{_loadout.Level}";
            }
            _resistances?.OnEvent(ev, _lines);
            _npcs?.OnEvent(ev, _lines, _parser.CharacterName, _loadout);
        }
        else
        {
            _resistances?.OnUnparsedLine(line.Timestamp, line.Message);
            _npcs?.OnUnparsedLine(line, _lines, _parser.CharacterName, _loadout);
        }

        switch (ev)
        {
            case ConsiderEvent con:
                MobConsidered?.Invoke(con, _session.Snapshot(line.Timestamp, 0).Zone ?? "(unknown zone)");
                break;
            case ConsiderAssessmentEvent assessment:
                ConsiderAssessment?.Invoke(assessment);
                break;
            case DamageEvent d:
                _fights.OnDamage(d);
                _taken.OnDamage(d);
                _timers.OnDamage(d);
                break;
            case HealEvent h:
                _heals.OnHeal(h);
                break;
            case CastBeginEvent c:
                _durationSpells.Add(c.Spell);
                UpdateWikiSpellDuration(c.Spell);
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

    public EngineSnapshot Snapshot(DateTime now, int topRows = 12, bool advanceNpcIdle = true)
    {
        lock (_gate)
        {
            UpdateWikiSpellDurations();
            _resistances?.Advance(now);
            if (advanceNpcIdle)
            {
                _npcs?.Advance(now);
            }
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
                _chat.Version,
                _loadout);
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
            UpdateWikiSpellDurations();
            return _timers.Store.List();
        }
    }

    private void UpdateWikiSpellDurations()
    {
        foreach (string spell in _durationSpells)
        {
            UpdateWikiSpellDuration(spell);
        }
    }

    private void UpdateWikiSpellDuration(string spell)
    {
        if (_spellMetadata?.GetSpell(spell) is { } info)
        {
            if (!_wikiDurationSources.TryGetValue(spell, out var cached) || cached.Text != info.Duration)
            {
                cached = (info.Duration, SpellDurationStore.TryParseWikiDuration(info.Duration, out double seconds) ? seconds : null);
                _wikiDurationSources[spell] = cached;
            }
            _timers.Store.SetWikiDuration(spell, cached.Seconds);
        }
    }

    /// <summary>Removes a spell's duration and active timers until it is manually added again.</summary>
    public void RemoveSpellDuration(string spell)
    {
        lock (_gate)
        {
            _timers.Store.Remove(spell);
            _timers.RemoveSpell(spell);
        }
    }

    /// <summary>Clears the learned duration so it is measured again from wear-offs after the latest log line.</summary>
    public void RelearnSpellDuration(string spell)
    {
        lock (_gate)
        {
            _timers.Store.Relearn(spell, _lastLogTime ?? DateTime.Now);
        }
    }

    /// <summary>Clears the learned respawn time so it is measured again from sightings after the latest log line.</summary>
    public void RelearnSpawn(string zone, string mob)
    {
        lock (_gate)
        {
            _spawns.Store.Relearn(zone, mob, _lastLogTime ?? DateTime.Now);
        }
    }

    public void SetSpellDurationOverride(string spell, double? seconds)
    {
        lock (_gate)
        {
            _timers.Store.SetOverride(spell, seconds);
        }
    }

    public void SetSpellTracked(string spell, bool tracked)
    {
        lock (_gate)
        {
            _timers.Store.SetTracked(spell, tracked);
        }
    }

    public Task SaveSpellDurationsAsync(string path, bool onlyIfDirty = true, CancellationToken ct = default)
    {
        return SnapshotStore.SaveAsync(_timers.Store, _gate, path, onlyIfDirty, ct);
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
        get
        {
            lock (_gate)
            {
                return _spawns.Zone;
            }
        }
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

    public void SetSpawnWikiRespawn(string mob, double? seconds)
    {
        lock (_gate)
        {
            _spawns.Store.SetWikiRespawn(mob, seconds);
        }
    }

    public Task SaveSpawnsAsync(string path, bool onlyIfDirty = true, CancellationToken ct = default)
    {
        return SnapshotStore.SaveAsync(_spawns.Store, _gate, path, onlyIfDirty, ct);
    }

    private string MoteCharacter
    {
        get
        {
            return _parser.CharacterName.Length > 0 ? _parser.CharacterName : "Unknown";
        }
    }

    public Task SaveMotesAsync(string path, bool onlyIfDirty = true, CancellationToken ct = default)
    {
        return SnapshotStore.SaveAsync(_motes.Store, _gate, path, onlyIfDirty, ct);
    }
}