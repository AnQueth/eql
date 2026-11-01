using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class AsyncTextFilterTests
{
    [Fact]
    public Task TypingCoalescesAndEqualKeysDoNotRecompute()
    {
        return Run(async dispatcher =>
        {
            int queries = 0;
            using var work = new AsyncTextFilter<string>(dispatcher, error => throw error);
            work.Request("first", _ =>
            {
                Interlocked.Increment(ref queries);
                return "first";
            });
            work.Request("second", _ =>
            {
                Interlocked.Increment(ref queries);
                return "second";
            });
            work.Request("final", _ =>
            {
                Interlocked.Increment(ref queries);
                return "final";
            });
            await work.Completion;
            Assert.Equal("final", work.Result);
            Assert.Equal(1, queries);
            work.Request("final", _ => throw new InvalidOperationException("Must not repeat"));
            await work.Completion;
            Assert.Equal(1, queries);
        });
    }

    [Fact]
    public Task BusyQueryDoesNotBlockDispatcherAndSupersededResultsCannotPublish()
    {
        return Run(async dispatcher =>
        {
            using var release = new ManualResetEventSlim();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var applied = new List<string>();
            using var work = new AsyncTextFilter<string>(dispatcher, error => throw error);
            work.Request("old", _ =>
            {
                started.SetResult(dispatcher.CheckAccess());
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                return "old";
            }, applied.Add, delayMilliseconds: 0);
            Assert.False(await started.Task);
            Assert.True(dispatcher.CheckAccess());
            Task old = work.Completion;
            work.Request("new", _ => "new", value =>
            {
                Assert.True(dispatcher.CheckAccess());
                applied.Add(value);
            }, delayMilliseconds: 0);
            release.Set();
            await Task.WhenAll(old, work.Completion);
            Assert.Equal(["new"], applied);
            Assert.Equal("new", work.Result);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task QuestBuilderCancellation_IsHandledWithoutPublishingOrReportingErrors(bool disposing)
    {
        return Run(async dispatcher =>
        {
            using var release = new ManualResetEventSlim();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var errors = new List<Exception>();
            var applied = new List<OverlayContent>();
            using var work = new AsyncTextFilter<OverlayContent>(dispatcher, errors.Add);
            work.Request("old", token =>
            {
                started.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                return MainWindow.BuildQuestOpportunities(
                    EqLog.Application.Inventory.InventoryReadModel.Empty, "Old",
                    new Dictionary<string, EqLog.Core.QuestLookupResult>(), "", true, token);
            }, applied.Add, delayMilliseconds: 0);

            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Task old = work.Completion;
                if (disposing)
                {
                    work.Dispose();
                }
                else
                {
                    work.Request("new", token => MainWindow.BuildQuestOpportunities(
                        EqLog.Application.Inventory.InventoryReadModel.Empty, "New",
                        new Dictionary<string, EqLog.Core.QuestLookupResult>(), "", true, token),
                        applied.Add, delayMilliseconds: 0);
                }

                release.Set();
                await Task.WhenAll(old, work.Completion);
                Assert.True(old.IsCompletedSuccessfully);
                Assert.Empty(errors);
                if (disposing)
                {
                    Assert.Empty(applied);
                    Assert.Null(work.Result);
                }
                else
                {
                    Assert.Equal("New", Assert.Single(applied).Subtitle);
                    Assert.Same(applied[0], work.Result);
                }
            }
            finally
            {
                release.Set();
            }
        });
    }

    [Fact]
    public Task UnrelatedCancellation_IsNotSilentlyDiscarded()
    {
        return Run(async dispatcher =>
        {
            var failure = new OperationCanceledException("Not canceled by this filter");
            using var work = new AsyncTextFilter<string>(dispatcher, error => throw error);
            work.Request("failed", _ => throw failure, delayMilliseconds: 0);

            var actual = await Assert.ThrowsAsync<OperationCanceledException>(() => work.Completion);

            Assert.Same(failure, actual);
            Assert.Null(work.Result);
        });
    }

    [Fact]
    public Task DisposingCancelsPendingPublication()
    {
        return Run(async dispatcher =>
        {
            int queries = 0;
            var work = new AsyncTextFilter<string>(dispatcher, error => throw error);
            work.Request("pending", _ =>
            {
                queries++;
                return "pending";
            });
            work.Dispose();
            await work.Completion;
            Assert.Null(work.Result);
            Assert.Equal(0, queries);
        });
    }

    [Fact]
    public Task FailuresAreReportedOnDispatcher()
    {
        return Run(async dispatcher =>
        {
            Exception? reported = null;
            using var work = new AsyncTextFilter<string>(dispatcher, error =>
            {
                Assert.True(dispatcher.CheckAccess());
                reported = error;
            });
            work.Request("failed", _ => throw new InvalidOperationException("Invalid filter"), delayMilliseconds: 0);
            await work.Completion;
            Assert.Equal("Invalid filter", reported?.Message);
            Assert.Null(work.Result);
        });
    }

    [Fact]
    public Task CharacterChangesClearPreviousContentWhileRefilteringRetainsIt()
    {
        return Run(async dispatcher =>
        {
            using var work = new AsyncTextFilter<string>(dispatcher, error => throw error);
            work.Request("first", _ => "First character", delayMilliseconds: 0, context: "Que");
            await work.Completion;
            work.Request("filter", _ => "Filtered", context: "Que");
            Assert.Equal("First character", work.Result);
            await work.Completion;
            work.Request("character", _ => "Other character", context: "Other");
            Assert.Null(work.Result);
            await work.Completion;
            Assert.Equal("Other character", work.Result);
        });
    }

    [Fact]
    public void LargeRowReplacementUsesOneNotification()
    {
        var rows = new OverlayRowCollection();
        int resets = 0;
        rows.CollectionChanged += (_, args) =>
        {
            Assert.Equal(NotifyCollectionChangedAction.Reset, args.Action);
            resets++;
        };
        rows.ReplaceAll(Enumerable.Range(0, 5000).Select(i => new OverlayRow($"Item {i}")).ToArray());
        Assert.Equal(5000, rows.Count);
        Assert.Equal("Item 4999", rows[4999].Left);
        Assert.Equal(1, resets);
    }

    private static async Task Run(Func<Dispatcher, Task> action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await action(dispatcher);
                    completed.SetResult();
                }
                catch (Exception error)
                {
                    completed.SetException(error);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }

    internal static void VerifyQuestVirtualization()
    {
        var panel = new OverlayPanel("quests", "Quests", false);
        var rows = Enumerable.Range(0, 5000).Select(i => new OverlayRow($"Item {i}")).ToArray();
        panel.Update("Quests", null, rows);
        Layout(panel);
        var list = FindList(panel);
        Assert.NotNull(list);
        Assert.Equal(5000, list.Items.Count);
        Assert.True(VirtualizingPanel.GetIsVirtualizing(list));
        Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(4999));
        list.ScrollIntoView(list.Items[4000]);
        Layout(panel);
        Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(4000));
        int resets = 0;
        var collection = Assert.IsAssignableFrom<INotifyCollectionChanged>(list.ItemsSource);
        collection.CollectionChanged += (_, _) => resets++;
        panel.Update("Quests", null, rows);
        Assert.Equal(0, resets);
        panel.Update("Quests", null, rows.Take(100).ToArray());
        Layout(panel);
        Assert.Equal(1, resets);
        Assert.Equal(100, list.Items.Count);
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(500, 400));
        element.Arrange(new Rect(0, 0, 500, 400));
        element.UpdateLayout();
    }

    private static ListBox? FindList(DependencyObject root)
    {
        if (root is ListBox list)
        {
            return list;
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindList(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }
        return null;
    }
}
