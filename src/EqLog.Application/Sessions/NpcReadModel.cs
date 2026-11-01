using EqLog.Core;
using EqLog.Core.Parsing;

namespace EqLog.Application.Sessions;

public enum NpcEncounterOutcome
{
    Active,
    LoadoutChanged,
    PlayerDied,
    NpcDied,
    Zoned,
    Login,
    Unknown,
}

/// <summary>Prepared encounter values; calculating durations and damage rates never runs in presentation.</summary>
public sealed record NpcEncounterDetails(string Id, DateTime Start, string Character, string? Classes, int? Level,
    NpcEncounterOutcome Outcome, long YourDamage, long OtherDamage, long IncomingDamage, double Seconds, double YourDps,
    IReadOnlyList<NpcSpellHistory> Spells, string? UnrecognizedOutcome = null);

public sealed record NpcReadModel(string Mob, string Zone, DateTime FirstSeen, DateTime LastSeen, long Considers,
    ConsiderEvent? LastConsider, string? LastAssessment, IReadOnlyList<NpcEncounterDetails> Encounters,
    double YourAverageDps, double OtherAverageDps, long YourDamage, long OtherDamage, long IncomingDamage,
    int ObservedDeaths, bool HasPersonalDamage, bool HasOtherDamage)
{
    internal static NpcReadModel From(NpcEntry entry)
    {
        return new(entry.Mob, entry.Zone, entry.FirstSeen, entry.LastSeen, entry.Considers, entry.LastConsider,
            entry.LastAssessment, entry.Encounters.Select(encounter => new NpcEncounterDetails(encounter.Id,
                encounter.Start, encounter.Character, encounter.Classes, encounter.Level, Outcome(encounter.Outcome),
                encounter.YourDamage, encounter.OtherDamage, encounter.IncomingDamage, encounter.Seconds,
                encounter.YourDps, encounter.Spells, Outcome(encounter.Outcome) == NpcEncounterOutcome.Unknown ? encounter.Outcome : null)).ToArray(), entry.YourAverageDps, entry.OtherAverageDps,
            entry.Encounters.Sum(encounter => encounter.YourDamage), entry.Encounters.Sum(encounter => encounter.OtherDamage),
            entry.Encounters.Sum(encounter => encounter.IncomingDamage),
            entry.Encounters.Count(encounter => Outcome(encounter.Outcome) == NpcEncounterOutcome.NpcDied),
            entry.Encounters.Any(encounter => encounter.YourDamage > 0),
            entry.Encounters.Any(encounter => encounter.OtherDamage > 0));
    }

    private static NpcEncounterOutcome Outcome(string stored)
    {
        return stored switch
        {
            "Active" => NpcEncounterOutcome.Active,
            "Loadout changed" => NpcEncounterOutcome.LoadoutChanged,
            "You died" => NpcEncounterOutcome.PlayerDied,
            "NPC died" => NpcEncounterOutcome.NpcDied,
            "Zoned" => NpcEncounterOutcome.Zoned,
            "Login" => NpcEncounterOutcome.Login,
            _ => NpcEncounterOutcome.Unknown,
        };
    }
}
