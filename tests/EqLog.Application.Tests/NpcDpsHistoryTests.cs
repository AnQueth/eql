using EqLog.Application.Sessions;
using EqLog.Core;

namespace EqLog.Application.Tests;

public sealed class NpcDpsHistoryTests
{
    private static readonly DateTime Start = new(2026, 11, 4, 1, 0, 0);

    [Fact]
    public void Comparisons_UseWeightedCompletedDpsAndKeepPartialHistoryWithoutAveragingIt()
    {
        var npc = Read(
            Encounter("short", 0, 10, 1000),
            Encounter("long", 20, 90, 1800),
            Encounter("active", 120, 10, 9000, outcome: "Active"),
            Encounter("zoned", 140, 10, 8000, outcome: "Zoned"),
            Encounter("death", 160, 10, 7000, outcome: "You died"),
            Encounter("changed", 180, 10, 6000, outcome: "Loadout changed"),
            Encounter("others", 200, 30, 0));

        var group = Assert.Single(NpcDpsHistory.From(npc));
        Assert.Equal(6, group.Encounters);
        Assert.Equal(2, group.CompletedFights);
        Assert.Equal(2800, group.Damage);
        Assert.Equal(100, group.Seconds);
        Assert.Equal(28, group.AverageDps);
        Assert.Equal(60, group.MeanDps);
        Assert.Equal(100, group.P90Dps);
        Assert.Equal(100, group.P99Dps);
        Assert.Equal(100, group.BestDps);
        Assert.Equal(["changed", "death", "zoned", "active", "long", "short"],
            group.History.Select(f => f.Encounter.Id));
    }

    [Fact]
    public void Comparisons_KeepCharacterLoadoutLevelAndUnknownContextSeparate()
    {
        var npc = Read(
            Encounter("one", 0, 10, 1000),
            Encounter("case", 20, 10, 500, character: "QUE", classes: "dru/wiz/enc"),
            Encounter("other-build", 40, 10, 2000, classes: "SHD/BRD/WIZ"),
            Encounter("other-level", 60, 10, 300, level: 49),
            Encounter("other-character", 80, 10, 4000, character: "Other"),
            Encounter("unknown", 100, 10, 600, classes: null, level: null),
            Encounter("blank", 120, 10, 700, classes: "", level: null));

        var groups = NpcDpsHistory.From(npc);
        Assert.Equal(5, groups.Count);
        var original = Assert.Single(groups, g => g.Character == "QUE" && g.Classes == "DRU/WIZ/ENC" && g.Level == 50);
        Assert.Equal(2, original.CompletedFights);
        Assert.Equal(75, original.AverageDps);
        var unknown = Assert.Single(groups, g => g.Classes is null);
        Assert.Null(unknown.Level);
        Assert.Equal(65, unknown.AverageDps);
        Assert.Equal(2, unknown.History.Count);
        Assert.Equal(400, groups[0].AverageDps);
    }

    [Fact]
    public void Details_PreservePhysicalSplitProcNamesRanksAndResistsWithoutInventingHitStatistics()
    {
        NpcSpellHistory[] spells =
        [
            new("Melee", 100, 0, 0),
            new("Ranged", 600, 0, 0),
            new("Whirlwind", 100, 0, 0),
            new("Conflagration", 100, 0, 0),
            new("Conflagration X", 100, 1, 2),
            new("Chaotic Feedback", 0, 0, 3),
        ];
        var encounter = Encounter("one", 0, 10, 1000) with
        {
            Spells = spells
        };
        var npc = Read(encounter);
        var group = Assert.Single(NpcDpsHistory.From(npc));
        var detail = Assert.Single(group.History);
        Assert.Equal(1000, detail.Abilities.Sum(a => a.Damage));
        Assert.Equal(100, detail.Abilities.Sum(a => a.Dps));
        Assert.Equal(100, detail.Abilities.Sum(a => a.Percent));
        var ranged = Assert.Single(detail.Abilities, a => a.Name == "Ranged");
        Assert.Equal(60, ranged.Dps);
        Assert.Equal(60, ranged.Percent);
        Assert.Equal(0, ranged.Landings);
        Assert.Equal(2, Assert.Single(detail.Abilities, a => a.Name == "Conflagration X").Resists);
        Assert.Equal(3, Assert.Single(detail.Abilities, a => a.Name == "Chaotic Feedback").Resists);
        spells[0] = new("Changed", 999, 0, 0);
        Assert.Equal(100, Assert.Single(detail.Abilities, a => a.Name == "Melee").Damage);
    }

    [Fact]
    public void NoCompletedFights_HasNoFabricatedComparisonAndEmptyHistoryHasNoGroups()
    {
        var group = Assert.Single(NpcDpsHistory.From(Read(Encounter("active", 0, 5, 100, outcome: "Active"))));
        Assert.Equal(0, group.CompletedFights);
        Assert.Null(group.AverageDps);
        Assert.Null(group.BestDps);
        Assert.Null(group.MeanDps);
        Assert.Null(group.P90Dps);
        Assert.Null(group.P99Dps);
        Assert.Single(group.History);
        Assert.Empty(NpcDpsHistory.From(Read()));
        Assert.Throws<ArgumentNullException>(() => NpcDpsHistory.From(null!));
    }

    [Fact]
    public void Percentiles_UseNearestRankOverSortedCompletedFightDps()
    {
        var fights = Enumerable.Range(1, 100).Reverse()
            .Select(i => Encounter(i.ToString(), i * 10, 10, i * 10)).ToArray();
        var group = Assert.Single(NpcDpsHistory.From(Read(fights)));
        Assert.Equal(50.5, group.MeanDps);
        Assert.Equal(90, group.P90Dps);
        Assert.Equal(99, group.P99Dps);
        Assert.Equal(100, group.BestDps);
    }

    [Fact]
    public void SingleCompletedFight_AllStatisticsEqualItsDps()
    {
        var group = Assert.Single(NpcDpsHistory.From(Read(Encounter("one", 0, 10, 750))));
        Assert.Equal(75, group.MeanDps);
        Assert.Equal(75, group.P90Dps);
        Assert.Equal(75, group.P99Dps);
        Assert.Equal(75, group.AverageDps);
    }

    private static NpcReadModel Read(params NpcEncounter[] encounters)
    {
        return NpcReadModel.From(new("A greater sphinx", "Sky 4 (Refined)", Start, Start, 0, null, encounters));
    }

    private static NpcEncounter Encounter(string id, int offset, int seconds, long damage,
        string character = "Que", string? classes = "DRU/WIZ/ENC", int? level = 50, string outcome = "NPC died")
    {
        return new(id, Start.AddSeconds(offset), Start.AddSeconds(offset + seconds - 1), character,
            classes, level, outcome, damage, 250, 50, []);
    }
}
