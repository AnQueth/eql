using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EqLog.Core;

public sealed record SkyQuest(string Class, string Name, string Reward, string Giver, string Phrase,
    string Rune, string Items, string Url, IReadOnlyList<SkyRequirement>? Requirements = null,
    IReadOnlyList<SkyItemLink>? RuneLinks = null, IReadOnlyList<SkyItemLink>? ItemLinks = null);
public sealed record SkyItemLink(string Label, string Item);
public sealed record SkyRequirement(string Item, int Quantity);
public sealed record SkyRequirementProgress(string Item, int Required, int Owned, int Carried, int Bank, int Historical,
    int Collection = 0, int Installed = 0, int Hoard = 0, int Depot = 0, int OtherStorage = 0)
{
    public bool Satisfied
    {
        get
        {
            return Owned >= Required;
        }
    }
}
public sealed record SkyQuestCatalog(DateTime CheckedUtc, IReadOnlyList<SkyQuest> Quests,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? DropSources = null)
{
    /// <summary>Mobs that drop an item according to its EQLWiki page; empty when unknown.</summary>
    public IReadOnlyList<string> DroppedBy(string item)
    {
        if (DropSources is null)
        {
            return [];
        }

        if (DropSources.TryGetValue(item, out var mobs))
        {
            return mobs;
        }

        string title = QuestLookup.ItemTitle(item);
        var match = DropSources.FirstOrDefault(p => string.Equals(QuestLookup.ItemTitle(p.Key), title, StringComparison.OrdinalIgnoreCase));
        return match.Value ?? [];
    }
}
public sealed record SkyOwnedQuest(SkyQuest Quest, IReadOnlyList<SkyRequirementProgress> Progress);

/// <summary>One required item under a dropper, with the quests (possibly from several classes) that need it.</summary>
public sealed record SkyDropItem(string Item, string Label, IReadOnlyList<SkyQuest> Quests);

/// <summary>Items dropped by one mob. <see cref="Mob"/> is empty for items with no known dropper.</summary>
public sealed record SkyDropGroup(string Mob, IReadOnlyList<SkyDropItem> Items);

public static partial class SkyQuests
{
    public const string SourceUrl = "https://eqlwiki.com/Plane_of_Sky#Plane_of_Sky_Class_Quests";

