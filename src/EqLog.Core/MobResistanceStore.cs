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

/// <summary>
/// Resisted counts every explicit resist. DotLanded/DotResisted are the assessed outcomes of damage-over-time casts
/// (first tick after a cast, or its resist), kept apart so older resist-only history doesn't skew DoT rates.
/// </summary>
public sealed record SpellOutcome(string Spell, long Landed, long Resisted,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Dictionary<string, Dictionary<long, long>>? DamageSamples = null,
    long DotLanded = 0, long DotResisted = 0);
public sealed record ResistanceObservation(ResistanceType Type, long Landed, long Resisted, long OtherResisted,
    long EstimatedPartial = 0, long UnassessedDamage = 0)
{
    public string DisplayText
    {
        get
        {
            decimal total = (decimal)Landed + Resisted;
            if (total == 0)
            {
                return $"{Status} \u2014 no assessed outcomes";
            }
            return $"{Status} \u2014 {100m * Resisted / total:0.#}% ({Resisted}/{total})";
        }
    }

    public string Status
    {
        get
        {
            decimal total = (decimal)Landed + Resisted;
            if (total == 0 || UnassessedDamage > 0)
            {
                return "Unknown";
            }
            if (Resisted == total)
            {
                return "Resists";
            }
            decimal rate = Resisted / total;
            if (rate > 0.5m)
            {
                return "Partial";
            }
            if (rate >= 0.1m)
            {
                return "Partially Resistant";
            }
            return "Not Resistant";
        }
    }
}
public sealed record MobWikiInfo(string Title, string Url, DateTime CheckedUtc, bool Found,
    string? Level, string? Class, string? HitPoints, IReadOnlyDictionary<ResistanceType, string> Resistances,
    string? Special = null, MobWikiStats? Stats = null);
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
    public string? AssessmentNote
    {
        get
        {
            if (SingleTargetDirectDamage)
            {
                return null;
            }
            if (DamageOverTime)
            {
                return "Damage-over-time spell: each cast counts once (its first tick landed, or it was resisted); ticks don't give a full/partial damage rating.";
            }
            if (TargetType?.Contains("AE", StringComparison.OrdinalIgnoreCase) == true ||
                TargetType?.Contains("Area", StringComparison.OrdinalIgnoreCase) == true)
            {
                return "Area-effect spell: hits and resists are recorded, but multiple targets or waves prevent a reliable full/partial damage rating.";
            }
            if (Duration is not null && !string.Equals(Duration, "Instant", StringComparison.OrdinalIgnoreCase))
            {
                return "Duration spell: damage ticks are not separate cast successes, so a full/partial damage rating is unavailable.";
            }
            if (TargetType is null)
            {
                return "Wiki target/damage details are missing. Refresh wiki to retry; recorded outcomes are preserved.";
            }
            return $"Wiki target: {TargetType}. No verified single-target instant damage profile; recorded outcomes are preserved.";
        }
    }
}
public sealed record MobResistanceInfo(IReadOnlyList<ResistanceObservation> Observations, long Unclassified,
    MobWikiInfo? Wiki, IReadOnlyList<string> Spells);
public sealed record RawSpellCounts(long FullResists, long LegacyLandings, long DamageHits,
    long DotLanded = 0, long DotResisted = 0);
public sealed record ResistanceWatermark(DateTime Time, long Line);

/// <summary>Thread-safe persistent observations keyed by zone and mob name, not individual NPC instances.</summary>
public sealed class MobResistanceStore
{
    private readonly object _gate = new();
    private Data _data = new();
    private long _version;
    private long _savedVersion;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Dictionary<(string Spell, string Character), long?> _damageBaselines = [];

    /// <summary>Spell metadata set since the last save, waiting to be appended to the spell cache file.</summary>
    private readonly List<SpellResistanceInfo> _pendingSpells = [];

