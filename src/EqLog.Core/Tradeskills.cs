using System.Net;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EqLog.Core;

public enum TradeskillPageKind
{
    Skill,
    Recipe,
    Merchants
}

public sealed record TradeskillIngredient(string Item, string PageTitle, int Quantity, string Source);
public sealed record TradeskillMerchant(string Name, string PageTitle, string Zone, string ZonePageTitle, string Area, string Location);
public sealed record TradeskillRecipe(string Key, string Name, string Outcome, string PageTitle, string Skill,
    string Trivial, string Container, int? Yield, IReadOnlyList<TradeskillIngredient> Ingredients)
{
    public int? TrivialValue
    {
        get
        {
            return int.TryParse(Trivial, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
        }
    }
}
public sealed record TradeskillWikiPage(TradeskillPageKind Kind, string Title, DateTime CheckedUtc,
    IReadOnlyList<TradeskillRecipe> Recipes, IReadOnlyList<TradeskillMerchant> Merchants)
{
    public int CatalogVersion { get; init; }
}

/// <summary>Converts raw item template fields returned by the MediaWiki API into typed tradeskill data.</summary>
public static class Tradeskills
{
    public const int CatalogVersion = 2;

    public static readonly IReadOnlyList<string> Skills =
        ["Alchemy", "Baking", "Blacksmithing", "Brewing", "Fletching", "Jewelcrafting", "Pottery", "Tailoring", "Tinkering"];

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public static IReadOnlyList<TradeskillRecipe> ParseCatalog(IEnumerable<TradeskillRecipe> recipes, string skill)
    {
        var catalog = recipes.Where(recipe => recipe.Skill.Equals(skill, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(recipe => recipe.Key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(recipe => recipe.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(recipe => recipe.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        if (catalog.Length == 0)
        {
            throw new FormatException($"No item-template recipes could be read from EQLWiki's {skill} page.");
        }
        return catalog;
    }

    public static IReadOnlyList<TradeskillRecipe> ParseRecipes(string wikitext, string title)
    {
        RequireItemTemplate(wikitext, title);
        string section = Field(wikitext, "playercrafted");
        var starts = Regex.Matches(section,
            @"^\s*(?:(?:\*\s*)?\[\[(?<skill>[^\]]+)\]\]|\*\s*(?!\*)(?<plainSkill>[^\r\n(]+))(?:\s*\((?:Trivial:\s*)?(?<trivial>[^)]+)\))?",
            RegexOptions.Multiline | RegexOptions.IgnoreCase, Timeout);
        var result = new Dictionary<string, TradeskillRecipe>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < starts.Count; i++)
        {
            var start = starts[i];
            string skill = NormalizeSkill(WikiText(start.Groups["skill"].Success
                ? "[[" + start.Groups["skill"].Value + "]]" : start.Groups["plainSkill"].Value));
            string block = section[start.Index..(i + 1 < starts.Count ? starts[i + 1].Index : section.Length)];
            var yield = Regex.Match(block, @"Yield:\s*(?<name>.*?)'{2,3}\s*x\s*(?<count>\d+)",
                RegexOptions.IgnoreCase, Timeout);
            var container = Regex.Match(block, @"\bIn\s*:?\s*(?<container>(?:\{\{.*?\}\}\s*)?\[\[[^\r\n]+)",
                RegexOptions.IgnoreCase, Timeout);
            var combinations = new List<List<TradeskillIngredient>> { new() };
            foreach (Match ingredient in Regex.Matches(block,
                @"^\s*(?:\*{2}:?|::)\s*(?:\{\{[^}]*\}\}\s*)?(?:(?<quantity>\d+)\s*x\s*)?(?<items>[^\r\n]+)",
                RegexOptions.Multiline | RegexOptions.IgnoreCase, Timeout))
            {
                string line = ingredient.Groups["items"].Value;
                if (!ingredient.Groups["quantity"].Success && !line.StartsWith("[[", StringComparison.Ordinal))
                {
                    continue;
                }
                int sourceStart = line.IndexOf(" - ", StringComparison.Ordinal);
                string items = sourceStart >= 0 ? line[..sourceStart] : line;
                string source = sourceStart >= 0 ? WikiText(line[(sourceStart + 3)..]) : "";
                var links = WikiLinks(items).ToArray();
                if (links.Length == 0)
                {
                    string name = WikiText(items);
                    if (name.Length == 0)
                    {
                        throw new FormatException($"EQLWiki's {title} ingredient has no readable item.");
                    }
                    links = [new(name, name)];
                }
                bool alternatives = Regex.IsMatch(items, @"\]\]\s*(?:/|or)\s*\[\[", RegexOptions.IgnoreCase, Timeout);
                if (links.Length > 1 && !alternatives &&
                    !Regex.IsMatch(items, @"\]\]\s*(?:\+|,)\s*\[\[", RegexOptions.None, Timeout))
                {
                    throw new FormatException($"EQLWiki's {title} ingredient alternatives are ambiguous.");
                }
                if (alternatives && combinations.Count * links.Length > 64)
                {
                    throw new FormatException($"EQLWiki's {title} recipe has too many ingredient alternatives.");
                }
                int quantity = ingredient.Groups["quantity"].Success ? int.Parse(ingredient.Groups["quantity"].Value) : 1;
                if (alternatives)
                {
                    combinations = combinations.SelectMany(combination => links.Select(link =>
                        combination.Append(new TradeskillIngredient(link.Title, link.Title, quantity, source)).ToList())).ToList();
                }
                else
                {
                    foreach (var combination in combinations)
                    {
                        combination.AddRange(links.Select(link => new TradeskillIngredient(link.Title, link.Title, quantity, source)));
                    }
                }
            }
            string outcome = yield.Success ? WikiText(yield.Groups["name"].Value) : title;
            string trivial = start.Groups["trivial"].Value.Trim();
            string[] trivials = trivial.Split('/', StringSplitOptions.TrimEntries);
            for (int j = 0; j < combinations.Count; j++)
            {
                var recipe = CreateRecipe(outcome, new(title, outcome), skill,
                    trivials.Length == combinations.Count ? trivials[j] : trivial,
                    WikiText(container.Groups["container"].Value).TrimEnd(':'),
                    yield.Success ? Number(yield.Groups["count"].Value) : null, combinations[j]);
                result[recipe.Key] = recipe;
            }
        }
        if (result.Count == 0 && section.Trim().Length > 0 &&
            !WikiText(section).Contains("not crafted", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"EQLWiki's {title} page has no readable player-crafted section.");
        }
        return result.Values.ToArray();
    }

    public static IReadOnlyList<TradeskillMerchant> ParseMerchants(string wikitext, string title)
    {
        RequireItemTemplate(wikitext, title);
        string section = Field(wikitext, "soldby");
        var merchants = new Dictionary<string, TradeskillMerchant>(StringComparer.OrdinalIgnoreCase);
        foreach (Match row in Regex.Matches(section, @"\{\{\s*ItemWhereRow[A-Za-z]*\s*\|(?<cells>.*?)\}\}",
            RegexOptions.Singleline | RegexOptions.IgnoreCase, Timeout))
        {
            string[] cells = SplitWikiCells(row.Groups["cells"].Value);
            if (cells.Length < 2)
            {
                throw new FormatException($"EQLWiki's {title} merchant row is incomplete.");
            }
            var zone = WikiLinks(cells[0]).FirstOrDefault();
            var merchant = WikiLinks(cells[1]).FirstOrDefault();
            if (zone is null || merchant is null)
            {
                throw new FormatException($"EQLWiki's {title} merchant row is missing its zone or merchant.");
            }
            var entry = new TradeskillMerchant(merchant.Label, merchant.Title, zone.Label, zone.Title,
                cells.Length > 2 ? WikiText(cells[2]) : "", cells.Length > 3 ? WikiText(cells[3]) : "");
            merchants[entry.Zone + "|" + entry.Name + "|" + entry.Location] = entry;
        }
        if (merchants.Count == 0 && section.Trim().Length > 0 &&
            !WikiText(section).Contains("cannot be purchased", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"EQLWiki's {title} merchant information could not be read.");
        }
        return merchants.Values.OrderBy(merchant => merchant.Zone, StringComparer.OrdinalIgnoreCase)
            .ThenBy(merchant => merchant.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static TradeskillRecipe CreateRecipe(string name, Link outcome, string skill, string trivial,
        string container, int? yield, IEnumerable<TradeskillIngredient> ingredients)
    {
        var combined = ingredients.GroupBy(ingredient => ingredient.PageTitle, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First() with { Quantity = group.Sum(ingredient => ingredient.Quantity) }).ToArray();
        string signature = string.Join("|", combined.OrderBy(ingredient => ingredient.PageTitle, StringComparer.OrdinalIgnoreCase)
            .Select(ingredient => ingredient.PageTitle + ":" + ingredient.Quantity));
        string key = skill + "|" + outcome.Title + "|" + (combined.Length == 0 && name != outcome.Label ? name : "") + "|" + signature;
        return new(key, name, outcome.Title, outcome.Title, skill, trivial, container, yield, combined);
    }

    public static bool HasItemTemplate(string text)
    {
        return Regex.IsMatch(text, @"\{\{\s*Itempage\b", RegexOptions.IgnoreCase, Timeout);
    }

    private static void RequireItemTemplate(string text, string title)
    {
        if (!HasItemTemplate(text))
        {
            throw new FormatException($"EQLWiki's {title} page has no Itempage template.");
        }
    }

    private static string Field(string text, string name)
    {
        return Regex.Match(text, @"^\s*\|\s*" + name +
            @"\s*=(?<value>[\s\S]*?)(?=^\s*\|\s*[A-Za-z_][A-Za-z0-9_ ]*\s*=|^\s*\}\}|\z)",
            RegexOptions.Multiline | RegexOptions.IgnoreCase, Timeout).Groups["value"].Value;
    }

    private static IEnumerable<Link> WikiLinks(string text)
    {
        return Regex.Matches(text, @"\[\[(?<title>[^\]|#]+)(?:#[^\]|]*)?(?:\|(?<label>[^\]]+))?\]\]",
            RegexOptions.None, Timeout).Select(match =>
        {
            string title = WebUtility.HtmlDecode(match.Groups["title"].Value).Trim().Replace('_', ' ');
            return new Link(title, match.Groups["label"].Success ? Text(match.Groups["label"].Value) : title);
        }).Where(link => !link.Title.Contains(':'));
    }

    private static string WikiText(string text)
    {
        string plain = Regex.Replace(text, @"\[\[(?<title>[^\]|]+)(?:\|(?<label>[^\]]+))?\]\]",
            match => match.Groups["label"].Success ? match.Groups["label"].Value : match.Groups["title"].Value,
            RegexOptions.None, Timeout);
        return Text(Regex.Replace(plain, @"\{\{.*?\}\}|'{2,3}", "", RegexOptions.Singleline, Timeout));
    }

    private static string[] SplitWikiCells(string text)
    {
        var cells = new List<string>();
        int start = 0;
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '[')
            {
                depth++;
            }
            else if (text[i] == ']')
            {
                depth--;
            }
            else if (text[i] == '|' && depth == 0)
            {
                cells.Add(text[start..i]);
                start = i + 1;
            }
        }
        cells.Add(text[start..]);
        return cells.ToArray();
    }

    private static string NormalizeSkill(string skill)
    {
        return skill.Trim().TrimStart(':').Replace('_', ' ').Replace("Skill ", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant() switch
        {
            "jewelry making" or "jewelcraft" or "jewelcrafting" => "Jewelcrafting",
            "smithing" or "blacksmithing" => "Blacksmithing",
            var name => Skills.FirstOrDefault(candidate => candidate.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? skill,
        };
    }

    private sealed record Link(string Title, string Label);

    private static string Text(string html)
    {
        string plain = WebUtility.HtmlDecode(Regex.Replace(html, @"<[^>]+>", " ", RegexOptions.None, Timeout));
        return Regex.Replace(plain, @"\s+", " ", RegexOptions.None, Timeout).Trim();
    }

    private static int? Number(string text)
    {
        return int.TryParse(text.Trim().TrimStart('x'), out int number) && number > 0 ? number : null;
    }

}
