using EqLog.Application.Wiki;
using EqLog.Core;

namespace EqLog.Application.Tests;

public sealed class TradeskillQueriesTests
{
    private static readonly TradeskillRecipe Recipe = new("key", "Practice Combine", "Practice Potion", "Practice Potion",
        "Alchemy", "25", "", null, [new("Test Leaf", "Test Leaf", 2, ""), new("Test Water", "Test Water", 1, "")]);

    [Theory]
    [InlineData(" combine ")]
    [InlineData("POTION")]
    public void SearchMatchesRecipeOrOutcome(string text)
    {
        Assert.Single(TradeskillQueries.Search(new(TradeskillPageKind.Skill, "Alchemy", DateTime.UtcNow, [Recipe], []), text));
    }

    [Theory]
    [InlineData(false, "alpha Recipe,beta Recipe,delta Recipe,empty Recipe,Zulu Recipe")]
    [InlineData(true, "alpha Recipe,delta Recipe,Zulu Recipe,beta Recipe,empty Recipe")]
    public void SearchSortsFilteredRecipesByNameOrNumericTrivialThenName(bool sortByTrivial, string expectedNames)
    {
        var catalog = new TradeskillWikiPage(TradeskillPageKind.Skill, "Alchemy", DateTime.UtcNow,
            [Recipe with { Name = "Zulu Recipe", Trivial = "10" },
                Recipe with { Name = "delta Recipe", Trivial = "2" },
                Recipe with { Name = "empty Recipe", Trivial = "" },
                Recipe with { Name = "alpha Recipe", Trivial = "2" },
                Recipe with { Name = "beta Recipe", Trivial = "?" },
                Recipe with { Name = "Unrelated", Trivial = "1" }], []);

        var result = TradeskillQueries.Search(catalog, " recipe ", sortByTrivial);

        Assert.Equal(expectedNames.Split(','), result.Select(recipe => recipe.Name));
    }

    [Fact]
    public void CurrentZoneFiltersMerchantsWithoutRemovingAnyIngredients()
    {
        var page = new TradeskillWikiPage(TradeskillPageKind.Merchants, "Test Leaf", DateTime.UtcNow, [],
            [new("Local", "Local", "West Freeport", "Freeport", "", ""),
                new("Other", "Other", "East Freeport", "Freeport", "", "")]);
        var pages = new Dictionary<string, TradeskillWikiPage>
        {
            [TradeskillQueries.CacheKey(page.Kind, page.Title)] = page,
            [TradeskillQueries.CacheKey(page.Kind, "Test Water")] = page with { Merchants = [page.Merchants[1]] },
        };

        var result = TradeskillQueries.Availability(Recipe, pages, new Dictionary<string, string>(), true, "Western Freeport");

        Assert.Equal(2, result.Count);
        Assert.Equal("Local", Assert.Single(result[0].Merchants).Name);
        Assert.Equal(TradeskillMerchantStatus.NoneInZone, result[1].Status);
        Assert.Empty(result[1].Merchants);
        Assert.Equal(TradeskillMerchantStatus.ZoneUnknown,
            TradeskillQueries.Availability(Recipe, pages, new Dictionary<string, string>(), true, null)[0].Status);
        Assert.Equal(2, TradeskillQueries.Merchants(page, false, null).Count);
    }

    [Theory]
    [InlineData(false, "alpha,Zulu,Aaron,Zed")]
    [InlineData(true, "Aaron,Zed")]
    public void MerchantsSortByZoneThenNameWithOrWithoutZoneFiltering(bool currentZoneOnly, string expectedNames)
    {
        var page = new TradeskillWikiPage(TradeskillPageKind.Merchants, "Test Leaf", DateTime.UtcNow, [],
            [new("Zed", "Zed", "West Freeport", "Freeport", "", ""),
                new("Zulu", "Zulu", "east Freeport", "Freeport", "", ""),
                new("Aaron", "Aaron", "West Freeport", "Freeport", "", ""),
                new("alpha", "alpha", "East Freeport", "Freeport", "", "")]);

        var result = TradeskillQueries.Merchants(page, currentZoneOnly, "Western Freeport");

        Assert.Equal(expectedNames.Split(','), result.Select(merchant => merchant.Name));
    }

    [Fact]
    public void EnrichmentPreservesSkillPageIngredientsAndGenuineAlternativeCombines()
    {
        var matching = Recipe with { Key = "matching", Trivial = "26", Container = "Test Bag", Yield = 2 };
        var other = matching with { Key = "other", Ingredients = [new("Other Leaf", "Other Leaf", 1, "")] };
        var wrongSkill = matching with { Key = "wrong-skill", Skill = "Brewing" };

        var options = TradeskillQueries.CombineOptions(Recipe,
            new(TradeskillPageKind.Recipe, Recipe.PageTitle, DateTime.UtcNow, [matching, other, wrongSkill], []));

        Assert.Equal(2, options.Count);
        Assert.Same(Recipe.Ingredients, options[0].Ingredients);
        Assert.Equal("25", options[0].Trivial);
        Assert.Equal("Test Bag", options[0].Container);
        Assert.Equal(2, options[0].Yield);
        Assert.Equal("other", options[1].Key);
    }

    [Fact]
    public void DeferredCatalogDetailsUseActualRecipeTrivialAndIngredients()
    {
        var summary = Recipe with { Ingredients = [], Trivial = "50" };
        var combine = Recipe with { Trivial = "17", Container = "Test Kiln" };

        var result = Assert.Single(TradeskillQueries.CombineOptions(summary,
            new(TradeskillPageKind.Recipe, Recipe.PageTitle, DateTime.UtcNow, [combine], [])));

        Assert.Equal("17", result.Trivial);
        Assert.Equal("Test Kiln", result.Container);
        Assert.Equal(Recipe.Ingredients, result.Ingredients);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ItemPageReplacesIncorrectCatalogIngredientsWithoutInventingAlternatives(bool multipleCombines)
    {
        var incorrect = Recipe with { Ingredients = [new("Wrong Leaf", "Wrong Leaf", 1, "")] };
        var alternative = Recipe with { Key = "alternative", Ingredients = [new("Other Leaf", "Other Leaf", 1, "")] };
        TradeskillRecipe[] variants = multipleCombines ? [Recipe, alternative] : [Recipe];

        var result = TradeskillQueries.CombineOptions(incorrect,
            new(TradeskillPageKind.Recipe, Recipe.PageTitle, DateTime.UtcNow, variants, []));

        Assert.Equal(variants, result);
    }
}
