using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using EqLog.Core.Tracking;
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

    [Fact]
    public void TryUpdate_KeepsGridColumnsSortAndSelectedTab()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new AbilityDetailsView(Breakdown(Row("Melee", 100), Row("Fireball", 50)));
                var tabs = Assert.IsType<TabControl>(view.Root);
                var table = Assert.IsType<DataGrid>(((TabItem)tabs.Items[0]).Content);
                var rows = table.ItemsSource;
                var totalColumn = table.Columns[1];
                totalColumn.Width = 222;
                table.Items.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
                tabs.SelectedIndex = 1;

                Assert.True(view.TryUpdate(Breakdown(Row("Melee", 300), Row("Backstab", 200))));

                Assert.Same(view.Root, tabs);
                Assert.Same(table, ((TabItem)tabs.Items[0]).Content);
                Assert.Same(rows, table.ItemsSource);
                Assert.Same(totalColumn, table.Columns[1]);
                Assert.Equal(222, totalColumn.Width.Value);
                Assert.Equal(1, tabs.SelectedIndex);
                Assert.Single(table.Items.SortDescriptions);
                Assert.Equal(["Backstab", "Melee"], table.Items.Cast<AbilityStats>().Select(r => r.Name));
                Assert.Equal(300, table.Items.Cast<AbilityStats>().Single(r => r.Name == "Melee").Total);
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
    public void TryUpdate_KeepsDamageTypesScrollPositionAcrossRefreshes()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
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

                var view = new AbilityDetailsView(WithLanes(40, 10));
                var tabs = Assert.IsType<TabControl>(view.Root);
                var lanesTab = (TabItem)tabs.Items[2];
                tabs.SelectedItem = lanesTab;
                var panel = Assert.IsType<DockPanel>(lanesTab.Content);
                var scroll = Assert.IsType<ScrollViewer>(panel.Children[1]);
                var grid = Assert.IsType<Grid>(scroll.Content);
                var size = new Size(800, 300);

                void Layout()
                {
                    tabs.Measure(size);
                    tabs.Arrange(new Rect(size));
                    tabs.UpdateLayout();
                }

                Layout();
                scroll.ScrollToVerticalOffset(120);
                Layout();
                Assert.Equal(120, scroll.VerticalOffset);

                foreach (var count in new[] { 40, 50, 20 })
                {
                    Assert.True(view.TryUpdate(WithLanes(count, 30)));
                    Layout();

                    Assert.Same(lanesTab, tabs.SelectedItem);
                    Assert.Same(panel, lanesTab.Content);
                    Assert.Same(scroll, panel.Children[1]);
                    Assert.Same(grid, scroll.Content);
                    Assert.Equal(120, scroll.VerticalOffset);
                    Assert.Equal(count + 1, grid.RowDefinitions.Count);
                    Assert.Contains("0:30", ((TextBlock)panel.Children[0]).Text);
                    Assert.Contains("3,000 damage", (string)((TextBlock)grid.Children[0]).ToolTip);
                }

                scroll.ScrollToVerticalOffset(192);
                Layout();
                Assert.Equal(192, scroll.VerticalOffset);
                Assert.True(view.TryUpdate(WithLanes(20, 40)));
                Layout();
                Assert.Equal(192, scroll.VerticalOffset);

                Assert.True(view.TryUpdate(WithLanes(3, 40)));
                Layout();
                Assert.Equal(0, scroll.ScrollableHeight);
                Assert.Equal(0, scroll.VerticalOffset);

                Assert.True(view.TryUpdate(Breakdown()));
                Layout();
                Assert.Equal(2, tabs.Items.Count);
                Assert.True(view.TryUpdate(WithLanes(40, 50)));
                tabs.SelectedIndex = 2;
                Layout();
                var newPanel = Assert.IsType<DockPanel>(((TabItem)tabs.Items[2]).Content);
                Assert.Equal(0, ((ScrollViewer)newPanel.Children[1]).VerticalOffset);
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
    public void TryUpdate_UnchangedChartValuesKeepVisualsEvenWithNewCollections()
    {
        RunOnSta(() =>
        {
            var breakdown = Breakdown(Row("Melee", 100)) with
            {
                Timeline = [new DamageTimelinePoint(1, 100)],
                AbilityTimelines = [new AbilityTimeline("Melee", [new DamageTimelinePoint(1, 100)])],
            };
            var view = new AbilityDetailsView(breakdown);
            var tabs = Assert.IsType<TabControl>(view.Root);
            var timeline = (TabItem)tabs.Items[1];
            var lanes = (TabItem)tabs.Items[2];
            var timelineContent = timeline.Content;
            var panel = Assert.IsType<DockPanel>(lanes.Content);
            var grid = Assert.IsType<Grid>(((ScrollViewer)panel.Children[1]).Content);
            var laneVisuals = grid.Children.Cast<UIElement>().ToArray();

            for (var i = 0; i < 3; i++)
            {
                var next = breakdown with
                {
                    Abilities = [Row("Melee", 200 + i)],
                    Timeline = [new DamageTimelinePoint(1, 100)],
                    AbilityTimelines = [new AbilityTimeline("Melee", [new DamageTimelinePoint(1, 100)])],
                };
                Assert.True(view.TryUpdate(next));
                Assert.Same(timelineContent, timeline.Content);
                Assert.Same(panel, lanes.Content);
                Assert.Equal(laneVisuals, grid.Children.Cast<UIElement>());
            }
        });
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
    public void TryUpdate_ChartInputChangesOnlyRefreshAffectedGraphs(string change, bool timelineChanges, bool lanesChange)
    {
        RunOnSta(() =>
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
            var view = new AbilityDetailsView(breakdown);
            var tabs = Assert.IsType<TabControl>(view.Root);
            var timeline = (TabItem)tabs.Items[1];
            var lanes = (TabItem)tabs.Items[2];
            var timelineContent = timeline.Content;
            var panel = Assert.IsType<DockPanel>(lanes.Content);
            var grid = Assert.IsType<Grid>(((ScrollViewer)panel.Children[1]).Content);
            var laneVisual = grid.Children[0];

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

            Assert.True(view.TryUpdate(breakdown));
            Assert.Equal(timelineChanges, !ReferenceEquals(timelineContent, timeline.Content));
            Assert.Equal(lanesChange, !ReferenceEquals(laneVisual, grid.Children[0]));
            Assert.Same(panel, lanes.Content);

            var refreshedTimeline = timeline.Content;
            var refreshedLane = grid.Children[0];
            Assert.True(view.TryUpdate(breakdown));
            Assert.Same(refreshedTimeline, timeline.Content);
            Assert.Same(refreshedLane, grid.Children[0]);
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

    [Fact]
    public void TryUpdate_RequestsRebuildWhenLayoutChanges()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new AbilityDetailsView(new AbilityBreakdown("No fight yet", "", "DPS", []));
                Assert.IsType<DataGrid>(view.Root);
                Assert.False(view.TryUpdate(Breakdown(Row("Melee", 1))));
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
