using System.Net;
using System.Text.RegularExpressions;

namespace EqLog.Core;

/// <summary>A supported item effect, linked to its wiki explanation when available.</summary>
/// <param name="Kind">Focus, Worn, Proc, or Modifier.</param>
/// <param name="Name">Wiki effect name or explicit stat modifier.</param>
/// <param name="Url">Effect page URL, if this is a linked effect.</param>
/// <param name="Description">A brief wiki description and explicit spell slot details.</param>
public sealed record ItemEffect(string Kind, string Name, string? Url, string? Description = null);

/// <summary>Cached supported effects from an original item page, not an exaltation page.</summary>
/// <param name="Item">Base item lookup key.</param>
/// <param name="Url">Resolved item page.</param>
/// <param name="CheckedUtc">Fetch time.</param>
/// <param name="Found">Whether a recognizable item page was found.</param>
/// <param name="Effects">Explicit supported effects only.</param>
/// <param name="SearchTerms">Item classes and instrument keywords for effect-first searching.</param>
public sealed record ItemEffectInfo(string Item, string Url, DateTime CheckedUtc, bool Found,
    IReadOnlyList<ItemEffect> Effects, string SearchTerms)
{
    /// <summary>Successful pages are refreshed weekly; missing pages daily.</summary>
    public bool NeedsRefresh
    {
        get
        {
            return DateTime.UtcNow - CheckedUtc >= TimeSpan.FromDays(Found ? 7 : 1);
        }
    }
}

