using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using EqLog.Core.Tracking;

namespace EqLog.Core;

/// <summary>Reads explicit NPC stats and spell resistance types from EQLWiki; absent fields remain unknown.</summary>
public sealed class MobWikiLookup(HttpClient http)
{
    public async Task<MobWikiInfo> FindMobAsync(string mob, CancellationToken token = default)
    {
        var page = await FindPageAsync(mob, token);
        if (page is null || !Regex.IsMatch(page.Value.Text, @"\{\{\s*(?:Namedmobpage|Mobpage)\b",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
        {
            return new(mob, QuestLookup.PageUrl(mob), DateTime.UtcNow, false, null, null, null,
                new Dictionary<ResistanceType, string>());
        }
        return ParseMob(page.Value.Title, page.Value.Text, DateTime.UtcNow);
    }

    public async Task<SpellResistanceInfo> FindSpellAsync(string spell, CancellationToken token = default)
    {
        var page = await FindPageAsync(spell, token);
        if (page is not null && !IsSpellPage(page.Value.Text))
        {
            page = null;
        }
        if (page is null && SpellNames.BaseName(spell) != spell)
        {
            page = await FindPageAsync(SpellNames.BaseName(spell), token);
        }
        if (page is not null && !IsSpellPage(page.Value.Text))
        {
            page = null;
        }
        string? detail = page is null ? null : Field(page.Value.Text, "resist");
        ResistanceType? type = null;
        if (detail is not null)
        {
            foreach (var candidate in Enum.GetValues<ResistanceType>())
            {
                if (Regex.IsMatch(detail, @"^" + candidate + @"\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
                {
                    type = candidate;
                    break;
                }
            }
        }
        string? duration = page is null ? null : Field(page.Value.Text, "duration");
        string? targetType = page is null ? null : Field(page.Value.Text, "target_type");
        bool singleTargetDirectDamage = page is not null &&
            string.Equals(duration, "Instant", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(targetType, "Single", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(targetType, "Lifetap", StringComparison.OrdinalIgnoreCase)) &&
            page.Value.Text.Contains("Decrease Hitpoints", StringComparison.OrdinalIgnoreCase);
        return new(spell, type, detail, DateTime.UtcNow, singleTargetDirectDamage, targetType, duration,
            SpellResistanceInfo.CurrentMetadataVersion, Found: page is not null);
    }

    public static MobWikiInfo ParseMob(string title, string text, DateTime checkedUtc)
    {
        var resistances = new Dictionary<ResistanceType, string>();
        foreach (var type in Enum.GetValues<ResistanceType>())
        {
            string abbreviation = type switch
            {
                ResistanceType.Magic => "MR",
                ResistanceType.Fire => "FR",
                ResistanceType.Cold => "CR",
                ResistanceType.Poison => "PR",
                ResistanceType.Disease => "DR",
                _ => throw new ArgumentOutOfRangeException(nameof(type)),
            };
            string? value = Field(text, abbreviation, type + "_resist", type + "_resistance", "resist_" + type);
            if (value is not null)
            {
                resistances[type] = value;
            }
        }
        return new(title, QuestLookup.PageUrl(title), checkedUtc, true,
            Field(text, "level"), Field(text, "class"), Field(text, "HP"), resistances, Field(text, "special"),
            new MobWikiStats(Field(text, "race"), Field(text, "AC"), Field(text, "HP_regen"), Field(text, "mana_regen"),
                Field(text, "attacks_per_round"), Field(text, "attack_speed"), Field(text, "damage_per_hit"),
                Field(text, "agro_radius", "aggro_radius"), Field(text, "run_speed"), Field(text, "respawn_time"),
                Field(text, "location")));
    }

    private static string? Field(string text, params string[] aliases)
    {
        foreach (string alias in aliases)
        {
            var match = Regex.Match(text, @"^[ \t]*\|[ \t]*" + Regex.Escape(alias) + @"[ \t]*=[ \t]*([^\r\n]*)",
                RegexOptions.Multiline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            if (!match.Success)
            {
                continue;
            }
            string value = Regex.Replace(match.Groups[1].Value.TrimEnd('|'), @"\[\[(?:[^\]|]+\|)?([^\]]+)\]\]", "$1");
            value = WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]*>", "")).Trim();
            if (value.Length > 0 && !value.Contains("{{", StringComparison.Ordinal))
            {
                return value.Length > 100 ? value[..100] : value;
            }
        }
        return null;
    }

    private static bool IsSpellPage(string text)
    {
        return Regex.IsMatch(text, @"\{\{\s*Spellpage(?:smart)?\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    }

    private async Task<(string Title, string Text)?> FindPageAsync(string name, CancellationToken token)
    {
        string alternate = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
        string url = "https://eqlwiki.com/api.php?action=query&format=json&formatversion=2&redirects=1&prop=revisions&rvprop=content&rvslots=main&maxlag=5&titles=" +
            Uri.EscapeDataString(name + "|" + alternate);
        using var response = await http.GetAsync(url, token);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
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
