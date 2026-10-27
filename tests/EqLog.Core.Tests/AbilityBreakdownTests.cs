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
