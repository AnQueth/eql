using System.Windows.Threading;
using EqLog.Application.Sessions;
using EqLog.Overlay.ViewModels;
using Xunit;

namespace EqLog.Overlay.Tests;

/// <summary>Focused tests for leveling overlay view-model state and commands.</summary>
public sealed class LevelingOverlayViewModelTests
{
    [Fact]
    public void ViewAndWindowCommandsUpdateViewState()
    {
        var vm = CreateViewModel();

        vm.PlacesCommand.Execute(null);

        Assert.Equal(1, vm.SelectedViewIndex);
        Assert.True(vm.IsBestPlacesView);
        Assert.False(vm.IsLevelsView);

        vm.WindowIndex = 2;
        Assert.Equal(2, vm.WindowIndex);

        vm.LevelsCommand.Execute(null);
        Assert.True(vm.IsLevelsView);
    }

    [Fact]
    public void PresetAndDatePropertiesUseCustomAndAllHistoryRanges()
    {
        var vm = CreateViewModel();

        vm.PresetIndex = (int)LevelingPeriod.AllHistory;
        Assert.Null(vm.FromDate);
        Assert.Null(vm.ToDate);

        vm.PresetIndex = (int)LevelingPeriod.Today;
        Assert.Equal(DateTime.Today, vm.FromDate);
        Assert.Equal(DateTime.Today, vm.ToDate);

        vm.FromDate = new DateTime(2026, 11, 1);
        vm.ToDate = new DateTime(2026, 11, 3);
        Assert.Equal((int)LevelingPeriod.Custom, vm.PresetIndex);
    }

    [Fact]
    public void TryGetRangeFollowsPresetsCustomDatesAndDayRollover()
    {
        var vm = CreateViewModel();
        var now = new DateTime(2026, 11, 18, 22, 39, 0);
        foreach (var (period, start, end) in new[]
        {
            (LevelingPeriod.Today, now.Date, now.Date.AddDays(1)),
            (LevelingPeriod.Yesterday, now.Date.AddDays(-1), now.Date),
            (LevelingPeriod.LastSevenDays, now.Date.AddDays(-6), now.Date.AddDays(1)),
            (LevelingPeriod.LastWeek, new DateTime(2026, 11, 9), new DateTime(2026, 11, 16)),
        })
        {
            vm.PresetIndex = (int)period;
            Assert.True(vm.TryGetRange(now, out var range, out var error), error);
            Assert.Equal((start, end), range);
            Assert.Equal(start, vm.FromDate);
            Assert.Equal(end.AddDays(-1), vm.ToDate);
        }

        vm.FromDate = new DateTime(2026, 11, 1);
        vm.ToDate = new DateTime(2026, 11, 3);
        Assert.Equal((int)LevelingPeriod.Custom, vm.PresetIndex);
        Assert.True(vm.TryGetRange(now, out var custom, out var customError), customError);
        Assert.Equal((new DateTime(2026, 11, 1), new DateTime(2026, 11, 4)), custom);

        vm.ToDate = new DateTime(2026, 10, 31);
        Assert.False(vm.TryGetRange(now, out _, out var reversed));
        Assert.Contains("on or after", reversed);

        vm.PresetIndex = (int)LevelingPeriod.AllHistory;
        Assert.True(vm.TryGetRange(now, out var allHistory, out var allHistoryError), allHistoryError);
        Assert.Equal(DateTime.MinValue, allHistory.Start);
        Assert.Null(vm.FromDate);
        Assert.Null(vm.ToDate);

        vm.PresetIndex = (int)LevelingPeriod.Today;
        Assert.True(vm.TryGetRange(now.AddDays(1), out var tomorrow, out var rolloverError), rolloverError);
        Assert.Equal(now.Date.AddDays(1), tomorrow.Start);
        Assert.Equal(tomorrow.Start, vm.FromDate);
    }

    [Fact]
    public void EditingOneDateFromAllHistoryFillsTheOtherEnd()
    {
        var vm = CreateViewModel();

        vm.ToDate = new DateTime(2026, 11, 3);

        Assert.Equal((int)LevelingPeriod.Custom, vm.PresetIndex);
        Assert.Equal(new DateTime(2026, 11, 3), vm.FromDate);
        Assert.True(vm.TryGetRange(DateTime.Now, out var singleDay, out var error), error);
        Assert.Equal((new DateTime(2026, 11, 3), new DateTime(2026, 11, 4)), singleDay);

        vm.ToDate = null;
        Assert.False(vm.TryGetRange(DateTime.Now, out _, out var missing));
        Assert.False(string.IsNullOrEmpty(missing));

        vm.PresetIndex = (int)LevelingPeriod.AllHistory;
        vm.FromDate = new DateTime(2026, 11, 1);

        Assert.Equal((int)LevelingPeriod.Custom, vm.PresetIndex);
        Assert.Equal(DateTime.Today, vm.ToDate);
    }

    [Fact]
    public void InvalidLevelFilterShowsAnInlineStatus()
    {
        var vm = CreateViewModel("Que");

        vm.Level = "-2";

        Assert.Equal("Enter a positive character level, or leave Level blank for all levels.", vm.GridStatus);
        Assert.Empty(vm.GridRows);
    }

    internal static LevelingOverlayViewModel CreateViewModel(string? character = null)
    {
        var definition = OverlayPresenters.All.Single(item => item.Key == "leveling");
        var source = EmptyOverlayViewSource.Instance;
        var leveling = new TestLevelingServices(character);
        return new LevelingOverlayViewModel(definition, NullOverlayActions.Instance,
            source, leveling, Dispatcher.CurrentDispatcher);
    }

    private sealed class TestLevelingServices(string? character) : ILevelingOverlayServices
    {
        public string? CharacterName { get; } = character;

        public long Revision { get; } = 1;

        public string? StorageError { get; }

        public LevelingReport LevelingReport(LevelingRequest request)
        {
            return new LevelingReport([], [], null, null);
        }
    }
}
