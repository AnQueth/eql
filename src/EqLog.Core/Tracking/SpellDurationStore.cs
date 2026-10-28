using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EqLog.Core.Tracking;

public sealed record SpellDurationInfo(string Spell, double? LearnedSeconds, int Samples, double? OverrideSeconds,
    bool Tracked = true, double? WikiSeconds = null);

/// <summary>
/// Spell durations learned by measuring the time between casting a spell and seeing
/// "Your X spell has worn off of Y.", plus user overrides and wiki fallbacks. Not thread-safe; the engine serializes access.
/// </summary>
public sealed class SpellDurationStore : ISnapshotStore<SpellDurationStore.Dto>
{
    private const int MaxSamples = 25;
    private const double Percentile = 0.8;

    private readonly Dictionary<string, List<Sample>> _samples = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _wikiDurations = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _untracked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);

    public bool IsDirty
    {
        get; private set;
    }

    /// <summary>
    /// Records a measured duration. <paramref name="observedAt"/> is the wear-off time; a second sample
    /// with the same wear-off time (e.g. re-reading the log history on the next launch) is ignored.
    /// </summary>
    public void AddSample(string spell, double seconds, DateTime observedAt)
    {
        if (_removed.Contains(spell))
        {
            return;
        }

        if (!_samples.TryGetValue(spell, out var list))
        {
            list = [];
            _samples[spell] = list;
        }

        if (list.Exists(x => x.At == observedAt))
        {
            return;
        }

        var sample = new Sample(observedAt, Math.Round(seconds, 1));

        // Keep the newest samples by observation time.
        int index = list.FindLastIndex(x => x.At <= observedAt) + 1;
        list.Insert(index, sample);
        if (list.Count > MaxSamples)
        {
            list.RemoveAt(0);
        }

        IsDirty = true;
    }

    public void SetOverride(string spell, double? seconds)
    {
        if (seconds.HasValue && !double.IsFinite(seconds.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), "Duration must be finite.");
        }
        if (seconds is > 0)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(seconds.Value, TimeSpan.MaxValue.TotalSeconds - 60);
            _removed.Remove(spell);
            _overrides[spell] = seconds.Value;
        }
        else
        {
            _overrides.Remove(spell);
        }

        IsDirty = true;
    }

    /// <summary>Caches a wiki estimate without replacing a learned duration or reviving a removed spell.</summary>
    public void SetWikiDuration(string spell, double? seconds)
    {
        if (_removed.Contains(spell))
        {
            return;
        }

        if (seconds is > 0 && double.IsFinite(seconds.Value) && seconds.Value <= TimeSpan.MaxValue.TotalSeconds - 60)
        {
            if (_wikiDurations.TryGetValue(spell, out double previous) && previous == seconds.Value)
            {
                return;
            }
            _wikiDurations[spell] = seconds.Value;
        }
        else if (!_wikiDurations.Remove(spell))
        {
            return;
        }
        IsDirty = true;
    }

    /// <summary>Parses explicit wiki durations, including an "Up to" maximum and six-second ticks.</summary>
    public static bool TryParseWikiDuration(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = Regex.Replace(text.Trim(), @"^Up to\s+", "", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        var ticks = Regex.Match(text, @"^(?<value>\d+(?:\.\d+)?)\s*ticks?$",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        if (ticks.Success)
        {
            seconds = double.Parse(ticks.Groups["value"].Value, CultureInfo.InvariantCulture) * 6;
        }
        else if (!SpawnStore.TryParseWikiRespawn(text, out seconds))
        {
            return false;
        }

        return seconds > 0 && double.IsFinite(seconds) && seconds <= TimeSpan.MaxValue.TotalSeconds - 60;
    }

    /// <summary>Deletes this spell's saved data and prevents automatic rediscovery until an override is added.</summary>
    public void Remove(string spell)
    {
        _samples.Remove(spell);
        _overrides.Remove(spell);
        _wikiDurations.Remove(spell);
        _untracked.Remove(spell);
        _removed.Add(spell);
        IsDirty = true;
    }

    public void SetTracked(string spell, bool tracked)
    {
        if (_removed.Contains(spell))
        {
            return;
        }
        bool changed = tracked
            ? _untracked.RemoveWhere(s => SpellNames.SameBase(s, spell)) > 0
            : _untracked.Add(spell);
        if (changed)
        {
            IsDirty = true;
        }
    }

    /// <summary>False when the user unchecked this spell (or another rank of it); durations are still learned.</summary>
    public bool IsTracked(string spell)
    {
        return !_removed.Contains(spell) && !_untracked.Contains(spell) && SpellNames.FindByBase(_untracked, spell) is null;
    }

    /// <summary>True when the spell (or another rank of it) has a learned, overridden or wiki duration.</summary>
    public bool IsKnown(string spell)
    {
        return GetDuration(spell) is not null;
    }

    /// <summary>Uses overrides and learned durations before wiki estimates, with fallback across ranks.</summary>
    public double? GetDuration(string spell)
    {
        if (_removed.Contains(spell))
        {
            return null;
        }

        if (_overrides.TryGetValue(spell, out double o))
        {
            return o;
        }

        if (_samples.TryGetValue(spell, out var s) && s.Count > 0)
        {
            return Estimate(s);
        }

        string? alt = SpellNames.FindByBase(_overrides.Keys, spell);
        if (alt is not null)
        {
            return _overrides[alt];
        }

        alt = SpellNames.FindByBase(_samples.Keys, spell);
        if (alt is not null && _samples[alt].Count > 0)
        {
            return Estimate(_samples[alt]);
        }

        if (_wikiDurations.TryGetValue(spell, out double wiki))
        {
            return wiki;
        }

        alt = SpellNames.FindByBase(_wikiDurations.Keys, spell);
        return alt is not null ? _wikiDurations[alt] : null;
    }

    public IReadOnlyList<SpellDurationInfo> List()
    {
        return _samples.Keys.Union(_overrides.Keys, StringComparer.OrdinalIgnoreCase)
            .Union(_wikiDurations.Keys, StringComparer.OrdinalIgnoreCase).Union(_untracked, StringComparer.OrdinalIgnoreCase)
            .Where(k => !_removed.Contains(k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(k => new SpellDurationInfo(
                k,
                _samples.TryGetValue(k, out var s) && s.Count > 0 ? Estimate(s) : null,
                _samples.TryGetValue(k, out var s2) ? s2.Count : 0,
                _overrides.TryGetValue(k, out var o) ? o : null,
                IsTracked(k),
                _wikiDurations.TryGetValue(k, out var wiki) ? wiki : null))
            .ToList();
    }

    // Mez/charm often break early, so use a high percentile instead of the mean to approximate the full duration.
    private static double Estimate(List<Sample> samples)
    {
        var sorted = samples.Select(x => x.Seconds).OrderBy(x => x).ToArray();
        int idx = Math.Clamp((int)Math.Ceiling(Percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[idx];
    }

    public static async Task<SpellDurationStore> LoadAsync(string path, CancellationToken ct = default)
    {
        var store = new SpellDurationStore();
        if (!File.Exists(path))
        {
            return store;
        }

        try
        {
            var dto = await AtomicFile.ReadJsonAsync<Dto>(path, ct: ct).ConfigureAwait(false);
            if (dto is not null)
            {
                foreach (var kv in dto.Samples ?? [])
                {
                    store._samples[kv.Key] = kv.Value.OrderBy(x => x.At).TakeLast(MaxSamples).ToList();
                }

                foreach (var kv in dto.Overrides ?? [])
                {
                    store._overrides[kv.Key] = kv.Value;
                }

                store._untracked.UnionWith(dto.Untracked ?? []);
                store._removed.UnionWith(dto.Removed ?? []);
                foreach (var kv in dto.WikiDurations ?? [])
                {
                    store._wikiDurations[kv.Key] = kv.Value;
                }
            }
        }
        catch (JsonException)
        {
            // Corrupt file: start fresh rather than failing to launch.
        }

        return store;
    }

    /// <summary>Saves without locking; use only when nothing else is changing the store.</summary>
    public Task SaveAsync(string path, CancellationToken ct = default)
    {
        return SnapshotStore.SaveAsync(this, null, path, onlyIfDirty: false, ct);
    }

    SemaphoreSlim ISnapshotStore<Dto>.SaveGate { get; } = new(1, 1);

    Dto? ISnapshotStore<Dto>.TakeSnapshot(bool onlyIfDirty)
    {
        if (onlyIfDirty && !IsDirty)
        {
            return null;
        }
        // Samples are immutable records, so copying the lists is enough to detach the snapshot.
        var dto = new Dto
        {
            Samples = _samples.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase),
            Overrides = new(_overrides, StringComparer.OrdinalIgnoreCase),
            Untracked = _untracked.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            Removed = _removed.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            WikiDurations = new(_wikiDurations, StringComparer.OrdinalIgnoreCase),
        };
        IsDirty = false;
        return dto;
    }

    void ISnapshotStore<Dto>.MarkDirty()
    {
        IsDirty = true;
    }

    internal sealed class Dto
    {
        public Dictionary<string, List<Sample>>? Samples
        {
            get; set;
        }

        public Dictionary<string, double>? Overrides
        {
            get; set;
        }

        public List<string>? Untracked
        {
            get; set;
        }

        public List<string>? Removed
        {
            get; set;
        }

        public Dictionary<string, double>? WikiDurations
        {
            get; set;
        }
    }

    public sealed record Sample(DateTime At, double Seconds);
}
