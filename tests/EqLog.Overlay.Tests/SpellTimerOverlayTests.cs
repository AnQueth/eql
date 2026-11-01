using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class SpellTimerOverlayTests
{
    [Fact]
    public void Timers_TogglingKnownSpells_PreservesActiveTimersAndTracking()
    {
        var now = new DateTime(2026, 11, 1, 17, 0, 0);
        var durations = new SpellDurationStore();
        durations.SetOverride("Adrenaline", 60);
        durations.SetOverride("Bite", 20);
        durations.SetTracked("Bite", false);
        durations.SetWikiDuration("Charm", 45);
        durations.SetTracked("Unknown", false);
        var engine = new LogEngine(durations: durations);
        var snapshot = engine.Snapshot(now) with
        {
            Timers =
            [
                new("adrenaline", "a kobold", true, false, now.AddSeconds(-10), 60, 10, 50, TimerState.Running)
            ]
        };
        var queries = EngineQuerySnapshot.Capture(engine);
        var presenter = Assert.Single(OverlayPresenters.All, definition => definition.Key == "timers");
        var view = new OverlayView(new HashSet<ChatChannel>(), []);

        var visible = presenter.Render(snapshot, queries, view);
        var hidden = presenter.Render(snapshot, queries, view with
        {
            ShowKnownSpells = false
        });
        var shownAgain = presenter.Render(snapshot, queries, view);

        Assert.Equal(["adrenaline \u2192 a kobold", "Known spells", "Bite", "Charm"],
            visible.Rows.Select(row => row.Left));
        Assert.True(visible.Rows[1].QuestHeading);
        Assert.Equal(new SpellTrackChange("Bite", false), visible.Rows[2].SpellTrack);
        Assert.Equal(new SpellTrackChange("Charm", true), visible.Rows[3].SpellTrack);
        Assert.Single(hidden.Rows);
        Assert.Equal(visible.Rows[0], hidden.Rows[0]);
        Assert.Equal("0:50", hidden.Rows[0].Right);
        Assert.Equal(
            visible.Rows.Select(row => (row.Left, row.Right, row.SpellTrack)),
            shownAgain.Rows.Select(row => (row.Left, row.Right, row.SpellTrack)));
        Assert.True(durations.IsTracked("Adrenaline"));
        Assert.False(durations.IsTracked("Bite"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Timers_NoKnownSpells_PreservesEmptyTimerMessage(bool showKnownSpells)
    {
        var engine = new LogEngine();
        var presenter = Assert.Single(OverlayPresenters.All, definition => definition.Key == "timers");
        var view = new OverlayView(new HashSet<ChatChannel>(), [], ShowKnownSpells: showKnownSpells);

        var content = presenter.Render(engine.Snapshot(DateTime.UtcNow), EngineQuerySnapshot.Capture(engine), view);

        Assert.Equal("No active timers", Assert.Single(content.Rows).Left);
    }
}
