namespace EqLog.Application.Sessions;

public sealed record HousekeepingTick(int Sequence)
{
    public bool RefreshDue
    {
        get
        {
            return Sequence % 3 == 0;
        }
    }
    public bool SaveDue
    {
        get
        {
            return Sequence % 5 == 0;
        }
    }
}

/// <summary>Application maintenance cadence; awaited handlers coalesce ticks rather than flooding a busy UI dispatcher.</summary>
public sealed class HousekeepingService(TimeProvider? timeProvider = null)
{
    private int _running;

    public async Task RunAsync(Func<HousekeepingTick, Task> handle, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            throw new InvalidOperationException("Housekeeping is already running.");
        }
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), timeProvider ?? TimeProvider.System);
            int sequence = 0;
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await handle(new(++sequence)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Application shutdown stops the maintenance clock.
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}
