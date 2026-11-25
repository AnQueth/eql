using EqLog.Application.Wiki;
using EqLog.Core;
using EqLog.Overlay;
using EqLog.Overlay.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace EqLog.Overlay.Tests;

/// <summary>Exercises the tradeskills overlay's XAML and view-model presentation.</summary>
public sealed class TradeskillOverlayTests
{
    /// <summary>Checks compact bound controls and recipe loading on an STA thread.</summary>
    internal static void VerifyCompactToolbarAndRecipeLoading()
    {
        var recipe = new TradeskillRecipe("selected", "Practice Potion", "Practice Potion", "Practice Potion",
            "Alchemy", "25", "", 1, []);
        var catalog = new TradeskillWikiPage(TradeskillPageKind.Skill, "Alchemy", DateTime.UtcNow, [recipe], []);
        var details = new TradeskillWikiPage(TradeskillPageKind.Recipe, "Practice Potion", DateTime.UtcNow, [recipe], []);
        var services = new FakeTradeskillServices([catalog, details]);
        var vm = CreateViewModel(services, recipe.Key);
        var panel = new OverlayPanel(vm);
        Layout(panel, 520, 580);

        var row = Named<Grid>(panel, "TradeskillFilterRow");
        var controls = new FrameworkElement[]
        {
            Named<ComboBox>(panel, "TradeskillSkills"),
            Named<TextBox>(panel, "TradeskillSearch"),
            Named<ComboBox>(panel, "TradeskillSort"),
            Named<Button>(panel, "TradeskillWiki"),
            Named<Button>(panel, "TradeskillRefresh"),
        };
        foreach (double width in new[] { 520.0, 840.0 })
        {
            row.Measure(new Size(width, double.PositiveInfinity));
            row.Arrange(new Rect(0, 0, width, row.DesiredSize.Height));
            row.UpdateLayout();
            foreach (var control in controls)
            {
                var position = control.TranslatePoint(new Point(), row);
                Assert.Equal(0, position.Y);
                Assert.True(control.ActualWidth > 0);
                Assert.True(position.X + control.ActualWidth <= width + 0.1);
            }
            Assert.True(controls[1].TranslatePoint(new Point(), row).X > controls[0].TranslatePoint(new Point(), row).X);
            Assert.True(controls[2].TranslatePoint(new Point(), row).X > controls[1].TranslatePoint(new Point(), row).X);
        }

        vm.RefreshCommand.Execute(null);
        vm.RefreshCommand.Completion.GetAwaiter().GetResult();
        Assert.Equal(recipe.Key, vm.SelectedRecipeItem?.Recipe?.Key);
        Assert.Equal("Practice Potion (trivial 25)", vm.SelectedRecipeItem?.Label);
        Assert.Single(vm.CombineChoices);
        Assert.Contains((TradeskillPageKind.Skill, "Alchemy"), services.LoadedPages);
        Assert.Contains((TradeskillPageKind.Recipe, "Practice Potion"), services.LoadedPages);
    }

    /// <summary>Checks that merchant rows remain virtualized and scroll to the final entry.</summary>
    internal static void VerifyMerchantListScrolling()
    {
        var panel = new OverlayPanel(CreateViewModel(new FakeTradeskillServices([]), ""));
        var rows = Enumerable.Range(0, 170).Select(index => new OverlayRow(
            $"Merchant {index} - Test Zone", Details: $"Shop {index}", Wrap: true)).ToArray();
        panel.ViewModel.Show("Tradeskills", "Alchemy", rows);
        Layout(panel, 520, 580);
        var list = Assert.Single(Visuals(panel).OfType<ListBox>());
        Assert.Equal(170, list.Items.Count);
        Assert.True(VirtualizingPanel.GetIsVirtualizing(list));
        list.ScrollIntoView(list.Items[^1]);
        panel.UpdateLayout();
        var last = Assert.IsAssignableFrom<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(169));
        Assert.Contains(Visuals(last).OfType<TextBlock>(), text => text.Text == "Shop 169");
    }

    /// <summary>Recipe filtering preserves ingredient quantities, zone-specific merchants, and wiki links.</summary>
    [Fact]
    public void ZoneFilteredDisplayKeepsQuantitiesAndEveryIngredientWithMerchantLinks()
    {
        var leaf = new TradeskillIngredient("Test Leaf", "Test Leaf", 2, "Bought");
        var water = new TradeskillIngredient("Test Water", "Test Water", 1, "");
        var recipe = new TradeskillRecipe("key", "Practice Combine", "Practice Potion", "Practice Potion",
            "Alchemy", "25", "Test Bag", 2, [leaf, water]);
        var merchant = new TradeskillMerchant("Local Merchant", "Local Merchant", "Halas", "Halas", "Shop", "(12, -34)");
        var content = TradeskillsOverlayViewModel.BuildTradeskillContent("Alchemy", recipe,
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

    private static TradeskillsOverlayViewModel CreateViewModel(FakeTradeskillServices services, string recipeKey)
    {
        return new TradeskillsOverlayViewModel(OverlayPresenters.All.Single(definition => definition.Key == "tradeskills"),
            NullOverlayActions.Instance, services, "Alchemy", "", recipeKey, false, false,
            _ => { }, _ => { }, _ => { }, _ => { }, _ => { });
    }

    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        return Assert.Single(Visuals(root).OfType<T>(), element => element.Name == name);
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

    private sealed class FakeTradeskillServices(IReadOnlyList<TradeskillWikiPage> pages) : ITradeskillsOverlayServices
    {
        public IReadOnlyList<TradeskillWikiPage> InitialPages { get; } = [];

        public Exception? InitialError
        {
            get;
        }

        public bool WikiLookupPaused
        {
            get
            {
                return false;
            }
        }

        public List<(TradeskillPageKind Kind, string Title)> LoadedPages { get; } = [];

        public IOverlayWikiLookupLease BeginLookup()
        {
            return new FakeLookupLease();
        }

        public Task<TradeskillWikiPage> GetTradeskillAsync(TradeskillPageKind kind, string title, bool force,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            LoadedPages.Add((kind, title));
            var page = pages.FirstOrDefault(candidate => candidate.Kind == kind &&
                candidate.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
            return page is null ? throw new KeyNotFoundException($"No test page for {kind} {title}.") : Task.FromResult(page);
        }

        public string? GetCacheError(string operation)
        {
            return null;
        }

        public void TrackWikiError(Exception error)
        {
            throw error;
        }
    }

    private sealed class FakeLookupLease : IOverlayWikiLookupLease
    {
        private readonly CancellationTokenSource _cancellation = new();

        public CancellationToken Token
        {
            get
            {
                return _cancellation.Token;
            }
        }

        public Task Completion
        {
            get
            {
                return Task.CompletedTask;
            }
        }

        public void Cancel()
        {
            _cancellation.Cancel();
        }

        public void Dispose()
        {
            _cancellation.Dispose();
        }
    }
}
