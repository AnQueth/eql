using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;
using EqLog.Overlay.ViewModels;
using EqLog.Overlay.Views;
using Xunit;

namespace EqLog.Overlay.Tests;

public class DpsFightPickerTests
{
    private static readonly DateTime Start = new(2026, 10, 27, 12, 0, 0);

    [Fact]
    public void BreakdownWindow_WithTimeline_AddsDamageTimelineTab()
    {
        RunOnSta(() =>
        {
            var breakdown = new AbilityBreakdown("You", "a goblin", "DPS",
                [new AbilityStats("Melee", 100, 10, 100, 1, 0, 100, 0)],
                [new(0, 100), new(4, 50)], 5, Start,
                [new AbilityTimeline("Melee", [new(0, 100)]), new AbilityTimeline("Fire", [new(1, 25), new(4, 25)])]);
            var window = new AbilityBreakdownWindow(breakdown);
            Assert.Equal("You - ability breakdown", window.Title);
            var viewModel = Assert.IsType<AbilityBreakdownWindowViewModel>(window.DataContext);
            Assert.Equal("Total 100 · 10.0 DPS", viewModel.TotalSummary);
            var details = Assert.IsType<AbilityDetailsView>(window.FindName("Details"));
            Assert.Same(viewModel.Details, details.DataContext);
            Assert.False(viewModel.Details.Compact);
            Assert.True(viewModel.Details.HasTabs);
            Assert.Equal(5, viewModel.Details.Timeline!.Bars.Count);
            Assert.True(viewModel.Details.HasLanes);
            Assert.Equal(["Melee", "Fire"], viewModel.Details.Lanes.Select(lane => lane.Name));

            var view = new AbilityDetailsView { DataContext = viewModel.Details };
            viewModel.Details.SelectedTabIndex = AbilityDetailsViewModel.TimelineTab;
            var size = new Size(800, 400);
            AbilityDetailsViewTests.Layout(view, size);
            var tabs = AbilityDetailsViewTests.Find<TabControl>(view, "DetailsTabs");
            Assert.Equal("Damage timeline", ((TabItem)tabs.Items[1]).Header);
            var bars = AbilityDetailsViewTests.Find<ItemsControl>(tabs, "TimelineBars");
            Assert.Equal(5, AbilityDetailsViewTests.Descendants<UniformGrid>(bars).Single().Children.Count);
            tabs.SelectedIndex = AbilityDetailsViewModel.TypesTab;
            AbilityDetailsViewTests.Layout(view, size);
            Assert.Equal("Damage types", ((TabItem)tabs.Items[2]).Header);
            Assert.Equal(["Melee", "Fire"], AbilityDetailsViewTests.Descendants<TextBlock>(tabs)
                .Where(text => text.Name == "LaneName").Select(text => text.Text));

            var plain = new AbilityBreakdownWindowViewModel(new AbilityBreakdown("Heals", "", "HPS", []));
            Assert.False(plain.Details.HasTabs);
            Assert.True(plain.Details.ShowOverheal);
        });
    }

    [Fact]
    public void BreakdownWindow_WithDamageEvents_AddsChronologicalGridAndScaledTimeMarks()
    {
        RunOnSta(() =>
        {
            var breakdown = new AbilityBreakdown(
                "You",
                "a goblin",
                "DPS",
                [],
                Timeline: [],
                TimelineSeconds: 30,
                TimelineStart: Start,
                AbilityTimelines:
                [
                    new AbilityTimeline("Melee", [new DamageTimelinePoint(0, 10), new DamageTimelinePoint(6, 99)])
                ],
                DamageEvents:
                [
                    new DamageEvent(Start.AddSeconds(6), Names.You, "a goblin", 99, DamageKind.DirectSpell, "Frost", "Critical"),
                    new DamageEvent(Start.AddSeconds(1), Names.You, "a goblin", 10, DamageKind.Melee, null, null),
                ]);
            var vm = new AbilityDetailsViewModel(breakdown, compact: false);
            var view = new AbilityDetailsView { DataContext = vm };
            vm.SelectedTabIndex = AbilityDetailsViewModel.EventsTab;
            var size = new Size(800, 400);
            AbilityDetailsViewTests.Layout(view, size);
            var tabs = AbilityDetailsViewTests.Find<TabControl>(view, "DetailsTabs");
            Assert.Equal("Damage data", ((TabItem)tabs.Items[3]).Header);
            var events = AbilityDetailsViewTests.Find<DataGrid>(tabs, "EventsTable");
            Assert.Equal(7, events.Columns.Count);
            Assert.Equal(2, events.Items.Count);
            Assert.Equal(
                new[] { Start.AddSeconds(1), Start.AddSeconds(6) },
                events.Items.Cast<DamageEvent>().Select(e => e.Time));
            Assert.Equal("Critical", events.Items.Cast<DamageEvent>().Last().Modifier);

            var lane = Assert.Single(vm.Lanes);
            Assert.Equal(6, lane.Guides.Count);
            Assert.Equal(5, lane.Guides.Count(guide => guide.ShowGuide));
            Assert.Contains(vm.LaneAxis!.Ticks, tick => tick.Label == "0:05" && tick.Length == 5);

            tabs.SelectedIndex = AbilityDetailsViewModel.TypesTab;
            AbilityDetailsViewTests.Layout(view, size);
            var guides = AbilityDetailsViewTests.Find<ItemsControl>(tabs, "LaneGuides");
            Assert.Equal(5, AbilityDetailsViewTests.Descendants<Border>(guides).Count(line => line.TemplatedParent is ContentPresenter && line.Visibility == Visibility.Visible));
            var track = AbilityDetailsViewTests.Find<Grid>(tabs, "LaneTrack");
            var guideEnds = AbilityDetailsViewTests.Descendants<Border>(guides)
                .Where(line => line.TemplatedParent is ContentPresenter && line.Visibility == Visibility.Visible)
                .Select(line => line.TranslatePoint(new Point(line.ActualWidth, 0), track).X)
                .Order()
                .ToArray();
            Assert.Equal(
                Enumerable.Range(1, 5).Select(i => track.ActualWidth * i / 6),
                guideEnds,
                new ToleranceComparer(1));
            var axis = AbilityDetailsViewTests.Find<ContentControl>(tabs, "LaneAxis");
            var tick = AbilityDetailsViewTests.Descendants<TextBlock>(axis).Single(label => label.Text == "0:05");
            Assert.True(tick.ActualWidth > 0);
        });
    }

    private static void RunOnSta(Action action)
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

    private sealed class ToleranceComparer(double tolerance) : IEqualityComparer<double>
    {
        public bool Equals(double x, double y)
        {
            return Math.Abs(x - y) <= tolerance;
        }

        public int GetHashCode(double obj)
        {
            return 0;
        }
    }
}
