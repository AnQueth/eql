using EqLog.Application.Wiki;
using EqLog.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class TradeskillOverlayTests
{
    internal static void VerifyCompactToolbarAndRecipeLoading()
    {
        var skills = new ComboBox { ItemsSource = new[] { "Blacksmithing" }, SelectedIndex = 0 };
        var search = new TextBox { Text = "magic" };
        var sort = new ComboBox { ItemsSource = new[] { "Trivial, then name" }, SelectedIndex = 0 };
        var wiki = new Button { Content = "Wiki" };
        var refresh = new Button { Content = "Refresh" };
        var row = MainWindow.BuildTradeskillFilterRow(skills, search, sort, wiki, refresh);
        foreach (double width in new[] { 520.0, 840.0 })
        {
            row.Measure(new Size(width, double.PositiveInfinity));
            row.Arrange(new Rect(0, 0, width, row.DesiredSize.Height));
            row.UpdateLayout();
            foreach (var control in new FrameworkElement[] { skills, search, sort, wiki, refresh })
            {
                var position = control.TranslatePoint(new Point(), row);
                Assert.Equal(0, position.Y);
                Assert.True(control.ActualWidth > 0);
                Assert.True(position.X + control.ActualWidth <= width + 0.1);
            }
            Assert.True(search.TranslatePoint(new Point(), row).X > skills.TranslatePoint(new Point(), row).X);
            Assert.True(sort.TranslatePoint(new Point(), row).X > search.TranslatePoint(new Point(), row).X);
        }

        var picker = new ComboBox { DisplayMemberPath = nameof(MainWindow.TradeskillChoice.Label) };
        var recipe = new TradeskillRecipe("selected", "Practice Potion", "Practice Potion", "Practice Potion",
            "Alchemy", "25", "", 1, []);
        var other = recipe with { Key = "other", Name = "Other Potion", Outcome = "Other Potion" };
        MainWindow.UpdateTradeskillRecipePicker(picker, [], recipe.Key, "Loading Alchemy recipes...", true);
        Assert.False(picker.IsEnabled);
        Assert.Equal("Loading Alchemy recipes...", Assert.IsType<ComboBoxItem>(picker.SelectedItem).Content);
        foreach (string status in new[] { "Loading Practice Potion combine details...", "Loading merchants 1/2: Test Leaf" })
        {
            MainWindow.UpdateTradeskillRecipePicker(picker, [recipe, other], recipe.Key, status, false);
            var selected = Assert.IsType<MainWindow.TradeskillChoice>(picker.SelectedItem);
            Assert.Same(recipe, selected.Recipe);
            Assert.StartsWith(status, selected.Label);
            Assert.True(picker.IsEnabled);
            Assert.DoesNotContain(status, Assert.Single(picker.Items.OfType<MainWindow.TradeskillChoice>(),
                choice => choice.Recipe.Key == other.Key).Label);
        }
        MainWindow.UpdateTradeskillRecipePicker(picker, [other], recipe.Key, "Loading combine details...", false);
        Assert.Equal("Loading combine details...", Assert.IsType<ComboBoxItem>(picker.SelectedItem).Content);
        Assert.Same(other, Assert.Single(picker.Items.OfType<MainWindow.TradeskillChoice>()).Recipe);
        MainWindow.UpdateTradeskillRecipePicker(picker, [recipe, other], recipe.Key, "", false);
        Assert.Equal("Practice Potion (trivial 25)", Assert.IsType<MainWindow.TradeskillChoice>(picker.SelectedItem).Label);
    }

    internal static void VerifyMerchantListScrolling()
    {
        var panel = new OverlayPanel("tradeskills", "Tradeskills", false);
        var rows = Enumerable.Range(0, 170).Select(index => new OverlayRow(
            $"Merchant {index} - Test Zone", Details: $"Shop {index}", Wrap: true)).ToArray();
        panel.Update("Tradeskills", "Alchemy", rows);
        panel.Measure(new Size(520, 580));
        panel.Arrange(new Rect(0, 0, 520, 580));
        panel.UpdateLayout();
        var list = Assert.Single(Visuals(panel).OfType<ListBox>());
        Assert.Equal(170, list.Items.Count);
        Assert.True(VirtualizingPanel.GetIsVirtualizing(list));
        list.ScrollIntoView(list.Items[^1]);
        panel.UpdateLayout();
        var last = Assert.IsAssignableFrom<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(169));
        Assert.Contains(Visuals(last).OfType<TextBlock>(), text => text.Text == "Shop 169");
    }

    [Fact]
    public void ZoneFilteredDisplayKeepsQuantitiesAndEveryIngredientWithMerchantLinks()
    {
        var leaf = new TradeskillIngredient("Test Leaf", "Test Leaf", 2, "Bought");
        var water = new TradeskillIngredient("Test Water", "Test Water", 1, "");
        var recipe = new TradeskillRecipe("key", "Practice Combine", "Practice Potion", "Practice Potion",
            "Alchemy", "25", "Test Bag", 2, [leaf, water]);
        var merchant = new TradeskillMerchant("Local Merchant", "Local Merchant", "Halas", "Halas", "Shop", "(12, -34)");
        var content = MainWindow.BuildTradeskillContent("Alchemy", recipe,
            [new(leaf, [merchant], TradeskillMerchantStatus.Available, null),
                new(water, [], TradeskillMerchantStatus.NoneInZone, null)], "", true, "Halas", true);

        Assert.Contains(content.Rows, row => row.Left == "2x Test Leaf" && row.Link == "https://eqlwiki.com/Test_Leaf");
        Assert.Contains(content.Rows, row => row.Left == "1x Test Water");
        Assert.Contains(content.Rows, row => row.Left == "Local Merchant - Halas" &&
            row.Details == "Shop | (12, -34)" && row.Link == "https://eqlwiki.com/Local_Merchant");
        Assert.Contains(content.Rows, row => row.Left == "No merchants listed in your current zone.");
        Assert.Contains(content.Rows, row => row.Left.Contains("Test Bag") && row.Left.Contains("Yield: 2"));
        Assert.Contains("Halas", content.Subtitle);
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Visuals(child))
            {
                yield return descendant;
            }
        }
    }
}
