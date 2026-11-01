using EqLog.Core.Tracking;

namespace EqLog.Application.Presentation;

public sealed record FightDetails(IReadOnlyList<AbilityStats> Abilities, IReadOnlyList<AbilityTimeline> Timelines);
public sealed record TimelineBucket(int StartSecond, int EndSecond, long Amount, double PerSecond);
public sealed record HealingDetails(double OverhealPercent, IReadOnlyDictionary<string, long> HealerTotals);

/// <summary>Combat aggregation and timeline rules produce detached values for charts and presentation.</summary>
public static class CombatQueries
{
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
