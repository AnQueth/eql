using System.Net;
using System.Text.Json;

namespace EqLog.Core.Tests;

public sealed class ItemEffectLookupTests
{
    [Fact]
    public void Parse_FocusAndInstrumentModifiers_AreSearchableByEffect()
    {
        var result = ItemEffectLookup.Parse("Cloak", "Cloak", """
            {{Itempage
            |statsblock =
            Class: BRD<br>
            String Resonance: 12<br>
            |focus_effect = Improved Damage III
            |notes = Not an effect
            }}
            """, DateTime.UtcNow);

        Assert.True(result.Found);
        Assert.Contains(result.Effects, effect => effect.Kind == "Focus" && effect.Name == "Improved Damage III");
        Assert.Contains(result.Effects, effect => effect.Kind == "Modifier" && effect.Name == "String Resonance: 12");
        Assert.Contains("Bard instruments", result.SearchTerms);
        Assert.Contains("string stringed", result.SearchTerms);
        Assert.DoesNotContain("percussion", result.SearchTerms, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_StatsBlock_RecognizesWornAndCombatButNotClickEffects()
    {
        var result = ItemEffectLookup.Parse("Sword", "Sword", """
            {{Itempage
            |statsblock =
            Effect: [[Ykesha|<span class='itemeff'>Ykesha</span>]] (Combat) at Level 37<br>
            Effect: [[Enduring Breath]] (Worn)<br>
            Effect: [[Illusion: Werewolf]] (Must Equip, Casting Time: 10.0)<br>
            Proc Effect: [[Fire Bolt]]<br>
            Haste: +21%<br>
            Skill: 1H Slashing Atk Delay: 24<br>
            |worn_effect = [[See Invisible]]
            }}
            """, DateTime.UtcNow);

        Assert.Equal(5, result.Effects.Count);
        Assert.Contains(result.Effects, effect => effect.Kind == "Proc" && effect.Name == "Ykesha");
        Assert.Contains(result.Effects, effect => effect.Kind == "Worn" && effect.Name == "Enduring Breath");
        Assert.Contains(result.Effects, effect => effect.Kind == "Worn" && effect.Name == "See Invisible");
        Assert.Contains(result.Effects, effect => effect.Kind == "Proc" && effect.Name == "Fire Bolt");
        Assert.Contains(result.Effects, effect => effect.Kind == "Modifier" && effect.Name == "Haste: +21%");
        Assert.DoesNotContain(result.Effects, effect => effect.Name.Contains("Werewolf"));
        Assert.DoesNotContain(result.Effects, effect => effect.Name.Contains("Slashing"));
    }

    [Fact]
    public void Parse_UnknownOrEmptyItem_DistinguishesUnsupportedPageFromNoEffects()
    {
        Assert.False(ItemEffectLookup.Parse("Missing", "Missing", "{{Spellpagesmart|}}", DateTime.UtcNow).Found);
        var result = ItemEffectLookup.Parse("Plain Item", "Plain Item", """
            {{Itempage
            |statsblock = AC: 10<br>
            |focus_effect =
            |notes = something
            }}
            """, DateTime.UtcNow);
        Assert.True(result.Found);
        Assert.Empty(result.Effects);
    }

    [Fact]
    public async Task Repository_FetchesEffectDescriptions_CachesAndForcesRefresh()
    {
        int requests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            requests++;
            string url = Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri);
            return url.Contains("titles=Test Cloak|", StringComparison.Ordinal)
                ? Page("Test Cloak", "{{Itempage\n|focus_effect = Damage Focus\n}}")
                : Page("Damage Focus", """
                    {{Spellpagesmart|
                    |description = Increases spell damage.
                    |slots =
                    {{SpellSlotRow | 1 | Increase Spell Damage by 20% }}
                    {{SpellSlotRow | 2 | Limit Max Level: 60 }}
                    |duration = Instant
                    }}
                    """);
        }));
        await using var repository = new WikiRepository(http, new MobResistanceStore(), () => Task.CompletedTask, TimeSpan.Zero);

        var first = await repository.GetItemEffectsAsync("Test Cloak +4", WikiPriority.Background, false, default);
        var effect = Assert.Single(first.Effects);
        Assert.Contains("Increases spell damage.", effect.Description);
        Assert.Contains("Increase Spell Damage by 20%", effect.Description);
        Assert.Contains("Limit Max Level: 60", effect.Description);
        Assert.Equal("https://eqlwiki.com/Damage_Focus", effect.Url);
        Assert.Same(first, await repository.GetItemEffectsAsync("Test Cloak", WikiPriority.Interactive, false, default));
        Assert.Equal(2, requests);
        await repository.GetItemEffectsAsync("Test Cloak", WikiPriority.Interactive, true, default);
        Assert.Equal(4, requests);

        var persisted = JsonSerializer.Deserialize<ItemEffectInfo>(JsonSerializer.Serialize(first))!;
        repository.SeedItemEffects(persisted with
        {
            CheckedUtc = DateTime.UtcNow.AddDays(-8)
        });
        await repository.GetItemEffectsAsync("Test Cloak", WikiPriority.Background, false, default);
        Assert.Equal(6, requests);
    }

    [Fact]
    public async Task Repository_MissingAndMalformedPages_AreNotSuccessShaped()
    {
        using var missingHttp = new HttpClient(new Handler(_ =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"query":{"pages":[{"missing":true}]}}""")
            }));
        await using var missing = new WikiRepository(missingHttp, new MobResistanceStore(), () => Task.CompletedTask, TimeSpan.Zero);
        var result = await missing.GetItemEffectsAsync("Missing Item", WikiPriority.Background, false, default);
        Assert.False(result.Found);
        Assert.False(result.NeedsRefresh);
        Assert.True((result with
        {
            CheckedUtc = DateTime.UtcNow.AddDays(-2)
        }).NeedsRefresh);

        using var badHttp = new HttpClient(new Handler(_ =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"query":{"pages":[{"title":"Bad"}]}}""")
            }));
        await using var bad = new WikiRepository(badHttp, new MobResistanceStore(), () => Task.CompletedTask, TimeSpan.Zero);
        await Assert.ThrowsAsync<JsonException>(() => bad.GetItemEffectsAsync("Bad", WikiPriority.Background, false, default));
        Assert.Contains(bad.GetActivity().Recent, entry => entry.IsError && entry.Name == "item-effects:Bad");
    }

    private static HttpResponseMessage Page(string title, string content)
    {
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                query = new
                {
                    pages = new[]
                    {
                        new { title, revisions = new[] { new { slots = new { main = new { content } } } } },
                    },
                },
            })),
        };
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(respond(request));
        }
    }
}
