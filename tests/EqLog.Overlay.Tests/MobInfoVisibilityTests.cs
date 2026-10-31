using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class MobInfoVisibilityTests
{
    private static readonly DateTime Start = new(2026, 10, 31, 13, 0, 0);

    [Fact]
    public void UnhoveredDisplay_ExpiresAfterConfiguredTimeout()
    {
        var visibility = new MobInfoVisibility();
        visibility.Show(Start, 5);

        Assert.True(visibility.IsActive(Start.AddSeconds(4)));
        Assert.False(visibility.IsActive(Start.AddSeconds(5)));
    }

    [Fact]
    public void Hover_HoldsDisplayAndStartsFullTimeoutWhenLeaving()
    {
        var visibility = new MobInfoVisibility();
        visibility.Show(Start, 5);
        visibility.SetHovered(true, Start.AddSeconds(4), 5);

        Assert.True(visibility.IsActive(Start.AddMinutes(10)));
        visibility.SetHovered(false, Start.AddMinutes(10), 5);
        Assert.True(visibility.IsActive(Start.AddMinutes(10).AddSeconds(4)));
        Assert.False(visibility.IsActive(Start.AddMinutes(10).AddSeconds(5)));
    }

    [Fact]
    public void ReenteringBeforeExpiry_HoldsDisplayAgain()
    {
        var visibility = new MobInfoVisibility();
        visibility.Show(Start, 5);
        visibility.SetHovered(true, Start.AddSeconds(1), 5);
        visibility.SetHovered(false, Start.AddSeconds(10), 5);
        visibility.SetHovered(true, Start.AddSeconds(14), 5);

        Assert.True(visibility.IsActive(Start.AddSeconds(100)));
        visibility.SetHovered(false, Start.AddSeconds(100), 5);
        Assert.False(visibility.IsActive(Start.AddSeconds(105)));
    }

    [Fact]
    public void NewConsiderAndTimeoutChangeWhileHovered_RemainHeldUntilDeparture()
    {
        var visibility = new MobInfoVisibility();
        visibility.Show(Start, 5);
        visibility.SetHovered(true, Start.AddSeconds(1), 5);
        visibility.Show(Start.AddSeconds(10), 2);

        Assert.True(visibility.IsActive(Start.AddSeconds(100)));
        visibility.SetHovered(false, Start.AddSeconds(100), 2);
        Assert.True(visibility.IsActive(Start.AddSeconds(101)));
        Assert.False(visibility.IsActive(Start.AddSeconds(102)));
    }

    [Fact]
    public void HoveringExpiredDisplay_DoesNotResurrectIt()
    {
        var visibility = new MobInfoVisibility();
        visibility.Show(Start, 5);
        visibility.SetHovered(true, Start.AddSeconds(6), 5);
        visibility.SetHovered(false, Start.AddSeconds(7), 5);

        Assert.False(visibility.IsActive(Start.AddSeconds(7)));
    }

    [Fact]
    public void ResetWhileHovered_HidesDisplayButNextConsiderCanBeHeld()
    {
        var visibility = new MobInfoVisibility();
        visibility.Show(Start, 5);
        visibility.SetHovered(true, Start.AddSeconds(1), 5);
        visibility.Reset();

        Assert.False(visibility.IsActive(Start.AddSeconds(2)));
        visibility.Show(Start.AddSeconds(3), 5);
        Assert.True(visibility.IsActive(Start.AddSeconds(100)));
    }

    [Fact]
    public void ZeroTimeout_RemainsVisibleAfterDeparture()
    {
        var visibility = new MobInfoVisibility();
        visibility.Show(Start, 0);
        visibility.SetHovered(true, Start.AddSeconds(1), 0);
        visibility.SetHovered(false, Start.AddSeconds(10), 0);

        Assert.True(visibility.IsActive(Start.AddDays(1)));
    }
}
