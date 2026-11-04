using System.Text.Json;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;
using EqLog.Infrastructure.Persistence;

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
        store = await TrackingTestPersistence.LoadNpcCompendiumAsync(_path);
        Import(store);
        await store.SaveAsync(_path);

        var npc = Assert.Single(store.Search("WRAITH"));
        Assert.Equal("A wraith", npc.Mob);
        Assert.Equal(1, npc.Considers);
        var saved = Assert.IsType<NpcEntry>(await TrackingTestPersistence.GetNpcAsync(store, "a WRAITH", "sky hard"));
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
    public async Task LoadoutHistory_RetainsClassesPhysicalSplitAndNamedEffectsAfterRestartAndReplay()
    {
        var store = new NpcCompendium();
        for (int replay = 0; replay < 2; replay++)
        {
            var engine = Engine(store);
            Lines(engine,
                (0, "You have entered Sky Hard."),
                (0, "[50 DRU/WIZ/ENC] Tester (Erudite)"),
                (1, "You kick A wraith for 42 points of damage."),
                (2, "You shoot A wraith for 688 points of damage. (Double Bow Shot)"),
                (3, "You hit A wraith for 100 points of magic damage by Whirlwind."),
                (4, "You have slain A wraith!"),
                (10, "[50 SHD/BRD/WIZ] Tester (Erudite)"),
                (11, "You hit A wraith for 75 points of magic damage by Soul Leech."),
                (12, "You have slain A wraith!"));
            await store.SaveAsync(_path);
            store = await TrackingTestPersistence.LoadNpcCompendiumAsync(_path);
            var npc = Assert.IsType<NpcEntry>(await TrackingTestPersistence.GetNpcAsync(store, "A wraith", "Sky Hard"));
            Assert.Equal(2, npc.Encounters.Count);
            var first = Assert.Single(npc.Encounters, e => e.Classes == "DRU/WIZ/ENC");
            Assert.Equal(50, first.Level);
            Assert.Equal(830, first.YourDamage);
            Assert.Equal(207.5, first.YourDps);
            Assert.Equal(688, Assert.Single(first.Spells, s => s.Spell == "Ranged").Damage);
            Assert.Equal(42, Assert.Single(first.Spells, s => s.Spell == "Melee").Damage);
            Assert.Equal(100, Assert.Single(first.Spells, s => s.Spell == "Whirlwind").Damage);
            var second = Assert.Single(npc.Encounters, e => e.Classes == "SHD/BRD/WIZ");
            Assert.Equal(37.5, second.YourDps);
            Assert.Equal(75, Assert.Single(second.Spells).Damage);
        }
    }

    [Fact]
    public async Task Save_ActiveEncounter_ReplayThenAppendContinuesItOnce()
    {
        var store = new NpcCompendium();
        var engine = Engine(store);
        Lines(engine, (0, "You have entered Sky Hard."), (0, "[50 DRU/WIZ/ENC] Tester (Erudite)"),
            (1, "You slash A wraith for 100 points of damage."));
        await store.SaveAsync(_path);
        store = await TrackingTestPersistence.LoadNpcCompendiumAsync(_path);
        engine = Engine(store);
        Lines(engine, (0, "You have entered Sky Hard."), (0, "[50 DRU/WIZ/ENC] Tester (Erudite)"),
            (1, "You slash A wraith for 100 points of damage."),
            (2, "You slash A wraith for 50 points of damage."),
            (3, "You have slain A wraith!"));

        var encounter = Assert.Single((await TrackingTestPersistence.GetNpcAsync(store, "A wraith", "Sky Hard"))!.Encounters);
        Assert.Equal(150, encounter.YourDamage);
        Assert.Equal(3, encounter.Seconds);
        Assert.Equal("NPC died", encounter.Outcome);
    }

    [Fact]
    public async Task Import_PartialReplayAndSave_DoesNotReplaceCompletedHistory()
    {
        var store = new NpcCompendium();
        Import(store);
        string id = (await TrackingTestPersistence.GetNpcAsync(store, "A wraith", "Sky Hard"))!.Encounters.Single().Id;
        var engine = Engine(store);
        Lines(engine, (0, "You have entered Sky Hard."), (0, "[50 DRU/WIZ/ENC] Tester (Erudite)"),
            (1, "You slash A wraith for 100 points of damage."));
        await store.SaveAsync(_path);
        var loadedStore = await TrackingTestPersistence.LoadNpcCompendiumAsync(_path);
        var fight = (await TrackingTestPersistence.GetNpcAsync(loadedStore, "A wraith", "Sky Hard"))!.Encounters.Single();
        Assert.Equal(id, fight.Id);
        Assert.Equal(150, fight.YourDamage);
        Assert.Equal("NPC died", fight.Outcome);
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

        store = await TrackingTestPersistence.LoadNpcCompendiumAsync(_path);
        engine = Engine(store);
        Lines(engine, (0, "You have entered Sky Hard."),
            (1, "You slash A wraith for 100 points of damage."),
            (600, "You begin casting Flame."),
            (1200, "You slash A wraith for 50 points of damage."),
            (1800, "You have slain A wraith!"));

        var encounter = Assert.Single((await TrackingTestPersistence.GetNpcAsync(store, "A wraith", "Sky Hard"))!.Encounters);
        Assert.Equal("NPC died", encounter.Outcome);
        Assert.Equal(150, encounter.YourDamage);
        Assert.Equal(1800, encounter.Seconds);
        Assert.False(store.HasActiveEncounter("A wraith", "Sky Hard"));
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
        var loadedStore = await TrackingTestPersistence.LoadNpcCompendiumAsync(_path);
        Assert.Equal(100, (await TrackingTestPersistence.GetNpcAsync(loadedStore, "A wraith", "Sky Hard"))!.Encounters.Count);
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
        await Assert.ThrowsAsync<JsonException>(() => TrackingTestPersistence.LoadNpcCompendiumAsync(_path));
        Assert.Equal(json, File.ReadAllText(index));
    }

    [Fact]
    public async Task Load_CorruptLegacyFile_ThrowsAndLeavesItInPlace()
    {
        string legacy = _path + ".json";
        File.WriteAllText(legacy, "{\"Entries\":null}");
        await Assert.ThrowsAsync<JsonException>(() => TrackingTestPersistence.LoadNpcCompendiumAsync(_path, legacy));
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
            Id = $"LOG\n{T0.Ticks}\n7",
            Source = "LOG",
            Character = "Tester",
            Start = T0,
            LastActivity = T0.AddSeconds(3),
            Outcome = "NPC died",
            YourDamage = 90,
            Spells = { ["FLAME"] = new() { Spell = "Flame", Damage = 90, Landings = 1 } },
        });
        entry.Encounters.Add(new()
        {
            Id = "custom-id",
            Source = "LOG",
            Character = "Tester",
            Start = T0.AddSeconds(4),
            LastActivity = T0.AddSeconds(5),
            Outcome = "Active",
            YourDamage = 10,
        });
        data.Entries["SKY HARD\nA WRAITH"] = entry;
        File.WriteAllText(legacy, JsonSerializer.Serialize(data));

        var store = await TrackingTestPersistence.LoadNpcCompendiumAsync(_path, legacy);

        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(legacy + ".migrated"));
        var history = Assert.Single(Directory.GetFiles(Path.Combine(_path, NpcCompendium.HistoryFolderName)));
        Assert.Single(File.ReadAllLines(history));
        Assert.DoesNotContain("custom-id", File.ReadAllText(history));
        var migratedStore = await TrackingTestPersistence.LoadNpcCompendiumAsync(_path, legacy);
        var reloaded = (await TrackingTestPersistence.GetNpcAsync(migratedStore, "A wraith", "Sky Hard"))!;
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
        var loadedStore = await TrackingTestPersistence.LoadNpcCompendiumAsync(_path);
        Assert.Equal(2, (await TrackingTestPersistence.GetNpcAsync(loadedStore, "A wraith", "Sky Hard"))!.Encounters.Count);
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

        store = await TrackingTestPersistence.LoadNpcCompendiumAsync(_path);
        Assert.Single((await TrackingTestPersistence.GetNpcAsync(store, "A wraith", "Sky Hard"))!.Encounters);
        Lines(Engine(store), (19, "You have entered Sky Hard."), (20, "You slash A wraith for 5 points of damage."),
            (21, "You have slain A wraith!"));
        await store.SaveAsync(_path);
        var loadedStore = await TrackingTestPersistence.LoadNpcCompendiumAsync(_path);
        Assert.Equal(2, (await TrackingTestPersistence.GetNpcAsync(loadedStore, "A wraith", "Sky Hard"))!.Encounters.Count);
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
