using EqLog.Core;

namespace EqLog.Application.Sessions;

public enum NpcSpellResultKind
{
    DirectDamage,
    DamageOverTime,
    MetadataOnly,
}

public sealed record NpcSpellDetails(
    string Spell, ResistanceType? School, NpcSpellResultKind Kind,
    SpellAssessmentKind? Assessment, string? TargetType, RawSpellCounts Counts,
    ResistanceDetails? Resistance, decimal DotAssessedCasts, decimal? DotResistPercent);

public sealed partial class GameSession
{
    public IReadOnlyList<NpcSpellDetails> NpcSpellDetails(string zone, string mob)
    {
        var results = new List<NpcSpellDetails>();
        foreach (string spell in Mobs.Get(zone, mob).Spells.Order(StringComparer.OrdinalIgnoreCase))
        {
            var details = Mobs.Get(zone, mob, spell);
            var counts = Mobs.GetRawSpellCounts(zone, mob, spell);
            var metadata = Mobs.GetSpell(spell);
            var observed = metadata?.Type is { } type ? details.Observations.Single(o => o.Type == type) : null;
            var kind = observed is not null && metadata?.SingleTargetDirectDamage == true
                ? NpcSpellResultKind.DirectDamage
                : metadata?.DamageOverTime == true ? NpcSpellResultKind.DamageOverTime : NpcSpellResultKind.MetadataOnly;
            decimal total = (decimal)counts.DotLanded + counts.DotResisted;
            results.Add(new(spell, metadata?.Type, kind, metadata?.AssessmentKind, metadata?.TargetType, counts,
                observed is null ? null : ResistanceDetails.From(observed), total,
                total == 0 ? null : 100m * counts.DotResisted / total));
        }
        return results.AsReadOnly();
    }
}
