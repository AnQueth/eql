using System.Windows.Threading;
using EqLog.Application.Inventory;
using EqLog.Core;
using EqLog.Overlay.ViewModels;
using Xunit;

namespace EqLog.Overlay.Tests;

/// <summary>Verifies quest filter, sorting and lookup progress state.</summary>
public sealed class QuestsOverlayViewModelTests
{
    /// <summary>Filter and sort changes persist, and progress values notify bindings.</summary>
    [Fact]
    public void FilterSortAndProgressStateNotifyAndPersistThroughCallbacks()
    {
        RunOnDispatcher(dispatcher =>
        {
            string? changedFilter = null;
            bool? changedSort = null;
            var viewModel = CreateViewModel(dispatcher, filter => changedFilter = filter, recent => changedSort = recent);
            var changedProperties = new List<string?>();
            viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

            viewModel.FilterText = " mask ";
            viewModel.SortIndex = 1;
            viewModel.UpdateProgress(8, 5, 3, 2, 1, "summary", "Looking up mask...");

            Assert.Equal(" mask ", changedFilter);
            Assert.False(changedSort);
            Assert.Equal(1, viewModel.SortIndex);
            Assert.Equal(8, viewModel.ProgressMaximum);
            Assert.Equal(5, viewModel.ProgressValue);
            Assert.Equal("Looking up mask...", viewModel.CurrentStatus);
            Assert.Equal(["Checked", "Done", "Cached", "Queued", "Failed"], viewModel.ProgressTable!.Header);
            Assert.Equal(["5 / 8", "63%", "3", "2", "1"], viewModel.ProgressTable.Rows[0]);
            Assert.Contains(nameof(QuestsOverlayViewModel.FilterText), changedProperties);
            Assert.Contains(nameof(QuestsOverlayViewModel.ProgressValue), changedProperties);
            viewModel.Dispose();
        });
    }

    /// <summary>Equal progress updates retain the existing table value.</summary>
    [Fact]
    public void RepeatedProgressDoesNotResetUnchangedMetrics()
    {
        RunOnDispatcher(dispatcher =>
        {
            var viewModel = CreateViewModel(dispatcher, _ => { }, _ => { });
            viewModel.UpdateProgress(3, 2, 1, 1, 0, "summary", "status");
            int notifications = 0;
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(QuestsOverlayViewModel.ProgressTable))
                {
                    notifications++;
                }
            };

            viewModel.UpdateProgress(3, 2, 1, 1, 0, "summary", "status");

            Assert.Equal(0, notifications);
            Assert.Equal(3, viewModel.ProgressMaximum);
            Assert.Equal("status", viewModel.CurrentStatus);
            viewModel.Dispose();
        });
    }

    private static QuestsOverlayViewModel CreateViewModel(
        Dispatcher dispatcher,
        Action<string> filterChanged,
        Action<bool> sortChanged)
    {
        return new QuestsOverlayViewModel(
            OverlayPresenters.All.Single(definition => definition.Key == "quests"),
            NullOverlayActions.Instance,
            dispatcher,
            () => new QuestsOverlayState(InventoryReadModel.Empty, "Que",
                new Dictionary<string, QuestLookupResult>(), 0, 0),
            "",
            true,
            filterChanged,
            sortChanged);
    }

    private static void RunOnDispatcher(Action<Dispatcher> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                action(dispatcher);
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                dispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
