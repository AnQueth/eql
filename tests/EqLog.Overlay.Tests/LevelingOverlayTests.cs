using EqLog.Application.Sessions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class LevelingOverlayTests
{
    internal static void VerifyGridAndDatePickerInteraction()
    {
        var dates = new LevelingDateRangePicker();
        var grid = new LevelingHistoryGrid();
        var panel = new OverlayPanel("leveling", "Leveling Analyzer", false);
        panel.SetToolbar(dates);
        panel.SetAlternateBody(grid);
        var host = new Window
        {
            Content = panel,
            Width = 680,
            Height = 620,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            host.Show();
            host.UpdateLayout();
            Assert.Equal((int)LevelingPeriod.AllHistory, dates.Preset.SelectedIndex);
            Assert.Null(dates.From.SelectedDate);
            Assert.Null(dates.To.SelectedDate);
            var now = new DateTime(2026, 11, 18, 22, 39, 0);
            foreach (var (period, start, end) in new[]
            {
                (LevelingPeriod.Today, now.Date, now.Date.AddDays(1)),
                (LevelingPeriod.Yesterday, now.Date.AddDays(-1), now.Date),
                (LevelingPeriod.LastSevenDays, now.Date.AddDays(-6), now.Date.AddDays(1)),
                (LevelingPeriod.LastWeek, new DateTime(2026, 11, 9), new DateTime(2026, 11, 16)),
            })
            {
                dates.Preset.SelectedIndex = (int)period;
                Assert.True(dates.TryGetRange(now, out var range, out var error), error);
                Assert.Equal((start, end), range);
                Assert.Equal(start, dates.From.SelectedDate);
                Assert.Equal(end.AddDays(-1), dates.To.SelectedDate);
            }
            dates.From.SelectedDate = new DateTime(2026, 11, 1);
            dates.To.SelectedDate = new DateTime(2026, 11, 3);
            Assert.Equal((int)LevelingPeriod.Custom, dates.Preset.SelectedIndex);
            Assert.True(dates.TryGetRange(now, out var custom, out var customError), customError);
            Assert.Equal((new DateTime(2026, 11, 1), new DateTime(2026, 11, 4)), custom);
            dates.To.SelectedDate = new DateTime(2026, 10, 31);
            Assert.False(dates.TryGetRange(now, out _, out var reversed));
            Assert.Contains("on or after", reversed);
            dates.From.Text = "not-a-date";
            Assert.False(dates.TryGetRange(now, out _, out var invalid));
            Assert.False(string.IsNullOrEmpty(invalid));
            dates.Preset.SelectedIndex = (int)LevelingPeriod.AllHistory;
            Assert.True(dates.TryGetRange(now, out var allHistory, out var allHistoryError), allHistoryError);
            Assert.Equal(DateTime.MinValue, allHistory.Start);
            Assert.Null(dates.From.SelectedDate);
            Assert.Null(dates.To.SelectedDate);
            dates.To.SelectedDate = new DateTime(2026, 11, 3);
            Assert.Equal((int)LevelingPeriod.Custom, dates.Preset.SelectedIndex);
            Assert.True(dates.TryGetRange(now, out var singleDay, out var singleDayError), singleDayError);
            Assert.Equal((new DateTime(2026, 11, 3), new DateTime(2026, 11, 4)), singleDay);
            dates.To.SelectedDate = null;
            Assert.False(dates.TryGetRange(now, out _, out var missingDate));
            Assert.False(string.IsNullOrEmpty(missingDate));
            dates.Preset.SelectedIndex = (int)LevelingPeriod.Today;
            Assert.True(dates.TryGetRange(now.AddDays(1), out var tomorrow, out var rolloverError), rolloverError);
            Assert.Equal(now.Date.AddDays(1), tomorrow.Start);
            Assert.Equal(tomorrow.Start, dates.From.SelectedDate);
            dates.From.IsDropDownOpen = true;
            host.UpdateLayout();
            Assert.True(dates.From.IsDropDownOpen);
            Assert.True(OverlayPanel.IsInteractive(dates.From));
            dates.From.IsDropDownOpen = false;

            var rows = Enumerable.Range(0, 500).Select(index =>
            {
                DateTime start = now.AddMinutes(-index);
                return new LevelingGridRow(start, 50 - index % 49, $"{start:d}\n{start:T}",
                    index == 0 ? "" : $"{start.AddMinutes(1):d}\n{start.AddMinutes(1):T}",
                    "WIZ / ENC", index, index * 1.25, index == 0, $"XP by zone: Test zone {index}");
            }).ToArray();
            grid.Update(rows, "");
            host.UpdateLayout();
            Assert.Equal(new[] { "Started", "Ended", "Level", "Build at the time", "Kills", "Minutes" },
                grid.Table.Columns.Select(column => column.Header));
            Assert.Equal(500, grid.Table.Items.Count);
            Assert.True(grid.Table.IsReadOnly);
            Assert.True(grid.Table.EnableRowVirtualization);
            Assert.True(VirtualizingPanel.GetIsVirtualizing(grid.Table));
            Assert.True(grid.Table.Columns.Sum(column => column.ActualWidth) <= grid.Table.ActualWidth + 0.1);
            grid.Table.SelectedItem = rows[2];
            host.UpdateLayout();
            var selected = Assert.IsType<DataGridRow>(grid.Table.ItemContainerGenerator.ContainerFromIndex(2));
            Assert.Equal(Visibility.Visible, selected.DetailsVisibility);
            Assert.Contains(Visuals(selected).OfType<TextBlock>(), text => text.Text == rows[2].Details);
            var refreshed = rows.Select((row, index) => index == 2 ? row with { Kills = row.Kills + 1 } : row).ToArray();
            grid.Update(refreshed, "");
            Assert.Equal(refreshed[2], grid.Table.SelectedItem);
            grid.Table.ScrollIntoView(grid.Table.Items[^1]);
            host.UpdateLayout();
            Assert.NotNull(grid.Table.ItemContainerGenerator.ContainerFromIndex(499));
            Assert.True(Visuals(grid.Table).OfType<DataGridRow>().Count() < 50);
            panel.SetAlternateBody(null);
            Assert.DoesNotContain(Visuals(panel), element => ReferenceEquals(element, grid.Table));
            panel.SetAlternateBody(grid);
            host.UpdateLayout();
            Assert.Equal(500, grid.Table.Items.Count);
            grid.Update([], "The selected dates are invalid.");
            Assert.Empty(grid.Table.Items);
            Assert.Contains(Visuals(grid).OfType<TextBlock>(), text => text.Text == "The selected dates are invalid.");
        }
        finally
        {
            host.Close();
        }
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

    [Fact]
    public void LevelGridRowsShowDatesBuildKillsMinutesAndXpDetailsWithBlankCurrentEnd()
    {
        var start = new DateTime(2026, 11, 9, 18, 0, 0);
        var summary = new LevelingSummary("Halas", 42, null, start, start.AddMinutes(30),
            1800, 20, 40, 0, 1, 25, 2, 1, 40, 12, start.AddMinutes(5));
        var request = new LevelingRequest("Que", start.Date, start.Date.AddDays(1));
        var report = new LevelingReport([], [], null, null)
        {
            Levels =
            [
                new(43, summary.End, null, true, true, false,
                    [summary with { Zone = "Kedge Keep", Level = 43, Experience = 5.7 }])
                {
                    Kills = 2,
                    Seconds = 78,
                    Builds = ["WIZ/ENC"],
                },
                new(42, start, summary.End, false, true, true,
                    [summary, summary with { Zone = "Blackburrow", Experience = 56.9 }])
                {
                    Kills = 25,
                    Seconds = 1800,
                    Builds = ["WIZ/ENC", "MAG/CLR"],
                },
            ],
        };
        var rows = MainWindow.BuildLevelingGridRows(report, request);
        Assert.Equal(2, rows.Count);
        var current = Assert.Single(rows, row => row.Level == 43);
        Assert.True(current.IsCurrent);
        Assert.Empty(current.EndText);
        Assert.Equal($"{summary.End:d}\n{summary.End:T}", current.StartText);
        Assert.Equal(2, current.Kills);
        Assert.Equal(1.3, current.Minutes);
        Assert.Equal("WIZ/ENC", current.Build);
        var completed = Assert.Single(rows, row => row.Level == 42);
        Assert.Equal($"{summary.End:d}\n{summary.End:T}", completed.EndText);
        Assert.Equal(25, completed.Kills);
        Assert.Equal(30, completed.Minutes);
        Assert.Equal("WIZ/ENC; MAG/CLR", completed.Build);
        Assert.Contains("Halas: 20% XP gained", completed.Details);
        Assert.Contains("Blackburrow: 56.9% XP gained", completed.Details);
        Assert.Contains("Kedge Keep: 5.7% XP gained", current.Details);
        Assert.Empty(MainWindow.BuildLevelingContent(report, request, false).Rows);

        foreach (int minutes in new[] { 5, 15, 30, 60 })
        {
            var otherWindow = MainWindow.BuildLevelingGridRows(report, request with
            {
                WindowMinutes = minutes
            });
            Assert.Equal(rows, otherWindow);
        }
    }

    [Fact]
    public void RepeatedLevelsHaveSeparateRowsAndOnlyTheCurrentOccurrenceHasABlankEnd()
    {
        var start = new DateTime(2026, 11, 9);
        var report = new LevelingReport([], [], null, null)
        {
            Levels =
            [
                new(42, start.AddDays(1), null, true, true, false, []),
                new(42, start, start.AddHours(1), false, true, true, []),
            ],
        };
        var repeated = MainWindow.BuildLevelingGridRows(report, new("Que", start, start.AddDays(2))).ToArray();
        Assert.Equal(2, repeated.Length);
        Assert.True(repeated[0].IsCurrent);
        Assert.Empty(repeated[0].EndText);
        Assert.Equal($"{start.AddHours(1):d}\n{start.AddHours(1):T}", repeated[1].EndText);
    }

    [Fact]
    public void BuildGroupingSplitsXpSourcesWithoutDuplicatingTheLevelRow()
    {
        var start = new DateTime(2026, 11, 9);
        var source = new LevelingSummary("Halas", 42, "Wizard", start, start.AddMinutes(10),
            600, 20, 5, 0, 0, 5, 0, 1, null, null, null);
        var report = new LevelingReport([], [], null, null)
        {
            Levels = [new(42, start, null, true, true, false, [source, source with { Build = "Unknown build" }])
            {
                Builds = ["Wizard", null],
            }],
        };
        var request = new LevelingRequest("Que", start, start.AddDays(1), GroupByBuild: true);
        var row = Assert.Single(MainWindow.BuildLevelingGridRows(report, request));
        Assert.Equal("Wizard; Unknown", row.Build);
        Assert.Contains("Halas | Wizard:", row.Details);
        Assert.Contains("Halas | Unknown build:", row.Details);
        Assert.Equal(row.Build, Assert.Single(MainWindow.BuildLevelingGridRows(report,
            request with { GroupByBuild = false })).Build);
    }

    [Theory]
    [InlineData(0, 1, 0, 0, "Reward XP recorded")]
    [InlineData(1, 0, 1, 0, "XP amount not logged")]
    [InlineData(2, 0, 1, 20, "20% XP recorded + unknown")]
    public void XpSourcesWithoutKnownAmountsOrLevelsAreNotInventedOrPresentedAsZero(
        int gains, int rewards, int unknown, double experience, string expected)
    {
        var start = new DateTime(2026, 11, 9);
        var source = new LevelingSummary("Halas", null, null, start, start,
            0, experience, gains, unknown, rewards, 0, 0, 1, null, null, null);
        var report = new LevelingReport([], [], null, start)
        {
            Levels = [new(null, start, start, false, false, false, [source])],
        };
        var result = Assert.Single(MainWindow.BuildLevelingGridRows(report, new("Que", start, start.AddDays(1))));
        Assert.Null(result.Level);
        Assert.Equal("Unknown", result.Build);
        Assert.Contains($"Halas: {expected}", result.Details);
        if (unknown > 0)
        {
            Assert.Contains($"XP gains without amounts: {unknown}", result.Details);
        }
        if (rewards > 0)
        {
            Assert.Contains($"Reward XP events: {rewards} (amount not logged)", result.Details);
        }
    }

    [Fact]
    public void LevelsWithoutXpStayVisibleAndUncertainDatesAreExplicitRatherThanFakeOrBlank()
    {
        var start = new DateTime(2026, 11, 9);
        var report = new LevelingReport([], [], null, null)
        {
            Levels =
            [
                new(42, start, start.AddHours(1), false, false, false, []),
                new(40, start.AddDays(-1), null, false, false, false, []),
            ],
        };
        var rows = MainWindow.BuildLevelingGridRows(report, new("Que", start.AddDays(-1), start.AddDays(1)));
        var observed = Assert.Single(rows, row => row.Level == 42);
        Assert.EndsWith(" *", observed.StartText);
        Assert.EndsWith(" *", observed.EndText);
        Assert.Contains($"Start (first observed): {start:G}\nEnd (next level observed): {start.AddHours(1):G}", observed.Details);
        Assert.Equal("Unknown", Assert.Single(rows, row => row.Level == 40).EndText);
        Assert.All(rows, row => Assert.Contains("No XP gains recorded", row.Details));
    }

    [Fact]
    public void GridSortsByActualStartThenLevelDescendingRatherThanFormattedDateText()
    {
        var october = new DateTime(2026, 10, 31, 23, 0, 0);
        var november = new DateTime(2026, 11, 1, 0, 0, 0);
        var report = new LevelingReport([], [], null, null)
        {
            Levels =
            [
                new(50, october, november, false, true, true, []),
                new(2, november, november, false, true, true, []),
                new(3, november, null, true, true, false, []),
            ],
        };
        var rows = MainWindow.BuildLevelingGridRows(report, new("Que", october.Date, november.AddDays(1)));
        Assert.Equal(new int?[] { 3, 2, 50 }, rows.Select(row => row.Level));
    }

    [Fact]
    public void ShortVisitsAreNotRankedByTheirExtrapolatedHourlyAverage()
    {
        var start = new DateTime(2026, 11, 12, 13, 15, 0);
        var shortVisits = new LevelingSummary("Befallen", 5, null, start, start.AddDays(2),
            1080, 396.1, 51, 0, 0, 51, 0, 4, 1331.44, null, null);
        var qualified = shortVisits with
        {
            Zone = "Other zone",
            Seconds = 1800,
            Experience = 60,
            Visits = 1,
            PercentPerHour = 120,
            BestWindowExperience = 35,
            BestWindowStart = start,
        };
        var shortVisit = shortVisits with
        {
            End = start.AddMinutes(4),
            Seconds = 240,
            Experience = 79.2,
            Visits = 1,
        };
        var report = new LevelingReport([shortVisit, qualified], [shortVisits, qualified], null, qualified.End);
        var request = new LevelingRequest("Que", start.Date, start.Date.AddDays(3));

        var ranked = MainWindow.BuildLevelingContent(report, request, true);
        Assert.DoesNotContain(ranked.Rows, row => row.Left == "Befallen | level 5");
        Assert.Equal("35% XP / 15m", Assert.Single(ranked.Rows, row => row.Left == "Other zone | level 5").Right);
        Assert.Contains(ranked.Rows, row => row.Left.Contains("1 zone/level group(s) excluded"));
        Assert.Contains("1 ranked place(s)", ranked.Subtitle);

    }

    [Theory]
    [InlineData(5, 120, "120% XP / 5m")]
    [InlineData(15, 20, "20% XP / 15m")]
    [InlineData(30, 40, "40% XP / 30m")]
    [InlineData(60, 80, "80% XP / 60m")]
    public void PrimaryAndRecentResultsShowMeasuredWindowGainRatherThanAnHourlyProjection(
        int minutes, double experience, string expected)
    {
        var start = new DateTime(2026, 11, 9);
        var summary = new LevelingSummary("Halas", 42, null, start, start.AddMinutes(minutes),
            minutes * 60, experience, 20, 0, 0, 20, 0, 1, 9000, experience, start);
        var request = new LevelingRequest("Que", start, start.AddDays(1), minutes);
        var content = MainWindow.BuildLevelingContent(new([summary], [summary], summary, summary.End), request, true);
        Assert.Equal(expected, Assert.Single(content.Rows, row => row.Left == "Halas | level 42").Right);
        Assert.Equal(expected, Assert.Single(content.Rows, row => row.Left.StartsWith($"Latest {minutes}m:")).Right);
        Assert.DoesNotContain(content.Rows, row => row.Right.Contains("/hr"));
        Assert.DoesNotContain(content.Rows, row => row.Details?.Contains("Average pace:") ?? false);
    }

    [Fact]
    public void UnqualifiedPlacesExplainShortSamplesWithoutClaimingHistoryIsMissing()
    {
        var start = new DateTime(2026, 11, 9);
        var summary = new LevelingSummary("Halas", 42, null, start, start.AddMinutes(2),
            120, 10, 5, 0, 0, 5, 0, 1, 300, null, null);
        var report = new LevelingReport([summary], [summary], summary, summary.End);
        var content = MainWindow.BuildLevelingContent(report, new("Que", start, start.AddDays(1)), true);
        Assert.Contains(content.Rows, row => row.Left.Contains("No continuous 15-minute runs"));
        Assert.DoesNotContain(content.Rows, row => row.Left.StartsWith("Latest 15m:"));
        Assert.DoesNotContain(content.Rows, row => row.Left.Contains("No recorded activity"));
        Assert.Contains("0 ranked place(s)", content.Subtitle);
    }

    [Fact]
    public void IdleOnlyPlacesAreNotRankedAsZeroXpPlaces()
    {
        var start = new DateTime(2026, 11, 9);
        var idle = new LevelingSummary("Halas", 42, null, start, start.AddHours(1),
            3600, 0, 0, 0, 0, 0, 0, 1, 0, 0, start);
        var report = new LevelingReport([idle], [idle], null, idle.End);
        var request = new LevelingRequest("Que", start, start.AddDays(1), 60);
        var places = MainWindow.BuildLevelingContent(report, request, true);
        Assert.DoesNotContain(places.Rows, row => row.Left == "Halas | level 42");
        Assert.Contains(places.Rows, row => row.Left.Contains("No continuous 60-minute runs"));
    }

    [Fact]
    public void EmptyHistoryExplainsBackfillAndMissingLogs()
    {
        var start = new DateTime(2026, 11, 9);
        var content = MainWindow.BuildLevelingContent(new([], [], null, null), new("Que", start, start.AddDays(7)), false);
        Assert.Contains(content.Rows, row => row.Left.Contains("imported automatically"));
        Assert.Contains(content.Rows, row => row.Left.Contains("missing or deleted logs"));
    }
}
