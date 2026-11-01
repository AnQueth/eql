using EqLog.Application.Motes;
using EqLog.Core;
using EqLog.Core.Tracking;

namespace EqLog.Application.Tests;

public sealed class MotePlanningTests
{
    [Fact]
    public void CopyQueriesRetainExportTargetsButExcludeInstalledAndNonEquipmentCollectionsFromMerging()
    {
        InventoryExportItem[] rows =
        [
            Copy(4, count: 2),
            new("Equipment", "Cloak +9", 1, 1, InventoryStorage.Other, Collection: true),
            new("Augmentation", "Cloak +3", 1, 1, InventoryStorage.Other, Collection: true, Installed: true),
            new("Mount", "A mount", 2, 1, InventoryStorage.Other, Collection: true),
        ];

        Assert.Equal(["A mount", "Cloak"], MotePlanningQueries.TargetNames(rows, ""));
        Assert.Equal(["Cloak"], MotePlanningQueries.TargetNames(rows, "clO"));
        Assert.Equal(9, MotePlanningQueries.TargetRank(rows, "cloak"));
        Assert.Equal([4, 4, 9], MotePlanningQueries.CopyRanks(rows, "CLOAK"));
        Assert.Empty(MotePlanningQueries.CopyRanks(rows, "A mount"));
        Assert.Null(MotePlanningQueries.TargetRank(rows, "missing"));
        var selection = MotePlanningQueries.SelectCopies(rows, "Cloak", 4);
        Assert.True(selection.RemovedTarget);
        Assert.Equal([4], selection.Spares);
        Assert.Equal([9], selection.HigherCopies);
        Assert.Equal(2, rows[0].Count);
    }

    [Fact]
    public void TargetSelectionAndProgressBoundsMatchTheCurrentControls()
    {
        Assert.Equal(Enumerable.Range(0, 11), MotePlanningQueries.Ranks);
        Assert.Equal(Enumerable.Range(0, 10), MotePlanningQueries.FodderRanks);
        Assert.Equal(10, MotePlanningQueries.TargetRanks(0, null).Selected);
        Assert.Equal(7, MotePlanningQueries.TargetRanks(4, 7).Selected);
        Assert.Equal(8, MotePlanningQueries.TargetRanks(8, 7).Selected);
        Assert.Equal([8, 9, 10], MotePlanningQueries.TargetRanks(8, 7).Ranks);
        Assert.Equal(15, MotePlanningQueries.MaximumProgress(4));
        Assert.Equal(0, MotePlanningQueries.MaximumProgress(10));
        Assert.False(MotePlanningQueries.CanAddProgress(10));
        Assert.Equal(0, MotePlanningQueries.CurrentRank(-1));
        Assert.Equal(12, MotePlanningQueries.TargetRank([Copy(12)], "Cloak"));
        Assert.Equal(10, MotePlanningQueries.BestCopyRank([Copy(12)], "Cloak"));
    }

    [Fact]
    public void EmptyEquipmentPlanRetainsMatchingMotesAndCarriesFixedOvershoot()
    {
        var model = MotePlanningService.Prepare(Request(4, 6), new Dictionary<int, int>(), []);
        Assert.Collection(model.Steps,
            step =>
            {
                AssertStep(step, 4, 5, 16, 0, 4, 4);
                Assert.Equal("Major", step.MissingTier);
                Assert.Equal(MoteMissingReason.SmallestUsableEquipmentTier, step.MissingReason);
            },
            step =>
            {
                AssertStep(step, 5, 6, 32, 4, 5, 2);
                Assert.Equal("Greater", step.MissingTier);
            });
        Assert.Empty(model.MotesUsed);
        Assert.Equal([new MoteResourceCount(5, "Major", 4), new MoteResourceCount(6, "Greater", 5)], model.Missing);
        Assert.Equal(48, model.TotalRequiredPoints);
        Assert.Equal(48, model.RemainingPoints);
    }