    /// <summary>
    /// The spell wiki cache is "spell-info.jsonl" beside the observations file and only ever appended to,
    /// so caching a spell doesn't rewrite every mob.
    /// </summary>
    public static string SpellCachePath(string path)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return Path.Combine(folder, "spell-info.jsonl");
    }

    /// <summary>The name an earlier build used for the spell cache ("mob-resistances.spells.jsonl").</summary>
    private static string LegacySpellCachePath(string path)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + ".spells.jsonl");
    }

    /// <summary>Renames a spell cache written under the earlier name, unless the new file already exists.</summary>
    private static Task MoveLegacySpellCacheAsync(string path, CancellationToken ct)
    {
        string legacy = LegacySpellCachePath(path);
        string current = SpellCachePath(path);
        return Task.Run(() =>
        {
            if (File.Exists(legacy) && !File.Exists(current))
            {
                File.Move(legacy, current);
            }
        }, ct);
    }

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
    /// </summary>
    public void Record(string log, long line, string zone, string mob, string spell, bool resisted,
        DateTime time = default, string? character = null, long? damage = null, bool damageOverTime = false)
    {
        lock (_gate)
        {
            string source = log.ToUpperInvariant();
            bool counted = Advance(source, time, line);
            bool dotCounted = damageOverTime && Advance(source + "\nDOT", time, line);
            if (damageOverTime && !resisted)
            {
                counted = false;
            }
            if (!counted && !dotCounted)
            {
                return;
            }
            string key = Key(zone, mob);
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
                string characterKey = MobKey(character);
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
        }
    }

    private bool Advance(string source, DateTime time, long line)
    {
        var watermark = _data.Watermarks.GetValueOrDefault(source);
        if (watermark is not null && (watermark.Time > time || (watermark.Time == time && watermark.Line >= line)))
        {
            return false;
        }
        _data.Watermarks[source] = new(time, line);
        return true;
    }

    public MobResistanceInfo Get(string zone, string mob, string? spellName = null)
    {
        lock (_gate)
        {
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
                        long? baseline = DamageBaseline(MobKey(spell.Spell), character);
                        foreach (var (amount, count) in samples)
                        {
                            if (baseline is null)
                            {
                                unknown += count;
                            }
                            else if ((decimal)amount < baseline.Value * 0.9m)
                            {
                                partial += count;
                            }
                            else
                            {
                                landed += count;
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
                    comparable.Sum(s => s.Resisted) + partial + dots.Sum(s => s.DotResisted),
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
            var outcome = _data.Mobs.GetValueOrDefault(Key(zone, mob))?.GetValueOrDefault(MobKey(spell));
            return new(outcome?.Resisted ?? 0, outcome?.Landed ?? 0,
                outcome?.DamageSamples?.Values.Sum(h => h.Values.Sum()) ?? 0,
                outcome?.DotLanded ?? 0, outcome?.DotResisted ?? 0);
        }
    }

    private long? DamageBaseline(string spell, string character)
    {
        var key = (spell, character);
        if (_damageBaselines.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var samples = _data.Mobs.Values.SelectMany(m => m.Values)
            .Where(s => MobKey(s.Spell) == spell && s.DamageSamples?.ContainsKey(character) == true)
            .SelectMany(s => s.DamageSamples![character]).ToArray();
        if (samples.Length == 0)
        {
            _damageBaselines[key] = null;
            return null;
        }
        long maximum = samples.Max(s => s.Key);
        // Require repeat evidence near the upper observed damage, not one potentially anomalous hit.
        long evidence = samples.Where(s => (decimal)s.Key >= maximum * 0.9m).Sum(s => s.Value);
        long? baseline = evidence >= 3 ? maximum : null;
        _damageBaselines[key] = baseline;
        return baseline;
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
            _version++;
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

    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        // One save at a time, so overlapping periodic/final saves cannot overwrite newer data with an older snapshot.
        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Data snapshot;
            long version;
            SpellResistanceInfo[] spells;
            lock (_gate)
            {
                if (_savedVersion == _version)
                {
                    return;
                }
                snapshot = _data.Snapshot();
                spells = [.. _pendingSpells];
                version = _version;
            }
            if (spells.Length > 0)
            {
                // Spells go first: if the observations write then fails, the cache already holds them.
                await JsonLines.AppendAsync(SpellCachePath(path), spells, ct: ct).ConfigureAwait(false);
                lock (_gate)
                {
                    _pendingSpells.RemoveRange(0, spells.Length);
                }
            }
            await AtomicFile.WriteJsonAsync(path, snapshot, ct: ct).ConfigureAwait(false);
            lock (_gate)
            {
                _savedVersion = version;
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public static async Task<MobResistanceStore> LoadAsync(string path, CancellationToken ct = default)
    {
        var store = new MobResistanceStore();
        if (File.Exists(path))
        {
            store._data = await AtomicFile.ReadJsonAsync<Data>(path, ct: ct).ConfigureAwait(false)
                ?? throw new JsonException("Mob resistance data is empty.");
            var data = store._data;
            if (data.Mobs is null || data.Spells is null || data.Wiki is null || data.Watermarks is null ||
                data.Mobs.Values.Any(spells => spells is null || spells.Values.Any(s => s is null ||
                    string.IsNullOrWhiteSpace(s.Spell) || s.Landed < 0 || s.Resisted < 0 ||
                    s.DamageSamples?.Values.Any(h => h is null || h.Any(pair => pair.Key <= 0 || pair.Value <= 0)) == true)) ||
                data.Wiki.Values.Any(w => w is null || w.Resistances is null) ||
                data.Spells.Values.Any(s => s is null || string.IsNullOrWhiteSpace(s.Spell)) ||
                data.Watermarks.Values.Any(w => w is null))
            {
                throw new JsonException("Mob resistance data contains invalid or null entries.");
            }
            // Older files held the spell cache inline; queue it so the next save moves it to the spell cache file.
            if (data.Spells.Count > 0)
            {
                store._pendingSpells.AddRange(data.Spells.Values);
                store._version++;
            }
        }

        await MoveLegacySpellCacheAsync(path, ct).ConfigureAwait(false);
        var cached = await JsonLines.LoadLatestAsync<SpellResistanceInfo>(SpellCachePath(path), s => MobKey(s.Spell ?? ""),
            ct: ct).ConfigureAwait(false);
        foreach (var (key, spell) in cached)
        {
            if (!string.IsNullOrWhiteSpace(spell.Spell))
            {
                store._data.Spells[key] = spell;
            }
        }
        return store;
    }

    public sealed class Data
    {
        public Dictionary<string, Dictionary<string, SpellOutcome>> Mobs { get; set; } = [];
        public Dictionary<string, MobWikiInfo> Wiki { get; set; } = [];
        public Dictionary<string, SpellResistanceInfo> Spells { get; set; } = [];
        public Dictionary<string, ResistanceWatermark> Watermarks { get; set; } = [];

        /// <summary>
        /// A copy safe to serialize outside the lock: containers are copied, immutable records are shared,
        /// and the damage histograms (mutated in place) are copied.
        /// </summary>
        internal Data Snapshot()
        {
            var mobs = new Dictionary<string, Dictionary<string, SpellOutcome>>(Mobs.Count, Mobs.Comparer);
            foreach (var (mob, spells) in Mobs)
            {
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
                Wiki = new(Wiki, Wiki.Comparer),
                // The spell cache lives in its own append-only file.
                Spells = [],
                Watermarks = new(Watermarks, Watermarks.Comparer),
            };
        }
    }
}
