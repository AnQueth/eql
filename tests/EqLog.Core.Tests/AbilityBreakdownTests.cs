using EqLog.Core.Parsing;
using EqLog.Core.Tracking;

namespace EqLog.Core.Tests;

public class AbilityBreakdownTests
{
    private static readonly DateTime Start = new(2026, 10, 27, 12, 0, 0);

    [Fact]
    public void FightBreakdown_SeparatesCombatantsAndAbilities_UsesFightDuration()
    {
        var tracker = new FightTracker();
        tracker.OnDamage(new(Start, Names.You, "a goblin", 100, DamageKind.DirectSpell, "Fire", "Critical"));
        tracker.OnDamage(new(Start.AddSeconds(4), Names.You, "a goblin", 50, DamageKind.DirectSpell, "fire", null));
        tracker.OnDamage(new(Start.AddSeconds(4), Names.You, "a goblin", 50, DamageKind.Melee, null, null));
        tracker.OnDamage(new(Start.AddSeconds(4), "a pet", "a goblin", 100, DamageKind.Melee, null, null));

        var fight = tracker.Current()!;
        var you = fight.You!;
        Assert.Equal(5, fight.DurationSeconds);
        Assert.Equal(you.Total, you.Abilities.Sum(a => a.Total));
        Assert.Equal(you.Dps, you.Abilities.Sum(a => a.PerSecond));
        var fire = Assert.Single(you.Abilities, a => a.Name == "Fire");
        Assert.Equal(150, fire.Total);
        Assert.Equal(30, fire.PerSecond);
        Assert.Equal(75, fire.Percent);
        Assert.Equal(2, fire.Hits);
        Assert.Equal(1, fire.Crits);
        Assert.Equal(100, fire.MaxHit);
        Assert.Single(fight.Attackers.Single(a => a.Name == "a pet").Abilities);

        tracker.OnKill(new(Start.AddSeconds(5), Names.You, "a goblin"));
        tracker.OnDamage(new(Start.AddSeconds(10), Names.You, "a goblin", 7, DamageKind.Melee, null, null));
        Assert.Equal(7, Assert.Single(tracker.Current()!.You!.Abilities).Total);
        Assert.Equal(200, tracker.Recent(2).Last().You!.Abilities.Sum(a => a.Total));
    }

