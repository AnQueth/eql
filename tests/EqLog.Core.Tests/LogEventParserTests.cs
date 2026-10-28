using EqLog.Core.Parsing;

namespace EqLog.Core.Tests;

public class LogEventParserTests
{
    private static readonly DateTime T = new(2026, 10, 21, 17, 7, 4);
    private readonly LogEventParser _parser = new("Quethrosar");

    private LogEvent? P(string message)
    {
        return _parser.Parse(T, message);
    }

    [Fact]
    public void ParsesTimestampedLine()
    {
        Assert.True(LogLine.TryParse("[Wed Oct 21 17:07:04 2026] You have entered Surefall Glade.", out var line));
        Assert.Equal(T, line.Timestamp);
        Assert.Equal("You have entered Surefall Glade.", line.Message);
    }

    [Fact]
    public void RejectsGarbage()
    {
        Assert.False(LogLine.TryParse("not a log line", out _));
        Assert.False(LogLine.TryParse("[garbage] hi", out _));
    }

    [Theory]
    [InlineData("You slash Lord Rikantus Everling for 118 points of damage.", "You", "Lord Rikantus Everling", 118, null)]
    [InlineData("You slash Guardian Golem for 88 points of damage. (Critical)", "You", "Guardian Golem", 88, "Critical")]
    [InlineData("A ravenous zombie punches YOU for 99 points of damage.", "A ravenous zombie", "You", 99, null)]
    [InlineData("An azarack backstabs YOU for 381 points of damage.", "An azarack", "You", 381, null)]
    [InlineData("Gibarn slashes Lord Rikantus Everling for 82 points of damage.", "Gibarn", "Lord Rikantus Everling", 82, null)]
    [InlineData("An azarack hits an azarack for 176 points of damage.", "An azarack", "An azarack", 176, null)]
    [InlineData("You slash Guardian Golem for 106 points of damage. (Finishing Blow)", "You", "Guardian Golem", 106, "Finishing Blow")]
    public void ParsesMelee(string msg, string attacker, string target, long amount, string? mod)
    {
        var e = Assert.IsType<DamageEvent>(P(msg));
        Assert.Equal(DamageKind.Melee, e.Kind);
        Assert.Equal(attacker, e.Attacker);
        Assert.Equal(target, e.Target);
        Assert.Equal(amount, e.Amount);
        Assert.Equal(mod, e.Modifier);
    }

    [Fact]
    public void ParsesSpellDamage()
    {
        var e = Assert.IsType<DamageEvent>(P("You hit Guardian Golem for 4806 points of fire damage by Conflagration X. (Critical)"));
        Assert.Equal(DamageKind.DirectSpell, e.Kind);
        Assert.Equal("You", e.Attacker);
        Assert.Equal("Guardian Golem", e.Target);
        Assert.Equal(4806, e.Amount);
        Assert.Equal("Conflagration X", e.Source);
        Assert.True(e.IsCritical);
    }

    [Fact]
    public void ParsesIncomingSpellDamage()
    {
        var e = Assert.IsType<DamageEvent>(P("an azarack hit you for 570 points of magic damage by Harm Touch."));
        Assert.Equal("An azarack", e.Attacker);
        Assert.Equal("You", e.Target);
        Assert.Equal("Harm Touch", e.Source);
    }

    [Fact]
    public void IgnoresSelfDamage()
    {
        Assert.Null(P("You hit yourself for 578 points of fire damage by Conflagration X."));
    }

    [Fact]
    public void ParsesYourDot()
    {
        var e = Assert.IsType<DamageEvent>(P("Lord Rikantus Everling has taken 8 damage from your Oathbreaker's Curse. (Critical)"));
        Assert.Equal(DamageKind.DamageOverTime, e.Kind);
        Assert.Equal("You", e.Attacker);
        Assert.Equal("Lord Rikantus Everling", e.Target);
        Assert.Equal("Oathbreaker's Curse", e.Source);
        Assert.True(e.IsCritical);
    }

    [Fact]
    public void ParsesDotOnYou()
    {
        var e = Assert.IsType<DamageEvent>(P("You have taken 125 damage from Ignite Blood by an azarack."));
        Assert.Equal("An azarack", e.Attacker);
        Assert.Equal("You", e.Target);
        Assert.Equal("Ignite Blood", e.Source);
    }

