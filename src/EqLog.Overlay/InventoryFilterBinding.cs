using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Data;
using EqLog.Core;

namespace EqLog.Overlay;

/// <summary>Compatibility binding for inventory grids not backed by InventoryViewModel.</summary>
internal static class InventoryFilterBinding
{
    public static InventoryFilterWork Attach(DataGrid grid, TextBox filter)
    {
        grid.Dispatcher.VerifyAccess();
        var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
        var work = new InventoryFilterWork(grid.Dispatcher);
        HashSet<QuestInventoryItem>? matches = null;
        bool suspended = false;
        view.Filter = item => item is QuestInventoryItem row &&
            (matches is null || matches.Contains(row));
        filter.TextChanged += (_, _) => Refresh(true);
        if (grid.ItemsSource is INotifyCollectionChanged changes)
        {
            changes.CollectionChanged += (_, _) => Refresh(false);
        }
        grid.Unloaded += (_, _) =>
        {
            suspended = true;
            work.Cancel();
        };
        grid.Loaded += (_, _) =>
        {
            suspended = false;
            Refresh(false);
        };
        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            Refresh(false);
        }
        return work;

        void Refresh(bool debounce)
        {
            grid.Dispatcher.VerifyAccess();
            work.Cancel();
            if (suspended)
            {
                return;
            }
            var rows = grid.ItemsSource.Cast<QuestInventoryItem>().ToArray();
            var names = rows.Select(row => row.Item).ToArray();
            string text = filter.Text;
            work.Start(
                cancellation => InventoryFilterWork.MatchNames(names, text, cancellation),
                (indices, cancellation) =>
                {
                    matches = indices.Select(index => rows[index]).ToHashSet();
                    view.Refresh();
                    return Task.CompletedTask;
                },
                error =>
                {
                    filter.ToolTip = $"Inventory filter failed: {error.Message}";
                    UsageTelemetry.TrackError(error, UsageErrorSource.InventoryImport);
                },
                debounce);
        }
    }
}
