using EqLog.Application.Wiki;
using EqLog.Core;
using EqLog.Overlay;
using EqLog.Overlay.ViewModels;
using System.Threading.Tasks;
using Xunit;

namespace EqLog.Overlay.Tests;

/// <summary>Tests the tradeskills view model's persisted state, commands, and wiki-loading pipeline.</summary>
public sealed class TradeskillsOverlayViewModelTests
{
    [Fact]
    public void CachedRecipesAreAvailableWhileWikiLookupsArePaused()
    {
        var recipe = new TradeskillRecipe("restored", "Restored recipe", "Restored potion", "Restored potion",
            "Alchemy", "25", "Medicine bag", 1, []);
        var pages = new[]
        {
            new TradeskillWikiPage(TradeskillPageKind.Skill, "Alchemy", DateTime.UtcNow, [recipe], []),
            new TradeskillWikiPage(TradeskillPageKind.Recipe, "Restored potion", DateTime.UtcNow, [recipe], []),
        };
        var services = new FakeTradeskillServices(pages) { InitialPages = pages, WikiLookupPaused = true };
        var vm = CreateViewModel(services, recipe.Key);

        Assert.Equal(recipe.Key, Assert.Single(vm.RecipeChoices).Recipe?.Key);
        Assert.Equal(recipe.Key, vm.SelectedCombineItem?.Recipe?.Key);
        Assert.Empty(services.LoadedPages);
        Assert.False(vm.RefreshCommand.CanExecute(null));
    }

    /// <summary>Refresh loads a selected recipe, alternate combine, and merchant pages through the service boundary.</summary>
    [Fact]
    public async Task RefreshCommand_LoadsCatalogRecipeAndMerchants()
    {
        var ingredient = new TradeskillIngredient("Test Leaf", "Test Leaf", 2, "Bought");
        var recipe = new TradeskillRecipe("recipe-key", "Practice Combine", "Practice Potion", "Practice Potion",
            "Alchemy", "25", "Test Bag", 1, [ingredient]);
        var merchant = new TradeskillMerchant("Local Merchant", "Local Merchant", "Halas", "Halas", "Shop", "(1, 2)");
        var pages = new[]
        {
            new TradeskillWikiPage(TradeskillPageKind.Skill, "Alchemy", DateTime.UtcNow, [recipe], []),
            new TradeskillWikiPage(TradeskillPageKind.Recipe, "Practice Potion", DateTime.UtcNow, [recipe], []),
            new TradeskillWikiPage(TradeskillPageKind.Merchants, "Test Leaf", DateTime.UtcNow, [], [merchant]),
        };
        var services = new FakeTradeskillServices(pages);
        var vm = CreateViewModel(services, recipe.Key);

        vm.RefreshCommand.Execute(null);
        await vm.RefreshCommand.Completion;

        Assert.Equal(new[]
        {
            (TradeskillPageKind.Skill, "Alchemy"),
            (TradeskillPageKind.Recipe, "Practice Potion"),
            (TradeskillPageKind.Merchants, "Test Leaf"),
        }, services.LoadedPages);
        Assert.Equal(recipe.Key, vm.SelectedRecipeItem?.Recipe?.Key);
        Assert.Equal("Practice Combine -> Practice Potion (trivial 25) - 2x Test Leaf", vm.SelectedCombineItem?.Label);
        Assert.Single(vm.CombineChoices);
        Assert.False(vm.IsCatalogLoading);
    }

    /// <summary>Filter, sort, and zone changes notify and persist through the supplied callbacks.</summary>
    [Fact]
    public void FilterSortAndZoneChanges_UpdateStateAndPersistenceCallbacks()
    {
        var savedSearch = "";
        var savedSort = false;
        var savedZone = false;
        string? savedSkill = null;
        string savedRecipe = "";
        var recipe = new TradeskillRecipe("recipe-key", "Practice Combine", "Practice Potion", "Practice Potion",
            "Alchemy", "25", "", null, []);
        var service = new FakeTradeskillServices([
            new TradeskillWikiPage(TradeskillPageKind.Recipe, "Practice Potion", DateTime.UtcNow, [recipe], []),
            new TradeskillWikiPage(TradeskillPageKind.Skill, "Baking", DateTime.UtcNow, [], []),
        ]);
        var vm = new TradeskillsOverlayViewModel(OverlayPresenters.All.Single(definition => definition.Key == "tradeskills"),
            NullOverlayActions.Instance, service, "Alchemy", "", "", false, false,
            skill => savedSkill = skill, search => savedSearch = search, recipeKey => savedRecipe = recipeKey,
            sort => savedSort = sort, zoneOnly => savedZone = zoneOnly);

        vm.SearchText = "silver";
        vm.SortIndex = 1;
        vm.SelectSortCommand.Execute(vm.SortIndex);
        vm.CurrentZoneOnlyCommand.Execute(true);
        vm.SelectRecipeCommand.Execute(new TradeskillRecipePickerItem(recipe, "Practice Potion"));
        vm.SelectedSkill = "Baking";
        vm.SelectSkillCommand.Execute("Baking");

        Assert.Equal("silver", savedSearch);
        Assert.True(savedSort);
        Assert.True(savedZone);
        Assert.Equal("Baking", savedSkill);
        Assert.Equal("", savedRecipe);
        Assert.Contains((TradeskillPageKind.Recipe, "Practice Potion"), service.LoadedPages);
        Assert.Contains((TradeskillPageKind.Skill, "Baking"), service.LoadedPages);
        Assert.Equal(1, vm.SortIndex);
        Assert.True(vm.CurrentZoneOnly);
    }

    private static TradeskillsOverlayViewModel CreateViewModel(FakeTradeskillServices services, string recipeKey)
    {
        return new TradeskillsOverlayViewModel(OverlayPresenters.All.Single(definition => definition.Key == "tradeskills"),
            NullOverlayActions.Instance, services, "Alchemy", "", recipeKey, false, false,
            _ => { }, _ => { }, _ => { }, _ => { }, _ => { });
    }

    private sealed class FakeTradeskillServices(IReadOnlyList<TradeskillWikiPage> pages) : ITradeskillsOverlayServices
    {
        public IReadOnlyList<TradeskillWikiPage> InitialPages { get; init; } = [];

        public Exception? InitialError
        {
            get;
        }

        public bool WikiLookupPaused { get; init; }

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
