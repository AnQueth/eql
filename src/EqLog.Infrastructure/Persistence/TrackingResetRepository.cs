using EqLog.Application.Tracking;

namespace EqLog.Infrastructure.Persistence;

public sealed class TrackingResetRepository : ITrackingResetRepository
{
    public Task RequestAsync(string folder)
    {
        return TrackingDataReset.RequestAsync(folder);
    }
    public Task<string?> ApplyPendingAsync(string folder)
    {
        return TrackingDataReset.ApplyPendingAsync(folder);
    }
}
