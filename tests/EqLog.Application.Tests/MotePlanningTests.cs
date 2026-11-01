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
        Assert.Equal(Enumerable.Range(0, 11).Select(r => $"+{r}"), MotePlanningQueries.RankLabels);
        Assert.Equal(Enumerable.Range(0, 10).Select(r => $"+{r}"), MotePlanningQueries.FodderRankLabels);
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
        var steps = StepTable(model);
        Assert.Equal(["Step", "Need", "Have", "Use", "Missing", "Carry"], steps.Headers);
        Assert.Equal(["+4 → +5", "16", "0", "", "Major ×4", "4"], steps.Rows[0]);
        Assert.Equal(["+5 → +6", "32", "4", "", "Greater ×5", "2"], steps.Rows[1]);
        var totals = Totals(model);
        Assert.Equal("none", totals["Owned motes used"]);
        Assert.Equal("Major ×4, Greater ×5", totals["Still missing"]);
        Assert.Equal("48 (from +4, 0 in)", totals["Points to +6"]);
        Assert.Null(model.Blocks.OfType<MotePlanTable>().Last().Headers);
        Assert.True(model.Blocks.OfType<MotePlanTable>().Last().RowLabels);
    }

    [Fact]
    public void ProgressAndFinalTargetDoNotSpendMotesMerelyToCarryBeyondTheTarget()
    {
        var owned = new Dictionary<int, int> { [5] = 2 };
        InventoryExportItem[] rows = [Copy(4, count: 2)];
        var model = MotePlanningService.Prepare(Request(4, 5), owned, rows);
        Assert.Equal(["+4 → +5", "16", "0", "copy +4", "", "0"], Assert.Single(StepTable(model).Rows));
        Assert.Equal("none", Totals(model)["Owned motes used"]);
        Assert.Equal("+4 ×1", Totals(model)["Copies merged"]);
        Assert.Equal(2, owned[5]);
        Assert.Equal(2, rows[0].Count);

        var progress = MotePlanningService.Prepare(Request(4, 5) with
        {
            Points = "15",
        }, new Dictionary<int, int>(), []);
        Assert.Equal(["+4 → +5", "16", "15", "", "Major ×1", "4"], Assert.Single(StepTable(progress).Rows));
        Assert.Equal("16 (from +4, 15 in)", Totals(progress)["Points to +5"]);
    }

    [Fact]
    public void GuideExplainsOutgrownMotesBeforeACopyThatAloneFinishesTheStep()
    {
        var request = Request(6, 8) with
        {
            Page = MotePlanPage.Upgrade,
        };
        var model = MotePlanningService.Prepare(request, new Dictionary<int, int> { [7] = 7 }, [Copy(6, count: 2)]);
        Assert.Contains("• +6 → +7: merge your +6 copy (that alone makes +7). " +
            "Use your Superior ×7 first anyway: they stop working at +7, " +
            "so their 49 points carry into +7 → +8", Lines(model));
        Assert.Contains("Stop at +8. No further upgrades are planned.", Lines(model));
        Assert.Contains("Your item is +6. It needs 192 more points to reach +8.", Lines(model));
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
        var fed = Assert.Single(model.Blocks.OfType<MotePlanTable>(), t => t.Headers?.FirstOrDefault() == "Copy");
        Assert.Equal(2, fed.Rows.Count);
        Assert.All(fed.Rows, row =>
        {
            Assert.Equal(["+0", "+3", "Infinitesimal ×1, Minor ×2, Lesser ×2", "8", ""], row);
        });
        Assert.Equal(["+4 → +5", "16", "0", "copy +3 ×2", "", "0"], Assert.Single(StepTable(model).Rows));
        var totals = Totals(model);
        Assert.Equal("Infinitesimal ×2, Minor ×4, Lesser ×4", totals["Owned motes used"]);
        Assert.Equal("+3 ×2", totals["Copies merged"]);
        Assert.Equal("Major ×3", totals["Feeding saves"]);
        Assert.Equal("Infinitesimal ×2, Minor ×4, Lesser ×4", totals["…by spending"]);
        Assert.Equal("none", totals["Still missing"]);
        Assert.Equal(5, owned[1]);

        var guide = MotePlanningService.Prepare(request with
        {
            Page = MotePlanPage.Upgrade,
        }, owned, [Copy(4), Copy(0, count: 2)]);
        Assert.Contains("• 2 +0 items: feed Infinitesimal ×1, Minor ×2, Lesser ×2 each → +3", Lines(guide));
        Assert.Contains("Leveling copies saves you Major ×3 by using Infinitesimal ×2, Minor ×4, Lesser ×4 instead.", Lines(guide));
    }

    [Fact]
    public void FeedingWasteIsPreparedWithoutAddingItToTheMergeValue()
    {
        var owned = new Dictionary<int, int> { [1] = 1, [2] = 2, [3] = 2, [4] = 2, [5] = 4 };
        var model = MotePlanningService.Prepare(Request(5, 10) with
        {
            Feeding = true,
        }, owned, [Copy(5), Copy(4)]);
        var fed = Assert.Single(model.Blocks.OfType<MotePlanTable>(), t => t.Headers?.FirstOrDefault() == "Copy");
        Assert.Equal(["+4", "+5", "Major ×4", "32", "4"], Assert.Single(fed.Rows));
        Assert.Equal("4", Totals(model)["Points wasted feeding"]);
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
        Assert.Contains("WHAT IF: spare merge value 3 to 64 points before feeding; main item stays +6. Inventory is unchanged.",
            model.Summary);
        Assert.Contains("• +6 → +7: merge your copies 2 × +5", Lines(model));
        Assert.Contains("You already have everything you need!", Lines(model));
        Assert.Equal([6, 0, 1], inventory.Select(row => InventoryExport.SplitRank(row.Name).Rank));
        Assert.Equal([5, 5], request.HypotheticalCopies);

        var actual = MotePlanningService.Prepare(request with
        {
            Page = MotePlanPage.Math,
        }, new Dictionary<int, int>(), inventory);
        Assert.Equal(["+6 → +7", "64", "0", "copy +0, copy +1", "Superior ×9", "2"], Assert.Single(StepTable(actual).Rows));
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
        var targets = model.Blocks.OfType<MotePlanLine>().Where(line => line.Bold).Select(line => line.Text);
        Assert.Equal(["Reach +8: farm 7 more +4 copies.", "Reach +9: farm 22 more +4 copies.",
            "Reach +10: farm 54 more +4 copies."], targets);
        var comparison = Assert.Single(model.Blocks.OfType<MotePlanComparison>());
        Assert.Equal("Compare: copies only, one level at a time", comparison.Heading);
        Assert.Equal(["+7 to +8: 8 copies at +4.", "+8 to +9: 16 copies at +4.",
            "+9 to +10: 32 copies at +4."], comparison.Lines.Skip(1));
    }

    [Theory]
    [InlineData(false, "Reach +7: farm 2 more +5 copies.")]
    [InlineData(true, "Reach +7: farm 1 more +5 copy.")]
    public void FarmCanFeedSelectedRankCopiesWithOwnedMotes(bool feeding, string expected)
    {
        var request = Request(6, 7) with
        {
            Page = MotePlanPage.Farm,
            FodderRank = 5,
            Feeding = feeding,
        };
        var model = MotePlanningService.Prepare(request, new Dictionary<int, int> { [6] = 6 }, []);
        Assert.Contains(expected, Lines(model));
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
        Assert.Contains("Reach +7: no more copies needed.", Lines(model));

        var extra = MotePlanningService.Prepare(Request(9, 10) with
        {
            Page = MotePlanPage.Farm,
            FodderRank = 4,
            Fodder = "1",
        }, new Dictionary<int, int> { [10] = 16 }, []);
        Assert.Contains("Reach +10: farm 21 more +4 copies.", Lines(extra));
        Assert.Contains("The 1 extra copies you entered are already counted. Only copies beyond those are listed here.", Lines(extra));
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
        Assert.Equal(["+0 → +1", "1", "0", "Infinitesimal ×1", "", "0"], StepTable(model).Rows[0]);
        Assert.Equal(["+1 → +2", "2", "0", "Infinitesimal ×2", "", "0"], StepTable(model).Rows[1]);
        Assert.Equal(["+2 → +3", "4", "0", "", "Lesser ×1", "0"], StepTable(model).Rows[2]);
        Assert.DoesNotContain("Copies merged", Totals(model).Keys);
        Assert.DoesNotContain(model.Blocks.OfType<MotePlanTable>(), t => t.Headers?.FirstOrDefault() == "Copy");
        Assert.Empty(model.Summary);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("16")]
    [InlineData("1.5")]
    [InlineData("not points")]
    public void InvalidProgressKeepsExactWarningAndPlansWithZero(string points)
    {
        var model = MotePlanningService.Prepare(Request(4, 5) with
        {
            Points = points,
        }, new Dictionary<int, int>(), []);
        Assert.Contains("Points must be a whole number from 0 to 15 at +4; using 0.", model.Summary);
        Assert.Equal("0", Assert.Single(StepTable(model).Rows)[2]);
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
        Assert.Contains("Extra copies must be a whole number from 0 to 1023; using 0.", model.Summary);
        Assert.Equal("Already at the +5 target. No points needed.", Assert.IsType<MotePlanComplete>(Assert.Single(model.Blocks)).Text);
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
        Assert.Contains("1 spare copy in inventory: +4 (one +4 is the item being upgraded).", model.Summary);
        Assert.Contains("You already have a +9 copy, so it isn't counted as a spare. Set Current to +9 to plan from it.", model.Summary);
        Assert.Equal(["+4 → +5", "16", "0", "", "Major ×4", "4"], Assert.Single(StepTable(model).Rows));
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

    private static MotePlanTable StepTable(MotePlanReadModel model)
    {
        return Assert.Single(model.Blocks.OfType<MotePlanTable>(), table => table.Headers?.FirstOrDefault() == "Step");
    }

    private static Dictionary<string, string> Totals(MotePlanReadModel model)
    {
        return model.Blocks.OfType<MotePlanTable>().Last().Rows.ToDictionary(row => row[0], row => row[1]);
    }

    private static IEnumerable<string> Lines(MotePlanReadModel model)
    {
        return model.Blocks.OfType<MotePlanLine>().Select(line => line.Text);
    }
}
