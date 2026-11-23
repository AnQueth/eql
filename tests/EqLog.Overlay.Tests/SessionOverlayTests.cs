using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class SessionOverlayTests
{
    [Fact]
    public void Session_HidesRecentKillsAndExperienceButKeepsSessionStatsAndAaWarning()
    {
        var now = new DateTime(2026, 11, 23, 12, 0, 0);
        var engine = new LogEngine { CharacterName = "Que" };
        var snapshot = engine.Snapshot(now) with
        {
            Session = new SessionSnapshot(
                "The Plane of Hate", now.AddMinutes(-4), now.AddHours(-2), 10, 1, "a golem",
                12, 23.5, 2, 46, 3, true, [new KillRecord(now, "a rock golem", "Que")])
        };
        var presenter = Assert.Single(OverlayPresenters.All, definition => definition.Key == "session");
        var view = new OverlayView(new HashSet<ChatChannel>(), []);

        var content = presenter.Render(snapshot, EngineQuerySnapshot.Capture(engine), view);

        Assert.Equal("The Plane of Hate", content.Title);
        Assert.Equal("Que", content.Subtitle);
        Assert.Equal(
            ["Classes", "In zone", "Session", "Kills", "Deaths", "AA POINTS CAPPED - spend AAs!"],
            content.Rows.Select(row => row.Left));
        Assert.Equal("10  (5/hr)", content.Rows[3].Right);
        Assert.Equal("1  (last: a golem)", content.Rows[4].Right);
        Assert.NotNull(content.Rows[4].Link);
    }
}
