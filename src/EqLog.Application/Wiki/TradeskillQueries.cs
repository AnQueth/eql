using EqLog.Core;

namespace EqLog.Application.Wiki;

public enum TradeskillMerchantStatus
{
    NotLoaded,
    Failed,
    NoListedMerchants,
    ZoneUnknown,
    NoneInZone,
    Available
}

public sealed record TradeskillIngredientAvailability(TradeskillIngredient Ingredient,
    IReadOnlyList<TradeskillMerchant> Merchants, TradeskillMerchantStatus Status, string? Error);

/// <summary>Tradeskill identity, recipe search, and merchant filtering outside presentation.</summary>
public static class TradeskillQueries
{
    public static IReadOnlyList<string> Skills
    {
        get
        {
            return Tradeskills.Skills;
        }
    }

    public static string CacheKey(TradeskillPageKind kind, string title)
    {
        return "tradeskill:" + kind + ":" + title.Trim().Replace('_', ' ');
    }

    public static string PageUrl(string title)
    {
        return QuestLookup.PageUrl(title);
    }

    public static IReadOnlyList<TradeskillRecipe> Search(TradeskillWikiPage? catalog, string filter,
        bool sortByTrivial = false)
    {
        string text = filter.Trim();
        var recipes = (catalog?.Recipes ?? []).Where(recipe =>
            recipe.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            recipe.Outcome.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (sortByTrivial)
        {
            return recipes.OrderBy(recipe => recipe.TrivialValue ?? int.MaxValue)
                .ThenBy(recipe => recipe.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        return recipes.OrderBy(recipe => recipe.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<TradeskillRecipe> Variants(TradeskillWikiPage page, string skill)
    {
        return page.Recipes.Where(recipe => recipe.Skill.Equals(skill, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public static TradeskillRecipe Enrich(TradeskillRecipe recipe, IReadOnlyList<TradeskillRecipe> variants)
    {
        var match = variants.FirstOrDefault(variant => SameIngredients(recipe, variant));
        if (recipe.Ingredients.Count == 0)
        {
            match = variants.FirstOrDefault(variant => variant.Trivial == recipe.Trivial) ??
                (variants.Count == 1 ? variants[0] : null);
        }
        if (match is null)
        {
            return recipe;
        }
        return recipe with
        {
            Container = recipe.Container.Length > 0 ? recipe.Container : match.Container,
            Yield = recipe.Yield ?? match.Yield,
            Ingredients = recipe.Ingredients.Count > 0 ? recipe.Ingredients : match.Ingredients,
            Trivial = recipe.Ingredients.Count > 0 ? recipe.Trivial : match.Trivial,
        };
    }

    public static IReadOnlyList<TradeskillRecipe> CombineOptions(TradeskillRecipe recipe, TradeskillWikiPage page)
    {
        var variants = Variants(page, recipe.Skill);
        if (variants.Count > 0 && !variants.Any(variant => SameIngredients(recipe, variant)))
        {
            return variants;
        }
        var enriched = Enrich(recipe, variants);
        if (enriched.Ingredients.Count > 0)
        {
            return new[] { enriched }.Concat(variants.Where(variant => !SameIngredients(enriched, variant))).ToArray();
        }
        return variants.Count > 0 ? variants : [recipe];
    }

    public static IReadOnlyList<TradeskillIngredientAvailability> Availability(TradeskillRecipe recipe,
        IReadOnlyDictionary<string, TradeskillWikiPage> pages, IReadOnlyDictionary<string, string> errors,
        bool currentZoneOnly, string? zone)
    {
        return recipe.Ingredients.Select(ingredient =>
        {
            string key = CacheKey(TradeskillPageKind.Merchants, ingredient.PageTitle);
            string? error = errors.GetValueOrDefault(key);
            if (!pages.TryGetValue(key, out var page))
            {
                return new TradeskillIngredientAvailability(ingredient, [], error is null
                    ? TradeskillMerchantStatus.NotLoaded : TradeskillMerchantStatus.Failed, error);
            }
            var merchants = Merchants(page, currentZoneOnly, zone);
            var status = page.Merchants.Count == 0 ? TradeskillMerchantStatus.NoListedMerchants :
                currentZoneOnly && string.IsNullOrWhiteSpace(zone) ? TradeskillMerchantStatus.ZoneUnknown :
                merchants.Count == 0 ? TradeskillMerchantStatus.NoneInZone : TradeskillMerchantStatus.Available;
            return new TradeskillIngredientAvailability(ingredient, merchants, status, error);
        }).ToArray();
    }

    private static bool SameIngredients(TradeskillRecipe first, TradeskillRecipe second)
    {
        return first.Ingredients.Count > 0 && first.Ingredients.Count == second.Ingredients.Count &&
            first.Ingredients.All(ingredient => second.Ingredients.Any(other =>
                other.PageTitle.Equals(ingredient.PageTitle, StringComparison.OrdinalIgnoreCase) &&
                other.Quantity == ingredient.Quantity));
    }

    public static IReadOnlyList<TradeskillMerchant> Merchants(TradeskillWikiPage page, bool currentZoneOnly, string? zone)
    {
        IEnumerable<TradeskillMerchant> merchants = page.Merchants;
        if (currentZoneOnly)
        {
            if (string.IsNullOrWhiteSpace(zone))
            {
                return [];
            }
            string key = ZoneKey(zone);
            merchants = merchants.Where(merchant => ZoneKey(merchant.Zone) == key);
        }
        return merchants.OrderBy(merchant => merchant.Zone, StringComparer.OrdinalIgnoreCase)
            .ThenBy(merchant => merchant.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ZoneKey(string zone)
    {
        string key = new(zone.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return key switch
        {
            "northernqeynos" => "northqeynos",
            "southernqeynos" => "southqeynos",
            "northernfreeport" => "northfreeport",
            "easternfreeport" => "eastfreeport",
            "westernfreeport" => "westfreeport",
            "thefeerrott" => "feerrott",
            _ => key,
        };
    }
}
