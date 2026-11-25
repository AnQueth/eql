using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EqLog.Application.Inventory;
using EqLog.Application.Motes;
using EqLog.Application.Sessions;
using EqLog.Core;
using EqLog.Core.Tracking;
using EqLog.Overlay;
using EqLog.Overlay.Messaging;
using EqLog.Overlay.Views;
using EqLog.Overlay.ViewModels;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class MotePlanOverlayViewModelTests
{
    [Fact]
    public void FrameLoadsInventoryNamesAndSelectingAnItemUsesItsBestRank()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var rows = new[]
            {
                new InventoryExportItem("Held", "Sword +3", 1, 1, InventoryStorage.Carried),
                new InventoryExportItem("Bank1", "Sword +7", 2, 1, InventoryStorage.Bank),
                new InventoryExportItem("Bank2", "Shield +2", 3, 1, InventoryStorage.Bank),
            };
            InventorySnapshot snapshot = Snapshot(1, "Que", rows);
            var vm = CreateViewModel(() => snapshot);
            vm.Receive(Frame("Que"));
            await vm.PendingTargetFilter;

            Assert.Contains("Sword", vm.InventoryNames);
            Assert.Contains("Shield", vm.InventoryNames);
            vm.SelectedInventoryName = "Sword";

            Assert.Equal(7, vm.CurrentRank);
            Assert.Equal(10, vm.TargetRank);
            Assert.Equal("Sword", vm.Plan?.ItemName);
            Assert.True(vm.Plan?.HasInventoryExport);
            Assert.Contains(vm.PlanBlocks, block => block.Kind == MotePlanBlockKind.Heading);

            vm.Page = MotePlanPage.Farm;
            Assert.Contains(vm.PlanBlocks, block => block.Kind == MotePlanBlockKind.Comparison);
            Assert.Contains(vm.PlanBlocks, block => block.Text.Contains("TOTAL", StringComparison.Ordinal));
            Assert.Contains(vm.PlanBlocks, block => block.Kind == MotePlanBlockKind.Table);

            vm.Page = MotePlanPage.Math;
            Assert.Contains(vm.PlanBlocks, block => block.Kind == MotePlanBlockKind.Table && block.ToolTip is not null);
        });
    }

    [Fact]
    public void FilterAndCommandsUpdateTheCalculatedPlan()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var rows = new[]
            {
                new InventoryExportItem("Bank1", "Sword +3", 1, 1, InventoryStorage.Bank),
                new InventoryExportItem("Bank2", "Shield +2", 2, 1, InventoryStorage.Bank),
            };
            InventorySnapshot snapshot = Snapshot(1, "Que", rows);
            var vm = CreateViewModel(() => snapshot);
            vm.Receive(Frame("Que"));
            await vm.PendingTargetFilter;
            vm.Name = "shield";
            await vm.PendingTargetFilter;

            Assert.Equal("Shield", Assert.Single(vm.InventoryNames));
            vm.SetUseMotesCommand.Execute(false);
            vm.SetFeedingCommand.Execute(true);

            Assert.False(vm.UseMotes);
            Assert.True(vm.Feeding);
            Assert.Empty(vm.Plan!.MotesUsed);
            Assert.Equal(MoteFeedingStatus.NotBeneficial, vm.Plan.FeedingStatus);
        });
    }

    [Fact]
    public void RankChangesClampTargetAndNotifyDependentState()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            InventorySnapshot snapshot = Snapshot(1, "Que", []);
            var vm = CreateViewModel(() => snapshot);
            vm.Receive(Frame("Que"));
            await vm.PendingTargetFilter;
            vm.TargetRank = 7;
            vm.CurrentRank = 8;

            Assert.Equal(8, vm.TargetRank);
            Assert.Equal([8, 9, 10], vm.TargetRanks);
            Assert.True(vm.CanAddProgress);
            vm.SelectedPageIndex = (int)MotePlanPage.Math;
            Assert.Equal(MotePlanPage.Math, vm.Page);
        });
    }

    private static MotePlanOverlayViewModel CreateViewModel(Func<InventorySnapshot> snapshot)
    {
        var definition = OverlayPresenters.All.Single(item => item.Key == "moteplan");
        return new MotePlanOverlayViewModel(definition, NullOverlayActions.Instance, snapshot);
    }

    private static OverlayFrameMessage Frame(string character)
    {
        var engine = new LogEngine { CharacterName = character };
        var logFrame = new LogSessionFrame(engine.Snapshot(DateTime.UtcNow), [], false, EngineQuerySnapshot.Capture(engine));
        return new OverlayFrameMessage(logFrame, DateTime.UtcNow, new TailProgress(0, 0, 0, true));
    }

    private static InventorySnapshot Snapshot(long revision, string character, IReadOnlyList<InventoryExportItem> rows)
    {
        var state = new QuestInventory();
        DateTime written = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        state.ApplyExport(character, rows, written);
        return new InventorySnapshot(revision, state,
            new InventoryExportSnapshot(character, new InventoryFileStamp("export", rows.Count, written), rows));
    }
}

