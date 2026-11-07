namespace EqLog.Core.Tests;

public sealed class TradeskillsTests
{
    [Fact]
    public void CatalogDeduplicatesCanonicalRecipesAndKeepsOnlyTheSelectedSkill()
    {
        const string source = """
            {{Itempage
            |playercrafted =
            * [[Tailoring]] (Trivial: 222)
            ** '''Yield: Fleeting Quiver''' x1
            ** In [[Large Sewing Kit]]:
            **: 1 x [[Aviak Egg Oil]] - Crafted
            **: 1 x [[Fleeting Quiver Pattern]] - Bought
            **: 1 x [[Griffon Feathers]] - Dropped
            **: 1 x [[High Quality Lion Skin]] - Dropped
            **: 1 x [[Vial of Distilled Mana]] - Crafted
            * [[Alchemy]] (Trivial: 20)
            ** In [[Medicine Bag]]:
            :: 1 x [[Other Leaf]]
            }}
            """;
        var recipes = Tradeskills.ParseRecipes(source, "Fleeting Quiver");

        var recipe = Assert.Single(Tradeskills.ParseCatalog(recipes.Concat(recipes), "Tailoring"));

        Assert.Equal("Fleeting Quiver", recipe.Name);
        Assert.Equal(222, recipe.TrivialValue);
        Assert.Equal(5, recipe.Ingredients.Count);
        Assert.Equal("Large Sewing Kit", recipe.Container);
        Assert.Contains(recipe.Ingredients, ingredient => ingredient.PageTitle == "Griffon Feathers");
        Assert.All(recipe.Ingredients, ingredient => Assert.Equal(1, ingredient.Quantity));
    }

    [Fact]
    public void ItemCombinesKeepQuantitiesContainersYieldAndSkillVariants()
    {
        const string source = """
            {{Itempage
            |playercrafted =
            * [[Alchemy]] (Trivial: 25)
            ** '''Yield: Practice Potion''' x2
            ** In [[Test Bag]]:
            :: {{SmIcon|1}} 2 x [[Test Leaf]] - Bought
            :: {{SmIcon|1}} 1 x [[Test Leaf]] - Bought
            * [[Brewing]] (Trivial: 30)
            ** '''Yield: Practice Potion''' x1
            ** In [[Test Barrel]]:
            :: {{SmIcon|1}} 1 x [[Test Water]] - Foraged
            |recipes = [[Unrelated Item]]
            }}
            """;

        var recipes = Tradeskills.ParseRecipes(source, "Practice Potion");

        Assert.Equal(2, recipes.Count);
        var alchemy = Assert.Single(recipes, recipe => recipe.Skill == "Alchemy");
        Assert.Equal("Test Bag", alchemy.Container);
        Assert.Equal(2, alchemy.Yield);
        Assert.Equal(3, Assert.Single(alchemy.Ingredients).Quantity);
        Assert.Equal("Bought", alchemy.Ingredients[0].Source);
    }

    [Fact]
    public void MerchantsKeepDisplayedZoneAndCoordinatesWithoutLeakingOtherFields()
    {
        const string source = """
            {{Itempage
            |soldby =
            {{ItemWhereTable|
            {{ItemWhereRow | [[Cabilis|West Cabilis]] | [[Test Merchant]] | Shop &amp; Inn | (12, -34) }}
            {{ItemWhereRowVel | [[Cabilis|West Cabilis]] | [[Test Merchant]] | Shop &amp; Inn | (12, -34) }}
            }}
            |related_quests =
            {{ItemWhereRow | [[Other Zone]] | [[Other Merchant]] | | }}
            }}
            """;

        var merchant = Assert.Single(Tradeskills.ParseMerchants(source, "Test Leaf"));

        Assert.Equal("West Cabilis", merchant.Zone);
        Assert.Equal("Cabilis", merchant.ZonePageTitle);
        Assert.Equal("Shop & Inn", merchant.Area);
        Assert.Equal("(12, -34)", merchant.Location);
    }

    [Fact]
    public void ExplicitUnavailableSourcesAreDifferentFromChangedWikiMarkup()
    {
        Assert.Empty(Tradeskills.ParseMerchants(
            "{{Itempage\n|itemname=Test Leaf\n}}", "Test Leaf"));
        Assert.Empty(Tradeskills.ParseRecipes(
            "{{Itempage\n|itemname=Test Leaf\n}}", "Test Leaf"));
        Assert.Throws<FormatException>(() => Tradeskills.ParseMerchants(
            "{{Itempage\n|soldby=unexpected merchant format\n}}", "Test Leaf"));
        Assert.Throws<FormatException>(() => Tradeskills.ParseCatalog([], "Alchemy"));
    }

