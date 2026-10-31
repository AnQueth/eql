using EqLog.Core.Tracking;
using Xunit;

namespace EqLog.Core.Tests;

public class MoteUpgradeTests
{
    private static readonly Dictionary<int, int> NoMotes = [];

    [Theory]
    [InlineData(6, 7, 2)]
    [InlineData(7, 8, 4)]
    [InlineData(8, 9, 8)]
    [InlineData(9, 10, 16)]
    [InlineData(6, 8, 6)]
    [InlineData(6, 9, 14)]
    public void FarmedPlusFiveCopiesCoverSelectedTarget(int startRank, int targetRank, int expected)
    {
        var feed = new FeedOptions(Enabled: false, FodderRank: 5);
        int additional = MoteUpgradePlanner.MinimumAdditionalCopiesToFinish(
            startRank, 0, NoMotes, [], feed, targetRank);

        Assert.Equal(expected, additional);
        var plan = MoteUpgradePlanner.Plan(UpgradeKind.Equipment, startRank, 0, NoMotes, [],
            feed with
            {
                FodderCopies = additional,
            }, targetRank);
        Assert.Equal(0, plan.MissingPoints);
        Assert.Equal(targetRank, plan.Steps[^1].ToRank);
        var fewer = MoteUpgradePlanner.Plan(UpgradeKind.Equipment, startRank, 0, NoMotes, [],
            feed with
            {
                FodderCopies = additional - 1,
            }, targetRank);
        Assert.True(fewer.MissingPoints > 0);
    }

    [Fact]
    public void FarmTargetSubtractsExistingCopiesAndProgress()
    {
        int additional = MoteUpgradePlanner.MinimumAdditionalCopiesToFinish(
            6, 32, NoMotes, [5], new FeedOptions(FodderRank: 5), targetRank: 7);

        Assert.Equal(0, additional);
    }

