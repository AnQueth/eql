using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EqLog.Core;

public enum ResistanceType
{
    Magic,
    Fire,
    Cold,
    Poison,
    Disease,
}

public enum ResistanceStatus
{
    Unknown,
    Resists,
    Partial,
    PartiallyResistant,
    NotResistant,
}

public enum SpellAssessmentKind
{
    DirectDamage,
    DamageOverTime,
    AreaEffect,
    Duration,
    MissingDetails,
    UnverifiedTarget,
}

/// <summary>
/// Resisted counts every explicit resist. DotLanded/DotResisted are the assessed outcomes of damage-over-time casts
/// (first tick after a cast, or its resist), kept apart so older resist-only history doesn't skew DoT rates.
/// </summary>
public sealed record SpellOutcome(string Spell, long Landed, long Resisted,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Dictionary<string, Dictionary<long, long>>? DamageSamples = null,
    long DotLanded = 0, long DotResisted = 0);
/// <summary>
/// Confirmed cast outcomes by school. EstimatedPartial counts reduced-damage landings (cause unknown),
/// and UnassessedDamage counts landings awaiting a baseline; neither adds to Resisted.
/// </summary>
public sealed record ResistanceObservation(ResistanceType Type, long Landed, long Resisted, long OtherResisted,
    long EstimatedPartial = 0, long UnassessedDamage = 0)
{
    public decimal AssessedOutcomes
    {
        get
        {
            return (decimal)Landed + Resisted;
        }
    }

    public decimal? ResistPercent
    {
        get
        {
            return AssessedOutcomes == 0 ? null : 100m * Resisted / AssessedOutcomes;
        }
    }

    public ResistanceStatus Status
    {
        get
        {
            decimal total = (decimal)Landed + Resisted;
            if (total == 0)
            {
                return ResistanceStatus.Unknown;
            }
            if (Resisted == total)
            {
                return ResistanceStatus.Resists;
            }
            decimal rate = Resisted / total;
            if (rate > 0.5m)
            {
                return ResistanceStatus.Partial;
            }
            if (rate >= 0.2m)
            {
                return ResistanceStatus.PartiallyResistant;
            }
            return ResistanceStatus.NotResistant;
        }
    }
}
public sealed record MobWikiInfo(string Title, string Url, DateTime CheckedUtc, bool Found,
    string? Level, string? Class, string? HitPoints, IReadOnlyDictionary<ResistanceType, string> Resistances,
    string? Special = null, MobWikiStats? Stats = null, IReadOnlyList<QuestReference>? Quests = null,
    string? IconFile = null);
public sealed record MobWikiStats(string? Race, string? ArmorClass, string? HpRegen, string? ManaRegen,
    string? AttacksPerRound, string? AttackSpeed, string? DamagePerHit, string? AggroRadius,
    string? RunSpeed, string? RespawnTime, string? Location);
public sealed record SpellResistanceInfo(string Spell, ResistanceType? Type, string? Detail, DateTime CheckedUtc,
    bool SingleTargetDirectDamage = false, string? TargetType = null, string? Duration = null, int MetadataVersion = 0,
    bool Found = true, bool DamageOverTime = false)
{
    public const int CurrentMetadataVersion = 2;

    [JsonIgnore]
    public bool NeedsRefresh
    {
        get
        {
            return MetadataVersion < CurrentMetadataVersion ||
                DateTime.UtcNow - CheckedUtc >= TimeSpan.FromDays(Found ? 7 : 1);
        }
    }

    [JsonIgnore]
    public SpellAssessmentKind AssessmentKind
    {
        get
        {
            if (SingleTargetDirectDamage)
            {
                return SpellAssessmentKind.DirectDamage;
            }
            if (DamageOverTime)
            {
                return SpellAssessmentKind.DamageOverTime;
            }
            if (TargetType?.Contains("AE", StringComparison.OrdinalIgnoreCase) == true ||
                TargetType?.Contains("Area", StringComparison.OrdinalIgnoreCase) == true)
            {
                return SpellAssessmentKind.AreaEffect;
            }
            if (Duration is not null && !string.Equals(Duration, "Instant", StringComparison.OrdinalIgnoreCase))
            {
                return SpellAssessmentKind.Duration;
            }
            if (TargetType is null)
            {
                return SpellAssessmentKind.MissingDetails;
            }
            return SpellAssessmentKind.UnverifiedTarget;
        }
    }
}
public sealed record MobResistanceInfo(IReadOnlyList<ResistanceObservation> Observations, long Unclassified,
    MobWikiInfo? Wiki, IReadOnlyList<string> Spells);
