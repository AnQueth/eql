using System.Text.Json;

namespace EqLog.Core.Tracking;

public sealed record MoteCount(string Tier, int Count);

public sealed record MoteMobRate(string Mob, int Kills, int Drops, IReadOnlyList<MoteCount> Tiers)
{
    /// <summary>Mote drops per kill, or null when no kills are known (e.g. a reward chest).</summary>
    public double? DropRate
    {
        get
        {
            return Kills > 0 ? (double)Drops / Kills : null;
        }
    }
}

/// <summary>
/// All-time mote totals per character and mote drops/kills per zone and mob, saved across sessions.
/// Each character has a watermark (the time of the last counted event and how many were counted at that second),
/// so reading the same log again on the next launch doesn't count anything twice.
/// Not thread-safe; the engine serializes access.
/// </summary>
public sealed class MoteStore : ISnapshotStore<MoteStore.Data>
{
    private Data _data = new();

    public bool IsDirty
    {
        get; private set;
    }

    /// <summary>Returns true if the event at <paramref name="time"/> hasn't been counted yet, and marks it counted.</summary>
    /// <param name="indexAtTime">1-based position of this event among the character's counted-type events at that same second.</param>
    public bool TryClaim(string character, DateTime time, int indexAtTime)
    {
        var c = Character(character);
        if (c.Watermark is DateTime w && (time < w || (time == w && indexAtTime <= c.WatermarkCount)))
        {
            return false;
        }

        if (c.Watermark == time)
        {
            c.WatermarkCount++;
        }
        else
        {
            c.Watermark = time;
            c.WatermarkCount = 1;
        }

        IsDirty = true;
        return true;
    }

    public void AddMote(string character, string tier, int quantity)
    {
        var tiers = Character(character).Tiers;
        tiers[tier] = tiers.GetValueOrDefault(tier) + quantity;
        IsDirty = true;
    }

    public void AddKill(string zone, string mob)
    {
        Mob(zone, mob).Kills++;
        IsDirty = true;
    }

    public void AddDrop(string zone, string mob, string tier, int quantity)
    {
        var m = Mob(zone, mob);
        m.Drops++;
        m.Tiers[tier] = m.Tiers.GetValueOrDefault(tier) + quantity;
        IsDirty = true;
    }

    public bool HasKills(string zone, string mob)
    {
        return _data.Zones.TryGetValue(SpawnStore.BaseZone(zone), out var mobs) && mobs.TryGetValue(mob, out var m) && m.Kills > 0;
    }

    public IReadOnlyList<MoteCount> Totals(string character)
    {
        return _data.Characters.TryGetValue(character, out var c) ? MoteTiers.Sort(c.Tiers) : [];
    }

    /// <summary>Mobs in the zone that have dropped at least one mote.</summary>
    public IReadOnlyList<MoteMobRate> ZoneRates(string zone)
    {
        return _data.Zones.TryGetValue(SpawnStore.BaseZone(zone), out var mobs)
            ? mobs.Where(kv => kv.Value.Drops > 0)
                .Select(kv => new MoteMobRate(kv.Key, kv.Value.Kills, kv.Value.Drops, MoteTiers.Sort(kv.Value.Tiers)))
                .ToList()
            : [];
    }

    private CharacterData Character(string name)
    {
        if (!_data.Characters.TryGetValue(name, out var c))
        {
            c = new CharacterData();
            _data.Characters[name] = c;
        }

        return c;
    }

    private MobData Mob(string zone, string mob)
    {
        string key = SpawnStore.BaseZone(zone);
        if (!_data.Zones.TryGetValue(key, out var mobs))
        {
            mobs = new(StringComparer.OrdinalIgnoreCase);
            _data.Zones[key] = mobs;
        }

        if (!mobs.TryGetValue(mob, out var m))
        {
            m = new MobData();
            mobs[mob] = m;
        }

        return m;
    }

    public static async Task<MoteStore> LoadAsync(string path, CancellationToken ct = default)
    {
        var store = new MoteStore();
        try
        {
            if (File.Exists(path))
            {
                var data = await AtomicFile.ReadJsonAsync<Data>(path, ct: ct).ConfigureAwait(false);
                if (data is not null)
                {
                    store._data = Normalize(data);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Start fresh rather than fail to launch.
        }

        return store;
    }

    /// <summary>Saves without locking; use only when nothing else is changing the store.</summary>
    public Task SaveAsync(string path, CancellationToken ct = default)
    {
        return SnapshotStore.SaveAsync(this, null, path, onlyIfDirty: false, ct);
    }

    SemaphoreSlim ISnapshotStore<Data>.SaveGate { get; } = new(1, 1);

    Data? ISnapshotStore<Data>.TakeSnapshot(bool onlyIfDirty)
    {
        if (onlyIfDirty && !IsDirty)
        {
            return null;
        }
        var snapshot = Normalize(_data);
        IsDirty = false;
        return snapshot;
    }

    void ISnapshotStore<Data>.MarkDirty()
    {
        IsDirty = true;
    }

    // Deep copy with case-insensitive dictionaries (System.Text.Json creates case-sensitive ones); also used for save snapshots.
    private static Data Normalize(Data d)
    {
        var result = new Data();
        foreach (var (name, c) in d.Characters)
        {
            result.Characters[name] = new CharacterData
            {
                Watermark = c.Watermark,
                WatermarkCount = c.WatermarkCount,
                Tiers = new(c.Tiers, StringComparer.OrdinalIgnoreCase),
            };
        }

        foreach (var (zone, mobs) in d.Zones)
        {
            var copy = new Dictionary<string, MobData>(StringComparer.OrdinalIgnoreCase);
            foreach (var (mob, m) in mobs)
            {
                copy[mob] = new MobData { Kills = m.Kills, Drops = m.Drops, Tiers = new(m.Tiers, StringComparer.OrdinalIgnoreCase) };
            }

            result.Zones[zone] = copy;
        }

        return result;
    }

    internal sealed class Data
    {
        public Dictionary<string, CharacterData> Characters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, Dictionary<string, MobData>> Zones { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class CharacterData
    {
        public DateTime? Watermark
        {
            get; set;
        }

        public int WatermarkCount
        {
            get; set;
        }

        public Dictionary<string, int> Tiers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class MobData
    {
        public int Kills
        {
            get; set;
        }

        public int Drops
        {
            get; set;
        }

        public Dictionary<string, int> Tiers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
