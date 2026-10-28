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
    public void Engine_LoadoutsAndLevels_DoNotMixDamageBaselines()
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
        Assert.Equal(0, fire.EstimatedPartial);
        Assert.Equal(0, fire.UnassessedDamage);
    }

    [Fact]
    public void Engine_BeforeWho_DoesNotLearnFromUnknownLoadout()
    {
        var store = new MobResistanceStore();
        store.SetSpell(new("Conflagration X", ResistanceType.Fire, "Fire", T0, true));
        var engine = new LogEngine(resistances: store, logIdentity: "log") { CharacterName = "Quethrosar" };
        engine.Process(new(T0, "You begin casting Conflagration X."));
        engine.Process(new(T0.AddSeconds(1), "You hit A rat for 100 points of fire damage by Conflagration X."));
        engine.Snapshot(T0.AddSeconds(5));
        Assert.Empty(store.Get("(unknown zone)", "A rat").Spells);
    }
}
