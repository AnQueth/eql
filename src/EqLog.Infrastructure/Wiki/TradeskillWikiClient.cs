using System.Text.Json;
using EqLog.Core;

namespace EqLog.Infrastructure.Wiki;

/// <summary>Discovers linked pages through the API and reads recipe/merchant data only from item templates.</summary>
public sealed class TradeskillWikiClient(HttpClient http)
{
    private const string Endpoint = "https://eqlwiki.com/api.php?format=json&formatversion=2&maxlag=5&redirects=1&";
    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web);

    public async Task<TradeskillWikiPage> FetchAsync(TradeskillPageKind kind, string title, CancellationToken token)
    {
        if (kind == TradeskillPageKind.Skill)
        {
            return await FetchCatalogAsync(title, token).ConfigureAwait(false);
        }
        if (kind is not (TradeskillPageKind.Recipe or TradeskillPageKind.Merchants))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        var response = await ReadAsync("action=parse&prop=wikitext&page=" + Uri.EscapeDataString(title), token)
            .ConfigureAwait(false);
        string content = response.Parse?.Wikitext ?? throw new FormatException("EQLWiki returned no item page content.");
        return kind == TradeskillPageKind.Recipe
            ? new(kind, title, DateTime.UtcNow, Tradeskills.ParseRecipes(content, title), [])
            : new(kind, title, DateTime.UtcNow, [], Tradeskills.ParseMerchants(content, title));
    }

    private async Task<TradeskillWikiPage> FetchCatalogAsync(string skill, CancellationToken token)
    {
        var response = await ReadAsync("action=parse&prop=links&page=" + Uri.EscapeDataString(skill), token)
            .ConfigureAwait(false);
        var links = response.Parse?.Links ?? throw new FormatException("EQLWiki returned no skill page links.");
        string[] titles = links.Where(link => link.Ns == 0 && link.Exists)
            .Select(link => link.Title ?? throw new FormatException("EQLWiki returned an unnamed linked page."))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var recipes = new List<TradeskillRecipe>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in titles.Chunk(50))
        {
            token.ThrowIfCancellationRequested();
            var items = await ReadAsync("action=query&prop=revisions&rvprop=content&rvslots=main&titles=" +
                Uri.EscapeDataString(string.Join("|", batch)), token).ConfigureAwait(false);
            var pages = items.Query?.Pages ?? throw new FormatException("EQLWiki returned no item revisions.");
            foreach (var page in pages)
            {
                string title = page.Title ?? throw new FormatException("EQLWiki returned an unnamed item revision.");
                if (page.Missing || !seen.Add(title))
                {
                    continue;
                }
                string content = page.Revisions?.FirstOrDefault()?.Slots?.Main?.Content
                    ?? throw new FormatException($"EQLWiki returned no revision content for {title}.");
                if (Tradeskills.HasItemTemplate(content))
                {
                    recipes.AddRange(Tradeskills.ParseRecipes(content, title));
                }
            }
        }
        return new(TradeskillPageKind.Skill, skill, DateTime.UtcNow, Tradeskills.ParseCatalog(recipes, skill), [])
        {
            CatalogVersion = Tradeskills.CatalogVersion,
        };
    }

    private async Task<ApiResponse> ReadAsync(string query, CancellationToken token)
    {
        using var response = await http.GetAsync(Endpoint + query, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var data = await JsonSerializer.DeserializeAsync<ApiResponse>(stream, ApiJson, token).ConfigureAwait(false)
            ?? throw new FormatException("EQLWiki returned no API response.");
        if (data.Error is { } error)
        {
            throw new InvalidOperationException("EQLWiki: " + error.Info);
        }
        return data;
    }

    private sealed record ApiResponse(ApiParse? Parse, ApiQuery? Query, ApiError? Error);
    private sealed record ApiError(string? Info);
    private sealed record ApiParse(string? Wikitext, IReadOnlyList<ApiLink>? Links);
    private sealed record ApiLink(int Ns, string? Title, bool Exists);
    private sealed record ApiQuery(IReadOnlyList<ApiPage>? Pages);
    private sealed record ApiPage(string? Title, bool Missing, IReadOnlyList<ApiRevision>? Revisions);
    private sealed record ApiRevision(ApiSlots? Slots);
    private sealed record ApiSlots(ApiSlot? Main);
    private sealed record ApiSlot(string? Content);
}
