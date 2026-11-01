using EqLog.Core.Parsing;
using EqLog.Core.Tracking;

namespace EqLog.Core.Tests;

public sealed class NpcCompendiumRuleTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 19, 0, 0);

    [Fact]
    public void Context_ChangedWhoAndLevel_SplitButRepeatedWhoDoesNot()
    {
        var store = new NpcCompendium();
        var tracker = new NpcInteractionTracker(store, "log");
        var who = new WhoLoadoutEvent(T0, "Tester", 50, "DRU/WIZ/ENC");
        tracker.OnEvent(who, 1, "Tester", who);
        tracker.OnEvent(Hit(1, 100), 2, "Tester", who);
        tracker.OnEvent(who with
        {
            Time = T0.AddSeconds(2)
        }, 3, "Tester", who);
        tracker.OnEvent(Hit(3, 100), 4, "Tester", who);
        var changed = who with
        {
            Classes = "SHD/BRD/WIZ",
            Time = T0.AddSeconds(4)
        };
        tracker.OnEvent(changed, 5, "Tester", changed);
        tracker.OnEvent(Hit(5, 50), 6, "Tester", changed);
        var leveled = changed with
        {
            Level = 51
        };
        tracker.OnEvent(new LevelEvent(T0.AddSeconds(6), 51), 7, "Tester", leveled);
        tracker.OnEvent(Hit(7, 25), 8, "Tester", leveled);

        var fights = store.Get("A wraith", "(unknown zone)")!.Encounters;
        Assert.Equal(3, fights.Count);
        Assert.Equal(200, fights.Single(encounter => encounter.Classes == who.Classes).YourDamage);
        Assert.Equal(25, fights.Single(encounter => encounter.Level == 51).YourDamage);
        Assert.Equal(2, fights.Count(encounter => encounter.Outcome == "Loadout changed"));
    }

    [Fact]
    public void Import_ZoneVariantsAndCharacters_StaySeparate()
    {
        var store = new NpcCompendium();
        var engine = Engine(store);
        Lines(
            engine,
            (0, "You have entered Sky Hard."),
            (1, "You slash A wraith for 100 points of damage."),
            (2, "You have entered Sky Easy."),
            (3, "You slash A wraith for 50 points of damage."));
        engine = Engine(store, "other-log", "Other");
        Lines(
            engine,
            (0, "You have entered Sky Hard."),
            (0, "[20 SHD/BRD/WIZ] Other (Human)"),
            (1, "You slash A wraith for 30 points of damage."));

        Assert.Equal(2, store.Search("wraith").Count);
        var hard = store.Get("A wraith", "Sky Hard")!;
        Assert.Equal(2, hard.Encounters.Count);
        Assert.Equal("Zoned", hard.Encounters.Single(encounter => encounter.Character == "Tester").Outcome);
        Assert.Equal("SHD/BRD/WIZ", hard.Encounters.Single(encounter => encounter.Character == "Other").Classes);
        Assert.Single(store.Get("A wraith", "Sky Easy")!.Encounters);
    }

    [Fact]
    public void Import_OlderSecondSource_FirstAndLastSeenUseAllSources()
    {
        var store = new NpcCompendium();
        var newer = Engine(store, "newer");
        var older = Engine(store, "older");
        Lines(
            newer,
            (0, "You have entered Sky Hard."),
            (100, "You slash A wraith for 10 points of damage."));
        Lines(
            older,
            (0, "You have entered Sky Hard."),
            (1, "You slash A wraith for 20 points of damage."));
        newer.Process(new(T0.AddSeconds(101), "A wraith scowls at you, ready to attack -- formidable. (Lvl: 48)"));
        older.Process(new(T0.AddSeconds(2), "A wraith judges you amiably -- easy. (Lvl: 30)"));

        var entry = store.Get("A wraith", "Sky Hard")!;
        Assert.Equal(T0.AddSeconds(1), entry.FirstSeen);
        Assert.Equal(T0.AddSeconds(101), entry.LastSeen);
        Assert.Equal(48, entry.LastConsider?.Level);
        Assert.Equal(2, entry.Considers);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Encounter_SnapshotsAndUnparsedLines_DoNotCloseDuringLongGaps(bool snapshot)
    {
        var store = new NpcCompendium();
        var engine = Engine(store);
        Lines(engine, (0, "You have entered Sky Hard."), (1, "You slash A wraith for 100 points of damage."));
        if (snapshot)
        {
            engine.Snapshot(T0.AddMinutes(10));
        }
        else
        {
            Lines(engine, (600, "Some unparsed message."));
        }

        Lines(engine, (601, "You slash A wraith for 50 points of damage."));
        var active = Assert.Single(store.Get("A wraith", "Sky Hard")!.Encounters);
        Assert.Equal("Active", active.Outcome);
        Assert.Equal(150, active.YourDamage);
        Lines(engine, (1200, "You have slain A wraith!"));
        var completed = Assert.Single(store.Get("A wraith", "Sky Hard")!.Encounters);
        Assert.Equal("NPC died", completed.Outcome);
        Assert.Equal(150, completed.YourDamage);
        Assert.Equal(1200, completed.Seconds);
    }

    [Fact]
    public void PersonalAverage_UsesWeightedEncounterSeconds_NotMeanOfRates()
    {
        var store = new NpcCompendium();
        var engine = Engine(store);
        Lines(
            engine,
            (0, "You have entered Sky Hard."),
            (1, "You slash A wraith for 100 points of damage."),
            (1, "You have slain A wraith!"),
            (10, "You slash A wraith for 100 points of damage."),
            (19, "A wraith has been slain by Helper!"),
            (30, "Helper slashes A wraith for 100 points of damage."),
            (39, "A wraith has been slain by Helper!"));
        var entry = store.Get("A wraith", "Sky Hard")!;
        Assert.Equal(3, entry.Encounters.Count);
        Assert.Equal(200d / 11, entry.YourAverageDps, 8);
        Assert.Equal(100d / 21, entry.OtherAverageDps, 8);
    }

    [Fact]
    public void BackgroundHistorySnapshots_DoNotSplitRecentFightsByWallClock()
    {
        var store = new NpcCompendium();
        var engine = Engine(store);
        Lines(engine, (0, "You have entered Sky Hard."), (1, "You slash A wraith for 100 points of damage."));
        engine.Snapshot(T0.AddMinutes(10));
        Lines(engine, (2, "You slash A wraith for 50 points of damage."), (3, "You have slain A wraith!"));

        Assert.Equal(150, Assert.Single(store.Get("A wraith", "Sky Hard")!.Encounters).YourDamage);
    }

    [Fact]
    public void IncomingDamage_OneWordNpcAndOwnDeath_AreRecorded()
    {
        var store = new NpcCompendium();
        var engine = Engine(store);
        Lines(
            engine,
            (0, "You have entered Sky Hard."),
            (1, "Cyndreela hits YOU for 20 points of damage."),
            (2, "You have been slain by Cyndreela!"));
        var encounter = Assert.Single(store.Get("Cyndreela", "Sky Hard")!.Encounters);
        Assert.Equal(20, encounter.IncomingDamage);
        Assert.Equal(0, encounter.YourDamage);
        Assert.Equal("You died", encounter.Outcome);
    }

    [Fact]
    public void OtherDamage_KnownSingleWordNpc_IsIncludedButUnknownPlayerTargetIsNot()
    {
        var store = new NpcCompendium();
        var engine = Engine(store);
        Lines(
            engine,
            (0, "You have entered Sky Hard."),
            (1, "You slash Cyndreela for 10 points of damage."),
            (2, "Helper slashes Cyndreela for 30 points of damage."),
            (3, "Helper slashes AnotherPlayer for 40 points of damage."));
        Assert.Equal(30, store.Get("Cyndreela", "Sky Hard")!.Encounters.Single().OtherDamage);
        Assert.Null(store.Get("AnotherPlayer", "Sky Hard"));
    }

    [Fact]
    public void SpellHistory_DotTicksDuplicateDirectHitsAndInterruptedCasts_DoNotInflateLandings()
    {
        var store = new NpcCompendium();
        var tracker = new NpcInteractionTracker(store, "log");
        tracker.OnEvent(new CastBeginEvent(T0, "Flame"), 1, "Tester", null);
        tracker.OnEvent(Hit(1, 100, DamageKind.DirectSpell, "Flame"), 2, "Tester", null);
        tracker.OnEvent(Hit(2, 100, DamageKind.DirectSpell, "Flame"), 3, "Tester", null);
        tracker.OnEvent(Hit(3, 10, DamageKind.DamageOverTime, "Flame"), 4, "Tester", null);
        tracker.OnEvent(new CastBeginEvent(T0.AddSeconds(4), "Flame"), 5, "Tester", null);
        tracker.OnEvent(new CastInterruptedEvent(T0.AddSeconds(5), "Flame"), 6, "Tester", null);
        tracker.OnEvent(Hit(6, 100, DamageKind.DirectSpell, "Flame"), 7, "Tester", null);
        tracker.OnEvent(new SpellResistedEvent(T0.AddSeconds(7), "A wraith", "Flame"), 8, "Tester", null);

        var spell = Assert.Single(
            store.Get("A wraith", "(unknown zone)")!.Encounters.Single().Spells);
        Assert.Equal(310, spell.Damage);
        Assert.Equal(1, spell.Landings);
        Assert.Equal(1, spell.Resists);
    }

    [Fact]
    public void SingleWordDeath_KeepsFinishingDamageButExcludesResistanceSample()
    {
        var store = new NpcCompendium();
        var resistances = new MobResistanceStore();
        var engine = new LogEngine(resistances: resistances, logIdentity: "log", npcs: store)
        {
            CharacterName = "Tester"
        };
        Lines(
            engine,
            (0, "You have entered Sky Hard."),
            (0, "[50 DRU/WIZ/ENC] Tester (Erudite)"),
            (1, "You begin casting Flame."),
            (2, "You hit Target for 25 points of fire damage by Flame."),
            (3, "Target died."));
        engine.Snapshot(T0.AddSeconds(10));

        var fight = Assert.Single(store.Get("Target", "Sky Hard")!.Encounters);
        Assert.Equal("NPC died", fight.Outcome);
        Assert.Equal(25, fight.YourDamage);
        Assert.Empty(resistances.Get("Sky Hard", "Target").Spells);
    }

    [Fact]
    public void SameNameTargets_AggregateWithoutPretendingThereAreDistinctInstances()
    {
        var store = new NpcCompendium();
        var engine = Engine(store);
        Lines(
            engine,
            (0, "You have entered Sky Hard."),
            (1, "You slash A wraith for 10 points of damage."),
            (2, "You slash A wraith for 20 points of damage."),
            (3, "You have slain A wraith!"),
            (4, "You slash A wraith for 30 points of damage."));

        var encounters = store.Get("A wraith", "Sky Hard")!.Encounters;
        Assert.Equal(2, encounters.Count);
        Assert.All(encounters, encounter => Assert.Equal(30, encounter.YourDamage));
        Assert.Single(encounters, encounter => encounter.Outcome == "Active");
        Assert.Single(encounters, encounter => encounter.Outcome == "NPC died");
    }

    [Fact]
    public void ResetSession_DoesNotEraseEncounters()
    {
        var store = new NpcCompendium();
        var engine = Engine(store);
        Lines(engine, (0, "You have entered Sky Hard."), (1, "You slash A wraith for 10 points of damage."));
        engine.ResetSession();
        Lines(engine, (2, "You slash A wraith for 20 points of damage."));

        Assert.Equal(30, Assert.Single(store.Get("A wraith", "Sky Hard")!.Encounters).YourDamage);
    }

    private static LogEngine Engine(NpcCompendium store, string source = "log", string character = "Tester")
    {
        return new LogEngine(logIdentity: source, npcs: store)
        {
            CharacterName = character
        };
    }

    private static DamageEvent Hit(
        int second,
        long amount,
        DamageKind kind = DamageKind.Melee,
        string? spell = null)
    {
        return new(T0.AddSeconds(second), Names.You, "A wraith", amount, kind, spell, null);
    }

    private static void Lines(LogEngine engine, params (int Second, string Message)[] lines)
    {
        foreach (var (second, message) in lines)
        {
            engine.Process(new(T0.AddSeconds(second), message));
        }
    }
}
