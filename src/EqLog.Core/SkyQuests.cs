using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EqLog.Core;

public sealed record SkyQuest(string Class, string Name, string Reward, string Giver, string Phrase,
    string Rune, string Items, string Url, IReadOnlyList<SkyRequirement>? Requirements = null);
public sealed record SkyRequirement(string Item, int Quantity);
public sealed record SkyRequirementProgress(string Item, int Required, int Owned, int Carried, int Bank, int Historical,
    int Collection = 0, int Installed = 0)
{
    public bool Satisfied => Owned >= Required;
}
public sealed record SkyQuestCatalog(DateTime CheckedUtc, IReadOnlyList<SkyQuest> Quests);

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
            throw new InvalidOperationException("EQLWiki: " + error.GetProperty("info").GetString());
        return new(DateTime.UtcNow, Parse(json.RootElement.GetProperty("parse").GetProperty("wikitext").GetProperty("*").GetString()!));
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
            if (table < 0) throw new FormatException($"EQLWiki's {className} quest table has changed.");
            int tableEnd = section.IndexOf("|}", table, StringComparison.Ordinal);
            if (tableEnd < 0) throw new FormatException($"Incomplete {className} quest table.");
            string rows = section[table..tableEnd].Replace("\r", "");
            foreach (string row in rows.Split("\n|-", StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                var cells = Cell().Matches(row).Select(m => m.Groups[1].Value).ToArray();
                if (cells.Length != 5) throw new FormatException($"Expected five columns in {className} quest row.");
                string reward = Clean(cells[0]);
                string name = Clean(cells[1]);
                if (name.Length == 0 || reward.Length == 0) throw new FormatException($"Missing {className} quest name or reward.");
                // Quest names in this table are plain text, so link to the class section that actually contains them.
                string url = "https://eqlwiki.com/Plane_of_Sky#" + Uri.EscapeDataString(className.Replace(' ', '_') + "_Tests");
                var requirements = Requirements(cells[3]).Concat(Requirements(cells[4]))
                    .GroupBy(r => r.Item, ItemNames.Comparer)
                    .Select(g => new SkyRequirement(g.Key, g.Sum(r => r.Quantity))).ToArray();
                quests.Add(new(className, name, reward, giver, Clean(cells[2]), Clean(cells[3]), Clean(cells[4]), url, requirements));
            }
        }
        if (quests.Count == 0) throw new FormatException("No Plane of Sky class quests found. EQLWiki's format may have changed.");
        return quests;
    }

    public static IReadOnlyList<SkyRequirementProgress> Progress(SkyQuest quest, QuestInventory inventory, string character)
        => (quest.Requirements ?? []).Select(requirement =>
        {
            var matches = inventory.Items.Where(i => i.Character.Equals(character, StringComparison.OrdinalIgnoreCase) &&
                QuestLookup.ItemTitle(i.Item).Equals(QuestLookup.ItemTitle(requirement.Item), StringComparison.OrdinalIgnoreCase)).ToArray();
            return new SkyRequirementProgress(requirement.Item, requirement.Quantity,
                matches.Sum(i => i.Owned + i.Collection), matches.Sum(i => i.Carried), matches.Sum(i => i.Bank), matches.Sum(i => i.Historical),
                matches.Sum(i => i.Collection), matches.Sum(i => i.Installed));
        }).ToArray();

    private static IEnumerable<SkyRequirement> Requirements(string cell)
    {
        var entries = Regex.Matches(cell, @"<li\b[^>]*>(.*?)</li>", RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        foreach (string entry in entries.Count > 0 ? entries.Select(m => m.Groups[1].Value) : new[] { cell })
        {
            var link = Regex.Match(entry, @"\[\[([^|\]]+)(?:\|[^\]]+)?\]\]", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (!link.Success) throw new FormatException("Sky requirement has no identifiable item link.");
            string clean = Clean(entry);
            var quantity = Regex.Match(clean, @"^\s*(\d+)\s*(?:x\s*|×\s*)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            yield return new(link.Groups[1].Value, quantity.Success ? int.Parse(quantity.Groups[1].Value) : 1);
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
