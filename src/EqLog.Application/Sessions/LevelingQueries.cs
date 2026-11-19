using EqLog.Core.Tracking;

namespace EqLog.Application.Sessions;

public enum LevelingPeriod
{
    Today,
    Yesterday,
    LastSevenDays,
    LastWeek,
    Custom,
    AllHistory,
}

public sealed record LevelingRequest(string Character, DateTime Start, DateTime End,
    int WindowMinutes = 15, bool GroupByBuild = false, string Zone = "", int? Level = null);

public sealed record LevelingSummary(string Zone, int? Level, string? Build, DateTime Start, DateTime End,
    double Seconds, double Experience, int Gains, int UnknownGains, int Rewards, int Kills, int Deaths,
    int Visits, double? PercentPerHour, double? BestWindowExperience, DateTime? BestWindowStart);

public sealed record LevelingReport(IReadOnlyList<LevelingSummary> History,
    IReadOnlyList<LevelingSummary> Places, LevelingSummary? Recent, DateTime? LatestActivity)
{
    public IReadOnlyList<LevelingLevel> Levels { get; init; } = [];
}

public sealed record LevelingLevel(int? Level, DateTime Start, DateTime? End, bool IsCurrent,
    bool StartConfirmed, bool EndConfirmed, IReadOnlyList<LevelingSummary> Sources)
{
    public int Kills { get; init; }
    public double Seconds { get; init; }
    public IReadOnlyList<string?> Builds { get; init; } = [];
}

public static class LevelingQueries
{
    public static (DateTime Start, DateTime End) Period(LevelingPeriod period, DateTime now,
        DateTime? customStart = null, DateTime? customEnd = null)
    {
        now = LevelingHistory.FloorMinute(now);
        DateTime today = now.Date;
        DateTime monday = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
        return period switch
        {
            LevelingPeriod.Today => (today, today.AddDays(1)),
            LevelingPeriod.Yesterday => (today.AddDays(-1), today),
            LevelingPeriod.LastSevenDays => (today.AddDays(-6), today.AddDays(1)),
            LevelingPeriod.LastWeek => (monday.AddDays(-7), monday),
            LevelingPeriod.AllHistory => (DateTime.MinValue, LevelingHistory.FloorMinute(DateTime.MaxValue)),
            LevelingPeriod.Custom when customStart.HasValue && customEnd.HasValue &&
                customEnd.Value.Date >= customStart.Value.Date && customEnd.Value.Date < DateTime.MaxValue.Date =>
                (customStart.Value.Date, customEnd.Value.Date.AddDays(1)),
            _ => throw new ArgumentException("Choose a valid start and end date."),
        };
    }

