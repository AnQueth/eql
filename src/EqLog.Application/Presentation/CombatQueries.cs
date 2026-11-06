using EqLog.Core.Tracking;
using EqLog.Core.Parsing;

namespace EqLog.Application.Presentation;

public sealed record FightDetails(IReadOnlyList<AbilityStats> Abilities, IReadOnlyList<AbilityTimeline> Timelines);
public sealed record TimelineBucket(int StartSecond, int EndSecond, long Amount, double PerSecond);
public sealed record HealingDetails(double OverhealPercent, IReadOnlyDictionary<string, long> HealerTotals);
public sealed record IncomingDamageDetails(
    DateTime Start,
    double Seconds,
    IReadOnlyList<AbilityStats> Abilities,
    IReadOnlyList<DamageTimelinePoint> Timeline,
    IReadOnlyList<AbilityTimeline> Timelines,
    IReadOnlyList<DamageEvent> Events);

/// <summary>Combat aggregation and timeline rules produce detached values for charts and presentation.</summary>
public static class CombatQueries
{
    public static IncomingDamageDetails IncomingDamage(
        IReadOnlyList<DamageEvent> incoming, string recipient, DateTime fightStart, double fightSeconds)
    {
        var events = incoming.Where(e => string.Equals(e.Target, recipient, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Time).ToArray();
        DateTime start = events.Length > 0 && events[0].Time < fightStart ? events[0].Time : fightStart;
        DateTime end = fightStart.AddSeconds(Math.Max(1, fightSeconds) - 1);
        if (events.Length > 0 && events[^1].Time > end)
        {
            end = events[^1].Time;
        }

        double seconds = Math.Max(1, (end - start).TotalSeconds + 1);
        var totals = new AbilityTotals();
        foreach (var e in events)
        {
            totals.Add(e.Source ?? (e.Kind == DamageKind.Melee ? "Melee" : e.Kind.ToString()), e.Amount, e.IsCritical);
        }

        var timeline = events.GroupBy(e => Math.Max(0, (int)(e.Time - start).TotalSeconds))
            .OrderBy(g => g.Key).Select(g => new DamageTimelinePoint(g.Key, g.Sum(e => e.Amount))).ToArray();
        var lanes = events.GroupBy(e => e.Source ?? (e.Kind == DamageKind.Melee ? "Melee" : e.Kind.ToString()),
                StringComparer.OrdinalIgnoreCase)
            .Select(g => new AbilityTimeline(g.Key, g.GroupBy(e => Math.Max(0, (int)(e.Time - start).TotalSeconds))
                .OrderBy(p => p.Key).Select(p => new DamageTimelinePoint(p.Key, p.Sum(e => e.Amount))).ToArray()));
        return new IncomingDamageDetails(start, seconds, totals.Snapshot(seconds), timeline, DamageTimeline.Merge(lanes), events);
    }

    public static double PersonalDps(FightSnapshot fight)
    {
        return fight.You?.Dps ?? 0;
    }

    public static FightDetails Fight(FightSnapshot fight)
    {
        var abilities = fight.Attackers.SelectMany(attacker => attacker.Abilities)
            .GroupBy(ability => ability.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AbilityStats(group.Key, group.Sum(ability => ability.Total),
                group.Sum(ability => ability.Total) / fight.DurationSeconds,
                fight.Total == 0 ? 0 : 100.0 * group.Sum(ability => ability.Total) / fight.Total,
                group.Sum(ability => ability.Hits), group.Sum(ability => ability.Crits), group.Max(ability => ability.MaxHit), 0))
            .OrderByDescending(ability => ability.Total).ToArray();
        return new(abilities, DamageTimeline.Merge(fight.Attackers.SelectMany(attacker => attacker.AbilityTimelines)));
    }

    public static HealingDetails Healing(HealSnapshot heals)
    {
        double percent = heals.OutgoingTotal + heals.OutgoingOverheal == 0 ? 0 :
            100.0 * heals.OutgoingOverheal / (heals.OutgoingTotal + heals.OutgoingOverheal);
        return new(percent, heals.Healers.ToDictionary(healer => healer.Name, healer => healer.Abilities.Sum(ability => ability.Total)));
    }

    public static IReadOnlyList<DamageSegment> Segments(IReadOnlyList<DamageTimelinePoint> points)
    {
        return DamageTimeline.Segments(points);
    }

    public static IReadOnlyList<TimelineBucket> Buckets(IReadOnlyList<DamageTimelinePoint> points, double seconds, int maximum)
    {
        return DamageTimeline.Bucket(points, seconds, maximum)
            .Select(bucket => new TimelineBucket(bucket.StartSecond, bucket.EndSecond, bucket.Amount, bucket.PerSecond)).ToArray();
    }
}