    [Theory]
    [InlineData("Jonekab has taken 351 damage by Envenomed Bolt.", "Jonekab", "Envenomed Bolt")]
    [InlineData("Zasanab has taken 80 damage by Bond of Death.", "Zasanab", "Bond of Death")]
    public void ParsesDotWithoutNamedCaster(string msg, string target, string spell)
    {
        var e = Assert.IsType<DamageEvent>(P(msg));
        Assert.Equal(DamageKind.DamageOverTime, e.Kind);
        Assert.Equal("Unknown", e.Attacker);
        Assert.Equal(target, e.Target);
        Assert.Equal(spell, e.Source);
    }

    [Theory]
    [InlineData("A ravenous zombie is burned by YOUR flames for 5 points of non-melee damage.", "You", "A ravenous zombie", 5)]
    [InlineData("YOU are pierced by a spiroc banisher's thorns for 20 points of non-melee damage!", "A spiroc banisher", "You", 20)]
    public void ParsesDamageShield(string msg, string attacker, string target, long amount)
    {
        var e = Assert.IsType<DamageEvent>(P(msg));
        Assert.Equal(DamageKind.DamageShield, e.Kind);
        Assert.Equal(attacker, e.Attacker);
        Assert.Equal(target, e.Target);
        Assert.Equal(amount, e.Amount);
    }

    [Fact]
    public void ParsesHealWithOverheal()
    {
        var e = Assert.IsType<HealEvent>(P("You healed Quethrosar for 20 (180) hit points by Life Leech."));
        Assert.Equal("You", e.Healer);
        Assert.Equal("You", e.Target);
        Assert.Equal(20, e.Amount);
        Assert.Equal(180, e.FullAmount);
        Assert.Equal("Life Leech", e.Spell);
    }

    [Fact]
    public void ParsesHealOverTimeAndItself()
    {
        var hot = Assert.IsType<HealEvent>(P("You healed Quethrosar over time for 204 hit points by Efflorescing Heal."));
        Assert.True(hot.OverTime);
        Assert.Equal(204, hot.Amount);

        var self = Assert.IsType<HealEvent>(P("Zasanab healed itself for 55 hit points by Specter Lifetap."));
        Assert.Equal("Zasanab", self.Healer);
        Assert.Equal("Zasanab", self.Target);
    }

    [Fact]
    public void ParsesKills()
    {
        var k = Assert.IsType<KillEvent>(P("You have slain a chapter of misery!"));
        Assert.Equal("You", k.Killer);
        Assert.Equal("A chapter of misery", k.Victim);

        var d = Assert.IsType<KillEvent>(P("You have been slain by Guardian Golem!"));
        Assert.Equal("You", d.Victim);
        Assert.Equal("Guardian Golem", d.Killer);

        var o = Assert.IsType<KillEvent>(P("A spiroc arbiter has been slain by heart harpie!"));
        Assert.Equal("A spiroc arbiter", o.Victim);
        Assert.Equal("Heart harpie", o.Killer);
    }

    [Fact]
    public void ParsesExperience()
    {
        var x = Assert.IsType<ExperienceEvent>(P("You gain party experience! (4.900%)"));
        Assert.Equal(ExperienceKind.Party, x.Kind);
        Assert.Equal(4.9, x.Percent);
        Assert.IsType<AaCapEvent>(P("You have reached the AA point cap, and cannot gain any further experience until some of your stored AA point pool is used."));
        Assert.Equal(51, Assert.IsType<LevelEvent>(P("You have gained a level! Welcome to level 51!")).Level);
    }

    [Fact]
    public void ParsesLoot()
    {
        var a = Assert.IsType<LootEvent>(P("--You have looted a Signet Ring of Lord Everling from Lord Rikantus Everling's corpse.--"));
        Assert.Equal("Signet Ring of Lord Everling", a.Item);
        Assert.Equal("Lord Rikantus Everling", a.Corpse);
        Assert.Equal("You", a.Looter);

        var b = Assert.IsType<LootEvent>(P("You looted 3 Deepwater Ink from a gloomwater mermaid's corpse and stored it in your tradeskill depot"));
        Assert.Equal("Deepwater Ink", b.Item);
        Assert.Equal(3, b.Quantity);
        Assert.True(b.Stored);

        var c = Assert.IsType<LootEvent>(P("You looted a Fetid Skin from a fetid fiend's corpse and sold it for 1 gold, 2 silver and 3 copper."));
        Assert.Equal(123, c.SoldFor!.Value.Copper);
    }

