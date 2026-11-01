using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;
using EqLog.Application.Inventory;
using EqLog.Application.Motes;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class MoteOverlayTests
{
    [Fact]
    public void InventorySelectionPreservesSearchAndPlansForSelectedItem()
    {
        OnSta(() =>
        {
            var search = new TextBox { Text = "bracer" };
            var selector = new ComboBox
            {
                ItemsSource = new[] { "Leather Bracer", "Steel Bracer" },
            };
            string? target = null;
            MainWindow.BindPlanInventorySelection(search, selector, name =>
            {
                target = name;
            });
            selector.SelectedIndex = 0;
            Assert.Equal("bracer", search.Text);
            Assert.Equal("Leather Bracer", target);
            Assert.Equal("Leather Bracer", MainWindow.PlanTargetName(MoteUpgradeKind.Equipment, search, selector));
            selector.SelectedIndex = 1;
            Assert.Equal("bracer", search.Text);
            Assert.Equal("Steel Bracer", target);
            MainWindow.UpdatePlanNameChoices(selector, ["Leather Bracer", "Steel Bracer"], search.Text);
            Assert.Equal("Steel Bracer", selector.SelectedItem);
            Assert.Equal("bracer", search.Text);
            search.Text = " leather ";
            Assert.Null(selector.SelectedItem);
            Assert.Equal("leather", MainWindow.PlanTargetName(MoteUpgradeKind.Equipment, search, selector));
            selector.SelectedIndex = 0;
            Assert.Equal("leather", MainWindow.PlanTargetName(MoteUpgradeKind.Spell, search, selector));
            MainWindow.UpdatePlanNameChoices(selector, ["Steel Bracer"], search.Text);
            Assert.Null(selector.SelectedItem);
            Assert.Equal(" leather ", search.Text);
        });
    }

    [Fact]
    public void PlannerGuideRendersTypedCalculationsWithOriginalTextAndLayout()
    {
        OnSta(() =>
        {
            var model = MotePlanningService.Prepare(PlanRequest(6, 8),
                new Dictionary<int, int> { [7] = 7 }, [PlanCopy(6, 2)]);
            var host = Render(model);
            Assert.Equal("What to do", Assert.IsType<TextBlock>(host.Children[0]).Text);
            Assert.Equal(FontWeights.SemiBold, Assert.IsType<TextBlock>(host.Children[0]).FontWeight);
            Assert.Equal("Your item is +6. It needs 192 more points to reach +8.",
                Assert.IsType<TextBlock>(host.Children[1]).Text);
            var line = Assert.Single(host.Children.OfType<TextBlock>(), text => text.Text.StartsWith("• +6"));
            Assert.Equal("• +6 → +7: merge your +6 copy (that alone makes +7). " +
                "Use your Superior ×7 first anyway: they stop working at +7, " +
                "so their 49 points carry into +7 → +8", line.Text);
            Assert.Equal(16, line.Margin.Left);
            Assert.Equal(TextWrapping.Wrap, line.TextWrapping);
            Assert.Contains("Stop at +8. No further upgrades are planned.", Texts(host));
        });
    }

    [Fact]
    public void PlannerMathRendersOriginalHeadersRowsTotalsAndToolTips()
    {
        OnSta(() =>
        {
            var model = MotePlanningService.Prepare(PlanRequest(4, 6), new Dictionary<int, int>(), []);
            var host = Render(model, MotePlanPage.Math);
            var tables = host.Children.OfType<Grid>().ToArray();
            Assert.Equal(2, tables.Length);
            Assert.Equal(["Step", "Need", "Have", "Use", "Missing", "Carry"], TableRow(tables[0], 0));
            Assert.Equal(["+4 → +5", "16", "0", "", "Major ×4", "4"], TableRow(tables[0], 1));
            Assert.Equal(["+5 → +6", "32", "4", "", "Greater ×5", "2"], TableRow(tables[0], 2));
            Assert.Equal("Need: points for the step. Have: points carried in. Use: owned motes and spare copies spent. " +
                "Missing: motes still to get. Carry: extra points that roll into the next step.", tables[0].ToolTip);
            Assert.Equal(["Owned motes used", "none"], TableRow(tables[1], 0));
            Assert.Equal(["Copies merged", "none"], TableRow(tables[1], 1));
            Assert.Equal(["Still missing", "Major ×4, Greater ×5"], TableRow(tables[1], 2));
            Assert.Equal(["Points to +6", "48 (from +4, 0 in)"], TableRow(tables[1], 3));
            Assert.All(tables[1].Children.OfType<TextBlock>().Where(text => Grid.GetColumn(text) == 1),
                text =>
                {
                    Assert.Equal(FontWeights.SemiBold, text.FontWeight);
                });
        });
    }

    [Fact]
    public void PlannerFeedingRendersGroupedGuideSavingsAndExactMath()
    {
        OnSta(() =>
        {
            var request = PlanRequest(4, 5) with
            {
                Feeding = true,
            };
            var model = MotePlanningService.Prepare(request,
                new Dictionary<int, int> { [1] = 5, [2] = 10, [3] = 10 }, [PlanCopy(4), PlanCopy(0, 2)]);
            var guide = Render(model);
            Assert.Contains("• 2 +0 items: feed Infinitesimal ×1, Minor ×2, Lesser ×2 each → +3", Texts(guide));
            Assert.Contains("Leveling copies saves you Major ×3 by using Infinitesimal ×2, Minor ×4, Lesser ×4 instead.", Texts(guide));
            Assert.Contains("You already have everything you need!", Texts(guide));
            var math = Render(model, MotePlanPage.Math);
            var tables = math.Children.OfType<Grid>().ToArray();
            Assert.Equal(["Copy", "To", "Feed", "Merge pts", "Wasted"], TableRow(tables[0], 0));
            Assert.Equal(["+0", "+3", "Infinitesimal ×1, Minor ×2, Lesser ×2", "8", ""], TableRow(tables[0], 1));
            Assert.Equal(TableRow(tables[0], 1), TableRow(tables[0], 2));
            Assert.Equal("Feed each copy these motes, using only the tier that matches each rank, then merge it into the main item. " +
                "Wasted: points past the copy's last rank, which merging doesn't pass on.", tables[0].ToolTip);
            Assert.Equal(["+4 → +5", "16", "0", "copy +3 ×2", "", "0"], TableRow(tables[1], 1));
            Assert.Equal(["Feeding saves", "Major ×3"], TableRow(tables[2], 3));
            Assert.Equal(["…by spending", "Infinitesimal ×2, Minor ×4, Lesser ×4"], TableRow(tables[2], 4));
        });
    }

    [Fact]
    public void PlannerFeedingWasteRemainsSeparateFromMergePoints()
    {
        OnSta(() =>
        {
            var model = MotePlanningService.Prepare(PlanRequest(5, 10) with
            {
                Feeding = true,
            }, new Dictionary<int, int> { [1] = 1, [2] = 2, [3] = 2, [4] = 2, [5] = 4 },
                [PlanCopy(5), PlanCopy(4)]);
            var host = Render(model, MotePlanPage.Math);
            var tables = host.Children.OfType<Grid>().ToArray();
            Assert.Equal(["+4", "+5", "Major ×4", "32", "4"], TableRow(tables[0], 1));
            Assert.Contains("Points wasted feeding", tables[^1].Children.OfType<TextBlock>().Select(text => text.Text));
            var wasteLabel = Assert.Single(tables[^1].Children.OfType<TextBlock>(), text => text.Text == "Points wasted feeding");
            Assert.Equal(["Points wasted feeding", "4"], TableRow(tables[^1], Grid.GetRow(wasteLabel)));
        });
    }

    [Fact]
    public void PlannerFarmRendersTotalTargetsAndExpandableOneStepComparisonWithoutGuide()
    {
        OnSta(() =>
        {
            var model = MotePlanningService.Prepare(PlanRequest(7, 10) with
            {
                Page = MotePlanPage.Farm,
                FodderRank = 4,
                Feeding = true,
            }, new Dictionary<int, int> { [8] = 3, [9] = 2 }, []);
            var host = Render(model, MotePlanPage.Farm);
            Assert.Equal("How many +4 copies should I farm?", Assert.IsType<TextBlock>(host.Children[0]).Text);
            Assert.Equal(["Reach +8: farm 7 more +4 copies.", "Reach +9: farm 22 more +4 copies.",
                "Reach +10: farm 54 more +4 copies."],
                host.Children.OfType<TextBlock>().Where(text => text.Text.StartsWith("Reach")).Select(text => text.Text));
            Assert.All(host.Children.OfType<TextBlock>().Where(text => text.Text.StartsWith("Reach")),
                text =>
                {
                    Assert.Equal(FontWeights.SemiBold, text.FontWeight);
                });
            Assert.Contains("Each number is the TOTAL to farm from where you are now. Do not add the numbers together.", Texts(host));
            Assert.Contains("These numbers use the motes, spare copies and progress selected above.", Texts(host));
            Assert.Contains("Feeding is on: the plan may spend your motes to raise farmed copies before merging them.", Texts(host));
            var comparison = Assert.Single(host.Children.OfType<Expander>());
            Assert.Equal("Compare: copies only, one level at a time", comparison.Header);
            Assert.Equal(["No motes, no spare copies, no feeding, and no saved progress. " +
                "Just merge +4 copies for ONE level at a time:", "+7 to +8: 8 copies at +4.",
                "+8 to +9: 16 copies at +4.", "+9 to +10: 32 copies at +4."],
                Texts(Assert.IsType<StackPanel>(comparison.Content)));
            Assert.DoesNotContain("What to do", Texts(host));
            Assert.NotEmpty(host.Children.OfType<Grid>());
        });
    }

    [Fact]
    public void PlannerFarmExplainsEnteredExtraCopiesAndSingularCounts()
    {
        OnSta(() =>
        {
            var model = MotePlanningService.Prepare(PlanRequest(9, 10) with
            {
                Page = MotePlanPage.Farm,
                FodderRank = 4,
                Fodder = "1",
            }, new Dictionary<int, int> { [10] = 16 }, []);
            var host = Render(model, MotePlanPage.Farm);
            Assert.Contains("Reach +10: farm 21 more +4 copies.", Texts(host));
            Assert.Contains("The 1 extra copies you entered are already counted. Only copies beyond those are listed here.", Texts(host));
        });
    }

    [Fact]
    public void PlannerWhatIfRendersHypotheticalValuesAndKeepsMainRankInSummaryAndGuide()
    {
        OnSta(() =>
        {
            var model = MotePlanningService.Prepare(PlanRequest(6, 7) with
            {
                Page = MotePlanPage.WhatIf,
                HypotheticalCopies = [5, 5],
            }, new Dictionary<int, int>(), [PlanCopy(6), PlanCopy(0), PlanCopy(1)]);
            Assert.Equal("WHAT IF: spare merge value 3 to 64 points before feeding; main item stays +6. Inventory is unchanged." +
                Environment.NewLine + "2 spare copies in inventory: +0, +1 (one +6 is the item being upgraded).",
                MainWindow.PlanSummary(model, MotePlanPage.WhatIf));
            var host = Render(model, MotePlanPage.WhatIf);
            Assert.Contains("• +6 → +7: merge your copies 2 × +5", Texts(host));
            Assert.Contains("You already have everything you need!", Texts(host));
            Assert.DoesNotContain(host.Children.Cast<UIElement>(), element => element is Grid or Expander);
        });
    }

    [Fact]
    public void PlannerValidationInventoryNotesCompletionAndRuleTipsKeepExactWording()
    {
        OnSta(() =>
        {
            var invalid = MotePlanningService.Prepare(PlanRequest(4, 5) with
            {
                Points = "bad",
                Fodder = "1024",
                Page = MotePlanPage.Farm,
            }, new Dictionary<int, int>(), [PlanCopy(4, 2), PlanCopy(9)]);
            Assert.Equal("Points must be a whole number from 0 to 15 at +4; using 0." + Environment.NewLine +
                "Extra copies must be a whole number from 0 to 1023; using 0." + Environment.NewLine +
                "1 spare copy in inventory: +4 (one +4 is the item being upgraded)." + Environment.NewLine +
                "You already have a +9 copy, so it isn't counted as a spare. Set Current to +9 to plan from it.",
                MainWindow.PlanSummary(invalid, MotePlanPage.Farm));
            var complete = MotePlanningService.Prepare(PlanRequest(5, 5), new Dictionary<int, int>(), []);
            Assert.Equal(["Already at the +5 target. No points needed."], Texts(Render(complete)));
            Assert.Equal("Each step uses its matching mote tier. Bigger motes are saved until a higher rank needs them. " +
                "Spare copies are merged after motes. Condensing and Void-Touched motes are not used.",
                MainWindow.PlanSummaryToolTip(MoteUpgradeKind.Equipment));
            Assert.Equal("Spell motes double in value each tier (1 to 512 points) and work at any +level. " +
                "Smallest motes are spent first. Condensing and Void-Touched motes are not used.",
                MainWindow.PlanSummaryToolTip(MoteUpgradeKind.Spell));
            var invalidAtZero = MotePlanningService.Prepare(PlanRequest(0, 1) with
            {
                Points = "1",
            }, new Dictionary<int, int>(), []);
            var tables = Render(invalidAtZero, MotePlanPage.Math).Children.OfType<Grid>().ToArray();
            Assert.Equal(["Points to +1", "1 (from +0, 0 in)"], TableRow(tables[^1], 3));
        });
    }

    [Fact]
    public void PlannerSpellRendersDoublingValuesWithoutEquipmentTotalsOrFeeding()
    {
        OnSta(() =>
        {
            var model = MotePlanningService.Prepare(PlanRequest(0, 3) with
            {
                Kind = MoteUpgradeKind.Spell,
                Feeding = true,
            }, new Dictionary<int, int> { [1] = 3 }, [PlanCopy(5)]);
            var host = Render(model, MotePlanPage.Math);
            var tables = host.Children.OfType<Grid>().ToArray();
            Assert.Equal(2, tables.Length);
            Assert.Equal(["+0 → +1", "1", "0", "Infinitesimal ×1", "", "0"], TableRow(tables[0], 1));
            Assert.Equal(["+1 → +2", "2", "0", "Infinitesimal ×2", "", "0"], TableRow(tables[0], 2));
            Assert.Equal(["+2 → +3", "4", "0", "", "Lesser ×1", "0"], TableRow(tables[0], 3));
            Assert.DoesNotContain("Copies merged", tables[1].Children.OfType<TextBlock>().Select(text => text.Text));
            Assert.Empty(MainWindow.PlanSummary(model, MotePlanPage.Math));
            Assert.Contains("Why Lesser? One of that mote is exactly what each step needs, so nothing is wasted.", Texts(Render(model)));
        });
    }

    [Fact]
    public void PlannerScrollBody_LongCopyEditorAndResultsAreReachableInEveryTab()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var copies = new StackPanel();
                for (int i = 0; i < 30; i++)
                {
                    var selector = MainWindow.CreateHypotheticalCopyRankSelector(4);
                    selector.Height = 40;
                    Assert.Equal(Enumerable.Range(0, 11).Select(rank => $"+{rank}"), selector.Items.Cast<string>());
                    Assert.Equal(4, selector.SelectedIndex);
                    selector.SelectedIndex = 10;
                    Assert.Equal("+10", selector.SelectedItem);
                    copies.Children.Add(selector);
                }
                var reset = new Button { Content = "Reset to inventory ranks", Height = 40 };
                copies.Children.Add(reset);
                var editor = new Expander { Content = copies, IsExpanded = true };
                var results = new TextBlock { Text = "Upgrade instructions", Height = 200 };
                var scroll = MainWindow.CreatePlanScrollBody(editor, results);
                var tabs = MainWindow.CreatePlanTabs(scroll, () => { });
                var size = new Size(600, 400);

                void Layout()
                {
                    tabs.Measure(size);
                    tabs.Arrange(new Rect(size));
                    tabs.UpdateLayout();
                }

                foreach (int index in new[] { 0, 1, 2, 3 })
                {
                    tabs.SelectedIndex = index;
                    Layout();
                    Assert.Same(scroll, ((TabItem)tabs.SelectedItem).Content);
                    Assert.True(scroll.ScrollableHeight > 800);
                    scroll.ScrollToEnd();
                    Layout();
                    Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset);
                    var resultBottom = results.TranslatePoint(new Point(0, results.ActualHeight), scroll);
                    Assert.InRange(resultBottom.Y, 0, scroll.ActualHeight);
                    var resetTop = reset.TranslatePoint(new Point(), scroll);
                    Assert.InRange(resetTop.Y, 0, scroll.ActualHeight);
                }

                ((ComboBox)copies.Children[0]).SelectedIndex = 1;
                Layout();
                Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset);
                editor.IsExpanded = false;
                Layout();
                Assert.Equal(0, scroll.ScrollableHeight);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [Fact]
    public void StopAtSelectorDefaultsToTenAndRetainsTargetsWithinCurrentRankRange()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var selector = new ComboBox();
                Assert.Equal(10, MainWindow.UpdatePlanTargetRanks(selector, 0));
                Assert.Equal(Enumerable.Range(0, 11), selector.Items.Cast<int>());
                selector.SelectedItem = 7;
                Assert.Equal(7, MainWindow.UpdatePlanTargetRanks(selector, 4));
                Assert.Equal(Enumerable.Range(4, 7), selector.Items.Cast<int>());
                Assert.Equal(7, MainWindow.UpdatePlanTargetRanks(selector, 6));
                Assert.Equal(8, MainWindow.UpdatePlanTargetRanks(selector, 8));
                Assert.Equal(10, MainWindow.UpdatePlanTargetRanks(selector, 10));
                Assert.Equal([10], selector.Items.Cast<int>());
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [Fact]
    public void PlannerTargets_IncludeEveryExportedItem_WhileCopiesIncludeEquipmentStorage()
    {
        var rows = InventoryExport.Parse(
            "Location\tName\tID\tCount\tSlots\n" +
            "Bank1\tWhite Dragonscale Cloak +4\t11603\t2\t0\n" +
            "Back-Slot7\tWhite Dragonscale Cloak (Exaltation)\t11603\t1\t11\n" +
            "General1\tMote of Greater Potential\t1\t3\t0\n" +
            "KeyRing\tName\tID\n" +
            "Equipment\tWhite Dragonscale Cloak +9\t11603\n" +
            "Augmentation\tWhite Dragonscale Cloak (Exaltation)\t11603\n" +
            "Mount\tA mount\t2\n");

        var names = MainWindow.PlanTargetNames(rows, "");

        Assert.Equal(["A mount", "Mote of Greater Potential", "White Dragonscale Cloak",
            "White Dragonscale Cloak (Exaltation)"], names);
        Assert.Equal(["White Dragonscale Cloak", "White Dragonscale Cloak (Exaltation)"],
            MainWindow.PlanTargetNames(rows, "white dragonscale"));
        Assert.Equal(9, MainWindow.PlanTargetRank(rows, "White Dragonscale Cloak"));
        Assert.Equal([4, 4, 9], MainWindow.PlanCopyRanks(rows, "White Dragonscale Cloak"));
        Assert.Empty(MainWindow.PlanCopyRanks(rows, "White Dragonscale Cloak (Exaltation)"));
        Assert.Empty(MainWindow.PlanCopyRanks(rows, "A mount"));
        Assert.Null(MainWindow.PlanTargetRank(rows, "Missing"));
    }

    [Fact]
    public void PlannerTargets_StoredOnlyCloakCountsTargetButNotInstalledAugments()
    {
        InventoryExportItem[] rows =
        [
            new("Equipment", "White Dragonscale Cloak +9", 11603, 1, InventoryStorage.Other, Collection: true),
            new("Augmentation", "White Dragonscale Cloak (Exaltation)", 11603, 1, InventoryStorage.Other,
                Collection: true, Installed: true),
        ];

        Assert.Contains("White Dragonscale Cloak", MainWindow.PlanTargetNames(rows, "cloak"));
        Assert.Equal(9, MainWindow.PlanTargetRank(rows, "White Dragonscale Cloak"));
        var copies = MainWindow.PlanCopyRanks(rows, "White Dragonscale Cloak");
        Assert.Equal([9], copies);
        Assert.True(copies.Remove(9));
        Assert.Empty(copies);
        Assert.Empty(MainWindow.PlanCopyRanks(rows, "White Dragonscale Cloak (Exaltation)"));
    }

    [Fact]
    public void PlannerCopies_CloakOfScalesIncludesBothStoredSparesAfterRemovingWornTarget()
    {
        var rows = InventoryExport.Parse(
            "Location\tName\tID\tCount\tSlots\n" +
            "Back\tCloak of Scales +8\t1546\t1\t11\n" +
            "KeyRing\tName\tID\n" +
            "Equipment\tCloak of Scales +2\t1546\n" +
            "Equipment\tCloak of Scales +2\t1546\n" +
            "Augmentation\tCloak of Scales +2\t1546\n" +
            "Mount\tCloak of Scales +2\t1546\n");

        var copies = MainWindow.PlanCopyRanks(rows, "Cloak of Scales");

        Assert.Equal(8, MainWindow.PlanTargetRank(rows, "Cloak of Scales"));
        Assert.Equal([2, 2, 8], copies);
        Assert.True(copies.Remove(8));
        Assert.Equal([2, 2], copies);
        Assert.Equal(8, copies.Sum(MoteValues.CopyPoints));
    }

    [Fact]
    public void PlannerTabs_SwitchTasksWithoutMixingPagesOrRefreshingForNestedControls()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var selector = new ComboBox { ItemsSource = new[] { "+0", "+5" } };
                var page = new StackPanel { Children = { selector } };
                int refreshes = 0;
                var tabs = MainWindow.CreatePlanTabs(page, () => refreshes++);
                Assert.Equal(["Upgrade", "Farm copies", "What if", "Math"],
                    tabs.Items.Cast<TabItem>().Select(tab => (string)tab.Header));
                foreach (int index in new[] { 1, 2, 3, 0 })
                {
                    tabs.SelectedIndex = index;
                    Assert.Same(page, ((TabItem)tabs.Items[index]).Content);
                    Assert.All(tabs.Items.Cast<TabItem>().Where(tab => tab != tabs.SelectedItem),
                        tab => Assert.Null(tab.Content));
                }
                Assert.Equal(4, refreshes);
                selector.SelectedIndex = 1;
                Assert.Equal(4, refreshes);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [Theory]
    [InlineData(8, 4, 1, "Reach +8: farm 1 more +4 copy.")]
    [InlineData(9, 4, 6, "Reach +9: farm 6 more +4 copies.")]
    [InlineData(10, 4, 24, "Reach +10: farm 24 more +4 copies.")]
    [InlineData(7, 5, 0, "Reach +7: no more copies needed.")]
    public void FarmTargetSummary_ExplainsTargetAndFarmRankInPlainLanguage(
        int targetRank, int farmRank, int count, string expected)
    {
        Assert.Equal(expected, MainWindow.FarmTargetSummary(targetRank, farmRank, count));
    }

    [Theory]
    [InlineData(8, 7)]
    [InlineData(9, 22)]
    [InlineData(10, 54)]
    public void FarmTargetSummary_UsesTotalFromCurrentRankRatherThanIndividualStep(int targetRank, int expected)
    {
        var motes = new Dictionary<int, int> { [8] = 3, [9] = 2 };
        int additional = MoteUpgradePlanner.MinimumAdditionalCopiesToFinish(
            7, 0, motes, [], new FeedOptions(FodderRank: 4), targetRank);

        Assert.Equal(expected, additional);
        Assert.Contains($"Reach +{targetRank}:", MainWindow.FarmTargetSummary(targetRank, 4, additional));
    }

    [Theory]
    [InlineData(false, false, "?")]
    [InlineData(true, false, "?")]
    [InlineData(true, true, "0")]
    public void Motes_WithoutDropsOrOwnedMotes_ShowsEveryKnownTier(bool hasInventory, bool hasExport, string expectedOwned)
    {
        var now = new DateTime(2026, 10, 21, 18, 0, 0, DateTimeKind.Utc);
        QuestInventory? inventory = hasInventory ? new QuestInventory() : null;
        if (inventory is not null && hasExport)
        {
            inventory.ApplyExport("Que", [], now);
        }
        var engine = new LogEngine { CharacterName = "Que" };

        var content = RenderMotes(engine.Snapshot(now), engine, inventory);
        var tiers = TierRows(content);

        Assert.Equal(MoteValues.Tiers, tiers.Select(row => row.Left));
        Assert.Equal(MoteValues.MaxRank, tiers.Length);
        Assert.All(tiers, row =>
        {
            Assert.Equal("0  (0)", row.Right);
            Assert.Equal(expectedOwned, row.ExtraColumn);
            Assert.False(string.IsNullOrEmpty(row.ValueColumn));
            Assert.NotEqual("?", row.ValueColumn);
            Assert.Equal(MoteTiers.ItemName(row.Left), row.LootItem);
        });
        var total = Assert.Single(content.Rows, row => row.Left == "Total");
        Assert.Equal("0  (0)", total.Right);
        Assert.Equal(expectedOwned, total.ExtraColumn);
    }

    [Fact]
    public void Motes_WithPartialInventory_ShowsZeroForEveryUnownedTier()
    {
        var now = new DateTime(2026, 10, 21, 18, 0, 0, DateTimeKind.Utc);
        var inventory = new QuestInventory();
        inventory.ApplyExport("Que",
        [
            new InventoryExportItem("General 1", "Mote of Greater Potential", 1, 3, InventoryStorage.Carried),
        ], now);
        var engine = new LogEngine { CharacterName = "Que" };

        var content = RenderMotes(engine.Snapshot(now), engine, inventory);
        var tiers = TierRows(content);

        Assert.Equal(MoteValues.Tiers, tiers.Select(row => row.Left));
        Assert.Equal("3", Assert.Single(tiers, row => row.Left == "Greater").ExtraColumn);
        Assert.All(tiers.Where(row => row.Left != "Greater"), row =>
        {
            Assert.Equal("0", row.ExtraColumn);
        });
        Assert.Equal("3", Assert.Single(content.Rows, row => row.Left == "Total").ExtraColumn);
    }

    [Fact]
    public void Motes_OrdersTiersFromAllSourcesByRank_ThenUnknownNames()
    {
        var now = new DateTime(2026, 10, 21, 18, 0, 0);
        var session = new MoteCount[]
        {
            new("Superior", 2),
            new("Lesser", 3),
            new("Potential", 5),
            new("Greater", 1),
            new("transcendent", 100),
        };
        var allTime = new MoteCount[]
        {
            new("Infinite", 1000),
            new("Minor", 800),
            new("Major", 400),
            new("Greater", 50),
            new("Ascendant", 2),
            new("Zulu", 2000),
        };
        var engine = new LogEngine { CharacterName = "Que" };
        var snapshot = engine.Snapshot(now.AddHours(1)) with
        {
            Motes = new MoteSnapshot(null, now, session, session.Sum(c => c.Count),
                allTime, allTime.Sum(c => c.Count), [], []),
        };
        var inventory = new QuestInventory
        {
            ExportTimes = new() { ["QUE"] = now },
            Items =
            [
                new() { Character = "qUE", Item = "Mote of Infinitesimal Potential", Owned = 7 },
                new() { Character = "Que", Item = "Mote of Grand Potential", Owned = 3 },
                new() { Character = "Que", Item = "Mote of minor Potential", Owned = 9 },
                new() { Character = "Que", Item = "Mote of Alpha Potential", Owned = 2 },
                new() { Character = "Other", Item = "Mote of OtherOnly Potential", Owned = 100 },
                new() { Character = "Que", Item = "A sword", Owned = 1 },
            ],
        };

        var content = RenderMotes(snapshot, engine, inventory);
        var tiers = TierRows(content);

        Assert.Equal(
            ["Infinitesimal", "Minor", "Lesser", "Potential", "Major", "Greater", "Superior", "Grand", "Ascendant", "Infinite",
                "Alpha", "transcendent", "Zulu"],
            tiers.Select(row => MoteTiers.TierOf(row.LootItem!)));

        Assert.Equal(("Tier", "Gear/Spell", "session (all)", "Owned"),
            (content.Rows[0].Left, content.Rows[0].ValueColumn, content.Rows[0].Right, content.Rows[0].ExtraColumn));
        Assert.Contains("the first number is for gear; the second is for spells", content.Rows[0].ToolTip);
        Assert.Contains("5/16 ≤+4 means 5 gear points at +4 or lower", content.Rows[0].ToolTip);
        var byTier = tiers.ToDictionary(row => MoteTiers.TierOf(row.LootItem!)!, StringComparer.OrdinalIgnoreCase);
        Assert.All(tiers, row =>
        {
            Assert.Equal(MoteTiers.TierOf(row.LootItem!), row.Left);
        });
        Assert.Equal(
            ["1/1 ≤+0", "1/2 ≤+1", "2/4 ≤+2", "4/8 ≤+3", "5/16 ≤+4",
                "6/32 ≤+5", "7/64 ≤+6", "8/128 ≤+7", "9/256 ≤+8", "10/512 ≤+9", "?", "?", "?"],
            tiers.Select(row => row.ValueColumn));
        Assert.Contains("Adds 5 points to gear at +4 or lower.", byTier["Major"].ToolTip);
        Assert.Contains("No upgrade value is known for Alpha", byTier["Alpha"].ToolTip);
        Assert.Equal(("0  (0)", "7"), (byTier["Infinitesimal"].Right, byTier["Infinitesimal"].ExtraColumn));
        Assert.Equal(("0  (800)", "9"), (byTier["Minor"].Right, byTier["Minor"].ExtraColumn));
        Assert.Equal(("5  (0)", "0"), (byTier["Potential"].Right, byTier["Potential"].ExtraColumn));
        Assert.Equal(("1  (50)", "0"), (byTier["Greater"].Right, byTier["Greater"].ExtraColumn));
        var total = Assert.Single(content.Rows, row => row.Left == "Total");
        Assert.Equal(("111  (4252)", "21"), (total.Right, total.ExtraColumn));
        Assert.Equal("", total.ValueColumn);
    }

    [Fact]
    public void Motes_FromLogWithoutInventory_OrdersTiersButKeepsRecentDropsNewestFirst()
    {
        var engine = new LogEngine { CharacterName = "Que" };
        engine.ProcessLines(
        [
            "[Wed Oct 21 18:00:10 2026] --You have looted 20 Mote of Major Potential from Reward Chest.--",
            "[Wed Oct 21 18:00:20 2026] --You have looted a Mote of Infinitesimal Potential from Reward Chest.--",
        ]);
        var snapshot = engine.Snapshot(new DateTime(2026, 10, 21, 19, 0, 0));

        var content = RenderMotes(snapshot, engine, inventory: null);
        var tiers = TierRows(content);

        Assert.Equal("Major", snapshot.Motes.Session[0].Tier);
        Assert.Equal(MoteValues.Tiers, tiers.Select(row => MoteTiers.TierOf(row.LootItem!)));
        Assert.Equal("1  (1)", Assert.Single(tiers, row => row.Left == "Infinitesimal").Right);
        Assert.Equal("20  (20)", Assert.Single(tiers, row => row.Left == "Major").Right);
        Assert.All(tiers.Where(row => row.Left != "Infinitesimal" && row.Left != "Major"), row =>
        {
            Assert.Equal("0  (0)", row.Right);
        });
        Assert.All(tiers, row =>
        {
            Assert.Equal("?", row.ExtraColumn);
        });
        var recent = content.Rows.SkipWhile(row => row.Left != "Recent").Skip(1).ToArray();
        Assert.Equal(["Mote of Infinitesimal Potential", "Mote of Major Potential"], recent.Select(row => row.LootItem));
    }

    [Theory]
    [InlineData("Infinitesimal", "Mote of Infinitesimal Potential", "1/1 ≤+0", 1, 0, 1)]
    [InlineData("Potential", "Mote of Potential", "4/8 ≤+3", 4, 3, 8)]
    [InlineData("Major", "Mote of Major Potential", "5/16 ≤+4", 5, 4, 16)]
    [InlineData("Infinite", "Mote of Infinite Potential", "10/512 ≤+9", 10, 9, 512)]
    public void MotesBuildExactValuesAndExplanationsFromTypedNumbers(
        string name, string item, string value, int rank, int limit, int spellPoints)
    {
        var now = new DateTime(2026, 11, 1, 19, 0, 0, DateTimeKind.Utc);
        var engine = new LogEngine { CharacterName = "Que" };
        var content = RenderMotes(engine.Snapshot(now), engine, inventory: null);
        var tier = Assert.Single(TierRows(content), row => row.Left == name);
        var total = Assert.Single(content.Rows, row => row.Left == "Total");
        string inventoryTip = "No inventory export: export inventory to confirm owned counts." +
            "\nOwned excludes collection, installed items and historical drops." +
            "\nLoot logs do not change Owned. Run /outputfile inventory after looting, spending, or moving motes to refresh.";
        Assert.Equal(inventoryTip, total.ToolTip);
        Assert.Equal(item, tier.LootItem);
        Assert.Equal(value, tier.ValueColumn);
        Assert.Equal($"{item} (rank {rank})\n" +
            $"Adds {rank} points to gear at +{limit} or lower.\n" +
            $"Adds {spellPoints} points to spells at any rank.\n\n" + inventoryTip, tier.ToolTip);
        Assert.Equal("A mote adds points to gear and spells. In the Gear/Spell column, the first number is for gear; the second is for spells.\n" +
            "The ≤+N part means this mote works on gear at +N or lower. Motes work on spells at any rank.\n" +
            "Example: 5/16 ≤+4 means 5 gear points at +4 or lower, or 16 spell points at any rank. " +
            "Values are from the EQLWiki Mote Guide.\n\n" + inventoryTip, content.Rows[0].ToolTip);
    }

    [Fact]
    public void MotesRenderRecentDropsRatesAndSessionSpeedWithOriginalFormatting()
    {
        var now = new DateTime(2026, 11, 1, 19, 0, 0, DateTimeKind.Utc);
        var engine = new LogEngine { CharacterName = "Que" };
        var recent = Enumerable.Range(0, 6).Select(index =>
            new MoteDrop(now.AddMinutes(-index), index == 0 ? Names.You : "Other", index == 0 ? "Major" : "Minor",
                index == 0 ? 2 : 1, "a rat")).ToArray();
        var snapshot = engine.Snapshot(now) with
        {
            Motes = new MoteSnapshot(null, now.AddHours(-2), [], 6, [], 10, recent,
                [new MoteMobRate("a rat", 2, 1, []), new MoteMobRate("a chest", 0, 2, [])]),
        };
        var inventory = new QuestInventory
        {
            ExportTimes = new() { ["QUE"] = now },
        };
        var content = RenderMotes(snapshot, engine, inventory);
        Assert.Equal("6 this session  3/hr", content.Subtitle);
        var total = Assert.Single(content.Rows, row => row.Left == "Total");
        Assert.Equal("6  (10)", total.Right);
        Assert.StartsWith($"Export {now.ToLocalTime():g}; owned counts are confirmed by this export.", total.ToolTip);
        var drops = content.Rows.SkipWhile(row => row.Left != "Recent").Skip(1)
            .TakeWhile(row => row.Left != "Drops here").ToArray();
        Assert.Equal(5, drops.Length);
        Assert.Equal(("Major x2  a rat", now.ToString("HH:mm"), "Mote of Major Potential"),
            (drops[0].Left, drops[0].Right, drops[0].LootItem));
        Assert.Equal(("[Other] Minor  a rat", now.AddMinutes(-1).ToString("HH:mm"), "Mote of Minor Potential"),
            (drops[1].Left, drops[1].Right, drops[1].LootItem));
        var rates = content.Rows.SkipWhile(row => row.Left != "Drops here").Skip(1).ToArray();
        Assert.Equal($"1/2  {0.5:P0}", rates[0].Right);
        Assert.Equal("2/0", rates[1].Right);
    }

    private static OverlayContent RenderMotes(EngineSnapshot snapshot, LogEngine engine, QuestInventory? inventory)
    {
        var readModel = inventory is null ? null : new InventoryReadModel(inventory);
        DateTime? exportTime = readModel is not null &&
            readModel.ExportTimes.TryGetValue(snapshot.CharacterName.ToUpperInvariant(), out DateTime exported)
            ? exported : null;
        var moteInventory = MoteInventoryReadModel.Capture(readModel?.Items ?? [], snapshot.CharacterName, exportTime);
        var view = new OverlayView(new HashSet<ChatChannel>(), [], Inventory: readModel, MoteInventory: moteInventory);
        var presenter = Assert.Single(OverlayPresenters.All, definition => definition.Key == "motes");
        return presenter.Render(snapshot, EngineQuerySnapshot.Capture(engine), view);
    }

    private static OverlayRow[] TierRows(OverlayContent content)
    {
        return content.Rows.Skip(1).TakeWhile(row => row.Left != "Total").ToArray();
    }

    private static MotePlanningRequest PlanRequest(int startRank, int targetRank)
    {
        return new MotePlanningRequest(MoteUpgradeKind.Equipment, startRank, targetRank,
            "0", "0", 0, false, true, true, true, MotePlanPage.Upgrade, "Cloak", true, []);
    }

    private static InventoryExportItem PlanCopy(int rank, int count = 1)
    {
        return new InventoryExportItem("Bank1", $"Cloak +{rank}", 1, count, InventoryStorage.Bank);
    }

    private static StackPanel Render(MotePlanReadModel model, MotePlanPage page = MotePlanPage.Upgrade)
    {
        var host = new StackPanel();
        MainWindow.RenderMotePlan(model, host, 13, page);
        return host;
    }

    private static IEnumerable<string> Texts(StackPanel host)
    {
        return host.Children.OfType<TextBlock>().Select(text => text.Text);
    }

    private static string[] TableRow(Grid table, int row)
    {
        return table.Children.OfType<TextBlock>().Where(text => Grid.GetRow(text) == row)
            .OrderBy(text => Grid.GetColumn(text)).Select(text => text.Text).ToArray();
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
