using System.Net;
using System.Net.Http;

namespace EqLog.Core.Tests;

public class QuestLookupTests
{
    [Theory]
    [InlineData("Nargon's Staff +10", "Nargon's Staff")]
    [InlineData("Schemer's Guise + 1", "Schemer's Guise")]
    [InlineData("  Gnoll Fang  ", "Gnoll Fang")]
    [InlineData("Sword +2 of Power", "Sword of Power")]
    [InlineData("Sword + 2 of Power +10", "Sword of Power")]
    [InlineData("Soul Leech (Exaltation)", "Soul Leech (Exaltation)")]
    public void ItemTitle_RemovesUpgradeLevels(string item, string expected)
    {
        Assert.Equal(expected, QuestLookup.ItemTitle(item));
    }

    [Fact]
    public async Task FindsQuestReferencesAcrossPagesAndTransclusionsWithoutDuplicates()
    {
        using var handler = new Responses(
            """{"query":{"pages":[{"title":"Gnoll Fang"}]}}""",
            """{"continue":{"continue":"-||","gblcontinue":"next"},"query":{"pages":[{"title":"Gnoll Bounty","categories":[{"title":"Category:Quests"}]},{"title":"A merchant","categories":[{"title":"Category:NPCs"}]}]}}""",
            """{"query":{"pages":[{"title":"Moonstones Quest","categories":[{"title":"Category:All Classes Quests"}]}]}}""",
            """{"query":{"pages":[{"title":"Gnoll Bounty","categories":[{"title":"Category:Quests"}]}]}}""");
        using var http = new HttpClient(handler);

        var result = await new QuestLookup(http).FindAsync("Gnoll Fang + 1");

        Assert.Equal(["Gnoll Bounty", "Moonstones Quest"], result.Quests.Select(q => q.Title));
        Assert.Equal("https://eqlwiki.com/Gnoll_Bounty", result.Quests[0].Url);
        Assert.Equal("Gnoll Fang", result.Item);
        Assert.DoesNotContain("%2B", handler.Requests[0]);
        Assert.Contains("gblcontinue=next", handler.Requests[2]);
        Assert.Contains("generator=embeddedin", handler.Requests[3]);
    }

    [Fact]
    public async Task MissingItemIsExplicitRatherThanReportedAsNoQuestUse()
    {
        using var handler = new Responses("""{"query":{"pages":[{"title":"Unknown item","missing":true}]}}""");
        using var http = new HttpClient(handler);
        var result = await new QuestLookup(http).FindAsync("Unknown item");
        Assert.False(result.Found);
        Assert.False(result.NeedsRefresh);
        Assert.Empty(result.Quests);
    }

    [Fact]
    public async Task ApiErrorIsReported()
    {
        using var handler = new Responses("""{"error":{"code":"maxlag","info":"Server is busy"}}""");
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new QuestLookup(http).FindAsync("Gnoll Fang"));
        Assert.Contains("Server is busy", error.Message);
    }

    private sealed class Responses(params string[] responses) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses[Requests.Count - 1]),
            });
        }
    }
}
