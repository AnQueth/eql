using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EqLog.Application.Inventory;
using EqLog.Application.Sessions;
using EqLog.Core;
using EqLog.Core.Tracking;
using EqLog.Overlay;
using EqLog.Overlay.Messaging;
using EqLog.Overlay.Views;
using EqLog.Overlay.ViewModels;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class MergeOverlayViewModelTests
{
    [Fact]
    public void FrameBuildsFilteredRankTablesAndRoutesItemActions()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var rows = new[]
            {
                new InventoryExportItem("Bank1", "Sword +3", 1, 1, InventoryStorage.Bank),
                new InventoryExportItem("Bank2", "Sword +1", 1, 2, InventoryStorage.Bank),
                new InventoryExportItem("Bank3", "Shield", 2, 1, InventoryStorage.Bank),
            };
            InventorySnapshot snapshot = Snapshot(1, "Que", rows);
            var actions = new RecordingActions();
            var definition = OverlayPresenters.All.Single(item => item.Key == "merge");
            var vm = new MergeOverlayViewModel(definition, actions, () => snapshot, () => "Que");
            vm.Receive(Frame("Que"));
            await vm.PendingFilter;

            var group = Assert.Single(vm.Groups);
            Assert.Equal("Sword", group.BaseName);
            Assert.Equal(3, group.Total);
            Assert.Single(group.Tables);
            Assert.Contains("1 item(s)", vm.Summary);
            vm.OpenMergeItemWikiCommand.Execute(group.BaseName);
            vm.LookupMergeQuestReferencesCommand.Execute(group.BaseName);
            Assert.Equal("Sword", Assert.Single(actions.OpenedItems));
            Assert.Equal("Sword", Assert.Single(actions.LookedUpItems));

            vm.Filter = "shield";
            await vm.PendingFilter;
            Assert.Empty(vm.Groups);
            Assert.Contains("0 match", vm.Summary);
        });
    }

    [Fact]
    public void HiddenOverlaySkipsFilteringUntilShownAgain()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var rows = new[]
            {
                new InventoryExportItem("Bank1", "Sword +3", 1, 1, InventoryStorage.Bank),
                new InventoryExportItem("Bank2", "Sword +1", 1, 2, InventoryStorage.Bank),
            };
            InventorySnapshot snapshot = Snapshot(1, "Que", rows);
            var definition = OverlayPresenters.All.Single(item => item.Key == "merge");
            var vm = new MergeOverlayViewModel(definition, new RecordingActions(), () => snapshot, () => "Que");
            vm.IsShowing = true;
            vm.IsShowing = false;

            vm.Refresh();
            await vm.PendingFilter;
            Assert.Empty(vm.Groups);

            vm.IsShowing = true;
            await vm.PendingFilter;
            Assert.Equal("Sword", Assert.Single(vm.Groups).BaseName);
        });
    }

    private static OverlayFrameMessage Frame(string character)
    {
        var engine = new LogEngine { CharacterName = character };
        var frame = new LogSessionFrame(engine.Snapshot(DateTime.UtcNow), [], false, EngineQuerySnapshot.Capture(engine));
        return new OverlayFrameMessage(frame, DateTime.UtcNow, new TailProgress(0, 0, 0, true));
    }

    private static InventorySnapshot Snapshot(long revision, string character, IReadOnlyList<InventoryExportItem> rows)
    {
        var state = new QuestInventory();
        DateTime written = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        state.ApplyExport(character, rows, written);
        return new InventorySnapshot(revision, state,
            new InventoryExportSnapshot(character, new InventoryFileStamp("export", rows.Count, written), rows));
    }

    private sealed class RecordingActions : IOverlayActions
    {
        public List<string> OpenedItems { get; } = [];
        public List<string> LookedUpItems { get; } = [];

        public void OpenLink(string url)
        {
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
