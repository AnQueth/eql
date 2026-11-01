using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EqLog.Core.Parsing;

namespace EqLog.Core;

public sealed record NpcSpellHistory(string Spell, long Damage, long Landings, long Resists);
public sealed record NpcCatalogEntry(string Mob, string Zone, DateTime FirstSeen, DateTime LastSeen, long Considers);
public sealed record NpcEncounter(string Id, DateTime Start, DateTime LastActivity, string Character,
    string? Classes, int? Level, string Outcome, long YourDamage, long OtherDamage, long IncomingDamage,
    IReadOnlyList<NpcSpellHistory> Spells)
{
    public double Seconds
    {
        get
        {
            return Math.Max(1, (LastActivity - Start).TotalSeconds + 1);
        }
    }

    public double YourDps
    {
        get
        {
            return YourDamage / Seconds;
        }
    }
}

public sealed record NpcEntry(string Mob, string Zone, DateTime FirstSeen, DateTime LastSeen,
    long Considers, ConsiderEvent? LastConsider, IReadOnlyList<NpcEncounter> Encounters, string? LastAssessment = null)
{
    public double YourAverageDps
    {
        get
        {
            double seconds = Encounters.Where(e => e.YourDamage > 0).Sum(e => e.Seconds);
            return seconds > 0 ? Encounters.Sum(e => e.YourDamage) / seconds : 0;
        }
    }

    public double OtherAverageDps
    {
        get
        {
            double seconds = Encounters.Sum(e => e.Seconds);
            return seconds > 0 ? Encounters.Sum(e => e.OtherDamage) / seconds : 0;
        }
    }
}

/// <summary>
/// Persistent, thread-safe NPC interaction summaries. Active encounters survive saves and replay.
/// Stored as a small index (NPC summaries, active encounters, replay watermarks) plus one append-only
/// JSON Lines history file per NPC, which is read only when that NPC is viewed.
/// </summary>
public sealed class NpcCompendium
{
    public const string IndexFileName = "index.json";
    public const string HistoryFolderName = "npcs";
    private const int CachedHistories = 8;

    private readonly object _gate = new();
    private Data _data = new();
    private long _version = 1;
    private long _savedVersion;
    private readonly Dictionary<string, List<EncounterData>> _active = [];
    private readonly Dictionary<string, Dictionary<string, EncounterData>> _histories = [];
    private readonly LinkedList<string> _historyOrder = new();

    public IReadOnlyList<NpcCatalogEntry> Search(string name)
    {
        lock (_gate)
        {
            return _data.Entries.Values.Where(e => e.Mob.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Mob, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Zone, StringComparer.OrdinalIgnoreCase)
                .Select(e => new NpcCatalogEntry(e.Mob, e.Zone, e.FirstSeen, e.LastSeen, e.Considers)).ToArray();
        }
    }

    /// <summary>Whether an encounter with the NPC is still open; active encounters are always in memory.</summary>
    public bool HasActiveEncounter(string mob, string zone)
    {
        lock (_gate)
        {
            return _data.Entries.TryGetValue(Key(mob, zone), out var entry) &&
                entry.Encounters.Any(e => e.Outcome == "Active");
        }
    }

    /// <summary>Returns the NPC and any history already supplied by its persistence repository.</summary>
    public NpcEntry? Get(string mob, string zone)
    {
        string key = Key(mob, zone);
        lock (_gate)
        {
            if (!_data.Entries.TryGetValue(key, out var entry))
            {
                return null;
            }
            if (TryCachedHistory(key, out var cached))
            {
                return Snapshot(entry, cached);
            }
            return Snapshot(entry, new Dictionary<string, EncounterData>());
        }
    }

    public Task<NpcEntry?> GetAsync(string mob, string zone, CancellationToken ct = default)
    {
        return Task.FromResult(Get(mob, zone));
    }

    /// <summary>Returns the history filename used by the repository for this NPC, if it is known.</summary>
    public string? HistoryFileFor(string mob, string zone)
    {
        lock (_gate)
        {
            string key = Key(mob, zone);
            return _data.Entries.TryGetValue(key, out var entry)
                ? entry.File ?? HistoryFileName(key)
                : null;
        }
    }