    [Fact]
    public void ApiSourceAlternativesBecomeSeparateRecipesInsteadOfRequiringEveryAlternative()
    {
        const string text = """
            {{Itempage
            |playercrafted =
            * [[Fletching]] (Trivial: 16 / 36)
            ** '''Yield: Practice Arrow''' x5
            ** In [[Test Kit|Test Table]]:
            :: {{SmIcon|1}} 1 x [[Test Shaft]] - Bought
            :: {{SmIcon|1}} 1 x [[Large Nock]] / [[Medium Nock]] - Bought
            }}
            """;

        var recipes = Tradeskills.ParseCatalog(Tradeskills.ParseRecipes(text, "Practice Arrow"), "Fletching");

        Assert.Equal(2, recipes.Count);
        Assert.Equal([16, 36], recipes.Select(recipe => recipe.TrivialValue).Order());
        Assert.All(recipes, recipe => Assert.Equal(2, recipe.Ingredients.Count));
        Assert.Equal(["Large Nock", "Medium Nock"], recipes.Select(recipe => recipe.Ingredients[1].Item));
        Assert.All(recipes, recipe => Assert.Equal("Test Table", recipe.Container));
    }

    [Fact]
    public void ItemSourceSupportsUnnumberedIngredientListsAndNamedUnlinkedIngredients()
    {
        const string source = """
            {{Itempage
            |playercrafted =
            * [[Skill Alchemy|Alchemy]] (Trivial: 187)
            ** [[Valerian Root]] + [[Sumbul]] + [[Celandine Herb]] + [[Feverfew]]
            * [[Baking]] (Trivial: 115)
            ** In [[Oven]]:
            :: {{SmIcon|775}} 1 x Red Roughy - Fished (Western Wastes)
            }}
            """;

        var recipes = Tradeskills.ParseRecipes(source, "Test Item");

        var alchemy = Assert.Single(recipes, recipe => recipe.Skill == "Alchemy");
        Assert.Equal(4, alchemy.Ingredients.Count);
        Assert.All(alchemy.Ingredients, ingredient => Assert.Equal(1, ingredient.Quantity));
        var fish = Assert.Single(Assert.Single(recipes, recipe => recipe.Skill == "Baking").Ingredients);
        Assert.Equal("Red Roughy", fish.Item);
        Assert.Equal("Fished (Western Wastes)", fish.Source);
    }

    [Theory]
    [InlineData("* [[Blacksmithing]]")]
    [InlineData("* [[Skill Brewing|Brewing]] (Trivial: ?)")]
    [InlineData("[[Fletching]]")]
    public void MissingPublishedRecipeDetailsAreNotFabricated(string header)
    {
        var recipe = Assert.Single(Tradeskills.ParseRecipes("{{Itempage\n|playercrafted=\n" + header + "\n}}", "Test Item"));

        Assert.Empty(recipe.Ingredients);
        Assert.Null(recipe.TrivialValue);
        Assert.Equal("", recipe.Container);
    }

    [Fact]
    public void LegacyItemFieldsAndOtherCraftingSkillsDoNotBreakCatalogDiscovery()
    {
        const string source = """
            {{Itempage
            |playercrafted =
            [[:Tailoring]] (82)
            :'''Yield:''' ''Cured Silk Cloak'' x1
            :In: [[Deluxe Sewing Kit]], [[Large Sewing Kit]], [[Loom]]
            :: {{SmIcon|504}} 1 x [[Cloak Pattern]] - Bought
            :: {{SmIcon|701}} 2 x [[Heady Kiola]] - Crafted
            * [[Fishing]] (Trivial: 135)
            * [[Non-Tradeskill]] (Trivial: ?)
            * No Skill Combine
            ** In [[Medium Clay Jar]]:
            :: 1 x [[Resin]] - Bought
            }}
            """;

        var recipe = Assert.Single(Tradeskills.ParseCatalog(Tradeskills.ParseRecipes(source, "Cured Silk Cloak"), "Tailoring"));

        Assert.Equal(82, recipe.TrivialValue);
        Assert.Equal("Cured Silk Cloak", recipe.Name);
        Assert.Equal(1, recipe.Yield);
        Assert.Equal("Deluxe Sewing Kit, Large Sewing Kit, Loom", recipe.Container);
        Assert.Equal(2, recipe.Ingredients.Count);
    }
}
