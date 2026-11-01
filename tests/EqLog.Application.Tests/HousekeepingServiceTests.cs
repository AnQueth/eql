using EqLog.Application.Sessions;

namespace EqLog.Application.Tests;

public sealed class HousekeepingServiceTests
{
    [Fact]
    public void CadencePreservesSixSecondRefreshAndTenSecondSave()
    {
        var ticks = Enumerable.Range(1, 15).Select(sequence => new HousekeepingTick(sequence)).ToArray();
        Assert.Equal([3, 6, 9, 12, 15], ticks.Where(tick => tick.RefreshDue).Select(tick => tick.Sequence));
        Assert.Equal([5, 10, 15], ticks.Where(tick => tick.SaveDue).Select(tick => tick.Sequence));
    }

    [Fact]
    public async Task CancellationStopsWithoutRunningAHandlerAndAllowsRestart()
    {
        var service = new HousekeepingService();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        int ticks = 0;
        await service.RunAsync(_ =>
        {
            ticks++;
            return Task.CompletedTask;
        }, stop.Token);
        await service.RunAsync(_ => Task.CompletedTask, stop.Token);
        Assert.Equal(0, ticks);
    }
}