/// <summary>Reads explicit focus, worn, proc, and instrument modifiers from EQLWiki item descriptions.</summary>
public sealed class ItemEffectLookup(HttpClient http)
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Fetches a base item and brief explanations of each supported linked effect.</summary>
    /// <param name="item">Original item name, without the exaltation suffix.</param>
    /// <param name="token">Cancels wiki requests.</param>
    /// <returns>Explicit effects, or a distinguishable missing/unsupported result.</returns>
    public async Task<ItemEffectInfo> FindAsync(string item, CancellationToken token = default)
    {
        item = ItemNames.Normalize(item);
        var pages = new MobWikiLookup(http);
        var page = await pages.FindPageAsync(item, token).ConfigureAwait(false);
        if (page is null)
        {
            return new(item, QuestLookup.PageUrl(item), DateTime.UtcNow, false, [], "");
        }
        var parsed = Parse(item, page.Value.Title, page.Value.Text, DateTime.UtcNow);
        var effects = new List<ItemEffect>();
        foreach (var effect in parsed.Effects)
        {
            if (effect.Url is null)
            {
                effects.Add(effect);
                continue;
            }
            string effectTitle = Uri.UnescapeDataString(new Uri(effect.Url).AbsolutePath.TrimStart('/')).Replace('_', ' ');
            var spell = await pages.FindPageAsync(effectTitle, token).ConfigureAwait(false);
            string? description = spell is null ? null : Field(spell.Value.Text, "description");
            string[] slots = spell is null ? [] : Regex.Matches(spell.Value.Text,
                @"\{\{\s*SpellSlotRow\s*\|\s*\d+\s*\|([^{}]+)\}\}", RegexOptions.IgnoreCase, MatchTimeout)
                .Select(match => Clean(match.Groups[1].Value)).Take(16).ToArray();
            string summary = string.Join(" ", new[] { description is null ? "" : Clean(description) }
                .Concat(slots).Where(value => value.Length > 0));
            effects.Add(effect with
            {
                Url = spell is null ? effect.Url : QuestLookup.PageUrl(spell.Value.Title),
                Description = spell is null ? "Effect page not found on EQLWiki." :
                    summary.Length == 0 ? "No description or spell-slot details listed on the effect page." : summary,
            });
        }
        return parsed with
        {
            Effects = effects
        };
    }

    /// <summary>Parses only supported effect slots; click effects are not assumed to transfer to exaltations.</summary>
    /// <param name="item">Base item key.</param>
    /// <param name="title">Resolved wiki title.</param>
    /// <param name="text">Raw item page wikitext.</param>
    /// <param name="checkedUtc">Fetch time.</param>
    /// <returns>Explicit effects and searchable class/instrument metadata.</returns>
    public static ItemEffectInfo Parse(string item, string title, string text, DateTime checkedUtc)
    {
        if (!Regex.IsMatch(text, @"\{\{\s*Itempage\b", RegexOptions.IgnoreCase, MatchTimeout))
        {
            return new(item, QuestLookup.PageUrl(title), checkedUtc, false, [], "");
        }
        var effects = new List<ItemEffect>();
        foreach (var (field, kind) in new[]
        {
            ("focus_effect", "Focus"), ("worn_effect", "Worn"), ("proc_effect", "Proc"),
        })
        {
            string? value = Field(text, field);
            if (value is not null)
            {
                AddEffect(effects, kind, value);
            }
        }
        string stats = Field(text, "statsblock") ?? "";
        string[] lines = Regex.Split(stats, @"<br\s*/?>|\r?\n", RegexOptions.IgnoreCase, MatchTimeout);
        foreach (string line in lines)
        {
            string clean = Clean(line);
            var explicitEffect = Regex.Match(clean, @"^(Focus(?: Effect)?|Worn(?: Effect)?|Proc(?: Effect)?|Effect)\s*:\s*(.+)",
                RegexOptions.IgnoreCase, MatchTimeout);
            if (explicitEffect.Success)
            {
                string label = explicitEffect.Groups[1].Value;
                string kind = label.StartsWith("Focus", StringComparison.OrdinalIgnoreCase) ? "Focus" :
                    label.StartsWith("Worn", StringComparison.OrdinalIgnoreCase) ? "Worn" :
                    label.StartsWith("Proc", StringComparison.OrdinalIgnoreCase) ? "Proc" :
                    Regex.IsMatch(clean, @"\(Worn\b", RegexOptions.IgnoreCase, MatchTimeout) ? "Worn" :
                    Regex.IsMatch(clean, @"\(Combat\b", RegexOptions.IgnoreCase, MatchTimeout) ? "Proc" : "";
                if (kind.Length > 0)
                {
                    string raw = line[(line.IndexOf(':') + 1)..];
                    AddEffect(effects, kind, raw);
                }
            }
            else if (Regex.IsMatch(clean,
                @"^(?:(?:String(?:ed)?|Brass|Wind|Percussion|Singing)(?: Instruments?| Resonance)?|Haste|Skill Mod|[A-Za-z ]+ Skill)\s*:",
                RegexOptions.IgnoreCase, MatchTimeout) ||
                Regex.IsMatch(clean, @"^Skill:\s*.+\+\s*\d+", RegexOptions.IgnoreCase, MatchTimeout))
            {
                effects.Add(new("Modifier", clean, null));
            }
        }
        string terms = string.Join(" ", lines.Select(Clean).Where(line =>
            line.StartsWith("Class:", StringComparison.OrdinalIgnoreCase)));
        if (terms.Contains("BRD", StringComparison.OrdinalIgnoreCase))
        {
            terms += " Bard";
        }
        if (text.Contains("Bard Instrument", StringComparison.OrdinalIgnoreCase) ||
            effects.Any(effect => Regex.IsMatch(effect.Name, @"String|Percussion|Brass|Wind|Singing",
                RegexOptions.IgnoreCase, MatchTimeout)))
        {
            terms += " Bard instruments resonance";
        }
        if (effects.Any(effect => effect.Name.Contains("String", StringComparison.OrdinalIgnoreCase)))
        {
            terms += " string stringed";
        }
        return new(item, QuestLookup.PageUrl(title), checkedUtc, true,
            effects.DistinctBy(effect => (effect.Kind, effect.Name)).ToArray(), terms);
    }

    private static void AddEffect(List<ItemEffect> effects, string kind, string raw)
    {
        var link = Regex.Match(raw, @"\[\[([^\]|]+)(?:\|[^\]]*)?\]\]", RegexOptions.None, MatchTimeout);
        string name = link.Success ? link.Groups[1].Value.Trim() :
            Regex.Replace(Clean(raw), @"\s+\((?:Worn|Combat|Must Equip)\b.*$", "", RegexOptions.IgnoreCase, MatchTimeout).Trim();
        if (name.Length > 0 && !name.Contains("{{", StringComparison.Ordinal) &&
            !name.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            effects.Add(new(kind, name, QuestLookup.PageUrl(name)));
        }
    }

    private static string? Field(string text, string name)
    {
        var match = Regex.Match(text, @"^[ \t]*\|[ \t]*" + Regex.Escape(name) +
            @"[ \t]*=[ \t]*(.*?)(?=^[ \t]*\|[ \t]*\w+[ \t]*=|^\s*\}\}|</onlyinclude>|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase, MatchTimeout);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string Clean(string text)
    {
        string value = Regex.Replace(text, @"\[\[(?:[^\]|]+\|)?([^\]]+)\]\]", "$1", RegexOptions.None, MatchTimeout);
        value = Regex.Replace(value, @"<[^>]*>|'{2,3}", "", RegexOptions.None, MatchTimeout);
        return Regex.Replace(WebUtility.HtmlDecode(value), @"\s+", " ", RegexOptions.None, MatchTimeout).Trim();
    }
}