public sealed record RawSpellCounts(long FullResists, long LegacyLandings, long DamageHits,
    long DotLanded = 0, long DotResisted = 0);
public sealed record ResistanceWatermark(DateTime Time, long Line);

/// <summary>Detached data captured for ordered persistence by the tracking repository.</summary>
public sealed record MobResistancePersistenceSnapshot(
    MobResistanceStore.Data Data,
    IReadOnlyList<SpellResistanceInfo> PendingSpells,
    long Version,
    IMobResistanceHistorySource? HistorySource = null,
    IReadOnlyList<string>? RemovedMobs = null,
    bool WikiCachesReset = false,
    bool IsDelta = false);

/// <summary>Resident history index providing detached entries only when an observation or query needs them.</summary>
public interface IMobResistanceHistorySource
{
    /// <summary>Loads one zone/mob's outcomes, or null when it has no saved history.</summary>
    Dictionary<string, SpellOutcome>? LoadMob(string key);

    /// <summary>Loads one mob's cached wiki information, or null when absent.</summary>
    MobWikiInfo? LoadWiki(string mob);

    /// <summary>Reads samples for one spell from mobs not already represented in the mutable store.</summary>
    IEnumerable<SpellOutcome> SpellOutcomes(string spell, IReadOnlySet<string> excludedMobs);
}

/// <summary>Thread-safe persistent observations keyed by zone and mob name, not individual NPC instances.</summary>
public sealed class MobResistanceStore
{
    public const int MinimumDamageSamples = 3;
    public const decimal ReducedDamageRatio = 0.9m;

    private readonly object _gate = new();
    private Data _data = new();
    private long _version;
    private long _savedVersion;
    private readonly Dictionary<(string Spell, string Character), decimal?> _damageBaselines = [];
    private IMobResistanceHistorySource? _historySource;
    private readonly HashSet<string> _loadedMobs = [];
    private readonly HashSet<string> _loadedWiki = [];
    private readonly HashSet<string> _removedMobs = [];
    private readonly Dictionary<string, long> _dirtyMobs = [];
    private readonly Dictionary<string, long> _dirtyWiki = [];
    private bool _wikiCachesReset;
    private long _wikiResetVersion;

    /// <summary>Spell metadata set since the last save, waiting to be appended to the spell cache file.</summary>
    private readonly List<SpellResistanceInfo> _pendingSpells = [];

    public static string MobKey(string name)
    {
        return Regex.Replace(name.Trim(), @"\s+", " ").ToUpperInvariant();
    }

    private static string Key(string zone, string mob)
    {
        return MobKey(zone) + "\n" + MobKey(mob);
    }