    public static async Task<SkyQuestCatalog> FetchAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(
            "https://eqlwiki.com/api.php?action=parse&page=Plane_of_Sky&prop=wikitext&format=json&maxlag=5",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (json.RootElement.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException("EQLWiki: " + error.GetProperty("info").GetString());
        }

        var quests = Parse(json.RootElement.GetProperty("parse").GetProperty("wikitext").GetProperty("*").GetString()!);
        var items = quests.SelectMany(q => q.Requirements ?? []).Select(r => r.Item)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        IReadOnlyDictionary<string, IReadOnlyList<string>> drops;
        try
        {
            drops = await FetchDropSourcesAsync(http, items, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // Drop sources are a nice-to-have; keep the quest list usable when item pages can't be read.
            drops = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        }

        return new(DateTime.UtcNow, quests, drops);
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> FetchDropSourcesAsync(HttpClient http,
        IReadOnlyList<string> items, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in items.Chunk(50))
        {
            string url = "https://eqlwiki.com/api.php?action=query&prop=revisions&rvprop=content&rvslots=main" +
                "&redirects=1&format=json&formatversion=2&maxlag=5&titles=" +
                Uri.EscapeDataString(string.Join("|", batch));
            using var response = await http.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
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
                    byTitle[page.GetProperty("title").GetString()!] = ParseDropSources(content);
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

    /// <summary>Reads the mobs listed in an item page's <c>dropsfrom</c> field, ignoring zone-only links.</summary>
    public static IReadOnlyList<string> ParseDropSources(string wikitext)
    {
        var field = Regex.Match(wikitext, @"\|\s*dropsfrom\s*=(.*?)(?=^\s*\||\}\})",
            RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        if (!field.Success)
        {
            return [];
        }

        return Regex.Matches(field.Groups[1].Value, @"\[\[([^|\]#]+)(?:[^\]]*)\]\]", RegexOptions.None, TimeSpan.FromSeconds(1))
            .Select(m => m.Groups[1].Value.Trim())
            .Where(name => name.Length > 0 && !name.Equals("Plane of Sky", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<SkyQuest> Parse(string text)
    {
        var quests = new List<SkyQuest>();
        var headings = ClassHeading().Matches(text);
        for (int i = 0; i < headings.Count; i++)
        {
            var heading = headings[i];
            string className = heading.Groups[1].Value;
            int start = heading.Index + heading.Length;
            int end = i + 1 < headings.Count ? headings[i + 1].Index : text.Length;
            string section = text[start..end];
            string giver = Clean(Giver().Match(section).Groups[1].Value);
            int table = section.IndexOf("! Reward || Quest || Trigger Phrases || Rune || Quest Items", StringComparison.Ordinal);
            if (table < 0)
            {
                throw new FormatException($"EQLWiki's {className} quest table has changed.");
            }

            int tableEnd = section.IndexOf("|}", table, StringComparison.Ordinal);
            if (tableEnd < 0)
            {
                throw new FormatException($"Incomplete {className} quest table.");
            }

            string rows = section[table..tableEnd].Replace("\r", "");
            foreach (string row in rows.Split("\n|-", StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                var cells = Cell().Matches(row).Select(m => m.Groups[1].Value).ToArray();
                if (cells.Length != 5)
                {
                    throw new FormatException($"Expected five columns in {className} quest row.");
                }

                string reward = Clean(cells[0]);
                string name = Clean(cells[1]);
                if (name.Length == 0 || reward.Length == 0)
                {
                    throw new FormatException($"Missing {className} quest name or reward.");
                }
                // Quest names in this table are plain text, so link to the class section that actually contains them.
                string url = "https://eqlwiki.com/Plane_of_Sky#" + Uri.EscapeDataString(className.Replace(' ', '_') + "_Tests");
                var requirements = Requirements(cells[3]).Concat(Requirements(cells[4]))
                    .GroupBy(r => r.Item, ItemNames.Comparer)
                    .Select(g => new SkyRequirement(g.Key, g.Sum(r => r.Quantity))).ToArray();
                quests.Add(new(className, name, reward, giver, Clean(cells[2]), Clean(cells[3]), Clean(cells[4]), url,
                    requirements, ItemLinks(cells[3]).ToArray(), ItemLinks(cells[4]).ToArray()));
            }
        }
        if (quests.Count == 0)
        {
            throw new FormatException("No Plane of Sky class quests found. EQLWiki's format may have changed.");
        }

        return quests;
    }

    public static IReadOnlyList<SkyRequirementProgress> Progress(SkyQuest quest, QuestInventory inventory, string character)
    {
        return ProgressAll([quest], inventory, character)[quest];
    }

    public static IReadOnlyList<SkyOwnedQuest> OwnedQuests(SkyQuestCatalog catalog, QuestInventory inventory,
        string character, string selectedClass)
    {
        return catalog.Quests.Where(q => selectedClass == "All classes" || q.Class.Equals(selectedClass, StringComparison.OrdinalIgnoreCase))
            .Select(q => new SkyOwnedQuest(q, Progress(q, inventory, character)))
            .Where(q => q.Progress.Any(i => i.Owned > 0))
            .OrderBy(q => q.Quest.Class, StringComparer.OrdinalIgnoreCase)
            .ThenBy(q => q.Quest.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Stable identity for a quest in the watch list; quest names are unique within a class.</summary>
    public static string Key(SkyQuest quest)
    {
        return quest.Class + "|" + quest.Name;
    }

    /// <summary>The display label for a required item (e.g. "Azure Ring (3-Gorga)"), or the item name.</summary>
    public static string Label(SkyQuest quest, string item)
    {
        return (quest.RuneLinks ?? []).Concat(quest.ItemLinks ?? [])
            .FirstOrDefault(l => ItemNames.Comparer.Equals(l.Item, item))?.Label ?? item;
    }

    /// <summary>Text search across quest name, reward, required items (names and labels), and their droppers.</summary>
    public static bool Matches(SkyQuest quest, string? filter, SkyQuestCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        string text = filter.Trim();
        if (Contains(quest.Name, text) || Contains(quest.Reward, text))
        {
            return true;
        }

        foreach (var requirement in quest.Requirements ?? [])
        {
            if (ItemMatches(quest, requirement.Item, text) || catalog.DroppedBy(requirement.Item).Any(m => Contains(m, text)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Groups the quests' required items by the mob that drops them. An item or mob matching the filter keeps
    /// every quest that needs it; otherwise only quests whose name or reward match are kept.
    /// </summary>
    public static IReadOnlyList<SkyDropGroup> ByDropper(IEnumerable<SkyQuest> quests, SkyQuestCatalog catalog, string? filter = null)
    {
        string text = filter?.Trim() ?? "";
        var groups = new Dictionary<string, Dictionary<string, (string Label, List<SkyQuest> Quests)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var quest in quests)
        {
            foreach (var requirement in quest.Requirements ?? [])
            {
                var mobs = catalog.DroppedBy(requirement.Item);
                foreach (string mob in mobs.Count > 0 ? mobs : [""])
                {
                    bool keep = text.Length == 0 || (mob.Length > 0 && Contains(mob, text)) ||
                        ItemMatches(quest, requirement.Item, text) || Contains(quest.Name, text) || Contains(quest.Reward, text);
                    if (!keep)
                    {
                        continue;
                    }

                    if (!groups.TryGetValue(mob, out var items))
                    {
                        items = new(ItemNames.Comparer);
                        groups[mob] = items;
                    }

                    if (!items.TryGetValue(requirement.Item, out var entry))
                    {
                        entry = (Label(quest, requirement.Item), []);
                        items[requirement.Item] = entry;
                    }

                    entry.Quests.Add(quest);
                }
            }
        }

        return groups
            .OrderBy(g => g.Key.Length == 0)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SkyDropGroup(g.Key, g.Value
                .OrderBy(i => i.Key, StringComparer.OrdinalIgnoreCase)
                .Select(i => new SkyDropItem(i.Key, i.Value.Label, i.Value.Quests))
                .ToArray()))
            .ToArray();
    }

    /// <summary>Progress for many quests using a single pass over the inventory.</summary>
    public static IReadOnlyDictionary<SkyQuest, IReadOnlyList<SkyRequirementProgress>> ProgressAll(IEnumerable<SkyQuest> quests,
        QuestInventory inventory, string character)
    {
        var owned = inventory.Items
            .Where(i => i.Character.Equals(character, StringComparison.OrdinalIgnoreCase))
            .GroupBy(i => QuestLookup.ItemTitle(i.Item), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<SkyQuest, IReadOnlyList<SkyRequirementProgress>>(ReferenceEqualityComparer.Instance);
        foreach (var quest in quests)
        {
            result[quest] = (quest.Requirements ?? []).Select(requirement =>
            {
                var matches = owned.GetValueOrDefault(QuestLookup.ItemTitle(requirement.Item)) ?? [];
                return new SkyRequirementProgress(requirement.Item, requirement.Quantity,
                    matches.Sum(i => i.Owned + i.Collection), matches.Sum(i => i.Carried), matches.Sum(i => i.Bank), matches.Sum(i => i.Historical),
                    matches.Sum(i => i.Collection), matches.Sum(i => i.Installed), matches.Sum(i => i.Hoard), matches.Sum(i => i.Depot),
                    matches.Sum(i => i.OtherStorage));
            }).ToArray();
        }
        return result;
    }

    private static bool ItemMatches(SkyQuest quest, string item, string text)
    {
        return Contains(item, text) || Contains(Label(quest, item), text);
    }

    private static bool Contains(string value, string text)
    {
        return value.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<SkyRequirement> Requirements(string cell)
    {
        foreach (var item in ItemLinks(cell))
        {
            var quantity = Regex.Match(item.Label, @"^\s*(\d+)\s*(?:x\s*|×\s*)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            yield return new(item.Item, quantity.Success ? int.Parse(quantity.Groups[1].Value) : 1);
        }
    }

    private static IEnumerable<SkyItemLink> ItemLinks(string cell)
    {
        var entries = Regex.Matches(cell, @"<li\b[^>]*>(.*?)</li>", RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        foreach (string entry in entries.Count > 0 ? entries.Select(m => m.Groups[1].Value) : new[] { cell })
        {
            var link = Regex.Match(entry, @"\[\[([^|\]]+)(?:\|[^\]]+)?\]\]", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (!link.Success)
            {
                throw new FormatException("Sky requirement has no identifiable item link.");
            }

            yield return new(Clean(entry), link.Groups[1].Value);
        }
    }

    private static string Clean(string text)
    {
        text = Regex.Replace(text, @"</li\s*>", "\n", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, @"<[^>]*>", "", RegexOptions.None, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, @"\[\[([^|\]]+)(?:\|([^\]]+))?\]\]",
            m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value, RegexOptions.None, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, @"\{\{:\s*([^}]+)\}\}", "$1", RegexOptions.None, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, @"\{\{SkyNoDrop\|([^}]+)\}\}", "$1", RegexOptions.None, TimeSpan.FromSeconds(1));
        return string.Join("\n", WebUtility.HtmlDecode(text.Replace("'''", "").Replace("''", ""))
            .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
    }

    [GeneratedRegex(@"^===\s*\[\[([^\]|]+)\]\]\s+Tests\s*===\s*$", RegexOptions.Multiline)]
    private static partial Regex ClassHeading();
    [GeneratedRegex(@"'''Quest Giver:'''\s*(.+)")]
    private static partial Regex Giver();
    [GeneratedRegex(@"^\| (.*?)(?=^\| |\z)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex Cell();
}