    /// <summary>Imports history records loaded by the repository into the bounded in-memory cache.</summary>
    public void ImportHistory(string mob, string zone, IEnumerable<EncounterRecord> records)
    {
        lock (_gate)
        {
            string key = Key(mob, zone);
            if (_data.Entries.ContainsKey(key))
            {
                CacheHistory(key, ToHistory(records));
            }
        }
    }

    public void Process(string source, long line, LogEvent ev, string character, WhoLoadoutEvent? loadout,
        string zone, bool confirmedLanding)
    {
        lock (_gate)
        {
            string sourceKey = source.ToUpperInvariant();
            var watermark = _data.Watermarks.GetValueOrDefault(sourceKey);
            if (watermark is not null && (watermark.Time > ev.Time ||
                (watermark.Time == ev.Time && watermark.Line >= line)))
            {
                return;
            }
            _data.Watermarks[sourceKey] = new(ev.Time, line);
            _version++;
            if (ev is ZoneEvent or LoginEvent)
            {
                CloseSource(sourceKey, ev is ZoneEvent ? "Zoned" : "Login");
                _data.LastConsiders.Remove(sourceKey);
                return;
            }
            if (ev is WhoLoadoutEvent or LevelEvent && loadout is not null)
            {
                foreach (var active in ActiveFor(sourceKey).Where(a =>
                    a.Classes != loadout.Classes || a.Level != loadout.Level))
                {
                    active.Outcome = "Loadout changed";
                }
                return;
            }
            if (ev is ConsiderEvent con)
            {
                var entry = Entry(con.Mob, zone, con.Time);
                entry.Considers++;
                if (entry.LastConsider is null || con.Time >= entry.LastConsider.Time)
                {
                    entry.LastConsider = con;
                    entry.LastAssessment = null;
                }
                _data.LastConsiders[sourceKey] = Key(con.Mob, zone);
                return;
            }
            if (ev is ConsiderAssessmentEvent assessment)
            {
                var previous = _data.LastConsiders.GetValueOrDefault(sourceKey);
                if (previous is not null && _data.Entries.TryGetValue(previous, out var entry) &&
                    entry.LastConsider is { } priorConsider && assessment.Time >= priorConsider.Time &&
                    assessment.Time - priorConsider.Time <= TimeSpan.FromSeconds(2))
                {
                    entry.LastAssessment = assessment.Text;
                }
                return;
            }
            if (ev is KillEvent kill)
            {
                if (kill.Victim == Names.You)
                {
                    foreach (var active in ActiveFor(sourceKey))
                    {
                        active.Outcome = "You died";
                        active.LastActivity = ev.Time;
                    }
                }
                else
                {
                    var entry = Entry(kill.Victim, zone, ev.Time);
                    var active = Active(entry, sourceKey);
                    if (active is not null)
                    {
                        active.Outcome = "NPC died";
                        active.LastActivity = ev.Time;
                    }
                }
                return;
            }
            if (ev is SpellResistedEvent { Target: not null } resist && resist.Target != Names.You)
            {
                var encounter = Encounter(resist.Target, zone, ev.Time, sourceKey, line, character, loadout);
                Spell(encounter, resist.Spell).Resists++;
                return;
            }
            if (ev is not DamageEvent damage || damage.Amount <= 0)
            {
                return;
            }
            bool incoming = damage.Target == Names.You && damage.Attacker != Names.You &&
                damage.Attacker != Names.Unknown;
            if (!incoming && (damage.Target == Names.You || damage.Target == damage.Attacker ||
                (damage.Attacker != Names.You && !damage.Target.Contains(' ') &&
                    !_data.Entries.ContainsKey(Key(damage.Target, zone)))))
            {
                return;
            }
            string mob = incoming ? damage.Attacker : damage.Target;
            var fight = Encounter(mob, zone, ev.Time, sourceKey, line, character, loadout);
            if (incoming)
            {
                fight.IncomingDamage += damage.Amount;
            }
            else if (damage.Attacker == Names.You)
            {
                fight.YourDamage += damage.Amount;
                var spell = Spell(fight, damage.Source ?? damage.Kind.ToString());
                spell.Damage += damage.Amount;
                if (confirmedLanding)
                {
                    spell.Landings++;
                }
            }
            else
            {
                // Logs cannot prove group membership; these are all other observed attackers.
                fight.OtherDamage += damage.Amount;
            }
        }
    }

