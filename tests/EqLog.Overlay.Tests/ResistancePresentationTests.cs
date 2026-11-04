using EqLog.Application.Sessions;
using EqLog.Core;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class ResistancePresentationTests
{
    [Theory]
    [InlineData(7, 13, "Partial \u2014 65% (13/20)")]
    [InlineData(0, 4, "Resists \u2014 100% (4/4)")]
    [InlineData(10, 0, "Not Resistant \u2014 0% (0/10)")]
    [InlineData(1, 1, "Partially Resistant \u2014 50% (1/2)")]
    [InlineData(80, 20, "Partially Resistant \u2014 20% (20/100)")]
    [InlineData(81, 19, "Not Resistant \u2014 19% (19/100)")]
    [InlineData(0, 0, "Unknown \u2014 no assessed outcomes")]
    public void Describe_RendersPreparedResistanceValues(long landed, long resisted, string expected)
    {
        var observation = new ResistanceObservation(ResistanceType.Fire, landed, resisted, 50, UnassessedDamage: 3);
        var details = new ResistanceDetails(observation.Type, observation.Status, landed, resisted,
            observation.AssessedOutcomes, observation.ResistPercent, observation.OtherResisted, 0, 3);
        Assert.Equal(expected, ResistancePresentation.Describe(details));
    }

    [Fact]
    public void Spell_DotEvidenceUsesPreparedCastRateNotAllResists()
    {
        var card = new NpcSpellDetails("Poison", ResistanceType.Poison, NpcSpellResultKind.DamageOverTime,
            SpellAssessmentKind.DamageOverTime, "Single", new(2, 0, 0, 1, 1), null, 2, 50);
        var rendered = ResistancePresentation.Spell(card);
        Assert.Equal("Poison DoT  /  50% resisted (1/2)", rendered.Result);
        Assert.Contains(("All resists", "2"), rendered.Evidence);
        Assert.Contains("first tick landed", rendered.Explanation);
    }

    [Fact]
    public void Spell_DirectDamageRendersEveryEvidenceAndBaselineExplanation()
    {
        var resistance = new ResistanceDetails(ResistanceType.Fire, ResistanceStatus.NotResistant, 3, 0, 3, 0, 0, 1, 2);
        var card = new NpcSpellDetails("Fire", ResistanceType.Fire, NpcSpellResultKind.DirectDamage,
            SpellAssessmentKind.DirectDamage, "Single", new(0, 4, 5), resistance, 0, null);
        var rendered = ResistancePresentation.Spell(card);
        Assert.Equal("Fire  /  Not Resistant \u2014 0% (0/3)", rendered.Result);
        Assert.Equal([("Confirmed landings", "3"), ("Full resists", "0"), ("Reduced damage", "1")], rendered.Evidence);
        Assert.Equal(
            "2 landed hits need at least 3 damage samples for a typical-damage baseline for this character/level/rank, shared across class loadouts.\n" +
            "Reduced-damage hits are below 90% of the sample-weighted median; cause unknown. They count as landed, not resisted.\n" +
            "4 landings from older history have no damage samples; they count as landed, but reduced damage cannot be assessed.", rendered.Explanation);
    }

    [Theory]
    [InlineData(null, "Spell metadata has not been loaded")]
    [InlineData(SpellAssessmentKind.AreaEffect, "Area-effect spell")]
    [InlineData(SpellAssessmentKind.Duration, "Duration spell")]
    [InlineData(SpellAssessmentKind.MissingDetails, "Wiki target/damage details are missing")]
    [InlineData(SpellAssessmentKind.UnverifiedTarget, "Wiki target: Group")]
    public void Spell_MetadataReasonsAreWordedInPresentation(SpellAssessmentKind? assessment, string expected)
    {
        var card = new NpcSpellDetails("Spell", null, NpcSpellResultKind.MetadataOnly, assessment,
            "Group", new(2, 1, 3), null, 0, null);
        var rendered = ResistancePresentation.Spell(card);
        Assert.Equal("School not verified  /  Group", rendered.Result);
        Assert.StartsWith(expected, rendered.Explanation);
        Assert.Contains(("Landings without samples", "1"), rendered.Evidence);
    }

    [Fact]
    public void Spell_UnassessedDirectDamageAndDotCastsRemainExplicitlyUnknown()
    {
        var resistance = new ResistanceDetails(ResistanceType.Fire, ResistanceStatus.Unknown, 0, 0, 0, null, 0, 0, 0);
        var card = new NpcSpellDetails("Fire", ResistanceType.Fire, NpcSpellResultKind.DirectDamage,
            SpellAssessmentKind.DirectDamage, "Single", new(0, 0, 0), resistance, 0, null);
        Assert.Equal("No assessed damage outcomes yet.", ResistancePresentation.Spell(card).Explanation);
        Assert.Equal("Fire DoT  /  no assessed casts yet", ResistancePresentation.Spell(card with
        {
            Kind = NpcSpellResultKind.DamageOverTime,
            Assessment = SpellAssessmentKind.DamageOverTime,
        }).Result);
    }

    [Theory]
    [InlineData(NpcEncounterOutcome.Active, "Active")]
    [InlineData(NpcEncounterOutcome.LoadoutChanged, "Loadout changed")]
    [InlineData(NpcEncounterOutcome.PlayerDied, "You died")]
    [InlineData(NpcEncounterOutcome.NpcDied, "NPC died")]
    [InlineData(NpcEncounterOutcome.Zoned, "Zoned")]
    [InlineData(NpcEncounterOutcome.Login, "Login")]
    [InlineData(NpcEncounterOutcome.Unknown, "Unknown outcome (future outcome)")]
    public void NpcOutcomeText_LabelsTypedOutcomesInPresentation(NpcEncounterOutcome outcome, string expected)
    {
        var encounter = new NpcEncounterDetails("one", DateTime.UtcNow, "Que", null, null, outcome, 0, 0, 0, 1, 0, [],
            UnrecognizedOutcome: "future outcome");
        Assert.Equal(expected, MainWindow.NpcOutcomeText(encounter));
    }
}
