using System.Net;
using System.Text.RegularExpressions;
using EqLog.Core.Tracking;

namespace EqLog.Core;

/// <summary>Reads explicit NPC stats and spell resistance types from EQLWiki; absent fields remain unknown.</summary>
public static class MobWikiLookup
{
    public static SpellResistanceInfo ParseSpell(string spell, (string Title, string Text)? page, DateTime checkedUtc)
    {
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
        bool damageOverTime = page is not null && duration is not null &&
            !string.Equals(duration, "Instant", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(targetType, "Single", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(targetType, "Lifetap", StringComparison.OrdinalIgnoreCase)) &&
            Regex.IsMatch(page.Value.Text, @"Decrease (Current )?Hit ?Points[^\n]*per tick", RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1));
        return new(spell, type, detail, checkedUtc, singleTargetDirectDamage, targetType, duration,
            SpellResistanceInfo.CurrentMetadataVersion, Found: page is not null, DamageOverTime: damageOverTime);
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
                Field(text, "location")), ParseQuestReferences(text));
    }

    private static IReadOnlyList<QuestReference> ParseQuestReferences(string text)
    {
        var field = Regex.Match(text,
            @"^[ \t]*\|[ \t]*related_quests[ \t]*=([\s\S]*?)(?=^[ \t]*\|[ \t]*[A-Za-z_][A-Za-z0-9_ ]*[ \t]*=|^[ \t]*\}\}|\z)",
            RegexOptions.Multiline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        if (!field.Success)
        {
            return [];
        }
        string value = Regex.Replace(field.Groups[1].Value, @"<!--[\s\S]*?-->", "",
            RegexOptions.None, TimeSpan.FromSeconds(1));
        var links = Regex.Matches(value, @"\[\[([^\]|]+)(?:\|[^\]]*)?\]\]|\{\{\s*:([^{}|]+)(?:\|[^{}]*)?\}\}",
            RegexOptions.None, TimeSpan.FromSeconds(1));
        return links.Select(link => WebUtility.HtmlDecode(
                (link.Groups[1].Success ? link.Groups[1].Value : link.Groups[2].Value)
                    .Split('#')[0].Replace('_', ' ').Trim()))
            .Where(title => title.Length > 0 && !title.Contains(':'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(title => new QuestReference(title, QuestLookup.PageUrl(title))).ToArray();
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

    public static bool IsSpellPage(string text)
    {
        return Regex.IsMatch(text, @"\{\{\s*Spellpage(?:smart)?\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    }

}
