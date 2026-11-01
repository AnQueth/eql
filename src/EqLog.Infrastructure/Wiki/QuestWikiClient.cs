using System.Text.Json;
using EqLog.Core;
using static EqLog.Core.QuestLookup;

namespace EqLog.Infrastructure.Wiki;

public sealed class QuestWikiClient(HttpClient http)
{
    public async Task<QuestLookupResult> FindAsync(string item, CancellationToken cancellationToken = default)
    {
        item = ItemTitle(item);
        using var resolved = await QueryAsync(new()
        {
            ["titles"] = item,
            ["redirects"] = "1",
        }, cancellationToken).ConfigureAwait(false);
        var page = resolved.RootElement.GetProperty("query").GetProperty("pages")[0];
        if (page.TryGetProperty("missing", out _))
        {
            return new(item, DateTime.UtcNow, [], Found: false);
        }
        string title = page.GetProperty("title").GetString()!;
        var quests = await FindReferencesAsync(title, cancellationToken).ConfigureAwait(false);
        return new(item, DateTime.UtcNow, quests);
    }

    internal async Task<IReadOnlyList<QuestReference>> FindReferencesAsync(string title, CancellationToken cancellationToken)
    {
        var quests = new Dictionary<string, QuestReference>(StringComparer.OrdinalIgnoreCase);
        foreach (string generator in new[] { "backlinks", "embeddedin" })
        {
            var args = new Dictionary<string, string>
            {
                ["generator"] = generator,
                ["prop"] = "categories",
                ["cllimit"] = "max",
            };
            string prefix = generator == "backlinks" ? "gbl" : "gei";
            args[prefix + "title"] = title;
            args[prefix + "namespace"] = "0";
            args[prefix + "limit"] = "50";
            var continuationKeys = new List<string>();
            do
            {
                using var response = await QueryAsync(args, cancellationToken).ConfigureAwait(false);
                var root = response.RootElement;
                if (root.TryGetProperty("query", out var query) && query.TryGetProperty("pages", out var pages))
                {
                    foreach (var candidate in pages.EnumerateArray())
                    {
                        if (!candidate.TryGetProperty("categories", out var categories) ||
                            !categories.EnumerateArray().Any(c =>
                            {
                                string category = c.GetProperty("title").GetString()!;
                                return category.Equals("Category:Quests", StringComparison.OrdinalIgnoreCase) ||
                                    category.EndsWith(" Quests", StringComparison.OrdinalIgnoreCase);
                            }))
                        {
                            continue;
                        }
                        string name = candidate.GetProperty("title").GetString()!;
                        quests[name] = new(name, PageUrl(name));
                    }
                }
                if (!root.TryGetProperty("continue", out var continuation))
                {
                    break;
                }
                foreach (string key in continuationKeys)
                {
                    args.Remove(key);
                }
                continuationKeys.Clear();
                foreach (var property in continuation.EnumerateObject())
                {
                    args[property.Name] = property.Value.GetString()!;
                    continuationKeys.Add(property.Name);
                }
            }
            while (true);
        }
        return quests.Values.OrderBy(q => q.Title, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<JsonDocument> QueryAsync(Dictionary<string, string> args, CancellationToken cancellationToken)
    {
        args["action"] = "query";
        args["format"] = "json";
        args["formatversion"] = "2";
        args["maxlag"] = "5";
        string url = "https://eqlwiki.com/api.php?" + string.Join("&", args.Select(p =>
            Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.TryGetProperty("error", out var error))
        {
            string message = error.GetProperty("info").GetString()!;
            document.Dispose();
            throw new InvalidOperationException("EQLWiki: " + message);
        }
        return document;
    }
}
