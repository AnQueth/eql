using System.Windows.Threading;
using EqLog.Application.Inventory;
using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Overlay.Messaging;
using EqLog.Overlay.ViewModels;
using Xunit;

namespace EqLog.Overlay.Tests;

/// <summary>Verifies Sky filter, class, view, watch and command behavior.</summary>
public sealed class SkyOverlayViewModelTests
{
    /// <summary>Commands execute shell actions, and settings bindings preserve the legacy tab order.</summary>
    [Fact]
    public void CommandsPersistSkySettingsAndKeepTabOrder()
    {
        RunOnDispatcher(dispatcher =>
        {
            var actions = new RecordingActions();
            bool? refreshed = null;
            string? changedFilter = null;
            string? changedClass = null;
            int? changedView = null;
            IReadOnlyList<string>? persistedWatches = null;
            var viewModel = new SkyOverlayViewModel(
                OverlayPresenters.All.Single(definition => definition.Key == "sky"),
                actions,
                dispatcher,
                () => new SkyOverlayState(InventoryReadModel.Empty, "Que", null, false, "", 0),
                (refresh, _) =>
                {
                    refreshed = refresh;
                    return Task.CompletedTask;
                },
                () => false,
                "",
                "All classes",
                2,
                [],
                filter => changedFilter = filter,
                selectedClass => changedClass = selectedClass,
                view => changedView = view,
                keys => persistedWatches = keys,
                _ => { },
                () => { });

            viewModel.FilterText = "rune";
            viewModel.SelectedClass = "Wizard";
            Assert.Equal(3, viewModel.SelectedTabIndex);
            viewModel.SelectedTabIndex = 2;
            viewModel.OpenWikiCommand.Execute(null);
            viewModel.RefreshCommand.Execute(null);
            viewModel.RefreshCommand.Completion.GetAwaiter().GetResult();
            viewModel.Show("Sky", null, [new OverlayRow("Quest", WatchKey: "Wizard|Test")]);
            viewModel.ToggleWatchCommand.Execute(viewModel.Rows.Single());

            Assert.Equal("rune", changedFilter);
            Assert.Equal("Wizard", changedClass);
            Assert.Equal(3, changedView);
            Assert.Equal(true, refreshed);
            Assert.Equal(SkyQueries.SourceUrl, actions.OpenedUrl);
            Assert.Equal(["Wizard|Test"], persistedWatches);
            Assert.Contains("Wizard|Test", viewModel.WatchedQuestKeys);
            Assert.Equal(2, viewModel.SelectedTabIndex);
            viewModel.Dispose();
        });
    }

    /// <summary>Class choices are sorted and case-insensitive matches retain canonical spelling.</summary>
    [Fact]
    public void ClassOptionsAreSortedAndSelectionUsesCanonicalSpelling()
    {
        var catalog = new SkyQuestCatalog(DateTime.UtcNow,
            [Quest("Wizard"), Quest("Bard"), Quest("wizard")]);

        Assert.Equal(["All classes", "Bard", "Wizard"], SkyOverlayViewModel.BuildClassOptions(catalog, "wizard"));
    }

    /// <summary>Class options refresh for catalog and selection changes while preserving valid selections.</summary>
    [Fact]
    public void FramesRefreshClassOptionsForCatalogAndSelectionChanges()
    {
        RunOnDispatcher(dispatcher =>
        {
            SkyOverlayState state = new(InventoryReadModel.Empty, "Que", null, false, "", 0);
            var viewModel = CreateViewModel(dispatcher, () => state, selectedClass: "wizard");

            Assert.Equal(["All classes", "wizard"], viewModel.ClassOptions);

            viewModel.Receive(Frame());
            state = state with
            {
                Catalog = new SkyQuestCatalog(DateTime.UtcNow, [Quest("Wizard"), Quest("Bard")]),
            };
            viewModel.Receive(Frame());

            Assert.Equal(["All classes", "Bard", "Wizard"], viewModel.ClassOptions);
            Assert.Equal("Wizard", viewModel.SelectedClass);

            state = state with
            {
                Catalog = new SkyQuestCatalog(DateTime.UtcNow, [Quest("Bard")]),
            };
            viewModel.Receive(Frame());

            Assert.Equal(["All classes", "Bard"], viewModel.ClassOptions);
            Assert.Equal("All classes", viewModel.SelectedClass);

            state = state with
            {
                Catalog = null,
            };
            viewModel.SelectedClass = "Cleric";
            viewModel.Receive(Frame());

            Assert.Equal(["All classes", "Cleric"], viewModel.ClassOptions);
            Assert.Equal("Cleric", viewModel.SelectedClass);
            viewModel.Dispose();
        });
    }

