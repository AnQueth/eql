using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EqLog.Application.Inventory;
using EqLog.Core;
using EqLog.Overlay;
using EqLog.Overlay.ViewModels;
using EqLog.Overlay.Views;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class InventoryOverlayViewModelTests
{
    [Fact]
    public void SelectionCommandsOpenWikiAndLookUpQuestReferences()
    {
        InventoryFilterTestDispatcher.Run(() =>
        {
            var inventory = new InventoryViewModel();
            inventory.Apply(Snapshot(), "Que");
            var actions = new RecordingActions();
            var definition = OverlayPresenters.All.Single(item => item.Key == "inventory");
            var vm = new InventoryOverlayViewModel(definition, actions, inventory);
            inventory.SelectedItem = inventory.Items[0];

            vm.OpenSelectedItemWikiCommand.Execute(null);
            vm.LookupSelectedQuestReferencesCommand.Execute(null);

            Assert.Equal("Sword", Assert.Single(actions.OpenedItems));
            Assert.Equal("Sword", actions.LookedUpItems.Last());
            Assert.True(vm.LookupSelectedQuestReferencesCommand.CanExecute(null));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void SelectingAnItemLoadsQuestReferencesIntoObservablePresentationState()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var inventory = new InventoryViewModel();
            inventory.Apply(Snapshot(), "Que");
            var actions = new RecordingActions();
            var definition = OverlayPresenters.All.Single(item => item.Key == "inventory");
            bool completionSaved = false;
            bool lookupsRetried = false;
            var vm = new InventoryOverlayViewModel(definition, actions, inventory,
                (item, cancellationToken) => Task.FromResult(new QuestLookupResult(item, DateTime.UtcNow,
                    [new QuestReference("A Test Quest", "https://example.test/quest")])),
                quest =>
                {
                    completionSaved = quest == "A Test Quest";
                    return Task.FromResult("Quest marked completed.");
                },
                () => lookupsRetried = true);

            vm.LookupQuestItemCommand.Execute("Sword");
            await vm.PendingQuestLookup;

            Assert.Equal("A Test Quest", Assert.Single(vm.QuestReferences).Title);
            Assert.Contains("1 quest reference", vm.QuestStatus);
            Assert.Equal(1, vm.SelectedDetailsTabIndex);
            vm.SelectedQuestReference = vm.QuestReferences[0];
            vm.OpenSelectedQuestCommand.Execute(null);
            vm.ToggleQuestCompletionCommand.Execute(null);
            await vm.PendingQuestCompletion;
            vm.RetryQuestLookupsCommand.Execute(null);
            await vm.PendingQuestLookup;
            Assert.True(completionSaved);
            Assert.True(lookupsRetried);
            Assert.Equal("https://example.test/quest", Assert.Single(actions.OpenedLinks));
        });
    }

    [Fact]
    public void DisposedOverlayDoesNotObserveSelectionAfterItsReplacement()
    {
        InventoryFilterTestDispatcher.Run(() =>
        {
            var inventory = new InventoryViewModel();
            inventory.Apply(Snapshot(), "Que");
            var oldActions = new RecordingActions();
            var currentActions = new RecordingActions();
            var definition = OverlayPresenters.All.Single(item => item.Key == "inventory");
            var oldViewModel = new InventoryOverlayViewModel(definition, oldActions, inventory);
            oldViewModel.Dispose();
            using var currentViewModel = new InventoryOverlayViewModel(definition, currentActions, inventory);

            inventory.SelectedItem = inventory.Items[0];

            Assert.Empty(oldActions.LookedUpItems);
            Assert.Equal("Sword", Assert.Single(currentActions.LookedUpItems));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void HidingTheOverlayPausesInventoryFilteringUntilShown()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var inventory = new InventoryViewModel();
            inventory.Apply(Snapshot(), "Que");
            var definition = OverlayPresenters.All.Single(item => item.Key == "inventory");
            var vm = new InventoryOverlayViewModel(definition, new RecordingActions(), inventory);
            vm.IsShowing = true;
            vm.IsShowing = false;

            inventory.Filter = "missing";
            await inventory.PendingFilter;
            Assert.Single(inventory.ItemsView.Cast<QuestInventoryItem>());

            vm.IsShowing = true;
            await inventory.PendingFilter;
            Assert.Empty(inventory.ItemsView.Cast<QuestInventoryItem>());
        });
    }

    private static InventorySnapshot Snapshot()
    {
        var rows = new[] { new InventoryExportItem("Held", "Sword", 1, 1, InventoryStorage.Carried) };
        var state = new QuestInventory();
        DateTime written = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        state.ApplyExport("Que", rows, written);
        return new InventorySnapshot(1, state,
            new InventoryExportSnapshot("Que", new InventoryFileStamp("export", rows.Length, written), rows));
    }

    private sealed class RecordingActions : IOverlayActions
    {
        public List<string> OpenedItems { get; } = [];
        public List<string> LookedUpItems { get; } = [];
        public List<string> OpenedLinks { get; } = [];

        public void OpenLink(string url)
        {
            OpenedLinks.Add(url);
        }

        public void OpenItemWiki(string item)
        {
            OpenedItems.Add(item);
        }

        public void LookupQuestReferences(string item)
        {
            LookedUpItems.Add(item);
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
