using System.Text.Json;
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

/// <summary>Persistent, thread-safe NPC interaction summaries. Active encounters survive saves and replay.</summary>
public sealed class NpcCompendium
{
    private readonly object _gate = new();
    private Data _data = new();
    private long _version;
    private long _savedVersion;
    private readonly Dictionary<string, List<EncounterData>> _active = [];
    public static readonly TimeSpan EncounterTimeout = TimeSpan.FromSeconds(15);

    public IReadOnlyList<NpcCatalogEntry> Search(string name)
    {
        lock (_gate)
        {
            return _data.Entries.Values.Where(e => e.Mob.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Mob, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Zone, StringComparer.OrdinalIgnoreCase)
                .Select(e => new NpcCatalogEntry(e.Mob, e.Zone, e.FirstSeen, e.LastSeen, e.Considers)).ToArray();
        }
    }

    public NpcEntry? Get(string mob, string zone)
    {
        lock (_gate)
        {
            return _data.Entries.TryGetValue(Key(mob, zone), out var entry) ? Snapshot(entry) : null;
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
            CloseExpired(sourceKey, ev.Time);
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

    public void Advance(string source, DateTime time)
    {
        lock (_gate)
        {
            if (CloseExpired(source.ToUpperInvariant(), time))
            {
                _version++;
            }
        }
    }

    private bool CloseExpired(string source, DateTime time)
    {
        bool changed = false;
        foreach (var encounter in ActiveFor(source).Where(e => time - e.LastActivity > EncounterTimeout))
        {
            encounter.Outcome = "Timed out";
            changed = true;
        }
        return changed;
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

    private static NpcEntry Snapshot(EntryData entry)
    {
        return new(entry.Mob, entry.Zone, entry.FirstSeen, entry.LastSeen, entry.Considers, entry.LastConsider,
            entry.Encounters.OrderByDescending(e => e.Start).Select(e => new NpcEncounter(e.Id, e.Start,
                e.LastActivity, e.Character, e.Classes, e.Level, e.Outcome, e.YourDamage, e.OtherDamage,
                e.IncomingDamage, e.Spells.Values.Select(s => new NpcSpellHistory(s.Spell, s.Damage,
                    s.Landings, s.Resists)).ToArray())).ToArray(), entry.LastAssessment);
    }

    public void Save(string path)
    {
        lock (_gate)
        {
            if (_savedVersion == _version)
            {
                return;
            }
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
            _savedVersion = _version;
        }
    }

    public static NpcCompendium Load(string path)
    {
        var store = new NpcCompendium();
        if (File.Exists(path))
        {
            var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(path))
                ?? throw new JsonException("NPC compendium data is empty.");
            if (data.Entries is null || data.Watermarks is null || data.LastConsiders is null ||
                data.Watermarks.Values.Any(w => w is null) ||
                data.Entries.Values.Any(e => e is null || string.IsNullOrWhiteSpace(e.Mob) || e.Zone is null ||
                    e.Encounters is null || e.Encounters.Any(c => c is null || c.Spells is null ||
                        c.Source is null || c.Character is null || c.Outcome is null ||
                        c.YourDamage < 0 || c.OtherDamage < 0 || c.IncomingDamage < 0 ||
                        c.Spells.Values.Any(s => s is null || s.Spell is null || s.Damage < 0 || s.Resists < 0 || s.Landings < 0))))
            {
                throw new JsonException("NPC compendium contains invalid or null entries.");
            }
            store._data = data;
            foreach (var encounter in data.Entries.Values.SelectMany(e => e.Encounters).Where(e => e.Outcome == "Active"))
            {
                if (!store._active.TryGetValue(encounter.Source, out var active))
                {
                    active = [];
                    store._active[encounter.Source] = active;
                }
                active.Add(encounter);
            }
        }
        return store;
    }

    public sealed class Data
    {
        public Dictionary<string, EntryData> Entries { get; set; } = [];
        public Dictionary<string, ResistanceWatermark> Watermarks { get; set; } = [];
        public Dictionary<string, string> LastConsiders { get; set; } = [];
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
        public List<EncounterData> Encounters { get; set; } = [];
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
    }
}
