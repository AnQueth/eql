using EqLog.Core.Parsing;
using EqLog.Core.Tracking;

namespace EqLog.Core.Tests;

public class LootTrackerTests
{
    [Fact]
    public void Groups_CombineVariantsAndQuantities_SeparateLootersAndSort()
    {
        var tracker = new LootTracker();
        var at = new DateTime(2026, 10, 27);
        tracker.OnLoot(new(at, Names.You, "Zebra +1", 2, null, null, false));
        tracker.OnLoot(new(at.AddSeconds(5), Names.You, "zebra + 2", 3, null, null, false));
        tracker.OnLoot(new(at.AddSeconds(1), Names.You, "Apple", 1, null, null, false));
        tracker.OnLoot(new(at.AddSeconds(2), "Other", "Zebra", 4, null, null, false));
        var snapshot = tracker.Snapshot(60, 10);
        Assert.Equal(3, snapshot.Groups.Count);
        var yours = Assert.Single(snapshot.Groups, g => g.Looter == Names.You && g.Quantity == 5);
        Assert.Equal(at.AddSeconds(5), yours.LastLoot);
        Assert.Equal(yours, snapshot.OrderedGroups(true).First());
        Assert.Equal("Apple", snapshot.OrderedGroups(false).First().Item);
        Assert.Equal(6, snapshot.ItemsLooted);
    }

    [Fact]
    public void Groups_KeepCountsBeyondRecentBuffer_AndResetWithoutMutatingSnapshot()
    {
        var tracker = new LootTracker();
        for (int i = 0; i < 80; i++)
        {
            tracker.OnLoot(new(DateTime.Today.AddSeconds(i), Names.You, "Bone Chips", 2, null, null, false));
        }

        var snapshot = tracker.Snapshot(60, 10);
        Assert.Equal(60, snapshot.Recent.Count);
        Assert.Equal(160, Assert.Single(snapshot.Groups).Quantity);
        tracker.ResetSession();
        Assert.Empty(tracker.Snapshot(60, 10).Groups);
        Assert.Equal(160, Assert.Single(snapshot.Groups).Quantity);
    }
}
