using System.Net;
using System.Text.Json;

namespace EqLog.Core.Tests;

public sealed class MobWikiQuestTests
{
    [Fact]
    public void ParseMob_RelatedQuestsPreserveTargetsAcrossMultilineLinksWithoutLootOrComments()
    {
        var info = MobWikiLookup.ParseMob("An NPC", """
            {{Namedmobpage
            | loot = [[Loot item]]
            | related_quests =
            * [[A Quest#Walkthrough|Quest label]]
            * [[Another_Quest]]
            * [[a quest]]
            * {{:Third Quest}}
            <!-- [[Hidden Quest]] -->
            [[Category:Quests]] [[File:Quest.png]]
            | faction = [[Not a Quest]]
            }}
            [[Other page outside related quests]]
            """, DateTime.UtcNow);

        var quests = Assert.IsAssignableFrom<IReadOnlyList<QuestReference>>(info.Quests);
        Assert.Equal(["A Quest", "Another Quest", "Third Quest"], quests.Select(quest => quest.Title));
        Assert.Equal(QuestLookup.PageUrl("A Quest"), quests[0].Url);
    }

    [Theory]
    [InlineData("{{Namedmobpage\n| related_quests = \n}}")]
    [InlineData("{{Namedmobpage\n| related_quests = [[Quest]]\n}}")]
    [InlineData("{{Namedmobpage\n| related_quests = [[Quest]]\n| HP = 100\n}}")]
    public void ParseMob_QuestFieldEndsAtTemplateOrNextParameter(string text)
    {
        var info = MobWikiLookup.ParseMob("NPC", text, DateTime.UtcNow);

        Assert.NotNull(info.Quests);
        Assert.Equal(text.Contains("[[Quest]]") ? 1 : 0, info.Quests.Count);
    }

    [Fact]
    public async Task FindMobAsync_CombinesExplicitQuestsWithPaginatedCategorizedReferencesUsingResolvedTitle()
    {
        using var handler = new Responses(
            Page("Resolved NPC", "{{Namedmobpage\n| related_quests = [[Direct Quest]]\n}}"),
            """{"continue":{"continue":"-||","gblcontinue":"next"},"query":{"pages":[{"title":"Backlink Quest","categories":[{"title":"Category:Quests"}]},{"title":"Merchant","categories":[{"title":"Category:NPCs"}]}]}}""",
            """{"query":{"pages":[{"title":"Direct Quest","categories":[{"title":"Category:Wizard Quests"}]}]}}""",
            """{"query":{"pages":[{"title":"Transcluded Quest","categories":[{"title":"Category:Quests"}]}]}}""");
        using var http = new HttpClient(handler);

        var info = await new MobWikiLookup(http).FindMobAsync("Logged NPC");

        Assert.Equal("Resolved NPC", info.Title);
        var quests = Assert.IsAssignableFrom<IReadOnlyList<QuestReference>>(info.Quests);
        Assert.Equal(["Backlink Quest", "Direct Quest", "Transcluded Quest"], quests.Select(quest => quest.Title));
        Assert.Contains("gbltitle=Resolved NPC", Uri.UnescapeDataString(handler.Requests[1]));
        Assert.Contains("gblcontinue=next", handler.Requests[2]);
        Assert.Contains("generator=embeddedin", handler.Requests[3]);
        Assert.All(quests, quest => Assert.Equal(QuestLookup.PageUrl(quest.Title), quest.Url));
    }

    [Fact]
    public async Task Repository_LegacyCacheRefreshesOnceAndPersistsQuestLinks()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "mob-resistances.json");
        Directory.CreateDirectory(folder);
        try
        {
            var store = new MobResistanceStore();
            var legacy = MobWikiLookup.ParseMob("NPC", "{{Namedmobpage\n| HP = 50\n}}", DateTime.UtcNow) with
            {
                Quests = null,
            };
            store.SetWiki("NPC", legacy);
            using var handler = new Responses(
                Page("NPC", "{{Namedmobpage\n| related_quests = [[Quest]]\n}}"),
                """{"query":{"pages":[]}}""",
                """{"query":{"pages":[]}}""");
            using var http = new HttpClient(handler);
            await using var repository = new WikiRepository(http, store, () => store.SaveAsync(path), TimeSpan.Zero);

            var updated = await repository.GetMobAsync("NPC", WikiPriority.RealTime, false, default);
            Assert.Equal("Quest", Assert.Single(updated.Quests!).Title);
            Assert.Same(updated, await repository.GetMobAsync("NPC", WikiPriority.Interactive, false, default));
            Assert.Equal(3, handler.Requests.Count);
            var loaded = await MobResistanceStore.LoadAsync(path);
            Assert.Equal("Quest", Assert.Single(loaded.Get("", "NPC").Wiki!.Quests!).Title);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Repository_QuestLookupFailurePreservesExistingWikiAndReferences()
    {
        var store = new MobResistanceStore();
        var cached = MobWikiLookup.ParseMob("NPC", "{{Namedmobpage\n| related_quests = [[Old Quest]]\n}}", DateTime.UtcNow);
        store.SetWiki("NPC", cached);
        using var handler = new Responses(
            Page("NPC", "{{Namedmobpage\n| related_quests = [[New Quest]]\n}}"),
            """{"error":{"info":"Quest lookup failed"}}""");
        using var http = new HttpClient(handler);
        await using var repository = new WikiRepository(http, store, () => Task.CompletedTask, TimeSpan.Zero);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.GetMobAsync("NPC", WikiPriority.Interactive, true, default));

        Assert.Same(cached, store.Get("", "NPC").Wiki);
        Assert.Equal("Old Quest", Assert.Single(store.Get("", "NPC").Wiki!.Quests!).Title);
    }

    [Fact]
    public async Task FindMobAsync_MissingPageDoesNotQueryQuestReferences()
    {
        using var handler = new Responses("""{"query":{"pages":[{"title":"NPC","missing":true}]}}""");
        using var http = new HttpClient(handler);

        Assert.False((await new MobWikiLookup(http).FindMobAsync("NPC")).Found);
        Assert.Single(handler.Requests);
    }

    private static string Page(string title, string text)
    {
        return JsonSerializer.Serialize(new
        {
            query = new
            {
                pages = new[]
                {
                    new { title, revisions = new[] { new { slots = new { main = new { content = text } } } } },
                },
            },
        });
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
