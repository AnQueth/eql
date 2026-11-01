using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using EqLog.Core;
using EqLog.Core.Tracking;

namespace EqLog.Infrastructure.Wiki;

public sealed class MobWikiClient(HttpClient http)
{
    public async Task<MobWikiInfo> FindMobAsync(string mob, CancellationToken token = default)
    {
        var page = await FindPageAsync(mob, token).ConfigureAwait(false);
        if (page is null || !Regex.IsMatch(page.Value.Text, @"\{\{\s*(?:Namedmobpage|Mobpage)\b",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
        {
            return new(mob, QuestLookup.PageUrl(mob), DateTime.UtcNow, false, null, null, null,
                new Dictionary<ResistanceType, string>());
        }
        var info = MobWikiLookup.ParseMob(page.Value.Title, page.Value.Text, DateTime.UtcNow);
        var references = await new QuestWikiClient(http).FindReferencesAsync(page.Value.Title, token).ConfigureAwait(false);
        return info with
        {
            Quests = info.Quests!.Concat(references)
                .DistinctBy(quest => quest.Url, StringComparer.OrdinalIgnoreCase)
                .OrderBy(quest => quest.Title, StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }

    public async Task<SpellResistanceInfo> FindSpellAsync(string spell, CancellationToken token = default)
    {
        var page = await FindPageAsync(spell, token).ConfigureAwait(false);
        if (page is not null && !MobWikiLookup.IsSpellPage(page.Value.Text))
        {
            page = null;
        }
        if (page is null && SpellNames.BaseName(spell) != spell)
        {
            page = await FindPageAsync(SpellNames.BaseName(spell), token).ConfigureAwait(false);
        }
        return MobWikiLookup.ParseSpell(spell, page, DateTime.UtcNow);
    }

    internal async Task<(string Title, string Text)?> FindPageAsync(string name, CancellationToken token)
    {
        string alternate = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
        string url = "https://eqlwiki.com/api.php?action=query&format=json&formatversion=2&redirects=1&prop=revisions&rvprop=content&rvslots=main&maxlag=5&titles=" +
            Uri.EscapeDataString(name + "|" + alternate);
        using var response = await http.GetAsync(url, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        if (document.RootElement.TryGetProperty("error", out var error))
        {
            string message = error.TryGetProperty("info", out var detail) ? detail.ToString() : error.ToString();
            throw new InvalidOperationException("EQLWiki: " + message);
        }
        if (!document.RootElement.TryGetProperty("query", out var query) ||
            !query.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("EQLWiki returned an unexpected page response.");
        }
        foreach (var page in pages.EnumerateArray())
        {
            if (page.TryGetProperty("missing", out _) || page.TryGetProperty("invalid", out _))
            {
                continue;
            }
            if (!page.TryGetProperty("title", out var titleElement) ||
                !page.TryGetProperty("revisions", out var revisions) || revisions.ValueKind != JsonValueKind.Array ||
                revisions.GetArrayLength() == 0 || !revisions[0].TryGetProperty("slots", out var slots) ||
                !slots.TryGetProperty("main", out var main) || !main.TryGetProperty("content", out var content))
            {
                throw new JsonException("EQLWiki returned a page without readable revision content.");
            }
            string title = titleElement.GetString() ?? throw new JsonException("EQLWiki page title is missing.");
            string text = content.GetString() ?? throw new JsonException("EQLWiki page content is missing.");
            return (title, text);
        }
        return null;
    }
}
