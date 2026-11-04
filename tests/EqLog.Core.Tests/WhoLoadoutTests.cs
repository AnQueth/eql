using EqLog.Core.Parsing;

namespace EqLog.Core.Tests;

public class WhoLoadoutTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 19, 0, 0);

    [Theory]
    [InlineData("[50 DRU/WIZ/ENC] Quethrosar (Erudite) <Mudder Gukkers> ZONE: The Plane of Sky 5398 (airplane)  ", 50, "DRU/WIZ/ENC")]
    [InlineData("[12 SHD/BRD/WIZ] Quethrosar (Half Dark Elf) <Mudder Gukkers> ZONE: Befallen (befallen)", 12, "SHD/BRD/WIZ")]
    [InlineData("[50 shd/wiz/enc] quethrosar (Erudite)", 50, "SHD/WIZ/ENC")]
    [InlineData("[60 NEC] Quethrosar", 60, "NEC")]
    public void Parser_OwnWhoEntry_ExtractsClassOrderAndLevel(string line, int level, string classes)
    {
        var parser = new LogEventParser("Quethrosar");
        var who = Assert.IsType<WhoLoadoutEvent>(parser.Parse(T0, line));
        Assert.Equal(level, who.Level);
        Assert.Equal(classes, who.Classes);
        Assert.Equal("Quethrosar", who.Character);
    }

    [Theory]
    [InlineData("[50 DRU/WIZ/ENC] Another (Erudite)")]
    [InlineData("[50 DRU/WIZ/ENC] Quethrosaralt (Erudite)")]
    [InlineData("[ANONYMOUS] Quethrosar (Erudite)")]
    [InlineData("[50 UNKNOWN] Quethrosar (Erudite)")]
    [InlineData("Bob says, '[50 DRU/WIZ/ENC] Quethrosar (Erudite)'")]
    public void Parser_OtherPlayersAnonymousAndChat_DoNotUpdateLoadout(string line)
    {
        Assert.IsNotType<WhoLoadoutEvent>(new LogEventParser("Quethrosar").Parse(T0, line));
    }

    [Fact]
    public void Engine_Loadout_ChangesOnOwnWhoAndClearsOnLogin()
    {
        var engine = new LogEngine { CharacterName = "Quethrosar" };
        engine.Process(new(T0, "[50 DRU/WIZ/ENC] Quethrosar (Erudite)"));
        Assert.Equal("DRU/WIZ/ENC", engine.Snapshot(T0).CurrentLoadout?.Classes);
        engine.Process(new(T0.AddSeconds(1), "[12 SHD/BRD/WIZ] Other (Human)"));
        Assert.Equal(50, engine.Snapshot(T0).CurrentLoadout?.Level);
        engine.ResetSession();
        Assert.Equal("DRU/WIZ/ENC", engine.Snapshot(T0).CurrentLoadout?.Classes);
        engine.Process(new(T0.AddSeconds(2), "[12 SHD/BRD/WIZ] Quethrosar (Half Dark Elf)"));
        Assert.Equal("SHD/BRD/WIZ", engine.Snapshot(T0).CurrentLoadout?.Classes);
        Assert.Equal(12, engine.Snapshot(T0).CurrentLoadout?.Level);
        engine.Process(new(T0.AddSeconds(3), "Welcome to EverQuest!"));
        Assert.Null(engine.Snapshot(T0).CurrentLoadout);
        engine.Process(new(T0.AddSeconds(4), "[50 DRU/WIZ/ENC] Quethrosar (Erudite)"));
        engine.CharacterName = "Other";
        Assert.Null(engine.Snapshot(T0).CurrentLoadout);
    }

    [Fact]
    public void Engine_LoadoutsShareDamageBaselinesButLevelsRemainSeparate()
    {
        var store = new MobResistanceStore();
        store.SetSpell(new("Conflagration X", ResistanceType.Fire, "Fire", T0, true));
        var engine = new LogEngine(resistances: store, logIdentity: "loadout-log") { CharacterName = "Quethrosar" };
        engine.Process(new(T0, "You have entered Sky."));
        int seconds = 0;
        foreach (var (classes, level, damage) in new[]
        {
            ("DRU/WIZ/ENC", 50, 1000),
            ("SHD/BRD/WIZ", 50, 500),
            ("SHD/BRD/WIZ", 12, 100),
        })
        {
            engine.Process(new(T0.AddSeconds(seconds++), $"[{level} {classes}] Quethrosar (Erudite)"));
            for (int i = 0; i < 3; i++)
            {
                engine.Process(new(T0.AddSeconds(seconds++), "You begin casting Conflagration X."));
                engine.Process(new(T0.AddSeconds(seconds++), $"You hit A rat for {damage} points of fire damage by Conflagration X."));
                seconds += 4;
            }
        }
        engine.Snapshot(T0.AddSeconds(seconds + 5));
        var fire = store.Get("Sky", "A rat").Observations.Single(o => o.Type == ResistanceType.Fire);
        Assert.Equal(9, fire.Landed);
        Assert.Equal(3, fire.EstimatedPartial);
        Assert.Equal(0, fire.UnassessedDamage);
    }

    [Fact]
    public void Engine_BeforeWho_CountsLandingsAndLearnsDamage()
    {
        var store = new MobResistanceStore();
        store.SetSpell(new("Conflagration X", ResistanceType.Fire, "Fire", T0, true));
        var engine = new LogEngine(resistances: store, logIdentity: "log") { CharacterName = "Quethrosar" };
        engine.Process(new(T0, "You begin casting Conflagration X."));
        engine.Process(new(T0.AddSeconds(1), "You hit A rat for 100 points of fire damage by Conflagration X."));
        engine.Snapshot(T0.AddSeconds(5));
        var fire = store.Get("(unknown zone)", "A rat").Observations.Single(o => o.Type == ResistanceType.Fire);
        Assert.Equal(1, fire.Landed);
        Assert.Equal(0, fire.EstimatedPartial);
        Assert.Equal(1, fire.UnassessedDamage);
        Assert.Equal(new RawSpellCounts(0, 0, 1), store.GetRawSpellCounts("(unknown zone)", "A rat", "Conflagration X"));
    }

    [Fact]
    public void Engine_UnknownLoadout_ReplayRecoversRendLandingsWithoutDuplicatingSavedOutcomes()
    {
        const string zone = "The Plane of Hate - Solo 4 (Refined)";
        const string mob = "Innoruuk`s Chosen";
        var store = new MobResistanceStore();
        store.SetSpell(new("Rend X", ResistanceType.Magic, "Magic", T0, true));
        // A later saved resist used to prevent replay from recovering these skipped hits.
        store.Record("log", 20, zone, mob, "Rend X", true, T0.AddSeconds(20));
        for (int replay = 0; replay < 2; replay++)
        {
            var engine = new LogEngine(resistances: store, logIdentity: "log") { CharacterName = "Quethrosar" };
            engine.Process(new(T0, $"You have entered {zone}."));
            engine.Process(new(T0.AddSeconds(1), "You begin casting Rend X."));
            engine.Process(new(T0.AddSeconds(3), $"You hit {mob} for 1753 points of magic damage by Rend X."));
            engine.Process(new(T0.AddSeconds(3), $"You hit {mob} for 1753 points of magic damage by Rend X."));
            engine.Process(new(T0.AddSeconds(8), "[50 DRU/WIZ/ENC] Quethrosar (Erudite)"));
            engine.Process(new(T0.AddSeconds(9), "You begin casting Rend X."));
            engine.Process(new(T0.AddSeconds(11), $"You hit {mob} for 1500 points of magic damage by Rend X."));
            engine.Process(new(T0.AddSeconds(21), "You begin casting Rend X."));
            engine.Process(new(T0.AddSeconds(23), $"You hit {mob} for 1600 points of magic damage by Rend X."));
            engine.Snapshot(T0.AddSeconds(30));

            var magic = store.Get(zone, mob).Observations.Single(o => o.Type == ResistanceType.Magic);
            Assert.Equal(2, magic.Landed);
            Assert.Equal(1, magic.Resisted);
            Assert.Equal(3m, magic.AssessedOutcomes);
            Assert.Equal(new RawSpellCounts(1, 0, 2), store.GetRawSpellCounts(zone, mob, "Rend X"));

            if (replay == 0)
            {
                var snapshot = Assert.IsType<MobResistancePersistenceSnapshot>(store.CapturePersistenceSnapshot());
                string json = System.Text.Json.JsonSerializer.Serialize(snapshot.Data);
                var data = Assert.IsType<MobResistanceStore.Data>(
                    System.Text.Json.JsonSerializer.Deserialize<MobResistanceStore.Data>(json));
                var metadata = Assert.IsType<SpellResistanceInfo>(store.GetSpell("Rend X"));
                var reloaded = new MobResistanceStore();
                reloaded.ImportData(data, [metadata]);
                store = reloaded;
            }
        }
    }

    [Fact]
    public void Engine_UnknownLoadout_DotCountsOncePerCastAndReplaysOnce()
    {
        var store = new MobResistanceStore();
        store.SetSpell(new("Ignite Blood IX", ResistanceType.Fire, "Fire", T0, DamageOverTime: true));
        store.Record("log", 20, "Sky", "A rat", "Ignite Blood IX", true, T0.AddSeconds(20), damageOverTime: true);
        for (int replay = 0; replay < 2; replay++)
        {
            var engine = new LogEngine(resistances: store, logIdentity: "log") { CharacterName = "Quethrosar" };
            engine.Process(new(T0, "You have entered Sky."));
            engine.Process(new(T0.AddSeconds(1), "You begin casting Ignite Blood IX."));
            engine.Process(new(T0.AddSeconds(7), "A rat has taken 100 damage from your Ignite Blood IX."));
            engine.Process(new(T0.AddSeconds(13), "A rat has taken 100 damage from your Ignite Blood IX."));
            engine.Snapshot(T0.AddSeconds(30));
        }

        var fire = store.Get("Sky", "A rat").Observations.Single(o => o.Type == ResistanceType.Fire);
        Assert.Equal(1, fire.Landed);
        Assert.Equal(1, fire.Resisted);
        Assert.Equal(new RawSpellCounts(1, 0, 0, 1, 1), store.GetRawSpellCounts("Sky", "A rat", "Ignite Blood IX"));
    }

    [Theory]
    [InlineData(" (Critical)", false)]
    [InlineData(" (Lucky)", false)]
    [InlineData("", true)]
    public void Engine_UnknownLoadout_PreservesModifiedAndFinishingHitExclusions(string modifier, bool killed)
    {
        var store = new MobResistanceStore();
        var engine = new LogEngine(resistances: store, logIdentity: "log") { CharacterName = "Quethrosar" };
        engine.Process(new(T0, "You begin casting Rend X."));
        engine.Process(new(T0.AddSeconds(2), $"You hit A rat for 1753 points of magic damage by Rend X.{modifier}"));
        if (killed)
        {
            engine.Process(new(T0.AddSeconds(3), "A rat has been slain by Quethrosar!"));
        }
        engine.Snapshot(T0.AddSeconds(10));

        Assert.Empty(store.Get("(unknown zone)", "A rat").Spells);
    }
}
