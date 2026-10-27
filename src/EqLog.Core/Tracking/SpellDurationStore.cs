using System.Text.Json;

namespace EqLog.Core.Tracking;

public sealed record SpellDurationInfo(string Spell, double? LearnedSeconds, int Samples, double? OverrideSeconds);

/// <summary>
/// Spell durations learned by measuring the time between casting a spell and seeing
/// "Your X spell has worn off of Y.", plus user overrides. Not thread-safe; the engine serializes access.
/// </summary>
public sealed class SpellDurationStore
{
    private const int MaxSamples = 25;
    private const double Percentile = 0.8;

    private readonly Dictionary<string, List<Sample>> _samples = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _overrides = new(StringComparer.OrdinalIgnoreCase);

    public bool IsDirty { get; private set; }

    /// <summary>
    /// Records a measured duration. <paramref name="observedAt"/> is the wear-off time; a second sample
    /// with the same wear-off time (e.g. re-reading the log history on the next launch) is ignored.
    /// </summary>
    public void AddSample(string spell, double seconds, DateTime observedAt)
    {
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
        if (seconds is > 0)
        {
            _overrides[spell] = seconds.Value;
        }
        else
        {
            _overrides.Remove(spell);
        }

        IsDirty = true;
    }

    /// <summary>True when the spell (or another rank of it) has a learned or overridden duration.</summary>
    public bool IsKnown(string spell) => GetDuration(spell) is not null;

    /// <summary>Override first, then learned duration; falls back to another rank of the same spell.</summary>
    public double? GetDuration(string spell)
    {
        if (_overrides.TryGetValue(spell, out double o)) return o;
        if (_samples.TryGetValue(spell, out var s) && s.Count > 0) return Estimate(s);

        string? alt = SpellNames.FindByBase(_overrides.Keys, spell);
        if (alt is not null) return _overrides[alt];
        alt = SpellNames.FindByBase(_samples.Keys, spell);
        return alt is not null && _samples[alt].Count > 0 ? Estimate(_samples[alt]) : null;
    }

    public IReadOnlyList<SpellDurationInfo> List() =>
        _samples.Keys.Union(_overrides.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(k => new SpellDurationInfo(
                k,
                _samples.TryGetValue(k, out var s) && s.Count > 0 ? Estimate(s) : null,
                _samples.TryGetValue(k, out var s2) ? s2.Count : 0,
                _overrides.TryGetValue(k, out var o) ? o : null))
            .ToList();

    // Mez/charm often break early, so use a high percentile instead of the mean to approximate the full duration.
    private static double Estimate(List<Sample> samples)
    {
        var sorted = samples.Select(x => x.Seconds).OrderBy(x => x).ToArray();
        int idx = Math.Clamp((int)Math.Ceiling(Percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[idx];
    }

    public static SpellDurationStore Load(string path)
    {
        var store = new SpellDurationStore();
        if (!File.Exists(path))
        {
            return store;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path));
            if (dto is not null)
            {
                foreach (var kv in dto.Samples ?? []) store._samples[kv.Key] = kv.Value.OrderBy(x => x.At).TakeLast(MaxSamples).ToList();
                foreach (var kv in dto.Overrides ?? []) store._overrides[kv.Key] = kv.Value;
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
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var dto = new Dto { Samples = new(_samples), Overrides = new(_overrides) };
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        IsDirty = false;
    }

    private sealed class Dto
    {
        public Dictionary<string, List<Sample>>? Samples { get; set; }

        public Dictionary<string, double>? Overrides { get; set; }
    }

    public sealed record Sample(DateTime At, double Seconds);
}
