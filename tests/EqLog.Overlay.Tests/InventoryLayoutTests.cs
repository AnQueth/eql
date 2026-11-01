using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using EqLog.Application.Inventory;
using EqLog.Core;
using EqLog.Overlay.ViewModels;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class InventoryLayoutTests
{
    [Fact]
    public void InventorySplitter_ResizesWholeQuestPane_AndPreservesMinimumHeights()
    {
        InventoryFilterTestDispatcher.Run(VerifySplitterAsync);
    }

    private static async Task VerifySplitterAsync()
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
        await VerifyBindingsAsync(grid, owned, filter, copies);
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

    private static async Task VerifyBindingsAsync(Grid grid, DataGrid owned, TextBox filter, DataGrid copies)
    {
        var model = new InventoryViewModel();
        var at = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        InventoryExportItem[] rows =
        [
            new("Held", "Sword +3", 1, 2, InventoryStorage.Carried),
            new("Bank1", "Other item", 2, 1, InventoryStorage.Bank),
        ];
        var state = new QuestInventory();
        state.ApplyExport("Que", rows, at);
        model.Apply(new(1, state.Clone(), new("Que", new("export", 2, at), Array.AsReadOnly(rows))), "Que");
        grid.DataContext = model;
        Layout(grid);
        Assert.Equal(2, owned.Items.Count);
        owned.SelectedItem = owned.Items[0];
        Layout(grid);
        Assert.Same(owned.SelectedItem, model.SelectedItem);
        Assert.Equal(2, Assert.Single(copies.Items.Cast<InventoryItemLocation>()).Quantity);
        filter.Text = "sword";
        Layout(grid);
        Assert.Equal("sword", model.Filter);
        Assert.Equal(2, owned.Items.Count);
        await model.PendingFilter;
        Layout(grid);
        Assert.Single(owned.Items.Cast<QuestInventoryItem>());
        rows = [new("Bank2", "Sword +10", 1, 1, InventoryStorage.Bank)];
        state.ApplyExport("Que", rows, at.AddMinutes(1));
        model.Apply(new(2, state.Clone(), new("Que", new("export", 1, at.AddMinutes(1)), Array.AsReadOnly(rows))), "Que");
        await model.PendingFilter;
        Layout(grid);
        Assert.Same(owned.SelectedItem, model.SelectedItem);
        Assert.Equal("Sword +10", Assert.Single(copies.Items.Cast<InventoryItemLocation>()).Item);
        Assert.Equal("1", Assert.IsType<TextBlock>(owned.Columns[2].GetCellContent(owned.SelectedItem)).Text);
        var cell = owned.Columns[0].GetCellContent(owned.SelectedItem);
        Assert.Contains(TextBlocks(cell).SelectMany(block => block.Inlines.OfType<Hyperlink>())
            .SelectMany(link => link.Inlines.OfType<Run>()), run => run.Text == "Sword +10");
        Assert.Contains("1 copies", Assert.IsType<TextBlock>(grid.FindName("InventoryCopiesStatus")).Text);
        filter.Text = "";
        await model.PendingFilter;
        Layout(grid);
        grid.DataContext = null;
    }

    private static IEnumerable<TextBlock> TextBlocks(DependencyObject parent)
    {
        if (parent is TextBlock text)
        {
            yield return text;
        }
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            foreach (var child in TextBlocks(VisualTreeHelper.GetChild(parent, index)))
            {
                yield return child;
            }
        }
    }
}