    [Fact]
    public void ParsesCoin()
    {
        var c = Assert.IsType<CoinEvent>(P("You receive 3 platinum, 2 gold, 1 silver and 5 copper from the corpse."));
        Assert.Equal(3215, c.Amount.Copper);
        Assert.Equal("3p 2g 1s 5c", c.Amount.ToString());
    }

    [Theory]
    [InlineData("Kung tells you, 'hello there'", ChatChannel.Tell, "Kung", "hello there")]
    [InlineData("You told Kung, 'hi'", ChatChannel.Tell, "You", "hi")]
    [InlineData("Brismoore tells the group, 'inc'", ChatChannel.Group, "Brismoore", "inc")]
    [InlineData("Kimobuk tells the guild, 'grats'", ChatChannel.Guild, "Kimobuk", "grats")]
    [InlineData("Shirris tells General:1, 'shut up screw ball'", ChatChannel.Channel, "Shirris", "shut up screw ball")]
    [InlineData("You tell General:1, 'what is the charm method ?'", ChatChannel.Channel, "You", "what is the charm method ?")]
    [InlineData("The Game Master shouts, 'You will pay for 100 points of damage.'", ChatChannel.Shout, "The Game Master", "You will pay for 100 points of damage.")]
    [InlineData("Plimptos says, 'Hail, Quethrosar'", ChatChannel.Say, "Plimptos", "Hail, Quethrosar")]
    public void ParsesChat(string msg, ChatChannel channel, string from, string text)
    {
        var c = Assert.IsType<ChatEvent>(P(msg));
        Assert.Equal(channel, c.Channel);
        Assert.Equal(from, c.From);
        Assert.Equal(text, c.Text);
    }

    [Fact]
    public void ParsesZoneButNotAreaMessages()
    {
        Assert.Equal("Nektropos Castle", Assert.IsType<ZoneEvent>(P("You have entered Nektropos Castle.")).Zone);
        Assert.Null(P("You have entered an area where levitation effects do not function."));
    }

    [Fact]
    public void ParsesSpellLifecycle()
    {
        Assert.Equal("Mesmerization X", Assert.IsType<CastBeginEvent>(P("You begin casting Mesmerization X.")).Spell);
        Assert.Equal("Denon's Desperate Dirge X", Assert.IsType<CastBeginEvent>(P("You begin singing Denon's Desperate Dirge X.")).Spell);
        Assert.Equal("Conflagration X", Assert.IsType<CastInterruptedEvent>(P("Your Conflagration X spell is interrupted.")).Spell);
        Assert.IsType<CastFizzleEvent>(P("Your spell fizzles!"));

        var r = Assert.IsType<SpellResistedEvent>(P("A chapter of misery resisted your Largo's Melodic Binding VII!"));
        Assert.Equal("A chapter of misery", r.Target);
        Assert.Equal("Largo's Melodic Binding VII", r.Spell);

        var w = Assert.IsType<SpellWornOffEvent>(P("Your Allure X spell has worn off of heart harpie."));
        Assert.Equal("Allure X", w.Spell);
        Assert.Equal("Heart harpie", w.Target);

        var c = Assert.IsType<EffectLandedEvent>(P("heart harpie has been charmed."));
        Assert.True(c.IsCharm);
        Assert.Equal("Heart harpie", c.Target);

        Assert.Equal("mesmerized", Assert.IsType<EffectLandedEvent>(P("an azarack has been mesmerized.")).Effect);
        Assert.Equal("An azarack", Assert.IsType<AwakenedEvent>(P("An azarack has been awakened by Quethrosar.")).Target);
    }

    [Fact]
    public void OtherSpellsInterruptedAreNotYours()
    {
        Assert.Null(P("Lord Rikantus Everling's Insidious Retrogression spell is interrupted."));
    }
}