    /// <summary>
    /// Records one observed outcome once per log path/line, preventing replay from inflating counts.
    /// DoT outcomes have their own watermark, so history read before DoTs were assessed is counted once.
    /// Landings without a known loadout have a separate watermark to recover previously skipped history.
    /// </summary>
    public void Record(string log, long line, string zone, string mob, string spell, bool resisted,
        DateTime time = default, string? character = null, long? damage = null, bool damageOverTime = false,
        bool unprofiledLanding = false)
    {
        lock (_gate)
        {
            string key = Key(zone, mob);
            if (_data.ResistanceResetTimes.TryGetValue(key, out var resetTime) && time <= resetTime)
            {
                return;
            }
            string source = log.ToUpperInvariant();
            if (unprofiledLanding)
            {
                source += "\nUNPROFILED";
            }
            bool advance = CanAdvance(source, time, line);
            bool counted = advance;
            bool dotCounted = damageOverTime && CanAdvance(source + "\nDOT", time, line);
            if (damageOverTime && !resisted)
            {
                counted = false;
            }
            if (!counted && !dotCounted)
            {
                if (advance)
                {
                    _data.Watermarks[source] = new(time, line);
                    _version++;
                }
                return;
            }
            EnsureMobLoaded(key);
            if (advance)
            {
                _data.Watermarks[source] = new(time, line);
            }
            if (dotCounted)
            {
                _data.Watermarks[source + "\nDOT"] = new(time, line);
            }
            if (!_data.Mobs.TryGetValue(key, out var spells))
            {
                spells = [];
                _data.Mobs[key] = spells;
            }
            string spellKey = MobKey(spell);
            var previous = spells.GetValueOrDefault(spellKey) ?? new SpellOutcome(spell, 0, 0);
            spells[spellKey] = previous with
            {
                Landed = previous.Landed + (counted && !resisted && damage is null ? 1 : 0),
                Resisted = previous.Resisted + (counted && resisted ? 1 : 0),
                DotLanded = previous.DotLanded + (dotCounted && !resisted ? 1 : 0),
                DotResisted = previous.DotResisted + (dotCounted && resisted ? 1 : 0),
            };
            if (counted && damage is long amount && character is not null)
            {
                var samples = previous.DamageSamples ?? [];
                string characterKey = DamageProfileKey(character);
                if (!samples.TryGetValue(characterKey, out var histogram))
                {
                    histogram = [];
                    samples[characterKey] = histogram;
                }
                histogram[amount] = histogram.GetValueOrDefault(amount) + 1;
                spells[spellKey] = spells[spellKey] with
                {
                    DamageSamples = samples
                };
                _damageBaselines.Remove((spellKey, characterKey));
            }
            _version++;
            _dirtyMobs[key] = _version;
        }
    }

    /// <summary>
    /// Clears resistance outcomes and damage samples for one mob in one zone, preserving wiki caches and other mobs.
    /// </summary>
    /// <param name="zone">The exact logged zone, including instance or difficulty suffixes.</param>
    /// <param name="mob">The mob name; matching ignores case and repeated whitespace.</param>
    /// <param name="resetTime">Local log-time cutoff; outcomes at or before this time remain excluded on replay.</param>
    public void ResetResistances(string zone, string mob, DateTime resetTime)
    {
        lock (_gate)
        {
            string key = Key(zone, mob);
            _data.Mobs.Remove(key);
            _loadedMobs.Add(key);
            _removedMobs.Add(key);
            _data.ResistanceResetTimes[key] = _data.ResistanceResetTimes.TryGetValue(key, out var previous) &&
                previous > resetTime ? previous : resetTime;
            _damageBaselines.Clear();
            _version++;
            _dirtyMobs[key] = _version;
        }
    }

    private bool CanAdvance(string source, DateTime time, long line)
    {
        var watermark = _data.Watermarks.GetValueOrDefault(source);
        if (watermark is not null && (watermark.Time > time || (watermark.Time == time && watermark.Line >= line)))
        {
            return false;
        }
        return true;
    }

    private void EnsureMobLoaded(string key)
    {
        if (_historySource is null || _loadedMobs.Contains(key))
        {
            return;
        }
        var spells = _historySource.LoadMob(key);
        if (spells is not null)
        {
            _data.Mobs[key] = spells;
        }
        _loadedMobs.Add(key);
    }

    private void EnsureWikiLoaded(string key)
    {
        if (_historySource is null || _wikiCachesReset || _loadedWiki.Contains(key))
        {
            return;
        }
        var wiki = _historySource.LoadWiki(key);
        if (wiki is not null)
        {
            _data.Wiki[key] = wiki;
        }
        _loadedWiki.Add(key);
    }

