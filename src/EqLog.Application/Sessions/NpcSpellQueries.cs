using EqLog.Core;

namespace EqLog.Application.Sessions;

public sealed record NpcSpellDetails(string Spell, string Result,
    IReadOnlyList<(string Label, string Value)> Evidence, string? Explanation);

public sealed partial class GameSession
{
    public IReadOnlyList<NpcSpellDetails> NpcSpellDetails(string zone, string mob)
    {
        var cards = new List<NpcSpellDetails>();
        foreach (string spell in Mobs.Get(zone, mob).Spells.Order(StringComparer.OrdinalIgnoreCase))
        {
            var details = Mobs.Get(zone, mob, spell);
            var counts = Mobs.GetRawSpellCounts(zone, mob, spell);
            var metadata = Mobs.GetSpell(spell);
            var observed = metadata?.Type is { } type ? details.Observations.Single(o => o.Type == type) : null;
            string school = metadata?.Type?.ToString() ?? "School not verified";
            string result;
            string explanation;
            var evidence = new List<(string Label, string Value)>
            {
                ("Full resists", $"{counts.FullResists:N0}"),
                ("Damage samples", $"{counts.DamageHits:N0}"),
            };
            if (counts.LegacyLandings > 0)
            {
                evidence.Add(("Earlier landings", $"{counts.LegacyLandings:N0}"));
            }
            if (observed is not null && metadata?.SingleTargetDirectDamage == true)
            {
                result = $"{school}  /  {observed.DisplayText}";
                evidence =
                [
                    ("Confirmed landings", $"{observed.Landed:N0}"),
                    ("Full resists", $"{counts.FullResists:N0}"),
                    ("Reduced damage", $"{observed.EstimatedPartial:N0}"),
                ];
                explanation = observed.UnassessedDamage > 0
                    ? $"{observed.UnassessedDamage:N0} landed hits need at least 3 damage samples for a typical-damage baseline for this character/loadout/rank."
                    : observed.Landed + observed.Resisted == 0 ? "No assessed damage outcomes yet." : "";
                if (observed.EstimatedPartial > 0)
                {
                    explanation += (explanation.Length > 0 ? "\n" : "") +
                        "Reduced-damage hits are below 90% of the sample-weighted median; cause unknown. They count as landed, not resisted.";
                }
                if (counts.LegacyLandings > 0)
                {
                    explanation += (explanation.Length > 0 ? "\n" : "") +
                        $"{counts.LegacyLandings:N0} earlier landings have no damage values; partial damage cannot be checked retrospectively.";
                }
            }
            else if (metadata?.DamageOverTime == true)
            {
                long total = counts.DotLanded + counts.DotResisted;
                result = total == 0
                    ? $"{school} DoT  /  no assessed casts yet"
                    : $"{school} DoT  /  {100m * counts.DotResisted / total:0.#}% resisted ({counts.DotResisted}/{total})";
                evidence =
                [
                    ("Casts landed", $"{counts.DotLanded:N0}"),
                    ("Casts resisted", $"{counts.DotResisted:N0}"),
                    ("All resists", $"{counts.FullResists:N0}"),
                ];
                explanation = metadata.AssessmentNote ?? "";
            }
            else
            {
                result = $"{school}  /  {metadata?.TargetType ?? "Wiki details pending"}";
                explanation = metadata?.AssessmentNote ?? "Spell metadata has not been loaded. Use Refresh wiki to retry.";
            }
            cards.Add(new(spell, result, evidence.AsReadOnly(), explanation.Length > 0 ? explanation : null));
        }
        return cards.AsReadOnly();
    }
}
