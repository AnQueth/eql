using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class InventoryLayoutTests
{
    [Fact]
    public void InventorySplitter_ResizesWholeQuestPane_AndPreservesMinimumHeights()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                VerifySplitter();
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

    private static void VerifySplitter()
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        var inventory = document.Descendants(presentation + "DataGrid")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "QuestOwnedGrid");
        Assert.Contains(inventory.Descendants(presentation + "Hyperlink"),
            link => (string?)link.Attribute("Click") == "InventoryItem_Click");
        var layout = new XElement(Assert.IsType<XElement>(inventory.Parent));
        XNamespace local = "clr-namespace:EqLog.Overlay;assembly=EqLogOverlay";
        foreach (var element in layout.Descendants()
            .Where(element => element.Name.NamespaceName == "clr-namespace:EqLog.Overlay"))
        {
            element.Name = local + element.Name.LocalName;
        }
        layout.SetAttributeValue(XNamespace.Xmlns + "x", xaml.NamespaceName);
        layout.SetAttributeValue(XNamespace.Xmlns + "local", local.NamespaceName);
        // Standalone XAML has no controller instance to receive these handlers.
        foreach (var attribute in layout.DescendantsAndSelf().Attributes()
            .Where(attribute => attribute.Name.LocalName is "Click" or "SelectionChanged" or "CellEditEnding").ToArray())
        {
            attribute.Remove();
        }

        var grid = Assert.IsType<Grid>(XamlReader.Parse(layout.ToString()));
        var splitter = Assert.Single(grid.Children.OfType<GridSplitter>());
        var owned = Assert.IsType<DataGrid>(grid.FindName("QuestOwnedGrid"));
        Assert.True(owned.IsReadOnly);
        Assert.DoesNotContain(owned.Columns, column => Equals(column.Header, "History"));
        Assert.Equal(["Item", "Carried", "Bank", "Hoard", "Depot", "Other", "Collection", "Installed"],
            owned.Columns.Select(column => column.Header));
        var filter = Assert.IsType<TextBox>(grid.FindName("InventoryItemFilter"));
        Assert.True(grid.Children.Contains(Assert.IsType<StackPanel>(filter.Parent)));
        var details = Assert.IsType<TabControl>(grid.FindName("InventoryDetailsTabs"));
        Assert.Equal(["Copy locations", "Quest references"], details.Items.Cast<TabItem>().Select(tab => tab.Header));
        var copies = Assert.IsType<DataGrid>(grid.FindName("InventoryCopiesGrid"));
        Assert.True(copies.IsReadOnly);
        Assert.Equal(["Item", "Rank", "Qty", "Location", "Kind"], copies.Columns.Select(column => column.Header));
        details.SelectedIndex = 1;
        var results = Assert.IsType<ListBox>(grid.FindName("QuestResults"));
        var status = Assert.IsAssignableFrom<FrameworkElement>(grid.FindName("QuestLookupStatus"));
        int upperRow = Grid.GetRow(splitter) - 1;
        int lowerRow = Grid.GetRow(splitter) + 1;
        var lowerPane = details;
        Assert.Equal(lowerRow, Grid.GetRow(lowerPane));
        var questPane = Assert.IsType<DockPanel>(((TabItem)details.SelectedItem).Content);
        Assert.True(grid.RowDefinitions[upperRow].Height.IsStar);
        Assert.True(grid.RowDefinitions[lowerRow].Height.IsStar);
        Assert.True(questPane.Children.Contains(results));
        Assert.True(questPane.Children.OfType<StackPanel>().Single().Children.Contains(status));

        Layout(grid);
        double initialUpper = owned.ActualHeight;
        double initialLower = lowerPane.ActualHeight;
        double initialResults = results.ActualHeight;
        double initialStatus = status.ActualHeight;
        var peer = new GridSplitterAutomationPeer(splitter);
        var transform = Assert.IsAssignableFrom<ITransformProvider>(peer.GetPattern(PatternInterface.Transform));

        transform.Move(0, 40);
        Layout(grid);
        Assert.Equal(initialUpper + 40, owned.ActualHeight, 3);
        Assert.Equal(initialLower - 40, lowerPane.ActualHeight, 3);
        Assert.Equal(initialResults - 40, results.ActualHeight, 3);
        Assert.Equal(initialStatus, status.ActualHeight, 3);

        transform.Move(0, -40);
        Layout(grid);
        Assert.Equal(initialUpper, owned.ActualHeight, 3);
        Assert.Equal(initialResults, results.ActualHeight, 3);

        transform.Move(0, 10000);
        Layout(grid);
        Assert.Equal(grid.RowDefinitions[lowerRow].MinHeight, lowerPane.ActualHeight, 3);
        Assert.True(results.ActualHeight > 0);

        transform.Move(0, -10000);
        Layout(grid);
        Assert.Equal(grid.RowDefinitions[upperRow].MinHeight, owned.ActualHeight, 3);
        Assert.True(results.ActualHeight > initialResults);

        details.SelectedIndex = 0;
        copies.ItemsSource = Enumerable.Range(1, 100)
            .Select(index => new { Item = "Sword", Rank = 3, Quantity = 1, Location = $"Bank slot {index}", Kind = "Loose / worn" })
            .ToArray();
        Layout(grid);
        Assert.True(copies.ActualHeight > 0);
        copies.ScrollIntoView(copies.Items[99]);
        Layout(grid);
        Assert.Equal(100, copies.Items.Count);
        Assert.IsType<DataGridRow>(copies.ItemContainerGenerator.ContainerFromIndex(99));
    }

    private static void Layout(Grid grid)
    {
        grid.Measure(new Size(1000, 600));
        grid.Arrange(new Rect(0, 0, 1000, 600));
        grid.UpdateLayout();
    }
}
