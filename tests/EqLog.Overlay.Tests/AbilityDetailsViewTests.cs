using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using EqLog.Core.Tracking;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class AbilityDetailsViewTests
{
    private static AbilityStats Row(string name, long total)
    {
        return new AbilityStats(name, total, total / 10.0, 0, 1, 0, total, 0);
    }

    private static AbilityBreakdown Breakdown(params AbilityStats[] rows)
    {
        return new AbilityBreakdown("Fight", "", "DPS", rows, Timeline: [], TimelineSeconds: 10, TimelineStart: DateTime.Now);
    }

    [Fact]
    public void TryUpdate_KeepsGridColumnsSortAndSelectedTab()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new AbilityDetailsView(Breakdown(Row("Melee", 100), Row("Fireball", 50)));
                var tabs = Assert.IsType<TabControl>(view.Root);
                var table = Assert.IsType<DataGrid>(((TabItem)tabs.Items[0]).Content);
                var rows = table.ItemsSource;
                var totalColumn = table.Columns[1];
                totalColumn.Width = 222;
                table.Items.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
                tabs.SelectedIndex = 1;

                Assert.True(view.TryUpdate(Breakdown(Row("Melee", 300), Row("Backstab", 200))));

                Assert.Same(view.Root, tabs);
                Assert.Same(table, ((TabItem)tabs.Items[0]).Content);
                Assert.Same(rows, table.ItemsSource);
                Assert.Same(totalColumn, table.Columns[1]);
                Assert.Equal(222, totalColumn.Width.Value);
                Assert.Equal(1, tabs.SelectedIndex);
                Assert.Single(table.Items.SortDescriptions);
                Assert.Equal(["Backstab", "Melee"], table.Items.Cast<AbilityStats>().Select(r => r.Name));
                Assert.Equal(300, table.Items.Cast<AbilityStats>().Single(r => r.Name == "Melee").Total);
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

    [Fact]
    public void TryUpdate_RequestsRebuildWhenLayoutChanges()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new AbilityDetailsView(new AbilityBreakdown("No fight yet", "", "DPS", []));
                Assert.IsType<DataGrid>(view.Root);
                Assert.False(view.TryUpdate(Breakdown(Row("Melee", 1))));
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
