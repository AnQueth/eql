using EqLog.Application.Settings;

namespace EqLog.Application.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public async Task SavesCaptureBeforeAwaitAndNewestQueuedSnapshotWins()
    {
        var repository = new Repository();
        var service = new SettingsService(repository);
        var settings = new AppSettings { FontSize = 10 };
        var first = service.SaveAsync(settings);
        await repository.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        settings.FontSize = 11;
        var obsolete = service.SaveAsync(settings);
        settings.FontSize = 12;
        var latest = service.SaveAsync(settings);
        settings.FontSize = 13;
        repository.Release.SetResult();
        await Task.WhenAll(first, obsolete, latest);
        Assert.Equal([10d, 12d], repository.Written.Select(snapshot => snapshot.FontSize));
    }

    [Fact]
    public async Task DamagedLoadBlocksAllWritesAndPreservesError()
    {
        var repository = new Repository();
        var original = new IOException("Damaged settings.");
        var service = new SettingsService(repository, original);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(new()));
        Assert.Same(original, error.InnerException);
        Assert.Empty(repository.Written);
    }

    [Fact]
    public async Task FailedWriteIsRetryable()
    {
        var repository = new Repository { Fail = true };
        repository.Release.SetResult();
        var service = new SettingsService(repository);
        await Assert.ThrowsAsync<IOException>(() => service.SaveAsync(new()));
        repository.Fail = false;
        await service.SaveAsync(new());
        Assert.Single(repository.Written);
    }

    private sealed class Repository : ISettingsRepository
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<AppSettings> Written { get; } = [];
        public bool Fail
        {
            get; set;
        }
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new AppSettings());
        }
        public async Task SaveAsync(AppSettings snapshot, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            if (Fail)
            {
                throw new IOException("Write failed.");
            }
            Written.Add(snapshot);
        }
    }
}
