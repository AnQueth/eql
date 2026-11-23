using EqLog.Core.Parsing;

namespace EqLog.Core.Tracking;

public sealed record LevelingContext(string Visit, string? Zone, int? Level, string? Build);

public sealed record LevelingMinute(
    string Source, string Character, LevelingContext Context, DateTime Minute,
    DateTime Start, DateTime End, double Seconds = 0, double Experience = 0,
    int Gains = 0, int UnknownGains = 0, int Rewards = 0, int Kills = 0, int Deaths = 0,
    DateTime? LevelStarted = null);

/// <summary>Minute-resolution history; watermarks and observations are committed together.</summary>
public sealed class LevelingHistory
{
    public sealed record Watermark(DateTime Time, int Index);
    public sealed record Data(List<LevelingMinute> Minutes, Dictionary<string, Watermark> Watermarks);

    private readonly object _gate = new();
    private readonly Dictionary<(string Source, string Character, LevelingContext Context, DateTime Minute), LevelingMinute> _minutes = [];
    private Dictionary<string, Watermark> _watermarks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Source, string Character, LevelingContext Context, DateTime Minute), long> _changedMinutes = [];
    private readonly Dictionary<string, long> _changedWatermarks = new(StringComparer.OrdinalIgnoreCase);
    private long _version;
    private long _savedVersion;

    public long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    internal void Record(string source, string character, DateTime time, int index,
        DateTime? previous, LevelingContext? before, LevelingContext current, LogEvent? observation)
    {
        source = source.ToUpperInvariant();
        character = character.ToUpperInvariant();
        string key = source + "\t" + character;
        lock (_gate)
        {
            if (_watermarks.TryGetValue(key, out var watermark) &&
                (time < watermark.Time || (time == watermark.Time && index <= watermark.Index)))
            {
                // Older saves lack level-up timestamps, but their XP must not be replayed.
                if (observation is LevelEvent &&
                    _minutes.TryGetValue((source, character, current, FloorMinute(time)), out var existing) &&
                    existing.LevelStarted is null)
                {
                    Put(existing with
                    {
                        LevelStarted = time
                    });
                    _version++;
                }
                return;
            }

            if (previous is DateTime start && before is not null &&
                time > start && time - start <= LevelingTracker.VisitGap)
            {
                while (start < time)
                {
                    DateTime minute = FloorMinute(start);
                    DateTime end = time < minute.AddMinutes(1) ? time : minute.AddMinutes(1);
                    var row = Get(source, character, before, minute, start);
                    Put(row with
                    {
                        Start = row.Start < start ? row.Start : start,
                        End = row.End > end ? row.End : end,
                        Seconds = row.Seconds + (end - start).TotalSeconds,
                    });
                    start = end;
                }
            }

            if (observation is ExperienceEvent or KillEvent or ZoneEvent or WhoLoadoutEvent or LevelEvent)
            {
                var row = Get(source, character, current, FloorMinute(time), time);
                if (observation is LevelEvent)
                {
                    row = row with
                    {
                        LevelStarted = row.LevelStarted ?? time
                    };
                }
                if (observation is ExperienceEvent xp)
                {
                    row = xp.Kind == ExperienceKind.Reward
                        ? row with
                        {
                            Rewards = row.Rewards + 1
                        }
                        : row with
                        {
                            Experience = row.Experience + (xp.Percent ?? 0),
                            Gains = row.Gains + 1,
                            UnknownGains = row.UnknownGains + (xp.Percent.HasValue ? 0 : 1),
                        };
                }
                if (observation is KillEvent kill)
                {
                    row = row with
                    {
                        Kills = row.Kills + (kill.Killer == Names.You && kill.Victim != Names.You ? 1 : 0),
                        Deaths = row.Deaths + (kill.Victim == Names.You ? 1 : 0),
                    };
                }
                Put(row with
                {
                    End = row.End > time ? row.End : time
                });
            }
            _watermarks[key] = new(time, index);
            _changedWatermarks[key] = _version + 1;
            _version++;
        }
    }

    private LevelingMinute Get(string source, string character, LevelingContext context, DateTime minute, DateTime at)
    {
        return _minutes.GetValueOrDefault((source, character, context, minute)) ??
            new(source, character, context, minute, at, at);
    }

    private void Put(LevelingMinute row)
    {
        var key = (row.Source, row.Character, row.Context, row.Minute);
        _minutes[key] = row;
        _changedMinutes[key] = _version + 1;
    }

    public static DateTime FloorMinute(DateTime time)
    {
        return new DateTime(time.Ticks - time.Ticks % TimeSpan.TicksPerMinute, time.Kind);
    }

    public IReadOnlyList<LevelingMinute> Snapshot(string character, DateTime start, DateTime end)
    {
        lock (_gate)
        {
            return _minutes.Values.Where(row =>
                row.Character.Equals(character, StringComparison.OrdinalIgnoreCase) &&
                row.Minute >= start && row.Minute < end).ToArray();
        }
    }

    public (long Version, Data Data)? Capture()
    {
        lock (_gate)
        {
            if (_version == _savedVersion)
            {
                return null;
            }
            return (_version, new([.. _minutes.Values], new(_watermarks, StringComparer.OrdinalIgnoreCase)));
        }
    }

    public void CompleteSave(long version)
    {
        lock (_gate)
        {
            _savedVersion = Math.Max(_savedVersion, version);
            foreach (var key in _changedMinutes.Where(pair => pair.Value <= version).Select(pair => pair.Key).ToArray())
            {
                _changedMinutes.Remove(key);
            }
            foreach (string key in _changedWatermarks.Where(pair => pair.Value <= version).Select(pair => pair.Key).ToArray())
            {
                _changedWatermarks.Remove(key);
            }
        }
    }

    /// <summary>Captures only changed minutes and watermarks as an atomic, idempotent persistence batch.</summary>
    public (long Version, Data Data)? CaptureChanges()
    {
        lock (_gate)
        {
            if (_version == _savedVersion)
            {
                return null;
            }
            return (_version, new(
                _changedMinutes.Keys.Select(key => _minutes[key]).ToList(),
                _changedWatermarks.Keys.ToDictionary(key => key, key => _watermarks[key], StringComparer.OrdinalIgnoreCase)));
        }
    }

    public void Import(Data data)
    {
        lock (_gate)
        {
            _minutes.Clear();
            foreach (var row in data.Minutes)
            {
                Put(row);
            }
            _watermarks = new(data.Watermarks, StringComparer.OrdinalIgnoreCase);
            _changedMinutes.Clear();
            _changedWatermarks.Clear();
            _version++;
            _savedVersion = _version;
        }
    }
}

