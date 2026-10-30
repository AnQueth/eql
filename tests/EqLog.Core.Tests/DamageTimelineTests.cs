using EqLog.Core.Parsing;
using EqLog.Core.Tracking;

namespace EqLog.Core.Tests;

public class DamageTimelineTests
{
    private static readonly DateTime Start = new(2026, 10, 27, 12, 0, 0);

    [Fact]
    public void Fight_RecordsPerSecondDamage_ForFightAndEachAttacker()
    {
        var tracker = new FightTracker();
        tracker.OnDamage(new(Start, Names.You, "a goblin", 100, DamageKind.Melee, null, null));
        tracker.OnDamage(new(Start.AddMilliseconds(500), "a pet", "a goblin", 20, DamageKind.Melee, null, null));
        tracker.OnDamage(new(Start.AddSeconds(3), Names.You, "a goblin", 50, DamageKind.DirectSpell, "Fire", null));

        var fight = tracker.Current()!;

        Assert.Equal([new DamageTimelinePoint(0, 120), new DamageTimelinePoint(3, 50)], fight.Timeline);
        Assert.Equal([new DamageTimelinePoint(0, 100), new DamageTimelinePoint(3, 50)], fight.You!.Timeline);
        Assert.Equal([new DamageTimelinePoint(0, 20)], fight.Attackers.Single(a => a.Name == "a pet").Timeline);
    }

    [Fact]
    public void Recent_ReturnsUpToTenFightsYouJoined()
    {
        var tracker = new FightTracker();
        for (int i = 0; i < 12; i++)
        {
            tracker.OnDamage(new(Start.AddMinutes(i), Names.You, $"a goblin {i}", 10, DamageKind.Melee, null, null));
        }

        var recent = tracker.Recent(10);

        Assert.Equal(10, recent.Count);
        Assert.Equal("a goblin 11", recent[0].Target);
        Assert.Equal("a goblin 2", recent[^1].Target);
    }

    [Fact]
    public void Bucket_UsesOneSecondBars_ForShortFights_IncludingIdleSeconds()
    {
        var buckets = DamageTimeline.Bucket([new(0, 10), new(3, 30)], 4, 90);

        Assert.Equal(4, buckets.Count);
        Assert.Equal([10L, 0L, 0L, 30L], buckets.Select(b => b.Amount));
        Assert.Equal(30, buckets[3].PerSecond);
    }

    [Fact]
    public void Bucket_WidensBars_ForLongFights()
    {
        var points = Enumerable.Range(0, 300).Select(s => new DamageTimelinePoint(s, 1)).ToArray();

        var buckets = DamageTimeline.Bucket(points, 300, 90);

        Assert.True(buckets.Count <= 90);
        Assert.Equal(4, buckets[0].EndSecond - buckets[0].StartSecond);
        Assert.Equal(300, buckets.Sum(b => b.Amount));
        Assert.Equal(300, buckets[^1].EndSecond);
        Assert.Equal(1, buckets[0].PerSecond);
    }

    [Fact]
    public void Bucket_EmptyTimeline_ReturnsZeroBars()
    {
        var buckets = DamageTimeline.Bucket([], 0, 90);

        Assert.Equal(0, Assert.Single(buckets).Amount);
    }

    [Fact]
    public void Fight_RecordsTimelinePerDamageType_InFirstLandedOrder()
    {
        var tracker = new FightTracker();
        tracker.OnDamage(new(Start, Names.You, "a goblin", 10, DamageKind.Melee, null, null));
        tracker.OnDamage(new(Start.AddSeconds(2), Names.You, "a goblin", 40, DamageKind.DamageOverTime, "Ignite Blood", null));
        tracker.OnDamage(new(Start.AddSeconds(8), Names.You, "a goblin", 40, DamageKind.DamageOverTime, "ignite blood", null));
        tracker.OnDamage(new(Start.AddSeconds(3), "a pet", "a goblin", 5, DamageKind.Melee, null, null));

        var you = tracker.Current()!.You!;

        Assert.Equal(["Melee", "Ignite Blood"], you.AbilityTimelines.Select(t => t.Name));
        Assert.Equal([new DamageTimelinePoint(2, 40), new DamageTimelinePoint(8, 40)], you.AbilityTimelines[1].Points);
    }

    [Fact]
    public void Merge_CombinesSameDamageTypeAcrossAttackers()
    {
        var merged = DamageTimeline.Merge([
            new AbilityTimeline("Melee", [new(1, 10), new(3, 10)]),
            new AbilityTimeline("Fire", [new(0, 50)]),
            new AbilityTimeline("melee", [new(1, 5)]),
        ]);

        Assert.Equal(["Fire", "Melee"], merged.Select(t => t.Name));
        Assert.Equal([new DamageTimelinePoint(1, 15), new DamageTimelinePoint(3, 10)], merged[1].Points);
    }

    [Fact]
    public void Segments_SplitOnGapsLongerThanThreshold_AndKeepDotTicksTogether()
    {
        var segments = DamageTimeline.Segments([new(0, 10), new(6, 10), new(12, 10), new(30, 5), new(31, 5)]);

        Assert.Equal([new DamageSegment(0, 12, 30, 3), new DamageSegment(30, 31, 10, 2)], segments);
        Assert.Empty(DamageTimeline.Segments([]));
        Assert.Equal(2, DamageTimeline.Segments([new(0, 1), new(2, 1)], maxGapSeconds: 1).Count);
    }
}
