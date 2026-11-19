using EqLog.Application.Sessions;
using EqLog.Core.Tracking;

namespace EqLog.Application.Tests;

public sealed class LevelingQueriesTests
{
    private static readonly DateTime At = new(2026, 11, 9, 18, 0, 0);

    [Fact]
    public void LevelTotalsIncludeNonXpTimeKillsAndBuildsWithoutDependingOnXpGrouping()
    {
        LevelingMinute[] rows =
        [
            Minute(0, "Wizard") with { Kills = 1 },
            Minute(1, "Wizard") with
            {
                Context = new("other", "Other zone", 42, "Wizard"),
                Experience = 0,
                Gains = 0,
                Kills = 2,
            },
            Minute(2, "Cleric") with { Experience = 0, Gains = 0, Seconds = 30 },
            Minute(3, null) with { Experience = 0, Gains = 0, Seconds = 0 },
            Minute(4, "Cleric") with
            {
                Context = new("next", "Halas", 43, "Cleric"),
                Experience = 0,
                Gains = 0,
                Seconds = 0,
                End = At.AddMinutes(4),
            },
        ];
        var request = new LevelingRequest("Que", At, At.AddHours(1));
        foreach (bool groupByBuild in new[] { false, true })
        {
            var report = LevelingQueries.Prepare(rows, request with { GroupByBuild = groupByBuild });
            var level = Assert.Single(report.Levels, row => row.Level == 42);
            Assert.Equal(3, level.Kills);
            Assert.Equal(150, level.Seconds);
            Assert.Equal(new string?[] { "Wizard", "Cleric", null }, level.Builds);
            Assert.Equal(groupByBuild ? 60 : 90, Assert.Single(level.Sources).Seconds);
        }
    }

    [Fact]
    public void RepeatedLevelsHaveSeparateDatesAndXpSourcesForEachTimeTheyWereReached()
    {
        var rows = Enumerable.Range(0, 4).Select(index => Minute(index, null) with
        {
            Context = new($"visit-{index}", index < 2 ? "Halas" : "Blackburrow", 42 + index % 2, null),
            Experience = (index + 1) * 10,
            LevelStarted = At.AddMinutes(index),
        }).ToArray();
        var request = new LevelingRequest("Que", At, At.AddHours(1));
        var report = LevelingQueries.Prepare(rows, request);
        Assert.Equal(4, report.Levels.Count);
        var earlier = Assert.Single(report.Levels, level => level.Level == 42 && level.Start == At);
        Assert.Equal(At.AddMinutes(1), earlier.End);
        Assert.Equal("Halas", Assert.Single(earlier.Sources).Zone);
        Assert.Equal(10, Assert.Single(earlier.Sources).Experience);
        var later = Assert.Single(report.Levels, level => level.Level == 42 && level.Start == At.AddMinutes(2));
        Assert.Equal(At.AddMinutes(3), later.End);
        Assert.Equal("Blackburrow", Assert.Single(later.Sources).Zone);
        Assert.Equal(30, Assert.Single(later.Sources).Experience);
        var current = Assert.Single(report.Levels, level => level.IsCurrent);
        Assert.Equal(43, current.Level);
        Assert.Equal(At.AddMinutes(3), current.Start);
        Assert.Null(current.End);
        Assert.Equal(current, report.Levels[0]);
        var old43 = Assert.Single(report.Levels, level => level.Level == 43 && !level.IsCurrent);
        Assert.Equal(At.AddMinutes(2), old43.End);
        Assert.False(old43.EndConfirmed);
        var filtered = LevelingQueries.Prepare(rows, request with { Start = At.AddMinutes(2), Level = 42 });
        var selected = Assert.Single(filtered.Levels);
        Assert.Equal(later.Start, selected.Start);
        Assert.Equal(later.End, selected.End);
        Assert.Equal(30, Assert.Single(selected.Sources).Experience);
        Assert.False(selected.IsCurrent);
    }

    [Fact]
    public void ConsecutiveLevelUpsInTheSameSecondRetainEveryLevelAndConfirmedBoundary()
    {
        var rows = Enumerable.Range(42, 3).Select(level => Minute(0, null) with
        {
            Context = new($"level-{level}", "Halas", level, null),
            Seconds = 0,
            End = At,
            Experience = 0,
            Gains = 0,
            LevelStarted = At,
        }).ToArray();
        var report = LevelingQueries.Prepare(rows, new("Que", At, At.AddHours(1)));
        Assert.Equal(3, report.Levels.Count);
        foreach (var level in report.Levels.Where(row => !row.IsCurrent))
        {
            Assert.Equal(At, level.Start);
            Assert.Equal(At, level.End);
            Assert.True(level.StartConfirmed);
            Assert.True(level.EndConfirmed);
        }
        Assert.Equal(44, Assert.Single(report.Levels, row => row.IsCurrent).Level);
    }

