using System.Net;
using System.Text.Json;
using EqLog.Infrastructure.Wiki;

namespace EqLog.Infrastructure.Tests;

public sealed class TradeskillWikiClientTests
{
    [Theory]
    [InlineData(TradeskillPageKind.Recipe, "playercrafted", "* [[Alchemy]] (Trivial: 20)\n** In [[Test Bag]]:\n:: 2 x [[Test Leaf]]")]
    [InlineData(TradeskillPageKind.Merchants, "soldby", "{{ItemWhereRow | [[Halas]] | [[Test Merchant]] | Shop | (12, -34) }}")]
    public async Task ItemLookupsReadApiWikitextRatherThanHtml(TradeskillPageKind kind, string field, string value)
    {
        string text = "{{Itempage\n|" + field + "=\n" + value + "\n}}";
        using var handler = new Handler(JsonSerializer.Serialize(new { parse = new { wikitext = text } }));
        using var http = new HttpClient(handler);

        var result = await new TradeskillWikiClient(http).FetchAsync(kind, "Test Item", default);

        Assert.Contains("prop=wikitext", handler.Request);
        Assert.Contains("redirects=1", handler.Request);
        Assert.Contains("formatversion=2", handler.Request);
        if (kind == TradeskillPageKind.Recipe)
        {
            Assert.Equal(2, Assert.Single(Assert.Single(result.Recipes).Ingredients).Quantity);
        }
        else
        {
            Assert.Equal("Test Merchant", Assert.Single(result.Merchants).Name);
        }
    }

    [Fact]
    public async Task ApiErrorsDoNotBecomeEmptySuccessfulResults()
    {
        using var handler = new Handler("""{"error":{"code":"missingtitle","info":"Page missing"}}""");
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TradeskillWikiClient(http).FetchAsync(TradeskillPageKind.Merchants, "Test Item", default));
        Assert.Contains("Page missing", error.Message);
    }

    [Fact]
    public async Task CatalogReadsBatchedCanonicalItemTemplatesWithoutAnyHtmlTableParsing()
    {
        var links = Enumerable.Range(0, 51).Select(index => new { ns = 0, title = $"Item {index}", exists = true })
            .Concat([
                new { ns = 0, title = "Alias", exists = true },
                new { ns = 0, title = "Item 0", exists = true },
                new { ns = 14, title = "Category:Tailoring", exists = true },
                new { ns = 0, title = "Missing Item", exists = false },
            ]).ToArray();
        var firstBatch = Enumerable.Range(0, 50).Select(index => new
        {
            title = $"Item {index}",
            revisions = new[] { new { slots = new { main = new
            {
                content = ItemSource(index == 49 ? "Brewing" : "Alchemy"),
            } } } },
        }).ToArray();
        var secondBatch = new[]
        {
            new { title = "Item 50", revisions = new[] { new { slots = new { main = new { content = "A town, not an item template." } } } } },
            new { title = "Item 0", revisions = new[] { new { slots = new { main = new { content = ItemSource("Alchemy") } } } } },
        };
        using var handler = new Handler(
            JsonSerializer.Serialize(new { parse = new { links } }),
            JsonSerializer.Serialize(new { query = new { pages = firstBatch } }),
            JsonSerializer.Serialize(new { query = new { pages = secondBatch } }));
        using var http = new HttpClient(handler);

        var result = await new TradeskillWikiClient(http).FetchAsync(TradeskillPageKind.Skill, "Alchemy", default);

        Assert.Equal(49, result.Recipes.Count);
        Assert.Single(result.Recipes, recipe => recipe.PageTitle == "Item 0");
        Assert.All(result.Recipes, recipe =>
        {
            Assert.Equal("Alchemy", recipe.Skill);
            Assert.Equal(20, recipe.TrivialValue);
            Assert.Equal(2, Assert.Single(recipe.Ingredients).Quantity);
        });
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("prop=links", handler.Requests[0]);
        Assert.All(handler.Requests.Skip(1), request =>
        {
            Assert.Contains("prop=revisions", request);
            Assert.Contains("rvslots=main", request);
            Assert.Contains("redirects=1", request);
        });
    }

    [Fact]
    public async Task FailedRevisionBatchDoesNotReturnAnIncompleteCatalog()
    {
        using var handler = new Handler(
            """{"parse":{"links":[{"ns":0,"title":"Item","exists":true}]}}""",
            """{"error":{"info":"Revision lookup failed"}}""");
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TradeskillWikiClient(http).FetchAsync(TradeskillPageKind.Skill, "Alchemy", default));

        Assert.Contains("Revision lookup failed", error.Message);
    }

    private static string ItemSource(string skill)
    {
        return "{{Itempage\n|playercrafted=\n* [[" + skill + "]] (Trivial: 20)\n** In [[Test Bag]]:\n:: 2 x [[Test Leaf]]\n}}";
    }

    private sealed class Handler(params string[] responses) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public string Request
        {
            get
            {
                return Requests[^1];
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string response = responses[Requests.Count];
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) });
        }
    }
}
