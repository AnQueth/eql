using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Data;
using EqLog.Application.Inventory;
using EqLog.Core;
using EqLog.Overlay.ViewModels;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class InventoryViewModelTests
{
    [Fact]
    public void DetachedBindingsPreserveSelectionAcrossRanksAndFiltersAndRefreshAllCopies()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var model = new InventoryViewModel();
            var snapshot = Snapshot(1, "Que",
            [
                new("Held", "Sword +3", 1, 2, InventoryStorage.Carried),
                new("Equipment", "Sword +9", 1, 1, InventoryStorage.Other, Collection: true),
                new("Bank1", "Other item", 2, 1, InventoryStorage.Bank),
            ]);
            model.Apply(snapshot, "Que");
            var selected = model.Items[0];
            model.SelectedItem = selected;
            Assert.Equal("Sword +9", selected.Item);
            Assert.Equal(3, model.Copies.Sum(copy => copy.Quantity));
            Assert.Contains("3 copies", model.CopyStatus);
            model.Filter = "  sWoRd ";
            Assert.Equal(2, model.ItemsView.Cast<QuestInventoryItem>().Count());
            await model.PendingFilter;
            Assert.Single(model.ItemsView.Cast<QuestInventoryItem>());
            Assert.Equal(2, model.Items.Count);
            selected.HasQuestReferences = true;
            model.Apply(Snapshot(2, "Que",
            [
                new("Bank1", "Sword +10", 1, 1, InventoryStorage.Bank),
                new("Held", "Other item", 2, 1, InventoryStorage.Carried),
            ]), "Que");
            await model.PendingFilter;
            Assert.Same(selected, model.SelectedItem);
            Assert.True(selected.HasQuestReferences);
            Assert.Equal("Sword +10", selected.Item);
            Assert.Equal(1, selected.Owned);
            Assert.Equal("Sword +10", Assert.Single(model.Copies).Item);
            Assert.Contains("Bank slot 1", model.Copies[0].Location);
            Assert.Contains("1 copies", model.CopyStatus);
            Assert.Equal("Sword +9", snapshot.State.Items[0].Item);
            model.Filter = "missing";
            await model.PendingFilter;
            Assert.Empty(model.ItemsView.Cast<QuestInventoryItem>());
            Assert.Same(selected, model.SelectedItem);
            model.Filter = "";
            await model.PendingFilter;
            Assert.Equal(2, model.ItemsView.Cast<QuestInventoryItem>().Count());
        });
    }

    [Fact]
    public void NewestFilterAndSourceSessionWinAndUnloadCancels()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var model = new InventoryViewModel();
            model.Apply(Snapshot(1, "Que",
            [
                new("Held", "Sword", 1, 1, InventoryStorage.Carried),
                new("Bank1", "Shield", 2, 1, InventoryStorage.Bank),
            ]), "Que");
            model.Filter = "sword";
            var superseded = model.PendingFilter;
            model.Filter = "sHiElD";
            await model.PendingFilter;
            await superseded;
            Assert.Equal("Shield", Assert.Single(model.ItemsView.Cast<QuestInventoryItem>()).Item);

            model.Filter = "sword";
            superseded = model.PendingFilter;
            model.Apply(Snapshot(2, "Other",
            [
                new("Held", "Other sword", 1, 7, InventoryStorage.Carried),
            ]), "Other");
            await model.PendingFilter;
            await superseded;
            Assert.Equal("Other sword", Assert.Single(model.ItemsView.Cast<QuestInventoryItem>()).Item);
            Assert.Equal(7, model.Items[0].Owned);

            model.Filter = "missing";
            var canceled = model.PendingFilter;
            model.SuspendFilters();
            await canceled;
            Assert.Single(model.ItemsView.Cast<QuestInventoryItem>());
            model.ResumeFilters();
            await model.PendingFilter;
            Assert.Empty(model.ItemsView.Cast<QuestInventoryItem>());
        });
    }

    [Fact]
    public void BoundGridFilteringKeepsDetachedSelectionAndCopyCounts()
    {
        InventoryFilterTestDispatcher.Run(async () =>
        {
            var model = new InventoryViewModel();
            model.Apply(Snapshot(1, "Que",
            [
                new("Held", "Sword", 1, 3, InventoryStorage.Carried),
                new("Bank1", "Shield", 2, 1, InventoryStorage.Bank),
            ]), "Que");
            var grid = new DataGrid
            {
                DataContext = model,
                ItemsSource = model.ItemsView,
                CanUserAddRows = false,
            };
            grid.SetBinding(DataGrid.SelectedItemProperty, new Binding(nameof(model.SelectedItem))
            {
                Mode = BindingMode.TwoWay,
            });
            var selected = model.Items.Single(item => item.Item == "Sword");
            grid.SelectedItem = selected;
            model.Filter = "shield";
            await model.PendingFilter;
            Assert.Same(selected, model.SelectedItem);
            Assert.Equal(3, model.Copies.Sum(copy => copy.Quantity));
            Assert.Equal("Shield", Assert.Single(grid.Items.Cast<QuestInventoryItem>()).Item);
            model.Filter = "";
            await model.PendingFilter;
            Assert.Same(selected, model.SelectedItem);
            Assert.Same(selected, grid.SelectedItem);
            Assert.Equal(2, grid.Items.Count);

            await Task.Run(() =>
            {
                Assert.Throws<InvalidOperationException>(() => model.Filter = "wrong thread");
            });
        });
    }

    [Fact]
    public void CharacterSwitchAndMissingExportCannotShowAnotherCharactersCopies()
    {
        OnSta(() =>
        {
            var model = new InventoryViewModel();
            var initial = Snapshot(1, "Que", [new("Held", "Sword", 1, 1, InventoryStorage.Carried)]);
            model.Apply(initial, "Que");
            model.SelectedItem = model.Items[0];
            var state = initial.State.Clone();
            model.Apply(new(2, state, null), "Que");
            Assert.Empty(model.Copies);
            Assert.Equal(1, model.Items[0].Owned);
            Assert.Contains("Waiting", model.CopyStatus);
            model.Apply(Snapshot(3, "Other", [new("Bank1", "Sword", 1, 5, InventoryStorage.Bank)]), "Other");
            Assert.Null(model.SelectedItem);
            Assert.Empty(model.Copies);
            model.SelectedItem = model.Items[0];
            Assert.Equal(5, model.Copies.Sum(copy => copy.Quantity));
        });
    }

    private static InventorySnapshot Snapshot(long revision, string character, InventoryExportItem[] rows)
    {
        var at = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(revision);
        var state = new QuestInventory();
        state.ApplyExport(character, rows, at);
        return new(revision, state, new(character, new("export", rows.Length, at), Array.AsReadOnly(rows)));
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