    [Fact]
    public void ProgressAndFinalTargetDoNotSpendMotesMerelyToCarryBeyondTheTarget()
    {
        var owned = new Dictionary<int, int> { [5] = 2 };
        InventoryExportItem[] rows = [Copy(4, count: 2)];
        var model = MotePlanningService.Prepare(Request(4, 5), owned, rows);
        var step = Assert.Single(model.Steps);
        AssertStep(step, 4, 5, 16, 0, 0, 0);
        Assert.Equal(new MoteResourceUse(4, 1, true, 16), Assert.Single(step.Used));
        Assert.Empty(model.MotesUsed);
        Assert.Equal(4, Assert.Single(model.CopiesUsed).Rank);
        Assert.Equal(1, Assert.Single(model.CopiesUsed).Count);
        Assert.Equal(2, owned[5]);
        Assert.Equal(2, rows[0].Count);

        var progress = MotePlanningService.Prepare(Request(4, 5) with
        {
            Points = "15",
        }, new Dictionary<int, int>(), []);
        AssertStep(Assert.Single(progress.Steps), 4, 5, 16, 15, 1, 4);
        Assert.Equal(16, progress.TotalRequiredPoints);
        Assert.Equal(1, progress.RemainingPoints);
    }

    [Fact]
    public void ClassifiesOutgrownMotesBeforeACopyThatAloneFinishesTheStep()
    {
        var request = Request(6, 8) with
        {
            Page = MotePlanPage.Upgrade,
        };
        var model = MotePlanningService.Prepare(request, new Dictionary<int, int> { [7] = 7 }, [Copy(6, count: 2)]);
        Assert.Equal(MoteStepClassification.OutgrownMotesBeforeSufficientCopies, model.Steps[0].Classification);
        Assert.Equal(49, model.Steps[0].MotePoints);
        Assert.Equal(64, model.Steps[0].CopyPoints);
        Assert.Equal(192, model.RemainingPoints);
        Assert.Equal(8, model.Steps[^1].ToRank);
    }

    [Fact]
    public void FeedingPreparesExactCopyMotesMergeValuesAndSavings()
    {
        var request = Request(4, 5) with
        {
            Feeding = true,
        };
        var owned = new Dictionary<int, int> { [1] = 5, [2] = 10, [3] = 10 };
        var model = MotePlanningService.Prepare(request, owned, [Copy(4), Copy(0, count: 2)]);
        Assert.Equal(2, model.FedCopies.Count);
        Assert.All(model.FedCopies, copy =>
        {
            Assert.Equal(0, copy.StartRank);
            Assert.Equal(3, copy.EndRank);
            Assert.Equal(8, copy.MergePoints);
            Assert.Equal(0, copy.Wasted);
            Assert.False(copy.IsFodder);
            Assert.Equal([new MoteResourceUse(1, 1, false, 1), new MoteResourceUse(2, 2, false, 2),
                new MoteResourceUse(3, 2, false, 4)], copy.Motes);
        });
        AssertStep(Assert.Single(model.Steps), 4, 5, 16, 0, 0, 0);
        Assert.Equal(new MoteResourceUse(3, 2, true, 16), Assert.Single(model.Steps[0].Used));
        Assert.Equal([new MoteResourceCount(1, "Infinitesimal", 2), new MoteResourceCount(2, "Minor", 4),
            new MoteResourceCount(3, "Lesser", 4)], model.MotesUsed);
        Assert.Equal(new MoteResourceCount(5, "Major", 3), Assert.Single(model.FeedingSavings));
        Assert.Equal(model.MotesUsed, model.FeedingCosts);
        Assert.Empty(model.Missing);
        Assert.Equal(MoteFeedingStatus.Used, model.FeedingStatus);
        Assert.Equal(5, owned[1]);

        var guide = MotePlanningService.Prepare(request with
        {
            Page = MotePlanPage.Upgrade,
        }, owned, [Copy(4), Copy(0, count: 2)]);
        Assert.Equal(model.FedCopies.Select(copy => copy.MergePoints), guide.FedCopies.Select(copy => copy.MergePoints));
        Assert.Equal(model.FeedingSavings, guide.FeedingSavings);
    }

    [Fact]
    public void FeedingWasteIsPreparedWithoutAddingItToTheMergeValue()
    {
        var owned = new Dictionary<int, int> { [1] = 1, [2] = 2, [3] = 2, [4] = 2, [5] = 4 };
        var model = MotePlanningService.Prepare(Request(5, 10) with
        {
            Feeding = true,
        }, owned, [Copy(5), Copy(4)]);
        var fed = Assert.Single(model.FedCopies);
        Assert.Equal(4, fed.StartRank);
        Assert.Equal(5, fed.EndRank);
        Assert.Equal(32, fed.MergePoints);
        Assert.Equal(new MoteResourceUse(5, 4, false, 20), Assert.Single(fed.Motes));
        Assert.Equal(4, fed.Wasted);
        Assert.Equal(4, model.FeedingWaste);
    }