    public MobResistanceInfo Get(string zone, string mob, string? spellName = null)
    {
        lock (_gate)
        {
            EnsureMobLoaded(Key(zone, mob));
            EnsureWikiLoaded(MobKey(mob));
            var spells = _data.Mobs.GetValueOrDefault(Key(zone, mob))?.Values.ToArray() ?? [];
            if (spellName is not null)
            {
                spells = spells.Where(s => MobKey(s.Spell) == MobKey(spellName)).ToArray();
            }
            var observations = Enum.GetValues<ResistanceType>().Select(type =>
            {
                var matching = spells.Where(s => _data.Spells.GetValueOrDefault(MobKey(s.Spell))?.Type == type).ToArray();
                var comparable = matching.Where(s => _data.Spells[MobKey(s.Spell)].SingleTargetDirectDamage).ToArray();
                long landed = comparable.Sum(s => s.Landed);
                long partial = 0;
                long unknown = 0;
                foreach (var spell in comparable)
                {
                    foreach (var (character, samples) in spell.DamageSamples ?? [])
                    {
                        decimal? baseline = DamageBaseline(MobKey(spell.Spell), DamageProfileKey(character));
                        foreach (var (amount, count) in samples)
                        {
                            landed += count;
                            if (baseline is null)
                            {
                                unknown += count;
                            }
                            else if ((decimal)amount < baseline.Value * ReducedDamageRatio)
                            {
                                partial += count;
                            }
                        }
                    }
                }
                var dots = matching.Where(s => !_data.Spells[MobKey(s.Spell)].SingleTargetDirectDamage &&
                    _data.Spells[MobKey(s.Spell)].DamageOverTime).ToArray();
                landed += dots.Sum(s => s.DotLanded);
                long other = matching.Where(s => !_data.Spells[MobKey(s.Spell)].SingleTargetDirectDamage).Sum(s => s.Resisted) -
                    dots.Sum(s => Math.Min(s.DotResisted, s.Resisted));
                return new ResistanceObservation(type, landed,
                    comparable.Sum(s => s.Resisted) + dots.Sum(s => s.DotResisted),
                    other, partial, unknown);
            }).ToArray();
            long unclassified = spells.Where(s => _data.Spells.GetValueOrDefault(MobKey(s.Spell))?.Type is null)
                .Sum(s => s.Landed + s.Resisted + s.DotLanded + (s.DamageSamples?.Values.Sum(h => h.Values.Sum()) ?? 0));
            return new(observations, unclassified, _data.Wiki.GetValueOrDefault(MobKey(mob)),
                spells.Select(s => s.Spell).ToArray());
        }
    }

    public RawSpellCounts GetRawSpellCounts(string zone, string mob, string spell)
    {
        lock (_gate)
        {
            EnsureMobLoaded(Key(zone, mob));
            var outcome = _data.Mobs.GetValueOrDefault(Key(zone, mob))?.GetValueOrDefault(MobKey(spell));
            return new(outcome?.Resisted ?? 0, outcome?.Landed ?? 0,
                outcome?.DamageSamples?.Values.Sum(h => h.Values.Sum()) ?? 0,
                outcome?.DotLanded ?? 0, outcome?.DotResisted ?? 0);
        }
    }

    private static string DamageProfileKey(string character)
    {
        string key = MobKey(character);
        int levelSeparator = key.LastIndexOf(' ');
        if (levelSeparator <= 0)
        {
            return key;
        }
        foreach (char digit in key.AsSpan(levelSeparator + 1))
        {
            if (!char.IsDigit(digit))
            {
                return key;
            }
        }
        int classSeparator = key.LastIndexOf(' ', levelSeparator - 1);
        if (classSeparator <= 0)
        {
            return key;
        }
        var classes = key.AsSpan(classSeparator + 1, levelSeparator - classSeparator - 1);
        if (classes.Length % 4 != 3)
        {
            return key;
        }
        for (int index = 0; index < classes.Length; index++)
        {
            char value = classes[index];
            if (index % 4 == 3 ? value != '/' : value is < 'A' or > 'Z')
            {
                return key;
            }
        }
        return string.Concat(key.AsSpan(0, classSeparator), key.AsSpan(levelSeparator));
    }