    private IEnumerable<EncounterData> ActiveFor(string source)
    {
        if (!_active.TryGetValue(source, out var encounters))
        {
            return [];
        }
        encounters.RemoveAll(e => e.Outcome != "Active");
        return encounters;
    }

    private void CloseSource(string source, string outcome)
    {
        foreach (var active in ActiveFor(source))
        {
            active.Outcome = outcome;
        }
    }

    private static EncounterData? Active(EntryData entry, string source)
    {
        return entry.Encounters.LastOrDefault(e => e.Source == source && e.Outcome == "Active");
    }

    private EncounterData Encounter(string mob, string zone, DateTime time, string source, long line,
        string character, WhoLoadoutEvent? loadout)
    {
        var entry = Entry(mob, zone, time);
        var encounter = Active(entry, source);
        if (encounter is null)
        {
            encounter = new EncounterData
            {
                Id = $"{source}\n{time.Ticks}\n{line}",
                Source = source,
                Character = character,
                Classes = loadout?.Classes,
                Level = loadout?.Level,
                Start = time,
                LastActivity = time,
            };
            entry.Encounters.Add(encounter);
            if (!_active.TryGetValue(source, out var active))
            {
                active = [];
                _active[source] = active;
            }
            active.Add(encounter);
        }
        encounter.LastActivity = time > encounter.LastActivity ? time : encounter.LastActivity;
        return encounter;
    }

    private EntryData Entry(string mob, string zone, DateTime time)
    {
        string key = Key(mob, zone);
        if (!_data.Entries.TryGetValue(key, out var entry))
        {
            entry = new EntryData { Mob = mob, Zone = zone, FirstSeen = time };
            _data.Entries[key] = entry;
        }
        entry.LastSeen = time > entry.LastSeen ? time : entry.LastSeen;
        entry.FirstSeen = time < entry.FirstSeen ? time : entry.FirstSeen;
        return entry;
    }

    private static SpellData Spell(EncounterData encounter, string spell)
    {
        string key = MobResistanceStore.MobKey(spell);
        if (!encounter.Spells.TryGetValue(key, out var data))
        {
            data = new SpellData { Spell = spell };
            encounter.Spells[key] = data;
        }
        return data;
    }

    private static string Key(string mob, string zone)
    {
        return MobResistanceStore.MobKey(zone) + "\n" + MobResistanceStore.MobKey(mob);
    }

    private static NpcEntry Snapshot(EntryData entry, IReadOnlyDictionary<string, EncounterData> history)
    {
        var encounters = new Dictionary<string, EncounterData>(history);
        foreach (var encounter in entry.Encounters)
        {
            encounters[encounter.Id] = encounter;
        }
        return new(entry.Mob, entry.Zone, entry.FirstSeen, entry.LastSeen, entry.Considers, entry.LastConsider,
            encounters.Values.OrderByDescending(e => e.Start).Select(e => new NpcEncounter(e.Id, e.Start,
                e.LastActivity, e.Character, e.Classes, e.Level, e.Outcome, e.YourDamage, e.OtherDamage,
                e.IncomingDamage, e.Spells.Values.Select(s => new NpcSpellHistory(s.Spell, s.Damage,
                    s.Landings, s.Resists)).ToArray())).ToArray(), entry.LastAssessment);
    }

    private bool TryCachedHistory(string key, out Dictionary<string, EncounterData> history)
    {
        if (!_histories.TryGetValue(key, out history!))
        {
            return false;
        }
        _historyOrder.Remove(key);
        _historyOrder.AddFirst(key);
        return true;
    }

