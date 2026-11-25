using EqLog.Application.Sessions;
using EqLog.Overlay.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class LevelingOverlayTests
{
    internal static void VerifyGridAndDatePickerInteraction()
    {
        var resources = System.Windows.Application.Current.Resources;
        var dateStyles = new ResourceDictionary
        {
            Source = new Uri("/EqLogOverlay;component/Styles/DatePicker.xaml", UriKind.Relative),
        };
        resources.MergedDictionaries.Add(dateStyles);
        using var vm = LevelingOverlayViewModelTests.CreateViewModel();
        var panel = new OverlayPanel(vm);
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
            var preset = Named<ComboBox>(panel, "LevelingDatePreset");
            var from = Named<DatePicker>(panel, "LevelingFromDate");
            var to = Named<DatePicker>(panel, "LevelingToDate");
            Assert.Equal((int)LevelingPeriod.AllHistory, preset.SelectedIndex);
            Assert.Null(from.SelectedDate);
            Assert.Equal(System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag, from.Language.IetfLanguageTag, ignoreCase: true);

            preset.SelectedIndex = (int)LevelingPeriod.Today;
            Assert.Equal(DateTime.Today, from.SelectedDate);
            Assert.Equal(DateTime.Today, to.SelectedDate);
            to.SelectedDate = new DateTime(2026, 11, 3);
            Assert.Equal((int)LevelingPeriod.Custom, preset.SelectedIndex);
            Assert.Equal(new DateTime(2026, 11, 3), vm.ToDate);

            var openButton = Assert.IsType<Button>(from.Template.FindName("PART_Button", from));
            openButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            host.UpdateLayout();
            Assert.True(from.IsDropDownOpen);
            Assert.True(OverlayPanel.IsInteractive(from));
            VerifyCalendar(from);
            from.IsDropDownOpen = false;

            var now = new DateTime(2026, 11, 18, 22, 39, 0);
            var rows = Enumerable.Range(0, 500).Select(index =>
            {
                DateTime start = now.AddMinutes(-index);
                return new LevelingGridRow(start, 50 - index % 49, $"{start:d}\n{start:T}",
                    index == 0 ? "" : $"{start.AddMinutes(1):d}\n{start.AddMinutes(1):T}",
                    "WIZ / ENC", index, index * 1.25, index == 0, $"XP by zone: Test zone {index}");
            }).ToArray();
            vm.SetGridRows(rows);
            host.UpdateLayout();
            var table = Named<DataGrid>(panel, "LevelingGrid");
            Assert.Equal(new[] { "Started", "Ended", "Level", "Build at the time", "Kills", "Minutes" },
                table.Columns.Select(column => column.Header));
            Assert.Equal(500, table.Items.Count);
            Assert.True(table.IsReadOnly);
            Assert.True(table.EnableRowVirtualization);
            Assert.True(VirtualizingPanel.GetIsVirtualizing(table));
            Assert.True(table.Columns.Sum(column => column.ActualWidth) <= table.ActualWidth + 0.1);
            panel.FontSize = 13;
            host.UpdateLayout();
            var compactRow = Assert.IsType<DataGridRow>(table.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.InRange(compactRow.ActualHeight, 1, 44);
            panel.FontSize = 18;
            host.UpdateLayout();
            var dateText = Assert.Single(Visuals(compactRow).OfType<TextBlock>(), text => text.Text == rows[0].StartText);
            var measuredDate = new TextBlock { Text = dateText.Text, FontSize = dateText.FontSize, FontFamily = dateText.FontFamily };
            measuredDate.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(compactRow.ActualHeight + 1 >= measuredDate.DesiredSize.Height);
            panel.FontSize = 13;
            host.UpdateLayout();
            table.SelectedItem = rows[2];
            host.UpdateLayout();
            Assert.Equal(rows[2], vm.SelectedGridRow);
            var selected = Assert.IsType<DataGridRow>(table.ItemContainerGenerator.ContainerFromIndex(2));
            Assert.Equal(Visibility.Visible, selected.DetailsVisibility);
            Assert.Contains(Visuals(selected).OfType<TextBlock>(), text => text.Text == rows[2].Details);
            var refreshed = rows.Select((row, index) => index == 2 ? row with { Kills = row.Kills + 1 } : row).ToArray();
            vm.SetGridRows(refreshed);
            Assert.Equal(refreshed[2], table.SelectedItem);
            table.ScrollIntoView(table.Items[^1]);
            host.UpdateLayout();
            Assert.NotNull(table.ItemContainerGenerator.ContainerFromIndex(499));
            Assert.True(Visuals(table).OfType<DataGridRow>().Count() < 50);

            vm.PlacesCommand.Execute(null);
            host.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, Named<DockPanel>(panel, "LevelingGridBody").Visibility);
            vm.LevelsCommand.Execute(null);
            host.UpdateLayout();
            Assert.Equal(Visibility.Visible, Named<DockPanel>(panel, "LevelingGridBody").Visibility);
            Assert.Same(table, Named<DataGrid>(panel, "LevelingGrid"));
        }
        finally
        {
            host.Close();
            resources.MergedDictionaries.Remove(dateStyles);
        }
    }

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        return Assert.Single(Visuals(root).OfType<T>(), element => element.Name == name);
    }

    private static void VerifyCalendar(DatePicker picker)
    {
        var popup = Assert.IsType<Popup>(picker.Template.FindName("PART_Popup", picker));
        var calendar = Assert.IsType<Calendar>(popup.Child);
        calendar.ApplyTemplate();
        calendar.Measure(new Size(300, 300));
        calendar.Arrange(new Rect(0, 0, 300, 300));
        calendar.UpdateLayout();
        var item = Assert.IsType<CalendarItem>(calendar.Template.FindName("PART_CalendarItem", calendar));
        item.ApplyTemplate();
        item.UpdateLayout();
        var frame = Assert.IsType<Border>(item.Template.FindName("PART_Root", item));
        Assert.Equal(System.Windows.Application.Current.Resources["NightSurface"], frame.Background);
        System.Windows.Application.Current.Resources["NightSurface"] = Brushes.Black;
        Assert.Equal(Brushes.Black, frame.Background);
        System.Windows.Application.Current.Resources["NightSurface"] = Brushes.Gray;

        DateTime before = calendar.DisplayDate;
        var previous = Assert.IsType<Button>(item.Template.FindName("PART_PreviousButton", item));
        previous.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(before.AddMonths(-1).Month, calendar.DisplayDate.Month);
        var header = Assert.IsType<Button>(item.Template.FindName("PART_HeaderButton", item));
        header.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(CalendarMode.Year, calendar.DisplayMode);
        var currentMonth = Assert.Single(Visuals(item).OfType<CalendarButton>(), button => button.HasSelectedDays);
        currentMonth.ApplyTemplate();
        var selectedMonth = Assert.IsType<Border>(currentMonth.Template.FindName("Frame", currentMonth));
        Assert.Equal(System.Windows.Application.Current.Resources["Selection"], selectedMonth.Background);
        var month = Assert.Single(Visuals(item).OfType<CalendarButton>(),
            button => button.DataContext is DateTime value && value.Month == before.Month);
        month.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(CalendarMode.Month, calendar.DisplayMode);
        var chosen = new DateTime(before.Year, before.Month, 15);
        var selected = Assert.Single(Visuals(item).OfType<CalendarDayButton>(),
            button => button.DataContext is DateTime value && value.Date == chosen);
        selected.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
        });
        selected.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent,
        });
        Assert.Equal(chosen, picker.SelectedDate);
        Assert.False(picker.IsDropDownOpen);
        var textBox = Assert.IsType<DatePickerTextBox>(picker.Template.FindName("PART_TextBox", picker));
        textBox.ApplyTemplate();
        var watermark = Assert.IsType<ContentControl>(textBox.Template.FindName("PART_Watermark", textBox));
        Assert.Equal(Visibility.Collapsed, watermark.Visibility);
        picker.SelectedDate = null;
        Assert.Equal(Visibility.Visible, watermark.Visibility);
        var typed = new DateTime(2026, 11, 12);
        textBox.Text = typed.ToString("d", System.Globalization.CultureInfo.CurrentCulture);
        var source = Assert.IsAssignableFrom<PresentationSource>(PresentationSource.FromVisual(textBox));
        textBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
        });
        Assert.Equal(typed, picker.SelectedDate);
        Assert.Equal(Visibility.Collapsed, watermark.Visibility);
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
        var rows = LevelingOverlayViewModel.BuildLevelingGridRows(report, request);
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
        Assert.Empty(LevelingOverlayViewModel.BuildLevelingContent(report, request, false).Rows);

        foreach (int minutes in new[] { 5, 15, 30, 60 })
        {
            var otherWindow = LevelingOverlayViewModel.BuildLevelingGridRows(report, request with
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
        var repeated = LevelingOverlayViewModel.BuildLevelingGridRows(report, new("Que", start, start.AddDays(2))).ToArray();
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
        var row = Assert.Single(LevelingOverlayViewModel.BuildLevelingGridRows(report, request));
        Assert.Equal("Wizard; Unknown", row.Build);
        Assert.Contains("Halas | Wizard:", row.Details);
        Assert.Contains("Halas | Unknown build:", row.Details);
        Assert.Equal(row.Build, Assert.Single(LevelingOverlayViewModel.BuildLevelingGridRows(report,
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
        var result = Assert.Single(LevelingOverlayViewModel.BuildLevelingGridRows(report, new("Que", start, start.AddDays(1))));
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
        var rows = LevelingOverlayViewModel.BuildLevelingGridRows(report, new("Que", start.AddDays(-1), start.AddDays(1)));
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
        var rows = LevelingOverlayViewModel.BuildLevelingGridRows(report, new("Que", october.Date, november.AddDays(1)));
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

        var ranked = LevelingOverlayViewModel.BuildLevelingContent(report, request, true);
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
        var content = LevelingOverlayViewModel.BuildLevelingContent(new([summary], [summary], summary, summary.End), request, true);
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
        var content = LevelingOverlayViewModel.BuildLevelingContent(report, new("Que", start, start.AddDays(1)), true);
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
        var places = LevelingOverlayViewModel.BuildLevelingContent(report, request, true);
        Assert.DoesNotContain(places.Rows, row => row.Left == "Halas | level 42");
        Assert.Contains(places.Rows, row => row.Left.Contains("No continuous 60-minute runs"));
    }

    [Fact]
    public void EmptyHistoryExplainsBackfillAndMissingLogs()
    {
        var start = new DateTime(2026, 11, 9);
        var content = LevelingOverlayViewModel.BuildLevelingContent(new([], [], null, null), new("Que", start, start.AddDays(7)), false);
        Assert.Contains(content.Rows, row => row.Left.Contains("imported automatically"));
        Assert.Contains(content.Rows, row => row.Left.Contains("missing or deleted logs"));
    }
}

