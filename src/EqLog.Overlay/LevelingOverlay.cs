using EqLog.Application.Sessions;

namespace EqLog.Overlay;

internal sealed record LevelingOverlayResult(OverlayContent Content, IReadOnlyList<LevelingGridRow> Levels);

public partial class MainWindow
{
    private string? _levelingStorageError;

    internal static IReadOnlyList<LevelingGridRow> BuildLevelingGridRows(LevelingReport report, LevelingRequest request)
    {
        return report.Levels.OrderByDescending(level => level.Start).ThenByDescending(level => level.Level)
            .Select(level =>
            {
                string build = level.Builds.Count == 0 ? "Unknown" :
                    string.Join("; ", level.Builds.Select(value => value ?? "Unknown"));
                string start = $"{level.Start:d}\n{level.Start:T}" + (level.StartConfirmed ? "" : " *");
                string end = level.IsCurrent ? "" : level.End is DateTime ended
                    ? $"{ended:d}\n{ended:T}" + (level.EndConfirmed ? "" : " *") : "Unknown";
                string dates = $"Start{(level.StartConfirmed ? "" : " (first observed)")}: {level.Start:G}\n" +
                    $"End{(!level.IsCurrent && level.End.HasValue && !level.EndConfirmed ? " (next level observed)" : "")}: " +
                    (level.IsCurrent ? "" : level.End?.ToString("G") ?? "Unknown");
                if (level.Level is null)
                {
                    dates = $"XP recorded: {level.Start:G} - {level.End:G}";
                }
                var details = new List<string>
                {
                    dates,
                    $"Recorded builds: {build}. Unknown means the historical build was not logged.",
                    $"{level.Seconds / 60:0.##} logged minutes; downtime included, gaps over 30 minutes excluded.",
                    "XP by zone:",
                };
                foreach (var source in level.Sources)
                {
                    string label = source.Zone;
                    if (request.GroupByBuild)
                    {
                        label += $" | {source.Build ?? "Unknown build"}";
                    }
                    string amount = RecordedGain(source);
                    if (source.UnknownGains > 0)
                    {
                        amount += $" | XP gains without amounts: {source.UnknownGains}";
                    }
                    if (source.Rewards > 0)
                    {
                        amount += $" | Reward XP events: {source.Rewards} (amount not logged)";
                    }
                    details.Add($"{label}: {amount}");
                }
                if (level.Sources.Count == 0)
                {
                    details.Add("No XP gains recorded.");
                }
                return new LevelingGridRow(level.Start, level.Level, start, end, build,
                    level.Kills, level.Seconds / 60, level.IsCurrent, string.Join("\n", details));
            }).ToArray();
    }

    internal static OverlayContent BuildLevelingContent(LevelingReport report, LevelingRequest request, bool places)
    {
        const string explanation = "XP gained is the percentage logged at the listed level, not an hourly projection. " +
            "100% is a full XP bar at that level; totals across visits are not actual levels gained. " +
            "Played time includes logged downtime but excludes gaps over 30 minutes. " +
            "Deaths may lose XP that the log does not quantify. Reward XP amounts are not included in the percentage.";
        var rows = new List<OverlayRow>
        {
            new(request.Start == DateTime.MinValue ? "All recorded history" :
                $"{request.Start:g} through {request.End.AddTicks(-1):g}", Bold: true, Wrap: true,
                ToolTip: explanation),
        };
        if (!places)
        {
            rows.Clear();
            if (report.Levels.Count == 0)
            {
                rows.Add(new("No recorded levels match these filters. History is imported automatically from the selected log. " +
                    "Select an older log to backfill it; missing or deleted logs cannot be reconstructed.", Wrap: true));
            }
            return new("Leveling Analyzer", $"{request.Character} | {report.Levels.Count(row => row.Level.HasValue)} level entries", rows);
        }
        rows.Add(new($"Best continuous {request.WindowMinutes}-minute runs - compare within each level",
            request.GroupByBuild ? "Split by build" : "Builds combined", Bold: true, Wrap: true, ToolTip: explanation));
        if (report.Recent is { } recent && recent.Seconds >= request.WindowMinutes * 60 - 0.001 &&
            recent.Level.HasValue && recent.UnknownGains == 0)
        {
            rows.Add(new($"Latest {request.WindowMinutes}m: {recent.Zone}, level {recent.Level?.ToString() ?? "?"}",
                WindowGain(recent.Experience, request.WindowMinutes), Bold: true, Wrap: true, ToolTip: explanation));
        }
        var summaries = report.Places.Where(row => row.BestWindowExperience is > 0).ToArray();
        if (report.Places.Count > summaries.Length)
        {
            rows.Add(new($"{report.Places.Count - summaries.Length} zone/level group(s) excluded: no complete " +
                $"{request.WindowMinutes}m run with measured XP gains at one level. See Levels for the full XP history.", Wrap: true));
        }
        foreach (var row in summaries)
        {
            string title = $"{row.Zone} | level {row.Level?.ToString() ?? "unknown"}";
            if (request.GroupByBuild)
            {
                title += $" | {row.Build ?? "Unknown build"}";
            }
            var activity = new List<string> { $"{ObservedTime(row.Seconds)} played", $"Kills: {row.Kills}" };
            if (row.Deaths > 0)
            {
                activity.Add($"Deaths: {row.Deaths} (XP loss not logged)");
            }
            if (row.Rewards > 0)
            {
                activity.Add($"Reward XP events: {row.Rewards} (amount not logged)");
            }
            if (row.UnknownGains > 0)
            {
                activity.Add($"XP gains without amounts: {row.UnknownGains}");
            }
            string primary = WindowGain(row.BestWindowExperience!.Value, request.WindowMinutes);
            string details = $"Best run started {row.BestWindowStart:g}\n" +
                $"All {row.Visits} recorded visit(s): {string.Join(" | ", activity)}";
            rows.Add(new(title, primary, Bold: true, Wrap: true,
                Details: details, ToolTip: explanation));
        }
        if (summaries.Length == 0 && report.Places.Count > 0)
        {
            rows.Add(new($"No continuous {request.WindowMinutes}-minute runs with measured XP gains at one known level. " +
                "Use a shorter duration or switch to Levels to see where each level's XP came from.", Wrap: true));
        }
        else if (summaries.Length == 0)
        {
            rows.Add(new("No recorded activity matches these filters. Existing history is imported automatically from the selected log. " +
                "Select an older log to backfill it; missing or deleted logs cannot be reconstructed.", Wrap: true));
        }
        string subtitle = $"{request.Character} | {summaries.Length} ranked place(s) | {request.WindowMinutes}-minute runs";
        return new("Leveling Analyzer", subtitle, rows);
    }

    private static string WindowGain(double experience, int minutes)
    {
        return $"{experience:0.##}% XP / {minutes}m";
    }

    private static string RecordedGain(LevelingSummary visit)
    {
        if (visit.Gains == 0)
        {
            return visit.Rewards > 0 ? "Reward XP recorded" : "No XP gains logged";
        }
        if (visit.UnknownGains == visit.Gains)
        {
            return "XP amount not logged";
        }
        return visit.UnknownGains > 0
            ? $"{visit.Experience:0.##}% XP recorded + unknown"
            : $"{visit.Experience:0.##}% XP gained";
    }

    private static string ObservedTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        if (time.TotalHours >= 1)
        {
            return $"{(long)time.TotalHours}h {time.Minutes}m {time.Seconds}s";
        }
        return $"{(long)time.TotalMinutes}m {time.Seconds}s";
    }
}