    [Fact]
    public void FarmTargetCanFeedSelectedCopiesWithOwnedMotes()
    {
        var motes = new Dictionary<int, int> { [6] = 6 };
        int additional = MoteUpgradePlanner.MinimumAdditionalCopiesToFinish(
            6, 0, motes, [], new FeedOptions(FodderRank: 5), targetRank: 7);

        Assert.Equal(1, additional);
        Assert.Equal(2, MoteUpgradePlanner.MinimumAdditionalCopiesToFinish(
            6, 0, motes, [], new FeedOptions(Enabled: false, FodderRank: 5), targetRank: 7));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HypotheticalPlusFiveCopiesFinishThePlusSixStepWithoutChangingInventory(bool feed)
    {
        var inventoryCopies = new List<int> { 0, 1 };
        var original = MoteUpgradePlanner.Plan(UpgradeKind.Equipment, 6, 0, NoMotes, inventoryCopies,
            new FeedOptions(Enabled: feed));
        var hypothetical = MoteUpgradePlanner.Plan(UpgradeKind.Equipment, 6, 0, NoMotes, [5, 5],
            new FeedOptions(Enabled: feed));

        Assert.Equal(9, original.Steps[0].Missing);
        var step = hypothetical.Steps[0];
        Assert.Equal(6, step.FromRank);
        Assert.Equal(7, step.ToRank);
        Assert.Equal(64, step.Needed);
        Assert.Equal(0, step.Missing);
        Assert.Equal(0, step.CarryAfter);
        Assert.Equal([new UpgradeUse(5, 2, IsCopy: true)], step.Used);
        Assert.Equal([0, 1], inventoryCopies);
    }

    [Theory]
    [InlineData("Infinitesimal", 1, 1, 0, 1)]
    [InlineData("Potential", 4, 4, 3, 8)]
    [InlineData("Major", 5, 5, 4, 16)]
    [InlineData("Infinite", 10, 10, 9, 512)]
    public void ValuesMatchTheMoteGuide(string tier, int rank, int itemPoints, int itemMaxTier, int spellPoints)
    {
        Assert.Equal(rank, MoteValues.RankOf(tier));
        Assert.Equal(itemPoints, MoteValues.ItemPoints(rank));
        Assert.Equal(itemMaxTier, MoteValues.ItemMaxTier(rank));
        Assert.Equal(spellPoints, MoteValues.SpellPoints(rank));
    }

    [Fact]
    public void UnknownTierHasNoRank()
    {
        Assert.Null(MoteValues.RankOf("Transcendent"));
    }

    [Fact]
    public void WithNothingOwnedEveryStepIsMissingMotesWithCarry()
    {
        var plan = MoteUpgradePlanner.Plan(UpgradeKind.Equipment, 4, 0, NoMotes, []);

        Assert.Equal(6, plan.Steps.Count);
        var first = plan.Steps[0];
        Assert.Equal(16, first.Needed);
        Assert.Equal("Major", first.MissingTier);
        Assert.Equal(4, first.Missing);
        Assert.Equal(4, first.CarryAfter);

        var second = plan.Steps[1];
        Assert.Equal(4, second.StartPoints);
        Assert.Equal("Greater", second.MissingTier);
        Assert.Equal(5, second.Missing);
        Assert.Equal(2, second.CarryAfter);
    }

    [Fact]
    public void EquipmentSkipsMotesAboveTheirTierLimit()
    {
        var owned = new Dictionary<int, int> { [2] = 10, [3] = 5 };

        var plan = MoteUpgradePlanner.Plan(UpgradeKind.Equipment, 2, 0, owned, []);

        Assert.Equal([new UpgradeUse(3, 2, IsCopy: false)], plan.Steps[0].Used);
        Assert.Equal(0, plan.Steps[0].Missing);
        Assert.Empty(plan.Steps[1].Used);
        Assert.Equal(2, plan.Steps[1].Missing);
        Assert.Equal("Potential", plan.Steps[1].MissingTier);
        Assert.False(plan.MotesUsed.ContainsKey(2));
    }

    [Fact]
    public void CopiesCountAsExactlyTheirRankAndAreMergedLowestFirst()
    {
        var plan = MoteUpgradePlanner.Plan(UpgradeKind.Equipment, 4, 0, NoMotes, [4, 4, 3]);

        var first = plan.Steps[0];
        Assert.Equal([new UpgradeUse(3, 1, IsCopy: true), new UpgradeUse(4, 1, IsCopy: true)], first.Used);
        Assert.Equal(8, first.CarryAfter);

        var second = plan.Steps[1];
        Assert.Equal([new UpgradeUse(4, 1, IsCopy: true)], second.Used);
        Assert.Equal(2, second.Missing);
        Assert.Equal(4, second.CarryAfter);
        Assert.Equal(2, plan.CopiesUsed[4]);
    }

    [Fact]
    public void SameRankCopyAloneLevelsTheItemAndSpentMotesCarryOver()
    {
        var copyOnly = MoteUpgradePlanner.Plan(UpgradeKind.Equipment, 6, 0, NoMotes, [6]);
        Assert.Equal(0, copyOnly.Steps[0].Missing);
        Assert.Equal(0, copyOnly.Steps[0].CarryAfter);

        // Superior motes stop working at +7, so they go in before the merge and their 49 points carry over.
        var withSuperior = MoteUpgradePlanner.Plan(UpgradeKind.Equipment, 6, 0, new Dictionary<int, int> { [7] = 7 }, [6]);
        Assert.Equal(0, withSuperior.Steps[0].Missing);
        Assert.Equal(49, withSuperior.Steps[0].CarryAfter);
        Assert.Equal(49, withSuperior.Steps[1].StartPoints);
    }

    [Fact]
    public void MotesAreSpentBeforeCopies()
    {
        var owned = new Dictionary<int, int> { [5] = 4 };

        var plan = MoteUpgradePlanner.Plan(UpgradeKind.Equipment, 4, 0, owned, [4]);

        Assert.Equal([new UpgradeUse(5, 4, IsCopy: false)], plan.Steps[0].Used);
        Assert.Equal([new UpgradeUse(4, 1, IsCopy: true)], plan.Steps[1].Used);
    }

    [Fact]
    public void EquipmentReservesHigherTierMotesForTheirMatchingRank()
    {
        var owned = new Dictionary<int, int> { [6] = 5 };

        var plan = MoteUpgradePlanner.Plan(UpgradeKind.Equipment, 4, 0, owned, []);

        Assert.Empty(plan.Steps[0].Used);
        Assert.Equal(4, plan.Steps[0].Missing);
        Assert.Equal([new UpgradeUse(6, 5, IsCopy: false)], plan.Steps[1].Used);
    }

    [Fact]
    public void CurrentPointsCountTowardTheFirstStep()
    {
        var plan = MoteUpgradePlanner.Plan(UpgradeKind.Equipment, 4, 15, NoMotes, []);

        Assert.Equal(15, plan.Steps[0].StartPoints);
        Assert.Equal(1, plan.Steps[0].Missing);
        Assert.Equal(4, plan.Steps[0].CarryAfter);
    }

    [Fact]
    public void SpellsUseDoublingValuesWithNoTierLimit()
    {
        var owned = new Dictionary<int, int> { [1] = 3 };

        var plan = MoteUpgradePlanner.Plan(UpgradeKind.Spell, 0, 0, owned, [5]);

        Assert.Equal([new UpgradeUse(1, 1, IsCopy: false)], plan.Steps[0].Used);
        Assert.Equal([new UpgradeUse(1, 2, IsCopy: false)], plan.Steps[1].Used);
        Assert.Empty(plan.Steps[2].Used);
        Assert.Equal(1, plan.Steps[2].Missing);
        Assert.Equal("Lesser", plan.Steps[2].MissingTier);
        Assert.Empty(plan.CopiesUsed);
        Assert.All(plan.Steps.Skip(2), s => Assert.Equal(1, s.Missing));
    }

    [Fact]
    public void AlreadyMaxedHasNoSteps()
    {
        var plan = MoteUpgradePlanner.Plan(UpgradeKind.Equipment, 10, 0, NoMotes, []);

        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void FeedingCopiesWithSmallMotesReplacesMajors()
    {
        var owned = new Dictionary<int, int> { [1] = 5, [2] = 10, [3] = 10 };

        var plan = MoteUpgradePlanner.Plan(UpgradeKind.Equipment, 4, 0, owned, [0, 0], new FeedOptions());

        Assert.Equal(2, plan.FedCopies.Count);
        Assert.All(plan.FedCopies, f =>
        {
            Assert.Equal(0, f.StartRank);
            Assert.Equal(3, f.EndRank);
            Assert.Equal(0, f.Wasted);
            Assert.Equal([new UpgradeUse(1, 1, false), new UpgradeUse(2, 2, false), new UpgradeUse(3, 2, false)], f.Motes);
        });
        Assert.Equal([new UpgradeUse(3, 2, IsCopy: true)], plan.Steps[0].Used);
        Assert.Equal(0, plan.Steps[0].Missing);
        Assert.NotNull(plan.Baseline);
        Assert.Equal(3, plan.Baseline!.Steps[0].Missing);
        Assert.True(plan.SavedVersusBaseline[5] >= 3);
        Assert.True(plan.IsBetterThan(plan.Baseline));
    }

    [Fact]
    public void FarmCopiesAreAvailableWhetherTheyAreFedOrMergedAsIs()
    {
        var owned = new Dictionary<int, int> { [1] = 1, [2] = 2 };

        var plan = MoteUpgradePlanner.Plan(UpgradeKind.Equipment, 4, 0, owned, [],
            new FeedOptions(Enabled: false, FodderCopies: 1, FodderRank: 4));

        Assert.Empty(plan.FedCopies);
        Assert.Equal([new UpgradeUse(4, 1, IsCopy: true)], plan.Steps[0].Used);
        Assert.Equal(0, plan.Steps[0].Missing);
    }

    [Fact]
    public void FarmEstimateUsesSelectedRankAndOwnedMotes()
    {
        var owned = new Dictionary<int, int> { [10] = 16 };

        int additional = MoteUpgradePlanner.MinimumAdditionalCopiesToFinish(
            9, 0, owned, [], new FeedOptions(Enabled: false, FodderRank: 4));

        Assert.Equal(22, additional);
    }

    [Fact]
    public void FarmEstimateIsZeroWhenCurrentMotesFinishTheUpgrade()
    {
        var owned = new Dictionary<int, int> { [10] = 52 };

        int additional = MoteUpgradePlanner.MinimumAdditionalCopiesToFinish(
            9, 0, owned, [], new FeedOptions(Enabled: false, FodderRank: 4));

        Assert.Equal(0, additional);
    }

    [Fact]
    public void FarmEstimateSubtractsExtraCopiesAlreadyEntered()
    {
        var owned = new Dictionary<int, int> { [10] = 16 };

        int additional = MoteUpgradePlanner.MinimumAdditionalCopiesToFinish(
            9, 0, owned, [], new FeedOptions(Enabled: false, FodderCopies: 1, FodderRank: 4));

        Assert.Equal(21, additional);
    }

    [Fact]
    public void WithoutUsableFeedingOnlyOutgrownMotesAreFed()
    {
        var owned = new Dictionary<int, int> { [1] = 1, [2] = 2, [5] = 10 };

        var plan = MoteUpgradePlanner.Plan(UpgradeKind.Equipment, 4, 0, owned, [0], new FeedOptions(FeedUsableMotes: false));

        var fed = Assert.Single(plan.FedCopies);
        Assert.Equal([new UpgradeUse(1, 1, IsCopy: false), new UpgradeUse(2, 2, IsCopy: false)], fed.Motes);
        Assert.Equal(2, fed.EndRank);
    }

    [Fact]
    public void FeedingUsableMotesUsesMotesTheItemWouldOutgrow()
    {
        var owned = new Dictionary<int, int> { [1] = 1, [2] = 2, [3] = 4, [4] = 8, [5] = 10 };

        var plan = MoteUpgradePlanner.Plan(UpgradeKind.Equipment, 4, 0, owned, [0], new FeedOptions());

        var fed = Assert.Single(plan.FedCopies);
        Assert.Equal(5, fed.EndRank);
        Assert.Equal(4, fed.Wasted);
        Assert.Equal([new UpgradeUse(1, 1, false), new UpgradeUse(2, 2, false), new UpgradeUse(3, 2, false),
            new UpgradeUse(4, 2, false), new UpgradeUse(5, 4, false)], fed.Motes);
        Assert.Equal(8, plan.MotesUsed[5]);
    }

    [Fact]
    public void FeedingDoesNotUseHigherTierMotesToRaiseALowerRankCopy()
    {
        var owned = new Dictionary<int, int> { [6] = 5 };

        var plan = MoteUpgradePlanner.Plan(UpgradeKind.Equipment, 4, 0, owned, [4], new FeedOptions());

        Assert.Empty(plan.FedCopies);
        Assert.Equal(5, plan.MotesUsed.GetValueOrDefault(6));
    }

    [Fact]
    public void OvershootWhenACopyStopsIsReportedAsWasted()
    {
        var owned = new Dictionary<int, int>
        {
            [1] = 1,
            [2] = 2,
            [3] = 2,
            [4] = 2,
            [5] = 4,
        };

        var plan = MoteUpgradePlanner.Plan(UpgradeKind.Equipment, 5, 0, owned, [4], new FeedOptions());

        var fed = Assert.Single(plan.FedCopies);
        Assert.Equal(5, fed.EndRank);
        Assert.Equal(4, fed.Wasted);
    }

    [Fact]
    public void FeedingIsNotUsedWhenItWouldNotHelp()
    {
        var owned = new Dictionary<int, int> { [1] = 1 };

        var plan = MoteUpgradePlanner.Plan(UpgradeKind.Equipment, 0, 0, owned, [0], new FeedOptions());

        Assert.Empty(plan.FedCopies);
        Assert.Equal([new UpgradeUse(1, 1, IsCopy: false)], plan.Steps[0].Used);
    }

    [Fact]
    public void SpellsNeverFeed()
    {
        var plan = MoteUpgradePlanner.Plan(UpgradeKind.Spell, 4, 0, new Dictionary<int, int> { [1] = 5 }, [0], new FeedOptions(FodderCopies: 3));

        Assert.Empty(plan.FedCopies);
        Assert.Null(plan.Baseline);
    }
}
