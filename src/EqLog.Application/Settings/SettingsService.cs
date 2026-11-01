namespace EqLog.Application.Settings;

public interface ISettingsRepository
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings snapshot, CancellationToken cancellationToken = default);
}

/// <summary>Captures UI-owned settings before awaiting, then orders writes and preserves damaged documents.</summary>
public sealed class SettingsService(ISettingsRepository repository, Exception? loadError = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _sequence;

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (loadError is not null)
        {
            throw new InvalidOperationException("Settings could not be loaded; saving is disabled until the existing document is repaired and the app is restarted.", loadError);
        }
        var snapshot = settings.Snapshot();
        long sequence = Interlocked.Increment(ref _sequence);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sequence == Interlocked.Read(ref _sequence))
            {
                await repository.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