    [Fact]
    public void LastWeekUsesPreviousMondayThroughSundayAndCustomEndIsInclusive()
    {
        var now = new DateTime(2026, 11, 18, 17, 3, 42);
        Assert.Equal((new DateTime(2026, 11, 9), new DateTime(2026, 11, 16)),
            LevelingQueries.Period(LevelingPeriod.LastWeek, now));
        Assert.Equal((new DateTime(2026, 11, 9), new DateTime(2026, 11, 10)),
            LevelingQueries.Period(LevelingPeriod.Custom, now, At, At));
        var seven = LevelingQueries.Period(LevelingPeriod.LastSevenDays, now);
        Assert.Equal(TimeSpan.FromDays(7), seven.End - seven.Start);
        Assert.Equal((new DateTime(2026, 11, 12), new DateTime(2026, 11, 19)), seven);
        Assert.Equal(0, seven.End.Second);
        Assert.Throws<ArgumentException>(() => LevelingQueries.Period(LevelingPeriod.Custom, now, now, At));
        Assert.Equal((DateTime.MinValue, LevelingHistory.FloorMinute(DateTime.MaxValue)),
            LevelingQueries.Period(LevelingPeriod.AllHistory, now));
    }

    [Fact]
    public void BuildGroupingIsOptionalAndDefaultWindowsCanCrossBuildChanges()
    {
        var rows = Enumerable.Range(0, 10).Select(index => Minute(index, index < 5 ? "Wizard" : "Cleric")).ToArray();
        var request = new LevelingRequest("Que", At, At.AddHours(1), 5);
        var combined = LevelingQueries.Prepare(rows, request);
        var place = Assert.Single(combined.Places);
        Assert.Null(place.Build);
        Assert.Equal(10, place.Experience);
        Assert.Equal(5, place.BestWindowExperience);
        Assert.Equal(60, place.PercentPerHour);
        var level = Assert.Single(combined.Levels);
        Assert.Equal(10, Assert.Single(level.Sources).Experience);
        var builds = Assert.Single(LevelingQueries.Prepare(rows, request with { GroupByBuild = true }).Levels);
        Assert.Equal(2, builds.Sources.Count);
        Assert.Equal(10, builds.Sources.Sum(source => source.Experience));
        Assert.Equal(2, LevelingQueries.Prepare(rows, request with
        {
            GroupByBuild = true
        }).Places.Count);

        var alternating = rows.Select((row, index) => row with
        {
            Context = row.Context with
            {
                Build = index % 2 == 0 ? "Wizard" : null
            },
        }).ToArray();
        Assert.Equal(5, Assert.Single(LevelingQueries.Prepare(alternating, request).Places).BestWindowExperience);
        var grouped = LevelingQueries.Prepare(alternating, request with
        {
            GroupByBuild = true
        });
        Assert.Contains(grouped.Places, row => row.Build == "Unknown build");
        Assert.All(grouped.Places, row => Assert.Null(row.BestWindowExperience));
    }

    [Fact]
    public void SessionGroupsFullLevelXpAcrossZonesAndVisitsWithoutClippingDatesOrMistakingFilteredLevelForCurrent()
    {
        var rows = Enumerable.Range(0, 4).Select(index => Minute(index, "Wizard") with
        {
            Context = new(index < 2 ? "a" : "b", index < 2 ? "Halas" : "Blackburrow", 42, "Wizard"),
            Experience = index < 2 ? 1 : 3,
            LevelStarted = index == 0 ? At : null,
        }).Append(Minute(4, "Wizard") with
        {
            Context = new("c", "Halas", 43, "Wizard"),
            Seconds = 0,
            End = At.AddMinutes(4),
            Experience = 0,
            Gains = 0,
            LevelStarted = At.AddMinutes(4),
        }).Append(Minute(60, null) with { Character = "Other" }).ToList();
        var history = new LevelingHistory();
        history.Import(new(rows, []));
        var session = new GameSession(new(new(), new(), new(), new(), new(), [], history));
        var request = new LevelingRequest("que", At.AddMinutes(2), At.AddMinutes(4), Zone: "BLACK", Level: 42);
        var level = Assert.Single(session.LevelingReport(request).Levels);
        Assert.Equal(42, level.Level);
        Assert.Equal(At, level.Start);
        Assert.Equal(At.AddMinutes(4), level.End);
        Assert.True(level.StartConfirmed);
        Assert.True(level.EndConfirmed);
        Assert.False(level.IsCurrent);
        Assert.Equal(2, level.Sources.Count);
        Assert.Equal(2, Assert.Single(level.Sources, source => source.Zone == "Halas").Experience);
        Assert.Equal(6, Assert.Single(level.Sources, source => source.Zone == "Blackburrow").Experience);

        var all = session.LevelingReport(new("Que", At, At.AddDays(1)));
        Assert.Equal(new int?[] { 43, 42 }, all.Levels.Select(row => row.Level));
        var current = Assert.Single(all.Levels, row => row.IsCurrent);
        Assert.Equal(43, current.Level);
        Assert.Null(current.End);
        Assert.True(current.StartConfirmed);
        Assert.Empty(current.Sources);
    }