    private decimal? DamageBaseline(string spell, string character)
    {
        var key = (spell, character);
        if (_damageBaselines.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var outcomes = _data.Mobs.Values.SelectMany(m => m.Values);
        if (_historySource is not null)
        {
            outcomes = outcomes.Concat(_historySource.SpellOutcomes(spell, _loadedMobs));
        }
        var samples = outcomes
            .Where(s => MobKey(s.Spell) == spell)
            .SelectMany(s => s.DamageSamples ?? [])
            .Where(profile => DamageProfileKey(profile.Key) == character)
            .SelectMany(profile => profile.Value).ToArray();
        long total = samples.Sum(sample => sample.Value);
        if (total < MinimumDamageSamples)
        {
            _damageBaselines[key] = null;
            return null;
        }
        long lowerIndex = (total - 1) / 2;
        long upperIndex = total / 2;
        long cumulative = 0;
        long? lower = null;
        foreach (var sample in samples.OrderBy(sample => sample.Key))
        {
            cumulative += sample.Value;
            if (cumulative > lowerIndex)
            {
                lower ??= sample.Key;
            }
            if (cumulative > upperIndex && lower is long lowerAmount)
            {
                decimal baseline = ((decimal)lowerAmount + sample.Key) / 2;
                _damageBaselines[key] = baseline;
                return baseline;
            }
        }
        throw new InvalidOperationException("Damage samples did not contain their median.");
    }

    public SpellResistanceInfo? GetSpell(string spell)
    {
        lock (_gate)
        {
            return _data.Spells.GetValueOrDefault(MobKey(spell));
        }
    }

    public void SetWiki(string mob, MobWikiInfo info)
    {
        lock (_gate)
        {
            _data.Wiki[MobKey(mob)] = info;
            _loadedWiki.Add(MobKey(mob));
            _version++;
            _dirtyWiki[MobKey(mob)] = _version;
        }
    }

    public void SetSpell(SpellResistanceInfo info)
    {
        lock (_gate)
        {
            _data.Spells[MobKey(info.Spell)] = info;
            _pendingSpells.Add(info);
            _version++;
        }
    }

    /// <summary>Clears NPC and spell wiki metadata without removing observations or reset cutoffs.</summary>
    public void ResetWikiCaches()
    {
        lock (_gate)
        {
            _data.Wiki.Clear();
            _loadedWiki.Clear();
            _wikiCachesReset = true;
            _data.Spells.Clear();
            _pendingSpells.Clear();
            _damageBaselines.Clear();
            _version++;
            _wikiResetVersion = _version;
        }
    }

    /// <summary>Captures a detached snapshot or null when every current version has been saved.</summary>
    public MobResistancePersistenceSnapshot? CapturePersistenceSnapshot(bool onlyChangedEntries = false)
    {
        lock (_gate)
        {
            if (_savedVersion == _version)
            {
                return null;
            }

            bool delta = onlyChangedEntries && (_historySource is not null || _savedVersion > 0);
            return new MobResistancePersistenceSnapshot(
                _data.Snapshot(delta ? _dirtyMobs.Keys : null, delta ? _dirtyWiki.Keys : null),
                [.. _pendingSpells], _version, _historySource,
                delta ? _dirtyMobs.Keys.Where(key => !_data.Mobs.ContainsKey(key)).ToArray() : [.. _removedMobs],
                delta ? _wikiResetVersion > _savedVersion : _wikiCachesReset, delta);
        }
    }

    /// <summary>Marks appended spell records as durable before the main file is updated.</summary>
    public void CompleteSpellCacheAppend(int appendedSpellCount)
    {
        lock (_gate)
        {
            if (appendedSpellCount > 0)
            {
                _pendingSpells.RemoveRange(0, Math.Min(appendedSpellCount, _pendingSpells.Count));
            }
        }
    }

    /// <summary>Marks a successfully written observation snapshot as saved.</summary>
    public void CompletePersistence(long version)
    {
        lock (_gate)
        {
            _savedVersion = Math.Max(_savedVersion, version);
            foreach (string key in _dirtyMobs.Where(pair => pair.Value <= _savedVersion).Select(pair => pair.Key).ToArray())
            {
                _dirtyMobs.Remove(key);
            }
            foreach (string key in _dirtyWiki.Where(pair => pair.Value <= _savedVersion).Select(pair => pair.Key).ToArray())
            {
                _dirtyWiki.Remove(key);
            }
        }
    }

    /// <summary>Imports validated observation data and the latest spell-cache records.</summary>
    public void ImportData(Data data, IEnumerable<SpellResistanceInfo>? cachedSpells = null,
        IMobResistanceHistorySource? historySource = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_gate)
        {
            _data = data;
            _historySource = historySource;
            _loadedMobs.Clear();
            _loadedMobs.UnionWith(data.Mobs.Keys);
            _loadedWiki.Clear();
            _loadedWiki.UnionWith(data.Wiki.Keys);
            _removedMobs.Clear();
            _dirtyMobs.Clear();
            _dirtyWiki.Clear();
            _wikiCachesReset = false;
            _wikiResetVersion = 0;
            _pendingSpells.Clear();
            _damageBaselines.Clear();
            _version = 0;
            _savedVersion = 0;
            if (data.Spells.Count > 0)
            {
                _pendingSpells.AddRange(data.Spells.Values);
                _version++;
            }
            foreach (var spell in cachedSpells ?? [])
            {
                if (!string.IsNullOrWhiteSpace(spell.Spell))
                {
                    _data.Spells[MobKey(spell.Spell)] = spell;
                }
            }
        }
    }