/// <summary>Reconstructs historical context during replay even for lines already persisted.</summary>
public sealed class LevelingTracker(LevelingHistory history, string source)
{
    public static readonly TimeSpan VisitGap = TimeSpan.FromMinutes(30);
    private DateTime? _last;
    private int _index;
    private LevelingContext? _context;

    public void OnLine(DateTime time, LogEvent? observation, string character)
    {
        int index = _last == time ? _index + 1 : 1;
        var before = _context;
        bool newVisit = _last is null || time < _last || time - _last > VisitGap;
        string? zone = before?.Zone;
        int? level = before?.Level;
        string? build = before?.Build;
        switch (observation)
        {
            case LoginEvent:
                zone = null;
                level = null;
                build = null;
                newVisit = true;
                break;
            case ZoneEvent entered:
                zone = entered.Zone;
                newVisit = true;
                break;
            case LevelEvent leveled:
                newVisit |= level != leveled.Level;
                level = leveled.Level;
                break;
            case WhoLoadoutEvent who:
                newVisit |= level != who.Level;
                level = who.Level;
                build = who.Classes;
                break;
        }
        string visit = newVisit ? $"{time.Ticks}:{index}" : before!.Visit;
        _context = new(visit, zone, level, build);
        history.Record(source, character, time, index, _last, before, _context, observation);
        _last = time;
        _index = index;
    }
}