    [Fact]
    public void WhatIfChangesOnlySpareMergeValueAndLeavesInventoryAndMainRankUnchanged()
    {
        InventoryExportItem[] inventory = [Copy(6), Copy(0), Copy(1)];
        var request = Request(6, 7) with
        {
            Page = MotePlanPage.WhatIf,
            HypotheticalCopies = new[] { 5, 5 },
        };
        var model = MotePlanningService.Prepare(request, new Dictionary<int, int>(), inventory);
        Assert.Equal(new MoteWhatIfCalculation(3, 64), model.WhatIf);
        Assert.Equal(6, model.StartRank);
        Assert.Equal(new MoteResourceUse(5, 2, true, 64), Assert.Single(Assert.Single(model.Steps).Used));
        Assert.Empty(model.Missing);
        Assert.Equal([6, 0, 1], inventory.Select(row => InventoryExport.SplitRank(row.Name).Rank));
        Assert.Equal([5, 5], request.HypotheticalCopies);

        var actual = MotePlanningService.Prepare(request with
        {
            Page = MotePlanPage.Math,
        }, new Dictionary<int, int>(), inventory);
        Assert.Null(actual.WhatIf);
        var actualStep = Assert.Single(actual.Steps);
        AssertStep(actualStep, 6, 7, 64, 0, 9, 2);
        Assert.Equal([new MoteResourceUse(0, 1, true, 1), new MoteResourceUse(1, 1, true, 2)], actualStep.Used);
    }

