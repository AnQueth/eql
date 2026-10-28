using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EqLog.Core.Tracking;

public sealed record SpawnInfo(string Zone, string Mob, double? LearnedSeconds, int Samples, double? OverrideSeconds, bool Tracked, DateTime? LastKill = null, DateTime? LastSeen = null, double? WikiSeconds = null)
{
    public double? RespawnSeconds
    {
        get
        {
            return OverrideSeconds ?? LearnedSeconds ?? WikiSeconds;
        }
    }
}

/// <summary>
/// Learned respawn times per zone and mob, the user's overrides, and which mobs are tracked.
/// Zones are keyed by <see cref="BaseZone"/> so instance versions of a zone share what was learned.
/// Not thread-safe; the engine serializes access.
/// </summary>
public sealed partial class SpawnStore
{
    private const int MaxSamples = 30;

    private readonly Dictionary<string, Dictionary<string, Entry>> _zones = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _wikiRespawns = new(StringComparer.OrdinalIgnoreCase);

    public void SetWikiRespawn(string mob, double? seconds)
    {
        if (seconds is > 0 && double.IsFinite(seconds.Value))
        {
            _wikiRespawns[MobResistanceStore.MobKey(mob)] = seconds.Value;
        }
        else
        {
            _wikiRespawns.Remove(MobResistanceStore.MobKey(mob));
        }
    }