    private void CacheHistory(string key, Dictionary<string, EncounterData> history)
    {
        _histories[key] = history;
        _historyOrder.Remove(key);
        _historyOrder.AddFirst(key);
        while (_historyOrder.Count > CachedHistories)
        {
            _histories.Remove(_historyOrder.Last!.Value);
            _historyOrder.RemoveLast();
        }
    }

    /// <summary>Converts history lines to encounters; replays after a crash can repeat one, and the latest copy wins.</summary>
    private Dictionary<string, EncounterData> ToHistory(IEnumerable<EncounterRecord> records)
    {
        var history = new Dictionary<string, EncounterData>();
        foreach (var record in records)
        {
            if (FromRecord(record) is { } encounter)
            {
                history[encounter.Id] = encounter;
            }
        }
        return history;
    }

    private EncounterRecord ToRecord(EncounterData encounter)
    {
        var record = new EncounterRecord
        {
            Source = SourceId(encounter.Source),
            Character = encounter.Character,
            Classes = encounter.Classes,
            Level = encounter.Level,
            Start = encounter.Start,
            LastActivity = encounter.LastActivity,
            Outcome = encounter.Outcome,
            YourDamage = encounter.YourDamage,
            OtherDamage = encounter.OtherDamage,
            IncomingDamage = encounter.IncomingDamage,
            Spells = [.. encounter.Spells.Values.Select(s => s.Clone())],
        };
        string[] parts = encounter.Id.Split('\n');
        if (parts.Length == 3 && parts[0] == encounter.Source &&
            parts[1] == encounter.Start.Ticks.ToString(CultureInfo.InvariantCulture) &&
            long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long line) &&
            parts[2] == line.ToString(CultureInfo.InvariantCulture))
        {
            record.Line = line;
        }
        else
        {
            record.Id = encounter.Id;
        }
        return record;
    }

    private EncounterData? FromRecord(EncounterRecord record)
    {
        if (record.Source < 0 || record.Source >= _data.Sources.Count || record.Character is null ||
            record.Outcome is null || record.Spells is null || record.YourDamage < 0 || record.OtherDamage < 0 ||
            record.IncomingDamage < 0 || record.Spells.Any(s => s is null || s.Spell is null || s.Damage < 0 ||
                s.Resists < 0 || s.Landings < 0))
        {
            return null;
        }
        string source = _data.Sources[record.Source];
        var encounter = new EncounterData
        {
            Id = record.Id ?? $"{source}\n{record.Start.Ticks.ToString(CultureInfo.InvariantCulture)}\n{record.Line.ToString(CultureInfo.InvariantCulture)}",
            Source = source,
            Character = record.Character,
            Classes = record.Classes,
            Level = record.Level,
            Start = record.Start,
            LastActivity = record.LastActivity,
            Outcome = record.Outcome,
            YourDamage = record.YourDamage,
            OtherDamage = record.OtherDamage,
            IncomingDamage = record.IncomingDamage,
        };
        foreach (var spell in record.Spells)
        {
            encounter.Spells[MobResistanceStore.MobKey(spell.Spell)] = spell;
        }
        return encounter;
    }

    private int SourceId(string source)
    {
        int id = _data.Sources.IndexOf(source);
        if (id < 0)
        {
            _data.Sources.Add(source);
            id = _data.Sources.Count - 1;
        }
        return id;
    }

    /// <summary>Stable, readable file name derived from the NPC key, so history files can't be orphaned by a crash.</summary>
    public static string HistoryFileName(string key)
    {
        string[] parts = key.Split('\n');
        string readable = parts.Length == 2 ? parts[1] + "-" + parts[0] : key;
        var name = new StringBuilder();
        foreach (char c in readable.ToLowerInvariant())
        {
            if (name.Length >= 48)
            {
                break;
            }
            if (char.IsAsciiLetterOrDigit(c))
            {
                name.Append(c);
            }
            else if (name.Length > 0 && name[^1] != '-')
            {
                name.Append('-');
            }
        }
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 6));
        return $"{name.ToString().Trim('-')}-{hash}.jsonl";
    }

    public sealed record HistoryAppend(
        string Key,
        string FileName,
        IReadOnlyList<string> EncounterIds,
        IReadOnlyList<EncounterRecord> Records);

    public sealed record PersistenceBatch(
        long Version,
        Data? SourcesIndex,
        IReadOnlyList<HistoryAppend> HistoryAppends);

    /// <summary>Creates a detached batch for the infrastructure repository to append and save.</summary>
    public PersistenceBatch? CapturePersistenceBatch()
    {
        lock (_gate)
        {
            if (_savedVersion == _version)
            {
                return null;
            }

            long version = _version;
            int sourceCount = _data.Sources.Count;
            var appends = new List<HistoryAppend>();
            var closed = _data.Entries
                .Select(pair => (
                    Key: pair.Key,
                    Entry: pair.Value,
                    Encounters: pair.Value.Encounters.Where(encounter => encounter.Outcome != "Active").ToArray()))
                .Where(item => item.Encounters.Length > 0)
                .ToArray();

            foreach (var encounter in closed.SelectMany(item => item.Encounters))
            {
                SourceId(encounter.Source);
            }

            Data? sourcesIndex = _data.Sources.Count == sourceCount ? null : _data.Snapshot();
            foreach (var item in closed)
            {
                item.Entry.File ??= HistoryFileName(item.Key);
                appends.Add(new HistoryAppend(
                    item.Key,
                    item.Entry.File,
                    [.. item.Encounters.Select(encounter => encounter.Id)],
                    [.. item.Encounters.Select(ToRecord)]));
            }

            return new PersistenceBatch(version, sourcesIndex, appends);
        }
    }

    /// <summary>Creates a detached index snapshot after any pending history appends complete.</summary>
    public Data ExportIndex()
    {
        lock (_gate)
        {
            return _data.Snapshot();
        }
    }

    /// <summary>Removes only encounters successfully appended by the repository.</summary>
    public void CompleteHistoryAppend(string key, IReadOnlyCollection<string> encounterIds)
    {
        lock (_gate)
        {
            if (!_data.Entries.TryGetValue(key, out var entry))
            {
                return;
            }

            var appended = entry.Encounters
                .Where(encounter => encounterIds.Contains(encounter.Id))
                .ToArray();
            entry.Encounters.RemoveAll(encounter => encounterIds.Contains(encounter.Id));
            if (_histories.TryGetValue(key, out var cached))
            {
                foreach (var encounter in appended)
                {
                    cached[encounter.Id] = encounter;
                }
            }
        }
    }

    public void CompletePersistence(long version)
    {
        lock (_gate)
        {
            _savedVersion = Math.Max(_savedVersion, version);
        }
    }

    /// <summary>Imports validated index data, dropping copies already present in history after a crash.</summary>
    public void ImportData(Data data, IReadOnlyDictionary<string, IReadOnlyCollection<string>>? historyIds = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_gate)
        {
            _data = data;
            _active.Clear();
            _histories.Clear();
            _historyOrder.Clear();
            foreach (var (key, entry) in _data.Entries)
            {
                if (historyIds is not null && historyIds.TryGetValue(key, out var savedIds))
                {
                    entry.Encounters.RemoveAll(encounter => savedIds.Contains(encounter.Id));
                }
            }

            foreach (var encounter in _data.Entries.Values.SelectMany(entry => entry.Encounters)
                .Where(encounter => encounter.Outcome == "Active"))
            {
                if (!_active.TryGetValue(encounter.Source, out var active))
                {
                    active = [];
                    _active[encounter.Source] = active;
                }
                active.Add(encounter);
            }

            _version = 0;
            _savedVersion = 0;
        }
    }

    /// <summary>Marks legacy migration or another in-memory change as requiring persistence.</summary>
    public void MarkDirty()
    {
        lock (_gate)
        {
            _version++;
        }
    }

    public string? GetHistoryIdentity(EncounterRecord record)
    {
        lock (_gate)
        {
            if (record.Id is not null)
            {
                return record.Id;
            }
            if (record.Source < 0 || record.Source >= _data.Sources.Count)
            {
                return null;
            }

            return $"{_data.Sources[record.Source]}\n{record.Start.Ticks.ToString(CultureInfo.InvariantCulture)}\n{record.Line.ToString(CultureInfo.InvariantCulture)}";
        }
    }

    public sealed class Data
    {
        public Dictionary<string, EntryData> Entries { get; set; } = [];
        public Dictionary<string, ResistanceWatermark> Watermarks { get; set; } = [];
        public Dictionary<string, string> LastConsiders { get; set; } = [];

        /// <summary>Log sources referenced by number from history lines.</summary>
        public List<string> Sources { get; set; } = [];

        /// <summary>A detached copy for streaming to disk outside the lock; immutable values are shared.</summary>
        public Data Snapshot()
        {
            return new Data
            {
                Entries = Entries.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), Entries.Comparer),
                Watermarks = new(Watermarks, Watermarks.Comparer),
                LastConsiders = new(LastConsiders, LastConsiders.Comparer),
                Sources = [.. Sources],
            };
        }
    }

    public sealed class EntryData
    {
        public string Mob { get; set; } = "";
        public string Zone { get; set; } = "";
        public DateTime FirstSeen
        {
            get; set;
        }
        public DateTime LastSeen
        {
            get; set;
        }
        public long Considers
        {
            get; set;
        }
        public ConsiderEvent? LastConsider
        {
            get; set;
        }
        public string? LastAssessment
        {
            get; set;
        }
        /// <summary>History file name under the npcs folder; null until the first finished encounter is saved.</summary>
        public string? File
        {
            get; set;
        }

        /// <summary>Active encounters plus finished ones not yet appended to the history file.</summary>
        public List<EncounterData> Encounters { get; set; } = [];

        internal EntryData Clone()
        {
            var copy = (EntryData)MemberwiseClone();
            copy.Encounters = Encounters.ConvertAll(e => e.Clone());
            return copy;
        }
    }

    public sealed class EncounterData
    {
        public string Id { get; set; } = "";
        public string Source { get; set; } = "";
        public string Character { get; set; } = "";
        public string? Classes
        {
            get; set;
        }
        public int? Level
        {
            get; set;
        }
        public DateTime Start
        {
            get; set;
        }
        public DateTime LastActivity
        {
            get; set;
        }
        public string Outcome { get; set; } = "Active";
        public long YourDamage
        {
            get; set;
        }
        public long OtherDamage
        {
            get; set;
        }
        public long IncomingDamage
        {
            get; set;
        }
        public Dictionary<string, SpellData> Spells { get; set; } = [];

        internal EncounterData Clone()
        {
            var copy = (EncounterData)MemberwiseClone();
            copy.Spells = Spells.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), Spells.Comparer);
            return copy;
        }
    }

    public sealed class SpellData
    {
        public string Spell { get; set; } = "";
        public long Damage
        {
            get; set;
        }
        public long Landings
        {
            get; set;
        }
        public long Resists
        {
            get; set;
        }

        internal SpellData Clone()
        {
            return (SpellData)MemberwiseClone();
        }
    }

    /// <summary>One finished encounter as a single line of an NPC's history file.</summary>
    public sealed class EncounterRecord
    {
        /// <summary>Index into <see cref="Data.Sources"/>.</summary>
        public int Source
        {
            get; set;
        }

        public long Line
        {
            get; set;
        }

        /// <summary>Only written when the id can't be rebuilt from source, start and line.</summary>
        public string? Id
        {
            get; set;
        }

        public string Character { get; set; } = "";
        public string? Classes
        {
            get; set;
        }
        public int? Level
        {
            get; set;
        }
        public DateTime Start
        {
            get; set;
        }
        public DateTime LastActivity
        {
            get; set;
        }
        public string Outcome { get; set; } = "";
        public long YourDamage
        {
            get; set;
        }
        public long OtherDamage
        {
            get; set;
        }
        public long IncomingDamage
        {
            get; set;
        }
        public List<SpellData> Spells { get; set; } = [];
    }
}
