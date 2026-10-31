using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Overlay;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class ItemUpgradesOverlayTests
{
    [Fact]
    public void Presenter_ShowsRankChangesAndLocalTimestampsForSelectedCharacter()
    {
        var written = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        var inventory = new QuestInventory
        {
            RecentUpgrades =
            [
                new("Que", "Sword", 1, 2, written),
                new("Other", "Shield", 0, 1, written.AddMinutes(1)),
            ],
        };
        var view = new OverlayView(new HashSet<ChatChannel>(), [], Inventory: inventory, CharacterName: "Que");
        var presenter = Assert.Single(OverlayPresenters.All, definition => definition.Key == "itemupgrades");

        var content = presenter.Render(null!, null!, view);

        Assert.Equal("Que", content.Subtitle);
        var row = Assert.Single(content.Rows);
        Assert.Equal("Sword", row.Left);
        Assert.Equal("+1 to +2", row.Right);
        Assert.Equal(written.ToLocalTime().ToString("g"), row.Details);
    }

    [Fact]
    public void Presenter_ShowsWaitingStateUntilCharacterIsKnown()
    {
        var view = new OverlayView(new HashSet<ChatChannel>(), []);
        var presenter = Assert.Single(OverlayPresenters.All, definition => definition.Key == "itemupgrades");

        var content = presenter.Render(null!, null!, view);

        Assert.Contains("Waiting", Assert.Single(content.Rows).Left);
    }
}