    public sealed class Data
    {
        public Dictionary<string, Dictionary<string, SpellOutcome>> Mobs { get; set; } = [];
        public Dictionary<string, MobWikiInfo> Wiki { get; set; } = [];
        public Dictionary<string, SpellResistanceInfo> Spells { get; set; } = [];
        public Dictionary<string, ResistanceWatermark> Watermarks { get; set; } = [];
        /// <summary>Per-zone/mob local timestamps excluding pre-reset observations, including copied or renamed logs.</summary>
        public Dictionary<string, DateTime> ResistanceResetTimes { get; set; } = [];

        /// <summary>
        /// A copy safe to serialize outside the lock: containers are copied, immutable records are shared,
        /// and the damage histograms (mutated in place) are copied.
        /// </summary>
        internal Data Snapshot(IEnumerable<string>? mobKeys = null, IEnumerable<string>? wikiKeys = null)
        {
            var mobs = new Dictionary<string, Dictionary<string, SpellOutcome>>(mobKeys is null ? Mobs.Count : 0, Mobs.Comparer);
            foreach (string mob in mobKeys ?? Mobs.Keys)
            {
                if (!Mobs.TryGetValue(mob, out var spells))
                {
                    continue;
                }
                var copy = new Dictionary<string, SpellOutcome>(spells.Count, spells.Comparer);
                foreach (var (key, outcome) in spells)
                {
                    copy[key] = outcome.DamageSamples is null ? outcome : outcome with
                    {
                        DamageSamples = outcome.DamageSamples.ToDictionary(
                            kv => kv.Key, kv => new Dictionary<long, long>(kv.Value), outcome.DamageSamples.Comparer)
                    };
                }
                mobs[mob] = copy;
            }
            return new Data
            {
                Mobs = mobs,
                Wiki = wikiKeys is null ? new(Wiki, Wiki.Comparer) :
                    wikiKeys.Where(Wiki.ContainsKey).ToDictionary(key => key, key => Wiki[key], Wiki.Comparer),
                // The spell cache lives in its own append-only file.
                Spells = [],
                Watermarks = new(Watermarks, Watermarks.Comparer),
                ResistanceResetTimes = new(ResistanceResetTimes, ResistanceResetTimes.Comparer),
            };
        }
    }
}
