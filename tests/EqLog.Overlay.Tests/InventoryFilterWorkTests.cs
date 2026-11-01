using System.Windows.Controls;
using System.Windows.Threading;
using EqLog.Core;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class InventoryFilterWorkTests
{
    [Fact]
    public void SupersededWorkerCannotPublishEvenWhenItIgnoresCancellation()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var work = new InventoryFilterWork(dispatcher);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var output = new TextBlock();
            Exception? failure = null;
            var old = work.Start(
                cancellation =>
                {
                    Assert.False(dispatcher.CheckAccess());
                    entered.SetResult();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                    return "old";
                },
                (value, cancellation) =>
                {
                    output.Text = value;
                    return Task.CompletedTask;
                },
                error => failure = error,
                false);
            await entered.Task;
            var newest = work.Start(
                cancellation => "new",
                (value, cancellation) =>
                {
                    Assert.True(dispatcher.CheckAccess());
                    output.Text = value;
                    return Task.CompletedTask;
                },
                error => failure = error,
                false);
            release.Set();
            await newest;
            await old;
            Assert.Null(failure);
            Assert.Equal("new", output.Text);
        });
    }

    [Fact]
    public void DebounceWaitsAndBatchedDispatcherApplyCanBeCanceledBetweenYields()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var work = new InventoryFilterWork(dispatcher);
            int queries = 0;
            int rows = 0;
            Exception? failure = null;
            var firstBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = work.Start(
                cancellation =>
                {
                    Interlocked.Increment(ref queries);
                    return 100;
                },
                async (count, cancellation) =>
                {
                    for (int index = 0; index < count; index++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        dispatcher.VerifyAccess();
                        rows++;
                        firstBatch.TrySetResult();
                        await Dispatcher.Yield(DispatcherPriority.Background);
                    }
                },
                error => failure = error);
            await Task.Delay(50);
            Assert.Equal(0, Volatile.Read(ref queries));
            await firstBatch.Task;
            work.Cancel();
            await pending;
            Assert.InRange(rows, 1, 99);
            Assert.Null(failure);
        });
    }

    [Fact]
    public void MergeQueryMatchesNamesAndLocationsAndUsesDetachedSource()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            InventoryExportItem[] source =
            [
                new("Held", "Sword", 1, 1, InventoryStorage.Carried),
                new("Bank1", "Sword +3", 1, 2, InventoryStorage.Bank),
                new("Held", "Shield", 2, 1, InventoryStorage.Carried),
                new("Held", "Shield +1", 2, 1, InventoryStorage.Carried),
            ];
            var work = new InventoryFilterWork(Dispatcher.CurrentDispatcher);
            InventoryMergeResult? result = null;
            Exception? failure = null;
            var detached = source.ToArray();
            var pending = work.Start(
                cancellation => InventoryFilterWork.QueryMerge(detached, " bAnK ", cancellation),
                (value, cancellation) =>
                {
                    result = value;
                    return Task.CompletedTask;
                },
                error => failure = error);
            source[0] = new("Held", "Changed", 1, 1, InventoryStorage.Carried);
            await pending;
            Assert.Null(failure);
            Assert.NotNull(result);
            Assert.Equal(2, result.Total);
            Assert.Equal("Sword", Assert.Single(result.Shown).BaseName);
            Assert.Equal(3, result.Shown[0].Total);

            await work.Start(
                cancellation => InventoryFilterWork.QueryMerge(detached, " sHiElD ", cancellation),
                (value, cancellation) =>
                {
                    result = value;
                    return Task.CompletedTask;
                },
                error => failure = error,
                false);
            Assert.Equal("Shield", Assert.Single(result.Shown).BaseName);
        });
    }

    [Fact]
    public void WorkerFailuresAreReportedOnDispatcher()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var work = new InventoryFilterWork(dispatcher);
            Exception? reported = null;
            await work.Start<int>(
                cancellation => throw new InvalidOperationException("query failed"),
                (value, cancellation) => Task.CompletedTask,
                error =>
                {
                    dispatcher.VerifyAccess();
                    reported = error;
                },
                false);
            Assert.IsType<InvalidOperationException>(reported);
        });
    }

    [Fact]
    public async Task DispatcherShutdownCancelsPendingDebounceWithoutPublishing()
    {
        Task? pending = null;
        bool published = false;
        InventoryFilterTestDispatcher.Run(() =>
        {
            var work = new InventoryFilterWork(Dispatcher.CurrentDispatcher);
            pending = work.Start(
                cancellation => "late",
                (value, cancellation) =>
                {
                    published = true;
                    return Task.CompletedTask;
                },
                error => throw error);
            return Task.CompletedTask;
        });
        Assert.NotNull(pending);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(published);
    }
}