    [Fact]
    public void MissingLevelUpsAreNotClaimedAsExactBoundariesAndUnassignedXpIsNotGuessed()
    {
        LevelingMinute[] rows =
        [
            Minute(0, null),
            Minute(1, null) with
            {
                Context = new("unknown", "Other zone", null, null),
                Experience = 0,
                UnknownGains = 1,
                Rewards = 1,
            },
            Minute(20, null) with
            {
                Context = new("later", "Halas", 45, null),
                LevelStarted = At.AddMinutes(20),
            },
        ];
        var report = LevelingQueries.Prepare(rows, new("Que", At, At.AddHours(1)));
        var first = Assert.Single(report.Levels, row => row.Level == 42);
        Assert.False(first.StartConfirmed);
        Assert.False(first.EndConfirmed);
        Assert.Equal(At.AddMinutes(20), first.End);
        var unknown = Assert.Single(report.Levels, row => row.Level is null);
        Assert.False(unknown.IsCurrent);
        var source = Assert.Single(unknown.Sources);
        Assert.Equal(1, source.UnknownGains);
        Assert.Equal(1, source.Rewards);
        Assert.Equal("Other zone", source.Zone);
        Assert.Equal(45, Assert.Single(report.Levels, row => row.IsCurrent).Level);
    }

    [Fact]
    public void SustainedWindowsRequireFullDurationAndNeverBridgeVisitsOrLevels()
    {
        var rows = Enumerable.Range(0, 15).Select(index => Minute(index, "Wizard") with
        {
            Experience = index < 5 ? 1 : 2,
            Context = new(index < 5 ? "a" : "b", "Halas", index < 10 ? 42 : 43, "Wizard"),
        }).ToArray();
        var request = new LevelingRequest("Que", At, At.AddHours(1), 5);
        var report = LevelingQueries.Prepare(rows, request);
        Assert.Equal(2, report.Places.Count);
        Assert.All(report.Places, row => Assert.Equal(10, row.BestWindowExperience));
        Assert.All(LevelingQueries.Prepare(rows, request with
        {
            WindowMinutes = 15
        }).Places,
            row => Assert.Null(row.BestWindowExperience));

        var partial = rows.Take(5).Select(row => row with { Seconds = 59 }).ToArray();
        Assert.Null(Assert.Single(LevelingQueries.Prepare(partial, request).Places).BestWindowExperience);
    }

    [Fact]
    public void DateCharacterZoneAndLevelFiltersKeepUnknownPercentagesExplicit()
    {
        LevelingMinute[] rows =
        [
            Minute(0, null) with { UnknownGains = 1, Gains = 1, Experience = 0 },
            Minute(1, null),
            Minute(2, null) with { Character = "Other", Experience = 100 },
            Minute(3, null) with { Context = new("visit", "Other zone", 42, null), Experience = 100 },
            Minute(4, null) with { Context = new("visit", "Halas", 43, null), Experience = 100 },
            Minute(60, null) with { Experience = 100 },
        ];
        var report = LevelingQueries.Prepare(rows, new("que", At, At.AddHours(1), 5, Zone: "HAL", Level: 42));
        var row = Assert.Single(report.Places);
        Assert.Equal(1, row.Experience);
        Assert.Equal(1, row.UnknownGains);
        Assert.Null(row.PercentPerHour);
        Assert.Null(row.BestWindowExperience);
    }

    private static LevelingMinute Minute(int index, string? build)
    {
        var time = At.AddMinutes(index);
        return new("log", "Que", new("visit", "Halas", 42, build), time, time, time.AddMinutes(1),
            Seconds: 60, Experience: 1, Gains: 1);
    }
}
