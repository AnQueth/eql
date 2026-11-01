using System.Text.Json;
using EqLog.Core;

namespace EqLog.Infrastructure.Wiki;

public sealed class SkyWikiClient(HttpClient http)
{
    /// <summary>Reports optional drop-source failures while retaining the usable quest catalog.</summary>
    public event Action<Exception>? DropSourcesFailed;

    public async Task<SkyQuestCatalog> FetchAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(
            "https://eqlwiki.com/api.php?action=parse&page=Plane_of_Sky&prop=wikitext&format=json&maxlag=5",
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (json.RootElement.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException("EQLWiki: " + error.GetProperty("info").GetString());
        }
        var quests = SkyQuests.Parse(json.RootElement.GetProperty("parse").GetProperty("wikitext").GetProperty("*").GetString()!);
        var items = quests.SelectMany(q => q.Requirements ?? []).Select(r => r.Item)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        IReadOnlyDictionary<string, IReadOnlyList<string>> drops;
        try
        {
            drops = await FetchDropSourcesAsync(items, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            if (DropSourcesFailed is not null)
            {
                DropSourcesFailed.Invoke(ex);
            }
            else
            {
                System.Diagnostics.Trace.TraceWarning("Sky drop-source lookup failed: {0}", ex.Message);
            }
            drops = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        }
        return new(DateTime.UtcNow, quests, drops);
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> FetchDropSourcesAsync(
        IReadOnlyList<string> items, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in items.Chunk(50))
        {
            string url = "https://eqlwiki.com/api.php?action=query&prop=revisions&rvprop=content&rvslots=main" +
                "&redirects=1&format=json&formatversion=2&maxlag=5&titles=" +
                Uri.EscapeDataString(string.Join("|", batch));
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException("EQLWiki: " + error.GetProperty("info").GetString());
            }
            var query = json.RootElement.GetProperty("query");
            var byTitle = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var page in query.GetProperty("pages").EnumerateArray())
            {
                if (page.TryGetProperty("revisions", out var revisions) && revisions.GetArrayLength() > 0)
                {
                    string content = revisions[0].GetProperty("slots").GetProperty("main").GetProperty("content").GetString() ?? "";
                    byTitle[page.GetProperty("title").GetString()!] = SkyQuests.ParseDropSources(content);
                }
            }
            var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string kind in new[] { "normalized", "redirects" })
            {
                if (query.TryGetProperty(kind, out var maps))
                {
                    foreach (var map in maps.EnumerateArray())
                    {
                        renamed[map.GetProperty("from").GetString()!] = map.GetProperty("to").GetString()!;
                    }
                }
            }
            foreach (string item in batch)
            {
                string title = item;
                for (int hops = 0; hops < 3 && renamed.TryGetValue(title, out var next); hops++)
                {
                    title = next;
                }
                if (byTitle.TryGetValue(title, out var mobs))
                {
                    result[item] = mobs;
                }
            }
        }
        return result;
    }
}