    public static LevelingReport Prepare(IReadOnlyList<LevelingMinute> history, LevelingRequest request)
    {
        if (request.End <= request.Start || request.Start != LevelingHistory.FloorMinute(request.Start) ||
            request.End != LevelingHistory.FloorMinute(request.End) ||
            request.WindowMinutes is not (5 or 15 or 30 or 60))
        {
            throw new ArgumentException("Choose a valid minute-aligned time range and a 5, 15, 30 or 60 minute window.");
        }

        var character = history.Where(row =>
            row.Character.Equals(request.Character, StringComparison.OrdinalIgnoreCase)).ToArray();
        bool Matches(LevelingMinute row)
        {
            return row.Minute >= request.Start && row.Minute < request.End &&
                (request.Level is null || row.Context.Level == request.Level) &&
                (string.IsNullOrWhiteSpace(request.Zone) ||
                    (row.Context.Zone ?? "Unknown zone").Contains(request.Zone.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        var minutes = character.Where(Matches)
            .Where(row => row.Seconds > 0 || row.Gains > 0 || row.Rewards > 0 || row.Kills > 0 || row.Deaths > 0)
            .ToArray();
        string? Build(LevelingMinute row)
        {
            return request.GroupByBuild ? row.Context.Build ?? "Unknown build" : null;
        }

        var visits = minutes.GroupBy(row => (row.Source, row.Context.Visit, Day: row.Minute.Date,
                Zone: row.Context.Zone ?? "Unknown zone", row.Context.Level, Build: Build(row)))
            .Select(group => Summarize(group.ToArray(), group.Key.Zone, group.Key.Level, group.Key.Build, request.WindowMinutes))
            .OrderByDescending(row => row.Start).ToArray();
        var places = minutes.GroupBy(row => (Zone: row.Context.Zone ?? "Unknown zone", row.Context.Level, Build: Build(row)))
            .Select(group => Summarize(group.ToArray(), group.Key.Zone, group.Key.Level, group.Key.Build, request.WindowMinutes))
            .OrderBy(row => row.Level ?? int.MaxValue)
            .ThenByDescending(row => row.BestWindowExperience)
            .ThenByDescending(row => row.PercentPerHour).ToArray();
        DateTime? latest = minutes.Length > 0 ? minutes.Max(row => row.End) : null;
        LevelingSummary? recent = null;
        if (latest is DateTime last)
        {
            var tail = minutes.MaxBy(row => row.End)!;
            DateTime end = LevelingHistory.FloorMinute(last);
            DateTime start = end.AddMinutes(-request.WindowMinutes);
            var window = minutes.Where(row => row.Source == tail.Source &&
                row.Context.Visit == tail.Context.Visit && row.Context.Level == tail.Context.Level &&
                Build(row) == Build(tail) && row.Minute >= start && row.Minute < end).ToArray();
            if (window.Length > 0)
            {
                recent = Summarize(window, tail.Context.Zone ?? "Unknown zone", tail.Context.Level,
                    Build(tail), request.WindowMinutes);
            }
        }
        var known = character.Where(row => row.Context.Level.HasValue)
            .OrderBy(row => row.Start).ThenBy(row => row.End).ThenBy(row => row.Context.Level).ToArray();
        var runs = new List<List<LevelingMinute>>();
        foreach (var row in known)
        {
            if (runs.Count == 0 || runs[^1][^1].Context.Level != row.Context.Level)
            {
                runs.Add([]);
            }
            runs[^1].Add(row);
        }
        int currentRun = runs.Count - 1;
        var unknown = character.Where(row => row.Context.Level is null).ToList();
        if (unknown.Any(row => row.Gains > 0 || row.Rewards > 0))
        {
            runs.Add(unknown);
        }
        var levels = new List<LevelingLevel>();
        for (int index = 0; index < runs.Count; index++)
        {
            var group = runs[index];
            if (!group.Any(Matches))
            {
                continue;
            }
            int? level = group[0].Context.Level;
            DateTime first = group.Min(row => row.Start);
            DateTime lastObservation = group.Max(row => row.End);
            bool current = index == currentRun;
            var next = index < currentRun ? runs[index + 1] : null;
            DateTime? nextStart = next?.Min(row => row.Start);
            var sources = group.GroupBy(row => (Zone: row.Context.Zone ?? "Unknown zone", Build: Build(row)))
                .Select(source => Summarize(source.ToArray(), source.Key.Zone, level, source.Key.Build, request.WindowMinutes))
                .Where(source => source.Gains > 0 || source.Rewards > 0)
                .OrderByDescending(source => source.Experience).ThenBy(source => source.Zone).ThenBy(source => source.Build)
                .ToArray();
            levels.Add(new(level, first, level is null ? lastObservation : nextStart, current,
                group.Any(row => row.LevelStarted == first),
                next is not null && next[0].Context.Level == level + 1 &&
                    next.Any(row => row.LevelStarted == nextStart),
                sources)
            {
                Kills = group.Sum(row => row.Kills),
                Seconds = group.Sum(row => row.Seconds),
                Builds = group.Select(row => row.Context.Build).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            });
        }
        return new(visits, places, recent, latest)
        {
            Levels = levels.OrderByDescending(row => row.Start).ThenByDescending(row => row.Level).ToArray(),
        };
    }

    private static LevelingSummary Summarize(LevelingMinute[] rows, string zone, int? level, string? build, int window)
    {
        double seconds = rows.Sum(row => row.Seconds);
        double xp = rows.Sum(row => row.Experience);
        int unknown = rows.Sum(row => row.UnknownGains);
        double? best = null;
        DateTime? bestStart = null;
        foreach (var visit in rows.GroupBy(row => (row.Source, row.Context.Visit)))
        {
            var minutes = visit.GroupBy(row => row.Minute).OrderBy(group => group.Key)
                .Select(group => (Time: group.Key, Seconds: group.Sum(row => row.Seconds),
                    Xp: group.Sum(row => row.Experience), Unknown: group.Sum(row => row.UnknownGains))).ToArray();
            double duration = 0;
            double total = 0;
            int missing = 0;
            int left = 0;
            for (int right = 0; right < minutes.Length; right++)
            {
                duration += minutes[right].Seconds;
                total += minutes[right].Xp;
                missing += minutes[right].Unknown;
                while (minutes[right].Time - minutes[left].Time >= TimeSpan.FromMinutes(window))
                {
                    duration -= minutes[left].Seconds;
                    total -= minutes[left].Xp;
                    missing -= minutes[left].Unknown;
                    left++;
                }
                if (level.HasValue && missing == 0 && duration >= window * 60 - 0.001 &&
                    (best is null || total > best))
                {
                    best = total;
                    bestStart = minutes[left].Time;
                }
            }
        }
        return new(zone, level, build, rows.Min(row => row.Start), rows.Max(row => row.End),
            seconds, xp, rows.Sum(row => row.Gains), unknown, rows.Sum(row => row.Rewards),
            rows.Sum(row => row.Kills), rows.Sum(row => row.Deaths),
            rows.Select(row => (row.Source, row.Context.Visit)).Distinct().Count(),
            level.HasValue && seconds >= 60 && unknown == 0 ? xp * 3600 / seconds : null, best, bestStart);
    }
}
