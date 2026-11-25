using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;
using EqLog.Overlay.ViewModels;
using EqLog.Overlay.Views;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class AbilityDetailsViewTests
{
    private static AbilityStats Row(string name, long total)
    {
        return new AbilityStats(name, total, total / 10.0, 0, 1, 0, total, 0);
    }

    private static AbilityBreakdown Breakdown(params AbilityStats[] rows)
    {
        return new AbilityBreakdown("Fight", "", "DPS", rows, Timeline: [], TimelineSeconds: 10, TimelineStart: DateTime.Now);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(180)]
    public void CompactDetails_KeepTabsAndChartUsableAtNarrowWidths(double width)
    {
        RunOnSta(() =>
        {
            var start = new DateTime(2026, 11, 23, 12, 0, 0);
            var breakdown = Breakdown(Row("Melee", 100)) with
            {
                TimelineStart = start,
                Timeline = [new DamageTimelinePoint(1, 100)],
                AbilityTimelines = [new AbilityTimeline("A long damage type name", [new DamageTimelinePoint(1, 100)])],
                DamageEvents = [new DamageEvent(start.AddSeconds(1), Names.You, "a goblin", 100, DamageKind.Melee, null, null)],
                IncomingDamageEvents = [],
            };
            var vm = new AbilityDetailsViewModel(breakdown, compact: true);
            var view = new AbilityDetailsView { DataContext = vm, Height = 180 };
            vm.SelectedTabIndex = AbilityDetailsViewModel.TimelineTab;
            var size = new Size(width, 180);
            Layout(view, size);

            var tabs = Find<TabControl>(view, "DetailsTabs");
            var header = Assert.IsType<UniformGrid>(tabs.Template.FindName("HeaderRow", tabs));
            Assert.Equal(5, header.Children.Count);
            Assert.Equal(1, header.Rows);
            Assert.All(header.Children.OfType<TabItem>(), tab =>
            {
                Assert.True(tab.ActualWidth <= width / 5 + 0.1,
                    $"{AutomationProperties.GetName(tab)} width {tab.ActualWidth} exceeds its {width / 5} cell.");
                Assert.Equal(0, tab.TranslatePoint(new Point(), header).Y, 1);
                Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(tab)));
                Assert.Equal(AutomationProperties.GetName(tab), tab.ToolTip);
            });

            var chart = Find<Border>(tabs, "TimelineFrame");
            Assert.True(chart.ActualHeight >= 50);
            Assert.True(chart.TranslatePoint(new Point(0, chart.ActualHeight), tabs).Y <= tabs.ActualHeight);

            vm.Update(breakdown with
            {
                Timeline = [new DamageTimelinePoint(2, 200)]
            });
            Layout(view, size);
            Assert.Same(tabs, Find<TabControl>(view, "DetailsTabs"));
            Assert.Equal(1, tabs.SelectedIndex);
            Assert.Equal(5, tabs.Items.Count);

            tabs.SelectedIndex = AbilityDetailsViewModel.TypesTab;
            Layout(view, size);
            Assert.Equal(AbilityDetailsViewModel.TypesTab, vm.SelectedTabIndex);
            Assert.True(Find<Grid>(tabs, "LaneTrack").ActualWidth >= 40);
            var axis = Find<ContentControl>(tabs, "LaneAxis");
            Assert.Equal(["0:00", "0:10"], Descendants<TextBlock>(axis).Select(label => label.Text));
        });
    }

    [Fact]
    public void IncomingDamageShowsOnlyDamageTakenByPlayerAcrossUpdates()
    {
        RunOnSta(() =>
        {
            var start = new DateTime(2026, 11, 6, 12, 0, 0);
            var breakdown = Breakdown() with
            {
                TimelineStart = start,
                IncomingDamageEvents =
                [
                    new(start, "a goblin", Names.You, 10, DamageKind.Melee, null, null),
                    new(start.AddSeconds(1), "a goblin", "Fluffy", 20, DamageKind.Melee, null, null),
                    new(start.AddSeconds(2), "a goblin", "Cleric", 30, DamageKind.Melee, null, null),
                ],
            };
            var vm = new AbilityDetailsViewModel(breakdown, compact: false);
            Assert.True(vm.HasIncoming);
            Assert.Contains("10 incoming damage", vm.IncomingSummary);
            Assert.DoesNotContain("20 incoming damage", vm.IncomingSummary);

            var view = new AbilityDetailsView { DataContext = vm };
            vm.SelectedTabIndex = AbilityDetailsViewModel.IncomingTab;
            var size = new Size(800, 400);
            Layout(view, size);
            var tabs = Find<TabControl>(view, "DetailsTabs");
            var incoming = tabs.Items.OfType<TabItem>().Single(t => Equals(t.Header, "Incoming damage"));
            Assert.Same(incoming, tabs.SelectedItem);
            var panel = Find<DockPanel>(tabs, "IncomingPanel");
            Assert.Empty(Descendants<ComboBox>(panel));
            Assert.DoesNotContain(Descendants<Button>(panel), b => b.TemplatedParent is null);
            var summary = Find<TextBlock>(panel, "IncomingSummary");
            Assert.Contains("10 incoming damage", summary.Text);
            var nested = vm.Incoming;
            Assert.NotNull(nested);

            vm.Update(breakdown);
            Assert.Same(incoming, tabs.SelectedItem);
            breakdown = breakdown with
            {
                IncomingDamageEvents = breakdown.IncomingDamageEvents!.Append(
                    new DamageEvent(start.AddSeconds(3), "a goblin", "NewMember", 40, DamageKind.Melee, null, null)).ToArray(),
            };
            vm.Update(breakdown);
            Assert.Contains("10 incoming damage", summary.Text);
            breakdown = breakdown with
            {
                IncomingDamageEvents = breakdown.IncomingDamageEvents!.Append(
                    new DamageEvent(start.AddSeconds(4), "a goblin", Names.You, 40, DamageKind.Melee, null, null)).ToArray(),
            };
            vm.Update(breakdown);
            Layout(view, size);
            Assert.Contains("50 incoming damage", summary.Text);
            Assert.Same(nested, vm.Incoming);
            Assert.Same(incoming, tabs.SelectedItem);
        });
    }

    [Fact]
    public void Update_KeepsGridColumnsSortAndSelectedTab()
    {
        RunOnSta(() =>
        {
            var vm = new AbilityDetailsViewModel(Breakdown(Row("Melee", 100), Row("Fireball", 50)), compact: false);
            var view = new AbilityDetailsView { DataContext = vm };
            var size = new Size(800, 400);
            Layout(view, size);
            var tabs = Find<TabControl>(view, "DetailsTabs");
            var table = Find<DataGrid>(tabs, "AbilityTable");
            var rows = table.ItemsSource;
            var totalColumn = table.Columns[1];
            totalColumn.Width = 222;
            table.Items.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
            table.SelectedItem = vm.Rows.Single(r => r.Name == "Melee");
            Assert.Equal("Melee", vm.SelectedAbility?.Name);
            tabs.SelectedIndex = 1;
            Layout(view, size);

            vm.Update(Breakdown(Row("Melee", 300), Row("Backstab", 200)));
            tabs.SelectedIndex = 0;
            Layout(view, size);

            Assert.Same(tabs, Find<TabControl>(view, "DetailsTabs"));
            Assert.Same(table, Find<DataGrid>(tabs, "AbilityTable"));
            Assert.Same(rows, table.ItemsSource);
            Assert.Same(totalColumn, table.Columns[1]);
            Assert.Equal(222, totalColumn.Width.Value);
            Assert.Single(table.Items.SortDescriptions);
            Assert.Equal(["Backstab", "Melee"], table.Items.Cast<AbilityStats>().Select(r => r.Name));
            Assert.Equal(300, table.Items.Cast<AbilityStats>().Single(r => r.Name == "Melee").Total);
            Assert.Equal(300, vm.SelectedAbility?.Total);
            Assert.Same(vm.SelectedAbility, table.SelectedItem);
        });
    }

    [Fact]
    public void Update_KeepsSelectedTabAndFallsBackWhenItIsHidden()
    {
        var withLanes = Breakdown() with
        {
            AbilityTimelines = [new AbilityTimeline("Melee", [new DamageTimelinePoint(1, 100)])],
        };
        var vm = new AbilityDetailsViewModel(withLanes, compact: false);
        vm.SelectedTabIndex = AbilityDetailsViewModel.TypesTab;

        vm.Update(withLanes with
        {
            TimelineSeconds = 20
        });
        Assert.Equal(AbilityDetailsViewModel.TypesTab, vm.SelectedTabIndex);

        vm.Update(Breakdown());
        Assert.False(vm.HasLanes);
        Assert.Equal(AbilityDetailsViewModel.AbilitiesTab, vm.SelectedTabIndex);
    }

    [Fact]
    public void Update_KeepsDamageTypesScrollPositionAcrossRefreshes()
    {
        RunOnSta(() =>
        {
            AbilityBreakdown WithLanes(int count, int seconds)
            {
                return Breakdown() with
                {
                    TimelineSeconds = seconds,
                    AbilityTimelines = Enumerable.Range(0, count)
                        .Select(i => new AbilityTimeline($"Type {i}", [new DamageTimelinePoint(seconds - 1, seconds * 100)]))
                        .ToArray(),
                };
            }

            var vm = new AbilityDetailsViewModel(WithLanes(40, 10), compact: false);
            var view = new AbilityDetailsView { DataContext = vm };
            vm.SelectedTabIndex = AbilityDetailsViewModel.TypesTab;
            var size = new Size(800, 300);
            Layout(view, size);
            var tabs = Find<TabControl>(view, "DetailsTabs");
            var lanesTab = (TabItem)tabs.Items[2];
            var panel = Find<DockPanel>(tabs, "LanesPanel");
            var scroll = Find<ScrollViewer>(panel, "LaneScroll");
            var rows = Find<ItemsControl>(scroll, "LaneRows");

            scroll.ScrollToVerticalOffset(120);
            Layout(view, size);
            Assert.Equal(120, scroll.VerticalOffset);

            foreach (var count in new[] { 40, 50, 20 })
            {
                vm.Update(WithLanes(count, 30));
                Layout(view, size);

                Assert.Same(lanesTab, tabs.SelectedItem);
                Assert.Same(panel, Find<DockPanel>(tabs, "LanesPanel"));
                Assert.Same(scroll, Find<ScrollViewer>(panel, "LaneScroll"));
                Assert.Same(rows, Find<ItemsControl>(scroll, "LaneRows"));
                Assert.Equal(120, scroll.VerticalOffset);
                Assert.Equal(count, rows.Items.Count);
                Assert.Contains("0:30", Find<TextBlock>(panel, "LanesDescription").Text);
                Assert.Contains("3,000 damage", (string)Find<TextBlock>(rows, "LaneName").ToolTip);
            }

            scroll.ScrollToVerticalOffset(192);
            Layout(view, size);
            Assert.Equal(192, scroll.VerticalOffset);
            vm.Update(WithLanes(20, 40));
            Layout(view, size);
            Assert.Equal(192, scroll.VerticalOffset);

            vm.Update(WithLanes(3, 40));
            Layout(view, size);
            Assert.Equal(0, scroll.ScrollableHeight);
            Assert.Equal(0, scroll.VerticalOffset);

            vm.Update(Breakdown());
            Layout(view, size);
            Assert.Equal(Visibility.Collapsed, lanesTab.Visibility);
            Assert.Equal(0, tabs.SelectedIndex);
            vm.Update(WithLanes(40, 50));
            tabs.SelectedIndex = 2;
            Layout(view, size);
            Assert.Equal(0, Find<ScrollViewer>(tabs, "LaneScroll").VerticalOffset);
        });
    }

    [Fact]
    public void Update_UnchangedChartValuesKeepChartsEvenWithNewCollections()
    {
        var breakdown = Breakdown(Row("Melee", 100)) with
        {
            Timeline = [new DamageTimelinePoint(1, 100)],
            AbilityTimelines = [new AbilityTimeline("Melee", [new DamageTimelinePoint(1, 100)])],
        };
        var vm = new AbilityDetailsViewModel(breakdown, compact: false);
        var timeline = vm.Timeline;
        var lanes = vm.Lanes.ToArray();
        var axis = vm.LaneAxis;

        for (var i = 0; i < 3; i++)
        {
            vm.Update(breakdown with
            {
                Abilities = [Row("Melee", 200 + i)],
                Timeline = [new DamageTimelinePoint(1, 100)],
                AbilityTimelines = [new AbilityTimeline("Melee", [new DamageTimelinePoint(1, 100)])],
            });
            Assert.Same(timeline, vm.Timeline);
            Assert.Equal(lanes, vm.Lanes);
            Assert.Same(axis, vm.LaneAxis);
            Assert.Equal(200 + i, Assert.Single(vm.Rows).Total);
        }
    }

    [Theory]
    [InlineData("timeline-amount", true, false)]
    [InlineData("timeline-second", true, false)]
    [InlineData("lane-amount", false, true)]
    [InlineData("lane-second", false, true)]
    [InlineData("lane-name", false, true)]
    [InlineData("lane-order", false, true)]
    [InlineData("duration", true, true)]
    [InlineData("start", true, true)]
    public void Update_ChartInputChangesOnlyRefreshAffectedGraphs(string change, bool timelineChanges, bool lanesChange)
    {
        var points = new[] { new DamageTimelinePoint(1, 100) };
        var lanePoints = new[] { new DamageTimelinePoint(1, 100) };
        var breakdown = Breakdown() with
        {
            Timeline = points,
            AbilityTimelines =
            [
                new AbilityTimeline("Melee", lanePoints),
                new AbilityTimeline("Fire", [new DamageTimelinePoint(2, 50)]),
            ],
        };
        var vm = new AbilityDetailsViewModel(breakdown, compact: false);
        var timeline = vm.Timeline;
        var lane = vm.Lanes[0];

        switch (change)
        {
            case "timeline-amount":
                points[0] = new DamageTimelinePoint(1, 200);
                break;
            case "timeline-second":
                points[0] = new DamageTimelinePoint(3, 100);
                break;
            case "lane-amount":
                lanePoints[0] = new DamageTimelinePoint(1, 200);
                break;
            case "lane-second":
                lanePoints[0] = new DamageTimelinePoint(3, 100);
                break;
            case "lane-name":
                breakdown = breakdown with
                {
                    AbilityTimelines = [new AbilityTimeline("Ice", lanePoints), breakdown.AbilityTimelines![1]]
                };
                break;
            case "lane-order":
                breakdown = breakdown with
                {
                    AbilityTimelines = breakdown.AbilityTimelines!.Reverse().ToArray()
                };
                break;
            case "duration":
                breakdown = breakdown with
                {
                    TimelineSeconds = 20
                };
                break;
            case "start":
                breakdown = breakdown with
                {
                    TimelineStart = breakdown.TimelineStart!.Value.AddSeconds(1)
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change));
        }

        vm.Update(breakdown);
        Assert.Equal(timelineChanges, !ReferenceEquals(timeline, vm.Timeline));
        Assert.Equal(lanesChange, !ReferenceEquals(lane, vm.Lanes[0]));

        var refreshedTimeline = vm.Timeline;
        var refreshedLane = vm.Lanes[0];
        vm.Update(breakdown);
        Assert.Same(refreshedTimeline, vm.Timeline);
        Assert.Same(refreshedLane, vm.Lanes[0]);
    }

    [Fact]
    public void View_SwitchesFromTableToTabsWhenATimelineArrives()
    {
        RunOnSta(() =>
        {
            var vm = new AbilityDetailsViewModel(new AbilityBreakdown("No fight yet", "", "DPS", []), compact: false);
            var view = new AbilityDetailsView { DataContext = vm };
            var size = new Size(800, 400);
            Layout(view, size);
            Assert.False(vm.HasTabs);
            Assert.Empty(Descendants<TabControl>(view));
            Assert.Single(Descendants<DataGrid>(view));

            vm.Update(Breakdown(Row("Melee", 1)));
            Layout(view, size);
            Assert.True(vm.HasTabs);
            Find<TabControl>(view, "DetailsTabs");
        });
    }

    internal static void Layout(FrameworkElement element, Size size)
    {
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();
    }

    internal static T Find<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        return Descendants<T>(root).First(element => element.Name == name);
    }

    internal static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
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
}
