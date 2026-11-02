using EqLog.Core.Parsing;
using EqLog.Core.Tracking;

namespace EqLog.Core.Tests;

public class ForageTrackerTests
{
    [Fact]
    public void Engine_CountsFindsFailuresAndSkillUps_AndResetsOnLogin()
    {
        var engine = new LogEngine { CharacterName = "Quethrosar" };
        engine.ProcessLines(
        [
            "[Wed Oct 21 18:42:38 2026] You have scrounged up Fruit.",
            "[Wed Oct 21 18:43:49 2026] You have scrounged up Fruit.",
            "[Wed Oct 21 18:44:00 2026] You fail to locate any food nearby.",
            "[Wed Oct 21 18:46:43 2026] You have scrounged up Rabbit Meat.",
            "[Wed Oct 21 18:46:43 2026] You have become better at Forage! (210)",
            "[Wed Oct 21 18:50:00 2026] You have become better at Forage! (211)",
            "[Wed Oct 21 18:53:29 2026] Bygon tells General:1, 'wts Fruit 100pp each'",
        ]);

        var forage = engine.Snapshot(new DateTime(2026, 10, 21, 19, 0, 0)).Forage;
        Assert.Equal(4, forage.Attempts);
        Assert.Equal(1, forage.Failures);
        Assert.Equal(211, forage.Skill);
        Assert.Equal(2, forage.SkillGains);
        Assert.Equal(new DateTime(2026, 10, 21, 18, 42, 38), forage.FirstAttempt);
        var fruit = forage.Items[0];
        Assert.Equal(("Fruit", 2, 2, new DateTime(2026, 10, 21, 18, 43, 49)), (fruit.Item, fruit.Quantity, fruit.Finds, fruit.LastFound));
        Assert.Equal("Rabbit Meat", forage.Items[1].Item);

        engine.ProcessLines(["[Wed Oct 21 20:00:00 2026] Welcome to EverQuest!"]);
        var reset = engine.Snapshot(new DateTime(2026, 10, 21, 20, 0, 1)).Forage;
        Assert.Equal(0, reset.Attempts);
        Assert.Empty(reset.Items);
        Assert.Equal(211, reset.Skill);
        Assert.Equal(0, reset.SkillGains);
    }

    [Fact]
    public void Items_OrderByQuantityThenName_AndTrackZones()
    {
        var tracker = new ForageTracker();
        var at = new DateTime(2026, 10, 21);
        tracker.OnForage(new(at, "Roots"));
        tracker.OnZone(new(at, "Qeynos Hills"));
        tracker.OnForage(new(at, "Berries"));
        tracker.OnForage(new(at, "Vegetables", 2));
        tracker.OnZone(new(at, "Surefall Glade"));
        tracker.OnForage(new(at.AddMinutes(1), "Vegetables", 3));

        var snapshot = tracker.Snapshot();
        Assert.Equal(["Vegetables", "Berries", "Roots"], snapshot.Items.Select(item => item.Item));
        Assert.Equal([new ForageZone("Surefall Glade", 3), new ForageZone("Qeynos Hills", 2)], snapshot.Items[0].Zones);
        Assert.Equal(ForageTracker.UnknownZone, Assert.Single(snapshot.Items[2].Zones).Zone);
        Assert.Equal(new ForageFind(at.AddMinutes(1), "Vegetables", 3, "Surefall Glade"), snapshot.Recent[0]);
        Assert.Equal(4, snapshot.Recent.Count);
    }
}