    /// <summary>Accepts explicit durations only, never guesses from ranges or spawn-condition prose.</summary>
    public static bool TryParseWikiRespawn(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        text = text.Trim();
        if (Regex.IsMatch(text, @"^\d+:\d{2}(?::\d{2})?$", RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            return TryParseDuration(text, out seconds) && double.IsFinite(seconds);
        }
        const string part = @"(?<value>\d+(?:\.\d+)?)\s*(?<unit>days?|d|hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)";
        if (!Regex.IsMatch(text, @"^(?:" + part + @"\s*)+$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
        {
            return false;
        }
        foreach (Match match in Regex.Matches(text, part, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
        {
            double value = double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
            seconds += value * (char.ToLowerInvariant(match.Groups["unit"].Value[0]) switch
            {
                'd' => 86400,
                'h' => 3600,
                'm' => 60,
                _ => 1,
            });
        }
        return seconds > 0 && double.IsFinite(seconds) && seconds <= TimeSpan.MaxValue.TotalSeconds;
    }

    public bool IsDirty
    {
        get; private set;
    }

    /// <summary>"Nektropos Castle 4 (Refined)" becomes "Nektropos Castle".</summary>
    public static string BaseZone(string zone)
    {
        return InstanceSuffixRegex().Replace(zone.Trim(), "");
    }

    /// <summary>
    /// Records that <paramref name="mob"/> was seen alive <paramref name="seconds"/> after it was killed at <paramref name="killedAt"/>.
    /// A sample with the same kill time (e.g. re-reading the log history on the next launch) is ignored.
    /// </summary>
    public void AddSample(string zone, string mob, DateTime killedAt, double seconds)
    {
        var e = GetOrAdd(zone, mob);
        if (e.Samples.Exists(x => x.At == killedAt))
        {
            return;
        }

        int index = e.Samples.FindLastIndex(x => x.At <= killedAt) + 1;
        e.Samples.Insert(index, new Sample(killedAt, Math.Round(seconds)));
        if (e.Samples.Count > MaxSamples)
        {
            e.Samples.RemoveAt(0);
        }

        IsDirty = true;
    }

    /// <summary>Makes sure the mob is listed for the zone (so it can be picked) without adding a sample.</summary>
    public void Remember(string zone, string mob)
    {
        if (!TryGet(zone, mob, out _))
        {
            GetOrAdd(zone, mob);
            IsDirty = true;
        }
    }

    public void SetTracked(string zone, string mob, bool tracked)
    {
        GetOrAdd(zone, mob).Tracked = tracked;
        IsDirty = true;
    }

    public void SetOverride(string zone, string mob, double? seconds)
    {
        GetOrAdd(zone, mob).Override = seconds is > 0 ? seconds : null;
        IsDirty = true;
    }

    /// <summary>Remembers when a listed mob was last killed, so its countdown survives restarts and new log files.</summary>
    public void NoteKill(string zone, string mob, DateTime at)
    {
        if (TryGet(zone, mob, out var e) && !(e.LastKill >= at))
        {
            e.LastKill = at;
            IsDirty = true;
        }
    }

    /// <summary>Remembers when a listed mob was last seen alive.</summary>
    public void NoteSeen(string zone, string mob, DateTime at)
    {
        if (TryGet(zone, mob, out var e) && !(e.LastSeen >= at))
        {
            e.LastSeen = at;
            IsDirty = true;
        }
    }

    public bool IsTracked(string zone, string mob)
    {
        return TryGet(zone, mob, out var e) && e.Tracked;
    }

    public SpawnInfo? Get(string zone, string mob)
    {
        return TryGet(zone, mob, out var e) ? Info(BaseZone(zone), mob, e) : null;
    }

    /// <summary>All mobs remembered for the zone.</summary>
    public IReadOnlyList<SpawnInfo> List(string zone)
    {
        string key = BaseZone(zone);
        return _zones.TryGetValue(key, out var mobs)
            ? mobs.Select(kv => Info(key, kv.Key, kv.Value)).ToList()
            : [];
    }

    private SpawnInfo Info(string zone, string mob, Entry e)
    {
        return new(zone, mob, Estimate(e.Samples.Select(x => x.Seconds)), e.Samples.Count, e.Override, e.Tracked,
            e.LastKill, e.LastSeen, _wikiRespawns.TryGetValue(MobResistanceStore.MobKey(mob), out double wiki) ? wiki : null);
    }

    /// <summary>
    /// Each sample is an upper bound (the mob had respawned by the time it was seen), so the respawn time is near the
    /// smallest sample. A single low outlier is ignored by preferring the smallest value that another sample agrees with.
    /// </summary>
    public static double? Estimate(IEnumerable<double> samples)
    {
        var s = samples.OrderBy(x => x).ToArray();
        if (s.Length == 0)
        {
            return null;
        }

        for (int i = 0; i < s.Length - 1; i++)
        {
            if (s[i + 1] <= (s[i] * 1.05) + 5)
            {
                return s[i];
            }
        }

        return s[0];
    }

    /// <summary>Parses "966", "16:06" or "1:02:03" into seconds.</summary>
    public static bool TryParseDuration(string text, out double seconds)
    {
        seconds = 0;
        text = text.Trim();
        if (text.Contains(':', StringComparison.Ordinal))
        {
            var parts = text.Split(':');
            if (parts.Length > 3)
            {
                return false;
            }

            foreach (var p in parts)
            {
                if (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                {
                    return false;
                }

                seconds = (seconds * 60) + n;
            }
        }
        else if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out seconds))
        {
            return false;
        }

        return seconds > 0;
    }

    private bool TryGet(string zone, string mob, out Entry entry)
    {
        entry = null!;
        return _zones.TryGetValue(BaseZone(zone), out var mobs) && mobs.TryGetValue(mob, out entry!);
    }

    private Entry GetOrAdd(string zone, string mob)
    {
        string key = BaseZone(zone);
        if (!_zones.TryGetValue(key, out var mobs))
        {
            mobs = new(StringComparer.OrdinalIgnoreCase);
            _zones[key] = mobs;
        }

        if (!mobs.TryGetValue(mob, out var e))
        {
            e = new Entry();
            mobs[mob] = e;
        }

        return e;
    }

    public static SpawnStore Load(string path)
    {
        var store = new SpawnStore();
        if (!File.Exists(path))
        {
            return store;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Entry>>>(File.ReadAllText(path));
            foreach (var (zone, mobs) in dto ?? [])
            {
                var map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                foreach (var (mob, e) in mobs)
                {
                    e.Samples = [.. (e.Samples ?? []).OrderBy(x => x.At).TakeLast(MaxSamples)];
                    map[mob] = e;
                }

                store._zones[zone] = map;
            }
        }
        catch (JsonException)
        {
            // Corrupt file: start fresh rather than failing to launch.
        }

        return store;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(_zones, new JsonSerializerOptions { WriteIndented = true }));
        IsDirty = false;
    }

    public sealed class Entry
    {
        public List<Sample> Samples { get; set; } = [];

        public double? Override
        {
            get; set;
        }

        public bool Tracked
        {
            get; set;
        }

        public DateTime? LastKill
        {
            get; set;
        }

        public DateTime? LastSeen
        {
            get; set;
        }
    }

    /// <summary>A kill at <see cref="At"/> after which the mob was next seen alive <see cref="Seconds"/> later.</summary>
    public sealed record Sample(DateTime At, double Seconds);

    [GeneratedRegex(@"\s+\d+\s+\([^)]*\)$")]
    private static partial Regex InstanceSuffixRegex();
}