    [Fact]
    public void Engine_SeparatesRangedMeleeAndNamedProcEffectsWithoutChangingTotals()
    {
        var npcs = new NpcCompendium();
        var engine = new LogEngine(logIdentity: "log", npcs: npcs) { CharacterName = "Quethrosar" };
        string[] messages =
        [
            "You kick a greater sphinx for 42 points of damage.",
            "You shoot a greater sphinx for 688 points of damage. (Double Bow Shot)",
            "You shoot a greater sphinx for 1676 points of damage. (Critical Double Bow Shot)",
            "You shoot a greater sphinx for 1932 points of damage. (Finishing Blow Double Bow Shot)",
            "You hit a greater sphinx for 100 points of magic damage by Whirlwind.",
            "You hit a greater sphinx for 33 points of magic damage by Chaotic Feedback.",
            "You hit a greater sphinx for 1324 points of fire damage by Conflagration.",
            "You hit a greater sphinx for 1700 points of fire damage by Conflagration X.",
            "You hit a greater sphinx for 75 points of magic damage by Soul Leech.",
            "You hit a greater sphinx for 35 points of magic damage by Lifetap Strike III.",
            "You try to shoot a greater sphinx, but miss!",
            "You have slain a greater sphinx!",
        ];
        for (int i = 0; i < messages.Length; i++)
        {
            engine.Process(new(Start.AddSeconds(i), messages[i]));
        }

        var snapshot = engine.Snapshot(Start.AddSeconds(messages.Length));
        var fight = Assert.IsType<FightSnapshot>(snapshot.CurrentFight);
        var you = Assert.IsType<CombatantStats>(fight.You);
        Assert.Equal(7605, you.Total);
        Assert.Equal(12, fight.DurationSeconds);
        Assert.Equal(7605.0 / 12, you.Dps);
        var melee = Assert.Single(you.Abilities, a => a.Name == "Melee");
        var ranged = Assert.Single(you.Abilities, a => a.Name == "Ranged");
        Assert.Equal(42, melee.Total);
        Assert.Equal(4296, ranged.Total);
        Assert.Equal(3, ranged.Hits);
        Assert.Equal(1, ranged.Crits);
        Assert.Equal(1932, ranged.MaxHit);
        Assert.Equal("ranged on A greater sphinx", snapshot.SessionMaxHitSource);
        foreach (var (name, amount) in new[]
        {
            ("Whirlwind", 100), ("Chaotic Feedback", 33), ("Conflagration", 1324),
            ("Conflagration X", 1700), ("Soul Leech", 75), ("Lifetap Strike III", 35),
        })
        {
            Assert.Equal(amount, Assert.Single(you.Abilities, a => a.Name == name).Total);
            Assert.Equal(amount, Assert.Single(you.AbilityTimelines, a => a.Name == name).Points.Sum(p => p.Amount));
        }
        Assert.Equal(you.Total, you.Abilities.Sum(a => a.Total));
        Assert.Equal(4296, Assert.Single(you.AbilityTimelines, a => a.Name == "Ranged").Points.Sum(p => p.Amount));
        var encounter = Assert.Single(npcs.Get("A greater sphinx", "(unknown zone)")!.Encounters);
        Assert.Equal(you.Total, encounter.YourDamage);
        Assert.Equal(4296, Assert.Single(encounter.Spells, s => s.Spell == "Ranged").Damage);
        Assert.Equal(42, Assert.Single(encounter.Spells, s => s.Spell == "Melee").Damage);
        Assert.Equal(75, Assert.Single(encounter.Spells, s => s.Spell == "Soul Leech").Damage);
    }

    [Fact]
    public void HealingBreakdown_TracksObservedHealersAndDirections_WithoutDoubleCountingSelfHeal()
    {
        var tracker = new HealTracker();
        tracker.OnHeal(new(Start, Names.You, Names.You, 100, 150, "Renew", false, "Critical"));
        tracker.OnHeal(new(Start.AddSeconds(4), Names.You, "a pet", 50, 50, "renew", true, null));
        tracker.OnHeal(new(Start.AddSeconds(4), "Cleric", Names.You, 200, 220, "Light", false, null));
        tracker.OnHeal(new(Start.AddSeconds(4), "a priest", "a goblin", 80, null, null, false, null));
        var snapshot = tracker.Snapshot(Start.AddSeconds(4), 10);

        Assert.Equal(150, snapshot.OutgoingTotal);
        Assert.Equal(300, snapshot.IncomingTotal);
        Assert.Equal(50, snapshot.OutgoingOverheal);
        Assert.Equal(5, snapshot.DurationSeconds);
        var you = Assert.Single(snapshot.Healers, h => h.Name == Names.You);
        var renew = Assert.Single(you.Abilities);
        Assert.Equal(150, renew.Total);
        Assert.Equal(30, renew.PerSecond);
        Assert.Equal(50, renew.Overheal);
        Assert.Equal(1, renew.Crits);
        Assert.Equal(50, Assert.Single(snapshot.OutgoingTargetAbilities["a pet"]).Total);
        Assert.Equal(200, Assert.Single(snapshot.IncomingHealerAbilities["Cleric"]).Total);
        Assert.Equal("(unknown)", Assert.Single(snapshot.Healers.Single(h => h.Name == "a priest").Abilities).Name);

        tracker.ResetSession();
        var reset = tracker.Snapshot(Start, 10);
        Assert.Empty(reset.Healers);
        Assert.Empty(reset.OutgoingTargetAbilities);
        Assert.Empty(reset.IncomingHealerAbilities);
        Assert.Equal(150, renew.Total);
    }
}