    [Fact]
    public void FarmCountsAreTotalsFromCurrentRankAndComparisonIsOneStepAtATime()
    {
        var request = Request(7, 10) with
        {
            Page = MotePlanPage.Farm,
            FodderRank = 4,
            Feeding = true,
        };
        var model = MotePlanningService.Prepare(request, new Dictionary<int, int> { [8] = 3, [9] = 2 }, []);
        Assert.Equal([new MoteFarmTarget(8, 7), new MoteFarmTarget(9, 22), new MoteFarmTarget(10, 54)], model.FarmTargets);
        Assert.Equal([new MoteCopyOnlyStep(7, 8, 8), new MoteCopyOnlyStep(8, 9, 16),
            new MoteCopyOnlyStep(9, 10, 32)], model.CopyOnlySteps);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public void FarmCanFeedSelectedRankCopiesWithOwnedMotes(bool feeding, int expected)
    {
        var request = Request(6, 7) with
        {
            Page = MotePlanPage.Farm,
            FodderRank = 5,
            Feeding = feeding,
        };
        var model = MotePlanningService.Prepare(request, new Dictionary<int, int> { [6] = 6 }, []);
        Assert.Equal(new MoteFarmTarget(7, expected), Assert.Single(model.FarmTargets));
        Assert.Equal(5, model.FodderRank);
    }

    [Fact]
    public void FarmSubtractsProgressSparesAndExtraCopiesAlreadyEntered()
    {
        var request = Request(6, 7) with
        {
            Page = MotePlanPage.Farm,
            FodderRank = 5,
            Points = "32",
        };
        var model = MotePlanningService.Prepare(request, new Dictionary<int, int>(), [Copy(6), Copy(5)]);
        Assert.Equal(new MoteFarmTarget(7, 0), Assert.Single(model.FarmTargets));

        var extra = MotePlanningService.Prepare(Request(9, 10) with
        {
            Page = MotePlanPage.Farm,
            FodderRank = 4,
            Fodder = "1",
        }, new Dictionary<int, int> { [10] = 16 }, []);
        Assert.Equal(new MoteFarmTarget(10, 21), Assert.Single(extra.FarmTargets));
        Assert.Equal(1, extra.FodderCopies);
    }

    [Fact]
    public void SpellPlanUsesDoublingValuesIgnoresInventoryCopiesAndNeverFeeds()
    {
        var request = Request(0, 3) with
        {
            Kind = MoteUpgradeKind.Spell,
            Feeding = true,
            Fodder = "10",
        };
        var model = MotePlanningService.Prepare(request, new Dictionary<int, int> { [1] = 3 }, [Copy(5)]);
        AssertStep(model.Steps[0], 0, 1, 1, 0, 0, 0);
        Assert.Equal(new MoteResourceUse(1, 1, false, 1), Assert.Single(model.Steps[0].Used));
        AssertStep(model.Steps[1], 1, 2, 2, 0, 0, 0);
        Assert.Equal(new MoteResourceUse(1, 2, false, 2), Assert.Single(model.Steps[1].Used));
        AssertStep(model.Steps[2], 2, 3, 4, 0, 1, 0);
        Assert.Equal(MoteMissingReason.ExactSpellValue, model.Steps[2].MissingReason);
        Assert.Empty(model.CopiesUsed);
        Assert.Empty(model.FedCopies);
        Assert.Equal(MoteFeedingStatus.NotApplicable, model.FeedingStatus);
        Assert.Empty(model.CopySelection.Spares);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("16")]
    [InlineData("1.5")]
    [InlineData("not points")]
    public void InvalidProgressReturnsValidationStatusAndPlansWithZero(string points)
    {
        var model = MotePlanningService.Prepare(Request(4, 5) with
        {
            Points = points,
        }, new Dictionary<int, int>(), []);
        Assert.Equal(MoteInputStatus.Invalid, model.PointsStatus);
        Assert.Equal(15, model.MaximumProgress);
        Assert.Equal(0, model.Progress);
        Assert.Equal(0, Assert.Single(model.Steps).StartPoints);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("  ", 0)]
    [InlineData(" 15 ", 15)]
    public void ValidProgressIsParsedWithoutFormatting(string text, int expected)
    {
        var model = MotePlanningService.Prepare(Request(4, 5) with
        {
            Points = text,
        }, new Dictionary<int, int>(), []);
        Assert.Equal(MoteInputStatus.Valid, model.PointsStatus);
        Assert.Equal(expected, model.Progress);
        Assert.Equal(expected, model.ParsedPoints);
    }

    [Fact]
    public void InvalidNumericProgressRetainsParsedValueButUsesZeroInCalculations()
    {
        var model = MotePlanningService.Prepare(Request(0, 1) with
        {
            Points = "1",
        }, new Dictionary<int, int>(), []);
        Assert.Equal(MoteInputStatus.Invalid, model.PointsStatus);
        Assert.Equal(1, model.ParsedPoints);
        Assert.Equal(0, model.Progress);
        Assert.Equal(0, model.MaximumProgress);
        Assert.Equal(1023, model.MaximumFodderCopies);
    }

    [Fact]
    public void InvalidFodderIsIgnoredAndAlreadyAtTargetSpendsNothing()
    {
        var model = MotePlanningService.Prepare(Request(5, 5) with
        {
            Points = "10",
            Fodder = "1024",
            Page = MotePlanPage.Farm,
            Feeding = true,
        }, new Dictionary<int, int> { [1] = 100 }, [Copy(5), Copy(0)]);
        Assert.Equal(MoteInputStatus.Invalid, model.FodderStatus);
        Assert.Equal(0, model.FodderCopies);
        Assert.Empty(model.Steps);
        Assert.Empty(model.MotesUsed);
        Assert.Empty(model.CopiesUsed);
        Assert.Empty(model.FarmTargets);
        Assert.Equal(0, model.RemainingPoints);
    }

    [Fact]
    public void HigherCopiesAreNotSparesAndOptionsDoNotSpendUncheckedResources()
    {
        var request = Request(4, 5) with
        {
            UseMotes = false,
            UseCopies = false,
        };
        var model = MotePlanningService.Prepare(request, new Dictionary<int, int> { [5] = 100 }, [Copy(4, count: 2), Copy(9)]);
        Assert.Equal([4], model.CopySelection.Spares);
        Assert.True(model.CopySelection.RemovedTarget);
        Assert.Equal([9], model.CopySelection.HigherCopies);
        AssertStep(Assert.Single(model.Steps), 4, 5, 16, 0, 4, 4);
        Assert.Empty(model.MotesUsed);
        Assert.Empty(model.CopiesUsed);
    }

    private static MotePlanningRequest Request(int startRank, int targetRank)
    {
        return new MotePlanningRequest(MoteUpgradeKind.Equipment, startRank, targetRank,
            "0", "0", 0, false, true, true, true, MotePlanPage.Math, "Cloak", true, []);
    }

    private static InventoryExportItem Copy(int rank, int count = 1)
    {
        return new InventoryExportItem("Bank1", $"Cloak +{rank}", 1, count, InventoryStorage.Bank);
    }

    private static void AssertStep(
        MoteUpgradeStep step, int from, int to, int needed, int progress, int missing, int carry)
    {
        Assert.Equal(from, step.FromRank);
        Assert.Equal(to, step.ToRank);
        Assert.Equal(needed, step.Needed);
        Assert.Equal(progress, step.StartPoints);
        Assert.Equal(missing, step.Missing);
        Assert.Equal(carry, step.CarryAfter);
    }
}
