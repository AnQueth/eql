using System.Text.Json;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;

namespace EqLog.Core.Tests;

public sealed class NpcCompendiumTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 21, 19, 0, 0);
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"eq-npc-{Guid.NewGuid():N}");

    [Fact]
    public async Task Import_ReplayAfterSave_DoesNotDuplicateEncountersOrSpellOutcomes()
    {
        var store = new NpcCompendium();
        Import(store);
        await store.SaveAsync(_path);
        store = await NpcCompendium.LoadAsync(_path);
        Import(store);
        await store.SaveAsync(_path);

        var npc = Assert.Single(store.Search("WRAITH"));
        Assert.Equal("A wraith", npc.Mob);
        Assert.Equal(1, npc.Considers);
        var saved = Assert.IsType<NpcEntry>(await store.GetAsync("a WRAITH", "sky hard"));
        var fight = Assert.Single(saved.Encounters);
        Assert.Equal(150, fight.YourDamage);
        Assert.Equal(70, fight.OtherDamage);
        Assert.Equal(20, fight.IncomingDamage);
        Assert.Equal(4, fight.Seconds);
        Assert.Equal(37.5, fight.YourDps);
        Assert.Equal("NPC died", fight.Outcome);
        Assert.Equal("Tester", fight.Character);
        Assert.Equal("DRU/WIZ/ENC", fight.Classes);
        Assert.Equal(50, fight.Level);
        var spell = Assert.Single(fight.Spells);
        Assert.Equal(150, spell.Damage);
        Assert.Equal(1, spell.Landings);
        Assert.Equal(1, spell.Resists);
        Assert.Equal(1, saved.Considers);
        Assert.Equal("scowls at you, ready to attack", saved.LastConsider?.Faction);
        Assert.Equal("This creature would take considerable effort to defeat!", saved.LastAssessment);
    }

    [Fact]
    public async Task Save_ActiveEncounter_ReplayThenAppendContinuesItOnce()
    {
        var store = new NpcCompendium();
        var engine = Engine(store);
        Lines(engine, (0, "You have entered Sky Hard."), (0, "[50 DRU/WIZ/ENC] Tester (Erudite)"),
            (1, "You slash A wraith for 100 points of damage."));
        await store.SaveAsync(_path);
        store = await NpcCompendium.LoadAsync(_path);
        engine = Engine(store);
        Lines(engine, (0, "You have entered Sky Hard."), (0, "[50 DRU/WIZ/ENC] Tester (Erudite)"),
            (1, "You slash A wraith for 100 points of damage."),
            (2, "You slash A wraith for 50 points of damage."),
            (3, "You have slain A wraith!"));

        var encounter = Assert.Single((await store.GetAsync("A wraith", "Sky Hard"))!.Encounters);
        Assert.Equal(150, encounter.YourDamage);
        Assert.Equal(3, encounter.Seconds);
        Assert.Equal("NPC died", encounter.Outcome);
    }

    [Fact]
    public async Task Import_PartialReplayAndSave_DoesNotReplaceCompletedHistory()
    {
        var store = new NpcCompendium();
        Import(store);
        string id = (await store.GetAsync("A wraith", "Sky Hard"))!.Encounters.Single().Id;
        var engine = Engine(store);
        Lines(engine, (0, "You have entered Sky Hard."), (0, "[50 DRU/WIZ/ENC] Tester (Erudite)"),
            (1, "You slash A wraith for 100 points of damage."));
        await store.SaveAsync(_path);
        var fight = (await (await NpcCompendium.LoadAsync(_path)).GetAsync("A wraith", "Sky Hard"))!.Encounters.Single();
        Assert.Equal(id, fight.Id);
        Assert.Equal(150, fight.YourDamage);
        Assert.Equal("NPC died", fight.Outcome);
    }

    [Fact]
    public async Task Import_ZoneVariantsAndCharacters_StaySeparate()
    {
        var store = new NpcCompendium();
        var engine = Engine(store);
        Lines(engine, (0, "You have entered Sky Hard."),
            (1, "You slash A wraith for 100 points of damage."),
            (2, "You have entered Sky Easy."),
            (3, "You slash A wraith for 50 points of damage."));
        engine = Engine(store, "other-log", "Other");
        Lines(engine, (0, "You have entered Sky Hard."), (0, "[20 SHD/BRD/WIZ] Other (Human)"),
            (1, "You slash A wraith for 30 points of damage."));
        Assert.Equal(2, store.Search("wraith").Count);
        var hard = (await store.GetAsync("A wraith", "Sky Hard"))!;
        Assert.Equal(2, hard.Encounters.Count);
        Assert.Equal("Zoned", hard.Encounters.Single(e => e.Character == "Tester").Outcome);
        Assert.Equal("SHD/BRD/WIZ", hard.Encounters.Single(e => e.Character == "Other").Classes);
        Assert.Single((await store.GetAsync("A wraith", "Sky Easy"))!.Encounters);
    }

    [Fact]
    public async Task Import_OlderSecondSource_FirstAndLastSeenUseAllSources()
    {
        var store = new NpcCompendium();
        var newer = Engine(store, "newer");
        var older = Engine(store, "older");
        Lines(newer, (0, "You have entered Sky Hard."), (100, "You slash A wraith for 10 points of damage."));
        Lines(older, (0, "You have entered Sky Hard."), (1, "You slash A wraith for 20 points of damage."));
        newer.Process(new(T0.AddSeconds(101), "A wraith scowls at you, ready to attack -- formidable. (Lvl: 48)"));
        older.Process(new(T0.AddSeconds(2), "A wraith judges you amiably -- easy. (Lvl: 30)"));
        var entry = (await store.GetAsync("A wraith", "Sky Hard"))!;
        Assert.Equal(T0.AddSeconds(1), entry.FirstSeen);
        Assert.Equal(T0.AddSeconds(101), entry.LastSeen);
        Assert.Equal(48, entry.LastConsider?.Level);
        Assert.Equal(2, entry.Considers);
    }

    [Fact]
    public async Task Context_ChangedWhoAndLevel_SplitButRepeatedWhoDoesNot()
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
        var fights = (await store.GetAsync("A wraith", "(unknown zone)"))!.Encounters;
        Assert.Equal(3, fights.Count);
        Assert.Equal(200, fights.Single(e => e.Classes == who.Classes).YourDamage);
        Assert.Equal(25, fights.Single(e => e.Level == 51).YourDamage);
        Assert.Equal(2, fights.Count(e => e.Outcome == "Loadout changed"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Encounter_SnapshotsAndUnparsedLines_DoNotCloseDuringLongGaps(bool snapshot)
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
        var active = Assert.Single((await store.GetAsync("A wraith", "Sky Hard"))!.Encounters);
        Assert.Equal("Active", active.Outcome);
        Assert.Equal(150, active.YourDamage);
        Lines(engine, (1200, "You have slain A wraith!"));
        var completed = Assert.Single((await store.GetAsync("A wraith", "Sky Hard"))!.Encounters);
        Assert.Equal("NPC died", completed.Outcome);
        Assert.Equal(150, completed.YourDamage);
        Assert.Equal(1200, completed.Seconds);
    }

    [Fact]
    public async Task Save_LongGapThenParsedEventAndRestart_ContinuesUntilExplicitDeath()
    {
        var store = new NpcCompendium();
        var engine = Engine(store);
        Lines(engine, (0, "You have entered Sky Hard."),
            (1, "You slash A wraith for 100 points of damage."),
            (600, "You begin casting Flame."));
        Assert.True(store.HasActiveEncounter("A wraith", "Sky Hard"));
        await store.SaveAsync(_path);

        store = await NpcCompendium.LoadAsync(_path);
        engine = Engine(store);
        Lines(engine, (0, "You have entered Sky Hard."),
            (1, "You slash A wraith for 100 points of damage."),
            (600, "You begin casting Flame."),
            (1200, "You slash A wraith for 50 points of damage."),
            (1800, "You have slain A wraith!"));

        var encounter = Assert.Single((await store.GetAsync("A wraith", "Sky Hard"))!.Encounters);
        Assert.Equal("NPC died", encounter.Outcome);
        Assert.Equal(150, encounter.YourDamage);
        Assert.Equal(1800, encounter.Seconds);
        Assert.False(store.HasActiveEncounter("A wraith", "Sky Hard"));
    }

    [Fact]
    public async Task Import_BackgroundHistorySnapshots_DoNotSplitRecentFightsByWallClock()
    {
        var store = new NpcCompendium();
        var engine = Engine(store);
        Lines(engine, (0, "You have entered Sky Hard."), (1, "You slash A wraith for 100 points of damage."));
        engine.Snapshot(T0.AddMinutes(10));
        Lines(engine, (2, "You slash A wraith for 50 points of damage."), (3, "You have slain A wraith!"));
        Assert.Equal(150, Assert.Single((await store.GetAsync("A wraith", "Sky Hard"))!.Encounters).YourDamage);
    }

    [Fact]
    public async Task PersonalAverage_UsesWeightedEncounterSeconds_NotMeanOfRates()
    {
        var store = new NpcCompendium();
        var engine = Engine(store);
        Lines(engine, (0, "You have entered Sky Hard."),
            (1, "You slash A wraith for 100 points of damage."),
            (1, "You have slain A wraith!"),
            (10, "You slash A wraith for 100 points of damage."),
            (19, "A wraith has been slain by Helper!"),
            (30, "Helper slashes A wraith for 100 points of damage."),
            (39, "A wraith has been slain by Helper!"));
        var entry = (await store.GetAsync("A wraith", "Sky Hard"))!;
        Assert.Equal(3, entry.Encounters.Count);
        Assert.Equal(200d / 11, entry.YourAverageDps, 8);
        Assert.Equal(100d / 21, entry.OtherAverageDps, 8);
    }

    [Fact]
    public async Task IncomingDamage_OneWordNpcAndOwnDeath_AreRecorded()
    {
        var store = new NpcCompendium();
        var engine = Engine(store);
        Lines(engine, (0, "You have entered Sky Hard."), (1, "Cyndreela hits YOU for 20 points of damage."),
            (2, "You have been slain by Cyndreela!"));
        var encounter = Assert.Single((await store.GetAsync("Cyndreela", "Sky Hard"))!.Encounters);
        Assert.Equal(20, encounter.IncomingDamage);
        Assert.Equal(0, encounter.YourDamage);
        Assert.Equal("You died", encounter.Outcome);
    }

    [Fact]
    public async Task OtherDamage_KnownSingleWordNpc_IsIncludedButUnknownPlayerTargetIsNot()
    {
        var store = new NpcCompendium();
        var engine = Engine(store);
        Lines(engine, (0, "You have entered Sky Hard."), (1, "You slash Cyndreela for 10 points of damage."),
            (2, "Helper slashes Cyndreela for 30 points of damage."),
            (3, "Helper slashes AnotherPlayer for 40 points of damage."));
        Assert.Equal(30, (await store.GetAsync("Cyndreela", "Sky Hard"))!.Encounters.Single().OtherDamage);
        Assert.Null(await store.GetAsync("AnotherPlayer", "Sky Hard"));
    }

    [Fact]
    public async Task SpellHistory_DotTicksDuplicateDirectHitsAndInterruptedCasts_DoNotInflateLandings()
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
        var spell = Assert.Single((await store.GetAsync("A wraith", "(unknown zone)"))!.Encounters.Single().Spells);
        Assert.Equal(310, spell.Damage);
        Assert.Equal(1, spell.Landings);
        Assert.Equal(1, spell.Resists);
    }

    [Fact]
    public async Task SingleWordDeath_KeepsFinishingDamageButExcludesResistanceSample()
    {
        var store = new NpcCompendium();
        var resistances = new MobResistanceStore();
        var engine = new LogEngine(resistances: resistances, logIdentity: "log", npcs: store)
        {
            CharacterName = "Tester",
        };
        Lines(engine, (0, "You have entered Sky Hard."), (0, "[50 DRU/WIZ/ENC] Tester (Erudite)"),
            (1, "You begin casting Flame."),
            (2, "You hit Target for 25 points of fire damage by Flame."),
            (3, "Target died."));
        engine.Snapshot(T0.AddSeconds(10));
        var fight = Assert.Single((await store.GetAsync("Target", "Sky Hard"))!.Encounters);
        Assert.Equal("NPC died", fight.Outcome);
        Assert.Equal(25, fight.YourDamage);
        Assert.Empty(resistances.Get("Sky Hard", "Target").Spells);
    }

    [Fact]
    public void PerSpellAssessment_ReusesBaselineAndKeepsExactRanksSeparate()
    {
        var store = new MobResistanceStore();
        store.SetSpell(new("Flame X", ResistanceType.Fire, null, T0, true));
        store.SetSpell(new("Flame IX", ResistanceType.Fire, null, T0, true));
        for (int i = 1; i <= 3; i++)
        {
            store.Record("log", i, "Sky", "Target", "Flame X", false, T0, "Tester", 1000);
        }
        store.Record("log", 4, "Sky", "Target", "Flame X", false, T0, "Tester", 400);
        store.Record("log", 5, "Sky", "Target", "Flame X", true, T0);
        store.Record("log", 6, "Sky", "Target", "Flame IX", true, T0);
        var exact = store.Get("Sky", "Target", "flame x");
        var fire = exact.Observations.Single(o => o.Type == ResistanceType.Fire);
        Assert.Equal(3, fire.Landed);
        Assert.Equal(2, fire.Resisted);
        Assert.Equal(1, fire.EstimatedPartial);
        Assert.Equal("Partially Resistant \u2014 40% (2/5)", fire.DisplayText);
        Assert.Single(exact.Spells);
        Assert.Equal(3, store.Get("Sky", "Target").Observations.Single(o => o.Type == ResistanceType.Fire).Resisted);
    }

    [Fact]
    public void PerSpellUnknownMetadata_RawResistsAndUnassessedHitsRemainAvailable()
    {
        var store = new MobResistanceStore();
        store.Record("log", 1, "Sky", "Target", "Unknown X", true, T0);
        store.Record("log", 2, "Sky", "Target", "Unknown X", false, T0, "Tester", 400);
        var counts = store.GetRawSpellCounts("sky", "target", "unknown x");
        Assert.Equal(1, counts.FullResists);
        Assert.Equal(1, counts.DamageHits);
        Assert.Equal(0, counts.LegacyLandings);
        Assert.Equal(2, store.Get("Sky", "Target", "Unknown X").Unclassified);
    }

    [Fact]
    public async Task Import_OverSixtyFights_RetainsEveryEncounterAndNoRawChat()
    {
        var store = new NpcCompendium();
        var engine = Engine(store);
        Lines(engine, (0, "You have entered Sky Hard."));
        for (int i = 1; i <= 100; i++)
        {
            Lines(engine, (i * 3, "You slash A wraith for 10 points of damage."),
                (i * 3 + 1, "You have slain A wraith!"));
        }
        Lines(engine, (310, "You say, 'Private chat is not encounter history.'"));
        await store.SaveAsync(_path);
        Assert.Equal(100, (await (await NpcCompendium.LoadAsync(_path)).GetAsync("A wraith", "Sky Hard"))!.Encounters.Count);
        Assert.All(Directory.EnumerateFiles(_path, "*", SearchOption.AllDirectories),
            file => Assert.DoesNotContain("Private chat", File.ReadAllText(file)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"Entries\":null}")]
    [InlineData("{\"Watermarks\":{\"LOG\":null}}")]
    [InlineData("{\"Entries\":{\"BAD\":null}}")]
    [InlineData("{\"Entries\":{\"BAD\":{\"Mob\":\"Target\",\"Encounters\":[null]}}}")]
    [InlineData("{\"Entries\":{\"BAD\":{\"Mob\":\"Target\",\"Encounters\":[{\"Spells\":null}]}}}")]
    [InlineData("{\"Sources\":null}")]
    [InlineData("{\"Sources\":[null]}")]
    public async Task Load_CorruptData_ThrowsWithoutOverwriting(string json)
    {
        Directory.CreateDirectory(_path);
        string index = Path.Combine(_path, NpcCompendium.IndexFileName);
        File.WriteAllText(index, json);
        await Assert.ThrowsAsync<JsonException>(() => NpcCompendium.LoadAsync(_path));
        Assert.Equal(json, File.ReadAllText(index));
    }

    [Fact]
    public async Task Load_CorruptLegacyFile_ThrowsAndLeavesItInPlace()
    {
        string legacy = _path + ".json";
        File.WriteAllText(legacy, "{\"Entries\":null}");
        await Assert.ThrowsAsync<JsonException>(() => NpcCompendium.LoadAsync(_path, legacy));
        Assert.True(File.Exists(legacy));
        Assert.False(File.Exists(Path.Combine(_path, NpcCompendium.IndexFileName)));
    }

    [Fact]
    public async Task Load_LegacySingleFile_MigratesToIndexAndHistoryFiles()
    {
        string legacy = _path + ".json";
        var data = new NpcCompendium.Data();
        var entry = new NpcCompendium.EntryData { Mob = "A wraith", Zone = "Sky Hard", FirstSeen = T0, LastSeen = T0.AddSeconds(5), Considers = 2 };
        entry.Encounters.Add(new()
        {
            Id = $"LOG\n{T0.Ticks}\n7", Source = "LOG", Character = "Tester", Start = T0, LastActivity = T0.AddSeconds(3),
            Outcome = "NPC died", YourDamage = 90, Spells = { ["FLAME"] = new() { Spell = "Flame", Damage = 90, Landings = 1 } },
        });
        entry.Encounters.Add(new()
        {
            Id = "custom-id", Source = "LOG", Character = "Tester", Start = T0.AddSeconds(4), LastActivity = T0.AddSeconds(5),
            Outcome = "Active", YourDamage = 10,
        });
        data.Entries["SKY HARD\nA WRAITH"] = entry;
        File.WriteAllText(legacy, JsonSerializer.Serialize(data));

        var store = await NpcCompendium.LoadAsync(_path, legacy);

        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(legacy + ".migrated"));
        var history = Assert.Single(Directory.GetFiles(Path.Combine(_path, NpcCompendium.HistoryFolderName)));
        Assert.Single(File.ReadAllLines(history));
        Assert.DoesNotContain("custom-id", File.ReadAllText(history));
        var reloaded = (await (await NpcCompendium.LoadAsync(_path, legacy)).GetAsync("A wraith", "Sky Hard"))!;
        Assert.Equal(2, reloaded.Encounters.Count);
        var died = reloaded.Encounters.Single(e => e.Outcome == "NPC died");
        Assert.Equal($"LOG\n{T0.Ticks}\n7", died.Id);
        Assert.Equal(90, Assert.Single(died.Spells).Damage);
        Assert.Equal("custom-id", reloaded.Encounters.Single(e => e.Outcome == "Active").Id);
        Assert.Equal(2, store.Search("wraith").Single().Considers);
    }

    [Fact]
    public async Task Save_FinishedEncounters_AppendToHistoryAndLeaveIndexSmall()
    {
        var store = new NpcCompendium();
        var engine = Engine(store);
        Lines(engine, (0, "You have entered Sky Hard."), (1, "You slash A wraith for 10 points of damage."),
            (2, "You have slain A wraith!"));
        await store.SaveAsync(_path);
        Lines(engine, (3, "You slash A wraith for 20 points of damage."), (4, "You have slain A wraith!"));
        await store.SaveAsync(_path);

        string history = Assert.Single(Directory.GetFiles(Path.Combine(_path, NpcCompendium.HistoryFolderName)));
        Assert.Equal(2, File.ReadAllLines(history).Length);
        Assert.DoesNotContain("YourDamage", File.ReadAllText(Path.Combine(_path, NpcCompendium.IndexFileName)));
        Assert.DoesNotContain("\"Encounters\":[]", File.ReadAllText(Path.Combine(_path, NpcCompendium.IndexFileName)));
        Assert.DoesNotContain(":null", File.ReadAllText(Path.Combine(_path, NpcCompendium.IndexFileName)));
        Assert.Equal(2, (await (await NpcCompendium.LoadAsync(_path)).GetAsync("A wraith", "Sky Hard"))!.Encounters.Count);
    }

    [Fact]
    public async Task Load_TruncatedHistoryLineAndDuplicateReplay_AreTolerated()
    {
        var store = new NpcCompendium();
        Import(store);
        await store.SaveAsync(_path);
        string history = Assert.Single(Directory.GetFiles(Path.Combine(_path, NpcCompendium.HistoryFolderName)));
        string line = File.ReadAllLines(history).Single();
        File.AppendAllText(history, line + "\n" + line[..(line.Length / 2)]);

        store = await NpcCompendium.LoadAsync(_path);
        Assert.Single((await store.GetAsync("A wraith", "Sky Hard"))!.Encounters);
        Lines(Engine(store), (19, "You have entered Sky Hard."), (20, "You slash A wraith for 5 points of damage."),
            (21, "You have slain A wraith!"));
        await store.SaveAsync(_path);
        Assert.Equal(2, (await (await NpcCompendium.LoadAsync(_path)).GetAsync("A wraith", "Sky Hard"))!.Encounters.Count);
    }

    [Fact]
    public async Task SameNameTargets_AggregateWithoutPretendingThereAreDistinctInstances()
    {
        var store = new NpcCompendium();
        var engine = Engine(store);
        Lines(engine, (0, "You have entered Sky Hard."), (1, "You slash A wraith for 10 points of damage."),
            (2, "You slash A wraith for 20 points of damage."),
            (3, "You have slain A wraith!"), (4, "You slash A wraith for 30 points of damage."));
        var encounters = (await store.GetAsync("A wraith", "Sky Hard"))!.Encounters;
        Assert.Equal(2, encounters.Count);
        Assert.All(encounters, e => Assert.Equal(30, e.YourDamage));
        Assert.Single(encounters, e => e.Outcome == "Active");
        Assert.Single(encounters, e => e.Outcome == "NPC died");
    }

    [Fact]
    public async Task ResetSession_DoesNotErasePersistentEncounters()
    {
        var store = new NpcCompendium();
        var engine = Engine(store);
        Lines(engine, (0, "You have entered Sky Hard."), (1, "You slash A wraith for 10 points of damage."));
        engine.ResetSession();
        Lines(engine, (2, "You slash A wraith for 20 points of damage."));
        Assert.Equal(30, Assert.Single((await store.GetAsync("A wraith", "Sky Hard"))!.Encounters).YourDamage);
    }

    private static void Import(NpcCompendium store)
    {
        var engine = Engine(store);
        Lines(engine, (0, "You have entered Sky Hard."), (0, "[50 DRU/WIZ/ENC] Tester (Erudite)"),
            (0, "A wraith scowls at you, ready to attack -- it appears to be quite formidable. (Lvl: 48)"),
            (0, "This creature would take considerable effort to defeat!"),
            (1, "You begin casting Flame."),
            (1, "You hit A wraith for 100 points of fire damage by Flame."),
            (2, "A wraith has taken 50 damage from your Flame."),
            (2, "Helper slashes A wraith for 70 points of damage."),
            (2, "A wraith hits YOU for 20 points of damage."),
            (3, "You begin casting Flame."), (3, "A wraith resisted your Flame!"),
            (4, "You have slain A wraith!"));
        engine.Snapshot(T0.AddSeconds(10));
    }

    private static LogEngine Engine(NpcCompendium store, string source = "log", string character = "Tester")
    {
        return new LogEngine(logIdentity: source, npcs: store) { CharacterName = character };
    }

    private static DamageEvent Hit(int second, long amount, DamageKind kind = DamageKind.Melee, string? spell = null)
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

    public void Dispose()
    {
        if (Directory.Exists(_path))
        {
            Directory.Delete(_path, recursive: true);
        }
        File.Delete(_path + ".json");
        File.Delete(_path + ".json.migrated");
    }
}