    /// <summary>Watched quest snapshots and filter keys follow collection changes.</summary>
    [Fact]
    public void WatchedSnapshotRefreshesWhenCollectionChanges()
    {
        RunOnDispatcher(dispatcher =>
        {
            SkyQuest quest = Quest("Wizard");
            SkyOverlayState state = new(
                InventoryReadModel.Empty,
                "Que",
                new SkyQuestCatalog(DateTime.UtcNow, [quest]),
                false,
                "",
                0);
            var viewModel = CreateViewModel(dispatcher, () => state);
            OverlayFrameMessage frame = Frame();

            viewModel.Receive(frame);
            WaitForDispatcherTask(dispatcher, viewModel.FilterCompletion);
            viewModel.Receive(frame);
            Assert.False(GetQuestRow(viewModel, quest).Watched);

            viewModel.WatchedQuestKeys.Add(SkyQueries.Key(quest));
            viewModel.Receive(frame);
            WaitForDispatcherTask(dispatcher, viewModel.FilterCompletion);
            viewModel.Receive(frame);
            Assert.True(GetQuestRow(viewModel, quest).Watched);

            viewModel.WatchedQuestKeys.RemoveAt(0);
            viewModel.Receive(frame);
            WaitForDispatcherTask(dispatcher, viewModel.FilterCompletion);
            viewModel.Receive(frame);
            Assert.False(GetQuestRow(viewModel, quest).Watched);
            viewModel.Dispose();
        });
    }

    private static SkyQuest Quest(string className)
    {
        return new SkyQuest(className, "Test", "Reward", "Giver", "", "", "", "url");
    }

    private static SkyOverlayViewModel CreateViewModel(
        Dispatcher dispatcher,
        Func<SkyOverlayState> getState,
        string selectedClass = "All classes")
    {
        return new SkyOverlayViewModel(
            OverlayPresenters.All.Single(definition => definition.Key == "sky"),
            new RecordingActions(),
            dispatcher,
            getState,
            (_, _) => Task.CompletedTask,
            () => false,
            "",
            selectedClass,
            0,
            [],
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });
    }

    private static OverlayRow GetQuestRow(SkyOverlayViewModel viewModel, SkyQuest quest)
    {
        return Assert.Single(viewModel.Rows, row => row.Row.WatchKey == SkyQueries.Key(quest)).Row;
    }

    private static OverlayFrameMessage Frame()
    {
        DateTime now = DateTime.UtcNow;
        var engine = new LogEngine();
        var logFrame = new LogSessionFrame(engine.Snapshot(now), [], false, EngineQuerySnapshot.Capture(engine));
        return new OverlayFrameMessage(logFrame, now, new TailProgress(0, 0, 0, true));
    }

    private static void WaitForDispatcherTask(Dispatcher dispatcher, Task task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(
            _ =>
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    frame.Continue = false;
                }));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
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

    private sealed class RecordingActions : IOverlayActions
    {
        public string? OpenedUrl
        {
            get;
            private set;
        }

        public void OpenLink(string url)
        {
            OpenedUrl = url;
        }

        public void OpenItemWiki(string item)
        {
        }

        public void LookupQuestReferences(string item)
        {
        }

        public bool HasQuestReferences(string item)
        {
            return false;
        }

        public void ShowBreakdown(AbilityBreakdown breakdown)
        {
        }

        public void ToggleDock(OverlayViewModel overlay)
        {
        }

        public void MoveToWorkspace(OverlayViewModel overlay, string workspaceId)
        {
        }
    }
}
