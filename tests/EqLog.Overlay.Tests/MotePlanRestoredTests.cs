using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EqLog.Application.Inventory;
using EqLog.Application.Motes;
using EqLog.Application.Sessions;
using EqLog.Core;
using EqLog.Core.Tracking;
using EqLog.Overlay;
using EqLog.Overlay.Messaging;
using EqLog.Overlay.ViewModels;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class MotePlanRestoredTests
{
    [Fact]
    public void InventorySelectionPreservesSearchAndPlansForSelectedItem()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var vm = await Create([Copy(4, 1, "Leather Bracer"), Copy(7, 1, "Steel Bracer")]);
            vm.Name = "bracer";
            await vm.PendingTargetFilter;
            vm.SelectedInventoryName = "Leather Bracer";
            Assert.Equal("bracer", vm.Name);
            Assert.Equal("Leather Bracer", vm.Plan!.ItemName);
            Assert.Equal(4, vm.CurrentRank);
            vm.SelectedInventoryName = "Steel Bracer";
            Assert.Equal("bracer", vm.Name);
            Assert.Equal("Steel Bracer", vm.Plan!.ItemName);
            Assert.Equal(7, vm.CurrentRank);
            vm.Name = " leather ";
            await vm.PendingTargetFilter;
            Assert.Null(vm.SelectedInventoryName);
            Assert.Equal("leather", vm.Plan!.ItemName);
            vm.SelectedInventoryName = "Leather Bracer";
            vm.Kind = MoteUpgradeKind.Spell;
            Assert.Equal("leather", vm.Plan!.ItemName);
        });
    }

    [Fact]
    public void PlannerGuideRendersTypedCalculationsWithOriginalTextAndLayout()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var vm = await Planner(6, 8, new() { [7] = 7 }, [Copy(6, 2)]);
            Assert.Equal("What to do", vm.PlanBlocks[0].Text);
            Assert.Equal(MotePlanBlockKind.Heading, vm.PlanBlocks[0].Kind);
            Assert.Equal("Your item is +6. It needs 192 more points to reach +8.", vm.PlanBlocks[1].Text);
            var line = Assert.Single(vm.PlanBlocks, block => block.Text.StartsWith("• +6"));
            Assert.Equal("• +6 → +7: merge your +6 copy (that alone makes +7). " +
                "Use your Superior ×7 first anyway: they stop working at +7, " +
                "so their 49 points carry into +7 → +8", line.Text);
            Assert.True(line.Indent);
            Assert.Contains("Stop at +8. No further upgrades are planned.", Texts(vm));
        });
    }

    [Fact]
    public void PlannerGuideCombinesLevelsCoveredByOneResourceAndKeepsMathPerLevel()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            foreach (var kind in Enum.GetValues<MoteUpgradeKind>())
            {
                foreach (bool fragment in new[] { false, true })
                {
                    int rank = kind == MoteUpgradeKind.Equipment ? (fragment ? 3 : 4) : (fragment ? 2 : 3);
                    var resources = new Dictionary<int, int> { [rank] = 1 };
                    var vm = await Planner(0, 4, fragment ? null : resources, fragments: fragment ? resources : null, kind: kind);
                    string resource = MotePlanningQueries.ResourceName(vm.Plan!.TierIds, rank, fragment);
                    foreach (var page in new[] { MotePlanPage.Upgrade, MotePlanPage.WhatIf })
                    {
                        vm.Page = page;
                        Assert.Contains($"• +0 → +2: use your {resource} ×1", Texts(vm));
                        Assert.DoesNotContain(Texts(vm), text => text.StartsWith("• +1"));
                        Assert.Contains(Texts(vm), text => text.StartsWith("• +2 → +3: add "));
                        Assert.DoesNotContain(Texts(vm), text => text.Contains("leftover points cover it"));
                    }
                    vm.Page = MotePlanPage.Math;
                    var table = Tables(vm)[0].Table!;
                    Assert.Equal(["+0 → +1", "1", "0", $"{resource} ×1", "", "3"], table.Rows[0]);
                    Assert.Equal(["+1 → +2", "2", "3", "", "", "1"], table.Rows[1]);
                }
            }
        });
    }

    [Theory]
    [InlineData(2, 15)]
    [InlineData(4, 3)]
    public void PlannerGuideCombinesCarryOnlyThroughTheSelectedTarget(int targetRank, int carry)
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var vm = await Planner(0, targetRank, fragments: new() { [9] = 1 });
            Assert.Contains($"• +0 → +{targetRank}: use your Fragment of Ascendant Potential ×1", Texts(vm));
            Assert.Single(Texts(vm), text => text.StartsWith("• "));
            Assert.Contains($"Stop at +{targetRank}. No further upgrades are planned.", Texts(vm));
            Assert.Contains($"The last resource or merge leaves {carry} extra points. " +
                "Resources add fixed amounts; do not add more points after reaching your target.", Texts(vm));
        });
    }

    [Fact]
    public void PlannerMathRendersOriginalHeadersRowsTotalsAndToolTips()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var vm = await Planner(4, 6);
            vm.Page = MotePlanPage.Math;
            var tables = Tables(vm);
            Assert.Equal(2, tables.Length);
            Assert.Equal(["Step", "Need", "Have", "Use", "Missing", "Carry"], tables[0].Table!.Header);
            Assert.Equal(["+4 → +5", "16", "0", "", "Major ×4", "4"], tables[0].Table!.Rows[0]);
            Assert.Equal(["+5 → +6", "32", "4", "", "Greater ×5", "2"], tables[0].Table!.Rows[1]);
            Assert.Equal("Need: points for the step. Have: points carried in. Use: owned motes, fragments and spare copies spent. " +
                "Missing: motes still to get. Carry: extra points that roll into the next step.", tables[0].ToolTip);
            Assert.Equal(["Owned motes used", "none"], tables[1].Table!.Rows[0]);
            Assert.Equal(["Copies merged", "none"], tables[1].Table!.Rows[1]);
            Assert.Equal(["Still missing", "Major ×4, Greater ×5"], tables[1].Table!.Rows[2]);
            Assert.Equal(["Points to +6", "48 (from +4, 0 in)"], tables[1].Table!.Rows[3]);
            Assert.True(tables[1].Table!.RowLabels);
        });
    }

    [Fact]
    public void PlannerFeedingRendersGroupedGuideSavingsAndExactMath()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var vm = await Planner(4, 5, new() { [1] = 5, [2] = 10, [3] = 10 }, [Copy(4), Copy(0, 2)]);
            vm.Feeding = true;
            Assert.Contains("• 2 +0 items: feed Infinitesimal ×1, Minor ×2, Lesser ×2 each → +3", Texts(vm));
            Assert.Contains("Leveling copies saves you Major ×3 by using Infinitesimal ×2, Minor ×4, Lesser ×4 instead.", Texts(vm));
            Assert.Contains("You already have everything you need!", Texts(vm));
            vm.Page = MotePlanPage.Math;
            var tables = Tables(vm);
            Assert.Equal(["Copy", "To", "Feed", "Merge pts", "Wasted"], tables[0].Table!.Header);
            Assert.Equal(["+0", "+3", "Infinitesimal ×1, Minor ×2, Lesser ×2", "8", ""], tables[0].Table!.Rows[0]);
            Assert.Equal(tables[0].Table!.Rows[0], tables[0].Table!.Rows[1]);
            Assert.Equal("Feed each copy these motes or fragments, using only tiers that work at each rank, then merge it into the main item. " +
                "Wasted: points past the copy's last rank, which merging doesn't pass on.", tables[0].ToolTip);
            Assert.Equal(["+4 → +5", "16", "0", "copy +3 ×2", "", "0"], tables[1].Table!.Rows[0]);
            Assert.Equal(["Feeding saves", "Major ×3"], tables[2].Table!.Rows[3]);
            Assert.Equal(["…by spending", "Infinitesimal ×2, Minor ×4, Lesser ×4"], tables[2].Table!.Rows[4]);
        });
    }

    [Fact]
    public void PlannerFeedingWasteRemainsSeparateFromMergePoints()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var vm = await Planner(5, 10, new() { [1] = 1, [2] = 2, [3] = 2, [4] = 2, [5] = 4 }, [Copy(5), Copy(4)]);
            vm.Feeding = true;
            vm.Page = MotePlanPage.Math;
            var tables = Tables(vm);
            Assert.Equal(["+4", "+5", "Major ×4", "32", "4"], tables[0].Table!.Rows[0]);
            Assert.Contains(tables[^1].Table!.Rows, row => row.SequenceEqual(["Points wasted feeding", "4"]));
        });
    }

    [Fact]
    public void PlannerFarmRendersTotalTargetsAndExpandableOneStepComparisonWithoutGuide()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var vm = await Planner(7, 10, new() { [8] = 3, [9] = 2 });
            vm.FodderRank = 4;
            vm.Feeding = true;
            vm.Page = MotePlanPage.Farm;
            Assert.Equal("How many +4 copies should I farm?", vm.PlanBlocks[0].Text);
            Assert.Equal(["Reach +8: farm 6 more +4 copies.", "Reach +9: farm 22 more +4 copies.", "Reach +10: farm 54 more +4 copies."],
                Texts(vm).Where(text => text.StartsWith("Reach")));
            Assert.All(vm.PlanBlocks.Where(block => block.Text.StartsWith("Reach")), block => Assert.True(block.Bold));
            Assert.Contains("Each number is the TOTAL to farm from where you are now. Do not add the numbers together.", Texts(vm));
            Assert.Contains("These numbers use the motes, fragments, spare copies and progress selected above.", Texts(vm));
            Assert.Contains("Feeding is on: the plan may spend your motes or fragments to raise farmed copies before merging them.", Texts(vm));
            var comparison = Assert.Single(vm.PlanBlocks, block => block.Kind == MotePlanBlockKind.Comparison);
            Assert.Equal("Compare: copies only, one level at a time", comparison.Text);
            Assert.Equal(["No motes or fragments, no spare copies, no feeding, and no saved progress. Just merge +4 copies for ONE level at a time:",
                "+7 to +8: 8 copies at +4.", "+8 to +9: 16 copies at +4.", "+9 to +10: 32 copies at +4."], comparison.Lines);
            Assert.DoesNotContain("What to do", Texts(vm));
            Assert.NotEmpty(Tables(vm));
        });
    }

    [Fact]
    public void PlannerFarmExplainsEnteredExtraCopiesAndSingularCounts()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var vm = await Planner(9, 10, new() { [10] = 16 });
            vm.FodderRank = 4;
            vm.Fodder = "1";
            vm.Page = MotePlanPage.Farm;
            Assert.Contains("Reach +10: farm 21 more +4 copies.", Texts(vm));
            Assert.Contains("The 1 extra copies you entered are already counted. Only copies beyond those are listed here.", Texts(vm));
        });
    }

    [Fact]
    public void PlannerWhatIfRendersHypotheticalValuesAndKeepsMainRankInSummaryAndGuide()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var vm = await Planner(6, 7, copies: [Copy(6), Copy(0), Copy(1)]);
            vm.Page = MotePlanPage.WhatIf;
            vm.CopyRanks[0].Rank = 5;
            vm.CopyRanks[1].Rank = 5;
            Assert.Equal("WHAT IF: spare merge value 3 to 64 points before feeding; main item stays +6. Inventory is unchanged." +
                Environment.NewLine + "2 spare copies in inventory: +0, +1 (one +6 is the item being upgraded).", vm.Summary);
            Assert.Contains("• +6 → +7: merge your copies 2 × +5", Texts(vm));
            Assert.Contains("You already have everything you need!", Texts(vm));
            Assert.DoesNotContain(vm.PlanBlocks, block => block.Kind is MotePlanBlockKind.Table or MotePlanBlockKind.Comparison);
        });
    }

    [Fact]
    public void PlannerValidationInventoryNotesCompletionAndRuleTipsKeepExactWording()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var vm = await Planner(4, 5, copies: [Copy(4, 2), Copy(9)]);
            vm.Points = "bad";
            vm.Fodder = "1024";
            vm.Page = MotePlanPage.Farm;
            Assert.Equal("Points must be a whole number from 0 to 15 at +4; using 0." + Environment.NewLine +
                "Extra copies must be a whole number from 0 to 1023; using 0." + Environment.NewLine +
                "1 spare copy in inventory: +4 (one +4 is the item being upgraded)." + Environment.NewLine +
                "You already have a +9 copy, so it isn't counted as a spare. Set Current to +9 to plan from it.", vm.Summary);
            Assert.Equal("Prefer fragments spends usable fragments before normal motes; off uses the smallest usable tier, normal motes first. " +
                "Fragments are worth twice their named mote and share its equipment +rank limit. " +
                "Spare copies are merged after motes and fragments. Condensing and Void-Touched resources are not used.", vm.PlanSummaryToolTip);
            vm.CurrentRank = 5;
            vm.TargetRank = 5;
            Assert.Equal(["Already at the +5 target. No points needed."], Texts(vm));
            vm.CurrentRank = 0;
            vm.TargetRank = 1;
            vm.Points = "1";
            vm.Page = MotePlanPage.Math;
            Assert.Equal(["Points to +1", "1 (from +0, 0 in)"], Tables(vm)[^1].Table!.Rows[3]);
        });
    }

    [Fact]
    public void PlannerSpellRendersDoublingValuesWithoutEquipmentTotalsOrFeeding()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var vm = await Planner(0, 3, new() { [1] = 3 }, [Copy(5)], kind: MoteUpgradeKind.Spell);
            Assert.Equal("Spell motes double in value each tier (1 to 512 points); fragments are worth twice their named mote. " +
                "Both work at any +level. Prefer fragments spends fragments first; off uses the smallest tier, normal motes first. " +
                "Condensing and Void-Touched resources are not used.", vm.PlanSummaryToolTip);
            vm.Feeding = true;
            vm.Page = MotePlanPage.Math;
            var tables = Tables(vm);
            Assert.Equal(2, tables.Length);
            Assert.Equal(["+0 → +1", "1", "0", "Infinitesimal ×1", "", "0"], tables[0].Table!.Rows[0]);
            Assert.Equal(["+1 → +2", "2", "0", "Infinitesimal ×2", "", "0"], tables[0].Table!.Rows[1]);
            Assert.Equal(["+2 → +3", "4", "0", "", "Lesser ×1", "0"], tables[0].Table!.Rows[2]);
            Assert.DoesNotContain(tables[1].Table!.Rows, row => row[0] == "Copies merged");
            Assert.Empty(vm.Summary);
            vm.Page = MotePlanPage.Upgrade;
            Assert.Contains("Why Lesser? One of those resources is exactly what each step needs, so nothing is wasted.", Texts(vm));
        });
    }

    [Fact]
    public void PlannerShowsOwnedFragmentsAndExplainsSelectedPriority()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var owned = new Dictionary<int, int> { [1] = 22, [2] = 7, [3] = 15, [4] = 14, [5] = 99, [6] = 39, [7] = 11, [8] = 3, [9] = 2 };
            var fragments = new Dictionary<int, int> { [3] = 1, [5] = 56, [6] = 3, [9] = 1 };
            var vm = await Planner(0, 10, owned, fragments: fragments);
            Assert.Equal(4, vm.Plan!.AvailableFragments.Count);
            Assert.Equal(56, Assert.Single(vm.Plan.AvailableFragments, resource => resource.Rank == 5).Count);
            Assert.Equal(4, vm.Plan.FragmentsUsed.Count);
            foreach (var page in Enum.GetValues<MotePlanPage>())
            {
                vm.Page = page;
                Assert.Contains("Owned fragments available (last inventory export): Fragment of Lesser Potential ×1, Fragment of Major Potential ×56, " +
                    "Fragment of Greater Potential ×3, Fragment of Ascendant Potential ×1.", vm.Summary);
                Assert.Contains("Usable fragments are spent before normal motes, smallest fragment tier first.", vm.Summary);
                Assert.Contains("Fragments have the same equipment rank limit as their named mote.", vm.Summary);
                Assert.Contains("Run /outputfile inventory after looting or spending resources to refresh these counts.", vm.Summary);
            }
            vm.PreferFragments = false;
            Assert.Equal(new MoteResourceCount(9, "Ascendant", 1, IsFragment: true), Assert.Single(vm.Plan!.FragmentsUsed));
            Assert.Contains("Regular motes are used first within each tier; unused fragments are kept.", vm.Summary);
            vm.UseMotes = false;
            Assert.Empty(vm.Plan!.AvailableFragments);
            Assert.Empty(vm.Plan.FragmentsUsed);
            Assert.DoesNotContain("Owned fragments available", vm.Summary);
        });
    }

    [Fact]
    public void PlannerRendersFragmentUseAndTotalsSeparately()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var vm = await Planner(4, 5, fragments: new() { [5] = 1 });
            vm.Page = MotePlanPage.Math;
            var tables = Tables(vm);
            Assert.Equal(10, vm.Plan!.Steps[0].MotePoints);
            Assert.Equal(["+4 → +5", "16", "0", "Fragment of Major Potential ×1", "Major ×2", "4"], tables[0].Table!.Rows[0]);
            Assert.Equal("Need: points for the step. Have: points carried in. Use: owned motes, fragments and spare copies spent. " +
                "Missing: motes still to get. Carry: extra points that roll into the next step.", tables[0].ToolTip);
            Assert.Contains(tables[1].Table!.Rows, row => row.SequenceEqual(["Owned fragments used", "Fragment of Major Potential ×1"]));
        });
    }

    [Fact]
    public void StopAtSelectorDefaultsToTenAndRetainsTargetsWithinCurrentRankRange()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var vm = await Create([]);
            Assert.Equal(10, vm.TargetRank);
            Assert.Equal(Enumerable.Range(0, 11), vm.TargetRanks);
            vm.TargetRank = 7;
            vm.CurrentRank = 4;
            Assert.Equal(7, vm.TargetRank);
            Assert.Equal(Enumerable.Range(4, 7), vm.TargetRanks);
            vm.CurrentRank = 6;
            Assert.Equal(7, vm.TargetRank);
            vm.CurrentRank = 8;
            Assert.Equal(8, vm.TargetRank);
            vm.CurrentRank = 10;
            Assert.Equal(10, vm.TargetRank);
            Assert.Equal([10], vm.TargetRanks);
        });
    }

    [Fact]
    public void PlannerTargets_IncludeEveryExportedItem_WhileCopiesIncludeEquipmentStorage()
    {
        var rows = InventoryExport.Parse("Location\tName\tID\tCount\tSlots\n" +
            "Bank1\tWhite Dragonscale Cloak +4\t11603\t2\t0\n" +
            "Back-Slot7\tWhite Dragonscale Cloak (Exaltation)\t11603\t1\t11\n" +
            "General1\tMote of Greater Potential\t1\t3\t0\n" +
            "KeyRing\tName\tID\nEquipment\tWhite Dragonscale Cloak +9\t11603\n" +
            "Augmentation\tWhite Dragonscale Cloak (Exaltation)\t11603\nMount\tA mount\t2\n");
        Assert.Equal(["A mount", "Mote of Greater Potential", "White Dragonscale Cloak", "White Dragonscale Cloak (Exaltation)"], MotePlanningQueries.TargetNames(rows, ""));
        Assert.Equal(["White Dragonscale Cloak", "White Dragonscale Cloak (Exaltation)"], MotePlanningQueries.TargetNames(rows, "white dragonscale"));
        Assert.Equal(9, MotePlanningQueries.TargetRank(rows, "White Dragonscale Cloak"));
        Assert.Equal([4, 4, 9], MotePlanningQueries.CopyRanks(rows, "White Dragonscale Cloak"));
        Assert.Empty(MotePlanningQueries.CopyRanks(rows, "White Dragonscale Cloak (Exaltation)"));
        Assert.Empty(MotePlanningQueries.CopyRanks(rows, "A mount"));
        Assert.Null(MotePlanningQueries.TargetRank(rows, "Missing"));
    }

    [Fact]
    public void PlannerTargets_StoredOnlyCloakCountsTargetButNotInstalledAugments()
    {
        InventoryExportItem[] rows =
        [
            new("Equipment", "White Dragonscale Cloak +9", 11603, 1, InventoryStorage.Other, Collection: true),
            new("Augmentation", "White Dragonscale Cloak (Exaltation)", 11603, 1, InventoryStorage.Other, Collection: true, Installed: true),
        ];
        Assert.Contains("White Dragonscale Cloak", MotePlanningQueries.TargetNames(rows, "cloak"));
        Assert.Equal(9, MotePlanningQueries.TargetRank(rows, "White Dragonscale Cloak"));
        var copies = MotePlanningQueries.SelectCopies(rows, "White Dragonscale Cloak", 9);
        Assert.True(copies.RemovedTarget);
        Assert.Empty(copies.Spares);
        Assert.Empty(MotePlanningQueries.CopyRanks(rows, "White Dragonscale Cloak (Exaltation)"));
    }

    [Fact]
    public void PlannerCopies_CloakOfScalesIncludesBothStoredSparesAfterRemovingWornTarget()
    {
        var rows = InventoryExport.Parse("Location\tName\tID\tCount\tSlots\nBack\tCloak of Scales +8\t1546\t1\t11\n" +
            "KeyRing\tName\tID\nEquipment\tCloak of Scales +2\t1546\nEquipment\tCloak of Scales +2\t1546\n" +
            "Augmentation\tCloak of Scales +2\t1546\nMount\tCloak of Scales +2\t1546\n");
        Assert.Equal(8, MotePlanningQueries.TargetRank(rows, "Cloak of Scales"));
        Assert.Equal([2, 2, 8], MotePlanningQueries.CopyRanks(rows, "Cloak of Scales"));
        var selection = MotePlanningQueries.SelectCopies(rows, "Cloak of Scales", 8);
        Assert.True(selection.RemovedTarget);
        Assert.Equal([2, 2], selection.Spares);
        Assert.Equal(8, selection.Spares.Sum(MoteValues.CopyPoints));
    }

    [Theory]
    [InlineData(8, 4, 1, "Reach +8: farm 1 more +4 copy.")]
    [InlineData(9, 4, 6, "Reach +9: farm 6 more +4 copies.")]
    [InlineData(10, 4, 24, "Reach +10: farm 24 more +4 copies.")]
    [InlineData(7, 5, 0, "Reach +7: no more copies needed.")]
    public void FarmTargetSummary_ExplainsTargetAndFarmRankInPlainLanguage(int targetRank, int farmRank, int count, string expected)
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            int start = targetRank - 1;
            var vm = await Planner(start, targetRank);
            vm.FodderRank = farmRank;
            vm.Page = MotePlanPage.Farm;
            int needed = vm.Plan!.FarmTargets[0].AdditionalCopies;
            vm.Fodder = Math.Max(0, needed - count).ToString();
            Assert.Contains(expected, Texts(vm));
        });
    }

    [Theory]
    [InlineData(8, 6)]
    [InlineData(9, 22)]
    [InlineData(10, 54)]
    public void FarmTargetSummary_UsesTotalFromCurrentRankRatherThanIndividualStep(int targetRank, int expected)
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var vm = await Planner(7, targetRank, new() { [8] = 3, [9] = 2 });
            vm.FodderRank = 4;
            vm.Page = MotePlanPage.Farm;
            Assert.Equal(expected, vm.Plan!.FarmTargets.Last().AdditionalCopies);
            Assert.Contains($"Reach +{targetRank}: farm {expected} more +4 copies.", Texts(vm));
        });
    }

    internal static void PlannerScrollBody_LongCopyEditorAndResultsAreReachableInEveryTab()
    {
        var rows = new[] { Copy(4), Copy(0, 30) };
        var vm = CreateForVisualTest(rows);
        vm.CurrentRank = 4;
        var panel = new OverlayPanel(vm);
        var host = Host(panel);
        try
        {
            host.Show();
            host.UpdateLayout();
            var tabs = Named<TabControl>(panel, "PlanTabs");
            for (int index = 0; index < 4; index++)
            {
                tabs.SelectedIndex = index;
                host.UpdateLayout();
                var selected = Assert.IsType<TabItem>(tabs.SelectedItem);
                var scroll = Assert.IsType<ScrollViewer>(selected.Content);
                Assert.True(Named<TextBlock>(panel, "PlanSummary").IsVisible);
                Assert.True(scroll.ExtentHeight >= scroll.ViewportHeight);
                scroll.ScrollToEnd();
                host.UpdateLayout();
                Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset, 2);
                var results = Assert.Single(Visuals(scroll).OfType<ItemsControl>(), control => ReferenceEquals(control.ItemsSource, vm.PlanBlocks));
                var finalBlock = Assert.IsAssignableFrom<FrameworkElement>(results.ItemContainerGenerator.ContainerFromIndex(results.Items.Count - 1));
                var resultBottom = finalBlock.TranslatePoint(new Point(0, finalBlock.ActualHeight), scroll);
                Assert.InRange(resultBottom.Y, 0, scroll.ActualHeight + 1);
                if (index == (int)MotePlanPage.Farm)
                {
                    var comparison = Assert.Single(Visuals(scroll).OfType<Expander>(), expander => Equals(expander.Header, "Compare: copies only, one level at a time"));
                    comparison.IsExpanded = true;
                    host.UpdateLayout();
                    Assert.Contains(Visuals(comparison).OfType<TextBlock>(), text => text.Text.StartsWith("No motes or fragments, no spare copies"));
                }
                if (index == (int)MotePlanPage.Upgrade)
                {
                    Assert.True(Named<TextBlock>(panel, "PlanSummary").IsVisible);
                    var heading = Assert.Single(Visuals(scroll).OfType<TextBlock>(), text => text.Text == "What to do");
                    Assert.Equal(FontWeights.SemiBold, heading.FontWeight);
                    var line = Assert.Single(Visuals(scroll).OfType<TextBlock>(), text => text.Text.StartsWith("• +4"));
                    Assert.Equal(16, line.Margin.Left);
                    Assert.Equal(TextWrapping.Wrap, line.TextWrapping);
                }
                if (index == (int)MotePlanPage.WhatIf)
                {
                    vm.CopyRanks[0].Rank = 5;
                    host.UpdateLayout();
                    Assert.Contains("WHAT IF: spare merge value", vm.Summary);
                    vm.CopyRanks[0].Rank = 0;
                    Assert.True(scroll.ScrollableHeight > 800);
                    scroll.ScrollToTop();
                    host.UpdateLayout();
                    var reset = Assert.Single(Visuals(scroll).OfType<Button>(), button => Equals(button.Content, "Reset to inventory ranks"));
                    reset.BringIntoView();
                    host.UpdateLayout();
                    var resetTop = reset.TranslatePoint(new Point(), scroll);
                    Assert.InRange(resetTop.Y, 0, scroll.ActualHeight);
                    var copyControls = Named<ItemsControl>(scroll, "PlanCopyControls");
                    Assert.Equal(30, copyControls.Items.Count);
                    var selectors = Visuals(copyControls).OfType<ComboBox>().ToArray();
                    Assert.Equal(30, selectors.Length);
                    Assert.Equal(Enumerable.Range(0, 11), selectors[0].Items.Cast<int>());
                    Assert.Equal(0, selectors[0].SelectedItem);
                    selectors[0].SelectedItem = 10;
                    Assert.Equal(10, vm.CopyRanks[0].Rank);
                    Assert.NotNull(reset.Command);
                    reset.Command.Execute(reset.CommandParameter);
                    Assert.Equal(0, vm.CopyRanks[0].Rank);
                    var editor = Named<Expander>(scroll, "PlanCopyEditor");
                    editor.IsExpanded = false;
                    host.UpdateLayout();
                    Assert.False(copyControls.IsVisible);
                    editor.IsExpanded = true;
                    host.UpdateLayout();
                    scroll.ScrollToEnd();
                    host.UpdateLayout();
                    var last = Assert.Single(Visuals(scroll).OfType<TextBlock>(), text => text.Text == vm.PlanBlocks.Last().Text);
                    var bottom = last.TranslatePoint(new Point(0, last.ActualHeight), scroll);
                    Assert.InRange(bottom.Y, 0, scroll.ActualHeight + 1);
                }
            }
        }
        finally
        {
            host.Close();
        }
    }

    internal static void PlannerTabs_SwitchTasksWithoutMixingPagesOrRefreshingForNestedControls()
    {
        var vm = CreateForVisualTest([Copy(4), Copy(0)]);
        vm.CurrentRank = 4;
        var panel = new OverlayPanel(vm);
        var host = Host(panel);
        try
        {
            host.Show();
            host.UpdateLayout();
            var tabs = Named<TabControl>(panel, "PlanTabs");
            Assert.Equal(["Upgrade", "Farm copies", "What if", "Math"], tabs.Items.Cast<TabItem>().Select(tab => (string)tab.Header));
            foreach (int index in new[] { 1, 2, 3, 0 })
            {
                tabs.SelectedIndex = index;
                host.UpdateLayout();
                Assert.Equal((MotePlanPage)index, vm.Page);
                var selected = Assert.IsType<TabItem>(tabs.SelectedItem);
                var scroll = Assert.IsType<ScrollViewer>(selected.Content);
                Assert.Contains(Visuals(scroll).OfType<ItemsControl>(), control => ReferenceEquals(control.ItemsSource, vm.PlanBlocks));
                Assert.Equal(index == 1, vm.PlanBlocks.Any(block => block.Kind == MotePlanBlockKind.Comparison));
                Assert.Equal(index == 3, vm.PlanBlocks.Any(block => block.Kind == MotePlanBlockKind.Table) && index != 1);
            }
            tabs.SelectedIndex = (int)MotePlanPage.WhatIf;
            host.UpdateLayout();
            int pageChanges = 0;
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MotePlanOverlayViewModel.Page))
                {
                    pageChanges++;
                }
            };
            var selector = Assert.Single(Visuals(Named<ItemsControl>(panel, "PlanCopyControls")).OfType<ComboBox>());
            selector.SelectedItem = 5;
            Assert.Equal(5, vm.CopyRanks[0].Rank);
            Assert.Equal(MotePlanPage.WhatIf, vm.Page);
            Assert.Equal(0, pageChanges);
        }
        finally
        {
            host.Close();
        }
    }

    private static MotePlanOverlayViewModel CreateForVisualTest(IReadOnlyList<InventoryExportItem> rows)
    {
        var inventory = new QuestInventory();
        DateTime time = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        inventory.ApplyExport("Que", rows, time);
        var snapshot = new InventorySnapshot(1, inventory,
            new InventoryExportSnapshot("Que", new InventoryFileStamp("export", rows.Count, time), rows));
        var definition = OverlayPresenters.All.Single(item => item.Key == "moteplan");
        var vm = new MotePlanOverlayViewModel(definition, NullOverlayActions.Instance, () => snapshot);
        var engine = new LogEngine { CharacterName = "Que" };
        var frame = new LogSessionFrame(engine.Snapshot(time), [], false, EngineQuerySnapshot.Capture(engine));
        vm.Receive(new OverlayFrameMessage(frame, time, new TailProgress(0, 0, 0, true)));
        vm.Name = "Cloak";
        return vm;
    }

    private static Window Host(OverlayPanel panel)
    {
        return new Window
        {
            Content = panel,
            Width = 600,
            Height = 400,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
    }

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        return Assert.Single(Visuals(root).OfType<T>(), element => element.Name == name);
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Visuals(child))
            {
                yield return descendant;
            }
        }
    }

    private static async Task<MotePlanOverlayViewModel> Planner(int start, int target,
        Dictionary<int, int>? motes = null, InventoryExportItem[]? copies = null,
        Dictionary<int, int>? fragments = null, MoteUpgradeKind kind = MoteUpgradeKind.Equipment)
    {
        var rows = new List<InventoryExportItem>(copies ?? []);
        foreach (var pair in motes ?? [])
        {
            rows.Add(new InventoryExportItem("General", MoteTiers.ItemName(MoteValues.Tiers[pair.Key - 1]), pair.Key, pair.Value, InventoryStorage.Carried));
        }
        foreach (var pair in fragments ?? [])
        {
            rows.Add(new InventoryExportItem("General", MoteTiers.FragmentItemName(MoteValues.Tiers[pair.Key - 1]), pair.Key + 100, pair.Value, InventoryStorage.Carried));
        }
        var vm = await Create(rows);
        vm.Name = "Cloak";
        vm.Kind = kind;
        vm.CurrentRank = start;
        vm.TargetRank = target;
        return vm;
    }

    private static async Task<MotePlanOverlayViewModel> Create(IReadOnlyList<InventoryExportItem> rows)
    {
        var inventory = new QuestInventory();
        DateTime time = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        inventory.ApplyExport("Que", rows, time);
        var snapshot = new InventorySnapshot(1, inventory,
            new InventoryExportSnapshot("Que", new InventoryFileStamp("export", rows.Count, time), rows));
        var definition = OverlayPresenters.All.Single(item => item.Key == "moteplan");
        var vm = new MotePlanOverlayViewModel(definition, NullOverlayActions.Instance, () => snapshot);
        var engine = new LogEngine { CharacterName = "Que" };
        var frame = new LogSessionFrame(engine.Snapshot(time), [], false, EngineQuerySnapshot.Capture(engine));
        vm.Receive(new OverlayFrameMessage(frame, time, new TailProgress(0, 0, 0, true)));
        await vm.PendingTargetFilter;
        return vm;
    }

    private static InventoryExportItem Copy(int rank, int count = 1, string name = "Cloak")
    {
        return new InventoryExportItem("Bank1", $"{name} +{rank}", 1, count, InventoryStorage.Bank);
    }

    private static string[] Texts(MotePlanOverlayViewModel vm)
    {
        return vm.PlanBlocks.Where(block => block.Kind != MotePlanBlockKind.Table).Select(block => block.Text).ToArray();
    }

    private static MotePlanBlock[] Tables(MotePlanOverlayViewModel vm)
    {
        return vm.PlanBlocks.Where(block => block.Kind == MotePlanBlockKind.Table).ToArray();
    }
}
