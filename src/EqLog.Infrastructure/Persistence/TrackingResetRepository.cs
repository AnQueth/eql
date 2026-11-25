using EqLog.Application.Tracking;

namespace EqLog.Infrastructure.Persistence;

public sealed class TrackingResetRepository : ITrackingResetRepository
{
    public Task RequestAsync(string folder, DateTime? ignoreLogLinesBefore)
    {
        return TrackingDataReset.RequestAsync(folder, ignoreLogLinesBefore);
    }
    public Task<string?> ApplyPendingAsync(string folder)
    {
        return TrackingDataReset.ApplyPendingAsync(folder);
    }
    public Task<DateTime?> LoadLogCutoffAsync(string folder, CancellationToken cancellationToken)
    {
        return TrackingDataReset.LoadLogCutoffAsync(folder, cancellationToken);
    }
}
