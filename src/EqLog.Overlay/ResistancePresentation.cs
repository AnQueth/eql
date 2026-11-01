using EqLog.Core;

namespace EqLog.Overlay;

internal sealed record NpcSpellPresentation(string Result,
    IReadOnlyList<(string Label, string Value)> Evidence, string? Explanation);

internal static class ResistancePresentation
{
    internal static string Describe(ResistanceDetails observation)
    {
        string status = observation.Status switch
        {
            ResistanceStatus.Unknown => "Unknown",
            ResistanceStatus.Resists => "Resists",
            ResistanceStatus.Partial => "Partial",
            ResistanceStatus.PartiallyResistant => "Partially Resistant",
            ResistanceStatus.NotResistant => "Not Resistant",
            _ => throw new ArgumentOutOfRangeException(nameof(observation)),
        };
        return observation.ResistPercent is decimal percent
            ? $"{status} \u2014 {percent:0.#}% ({observation.Resisted}/{observation.AssessedOutcomes})"
            : $"{status} \u2014 no assessed outcomes";
    }

    internal static NpcSpellPresentation Spell(NpcSpellDetails card)
    {
        string school = card.School?.ToString() ?? "School not verified";
        var counts = card.Counts;
        var evidence = new List<(string Label, string Value)>
        {
            ("Full resists", $"{counts.FullResists:N0}"),
            ("Damage samples", $"{counts.DamageHits:N0}"),
        };
        if (counts.LegacyLandings > 0)
        {
            evidence.Add(("Earlier landings", $"{counts.LegacyLandings:N0}"));
        }
        string result;
        var explanations = new List<string>();
        switch (card.Kind)
        {
            case NpcSpellResultKind.DirectDamage:
                var observation = card.Resistance ?? throw new InvalidOperationException("Direct damage requires resistance evidence.");
                result = $"{school}  /  {Describe(observation)}";
                evidence =
                [
                    ("Confirmed landings", $"{observation.Landed:N0}"),
                    ("Full resists", $"{counts.FullResists:N0}"),
                    ("Reduced damage", $"{observation.EstimatedPartial:N0}"),
                ];
                if (observation.UnassessedDamage > 0)
                {
                    explanations.Add($"{observation.UnassessedDamage:N0} landed hits need at least {observation.MinimumDamageSamples} damage samples for a typical-damage baseline for this character/loadout/rank.");
                }
                else if (observation.AssessedOutcomes == 0)
                {
                    explanations.Add("No assessed damage outcomes yet.");
                }
                if (observation.EstimatedPartial > 0)
                {
                    explanations.Add($"Reduced-damage hits are below {observation.ReducedDamageThresholdPercent:0.#}% of the sample-weighted median; cause unknown. They count as landed, not resisted.");
                }
                if (counts.LegacyLandings > 0)
                {
                    explanations.Add($"{counts.LegacyLandings:N0} earlier landings have no damage values; partial damage cannot be checked retrospectively.");
                }
                break;
            case NpcSpellResultKind.DamageOverTime:
                result = card.DotResistPercent is decimal percent
                    ? $"{school} DoT  /  {percent:0.#}% resisted ({counts.DotResisted}/{card.DotAssessedCasts})"
                    : $"{school} DoT  /  no assessed casts yet";
                evidence =
                [
                    ("Casts landed", $"{counts.DotLanded:N0}"),
                    ("Casts resisted", $"{counts.DotResisted:N0}"),
                    ("All resists", $"{counts.FullResists:N0}"),
                ];
                AddAssessment();
                break;
            case NpcSpellResultKind.MetadataOnly:
                result = $"{school}  /  {card.TargetType ?? "Wiki details pending"}";
                AddAssessment();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(card));
        }
        return new(result, evidence.AsReadOnly(), explanations.Count > 0 ? string.Join("\n", explanations) : null);

        void AddAssessment()
        {
            string? explanation = card.Assessment switch
            {
                null => "Spell metadata has not been loaded. Use Refresh wiki to retry.",
                SpellAssessmentKind.DirectDamage => null,
                SpellAssessmentKind.DamageOverTime => "Damage-over-time spell: each cast counts once (its first tick landed, or it was resisted); ticks don't give a full/partial damage rating.",
                SpellAssessmentKind.AreaEffect => "Area-effect spell: hits and resists are recorded, but multiple targets or waves prevent a reliable full/partial damage rating.",
                SpellAssessmentKind.Duration => "Duration spell: damage ticks are not separate cast successes, so a full/partial damage rating is unavailable.",
                SpellAssessmentKind.MissingDetails => "Wiki target/damage details are missing. Refresh wiki to retry; recorded outcomes are preserved.",
                SpellAssessmentKind.UnverifiedTarget => $"Wiki target: {card.TargetType}. No verified single-target instant damage profile; recorded outcomes are preserved.",
                _ => throw new ArgumentOutOfRangeException(nameof(card)),
            };
            if (explanation is not null)
            {
                explanations.Add(explanation);
            }
        }
    }
}
