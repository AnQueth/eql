namespace EqLog.Application.Sessions;

/// <summary>Saved effect totals with rates prepared using the enclosing encounter duration.</summary>
public sealed record NpcDpsAbility(string Name, long Damage, double Dps, double Percent, long Landings, long Resists);

/// <summary>One saved encounter and its named physical, spell, and proc damage breakdown.</summary>
public sealed record NpcDpsEncounter(NpcEncounterDetails Encounter, IReadOnlyList<NpcDpsAbility> Abilities);

/// <summary>Per-character, class-build, and level comparison for one NPC in one exact zone.</summary>
public sealed record NpcLoadoutDps(string Character, string? Classes, int? Level, int Encounters,
    int CompletedFights, long Damage, double Seconds, double? AverageDps, double? BestDps,
    IReadOnlyList<NpcDpsEncounter> History, double? MeanDps, double? P90Dps, double? P99Dps);

/// <summary>Prepares loadout comparisons without changing shared resistance or damage-learning data.</summary>
public static class NpcDpsHistory
{
    /// <summary>
    /// Groups personal encounters by their recorded context. Only observed NPC deaths enter comparison rates;
    /// unknown context and partial encounters remain visible without inferred loadouts or missing hit statistics.
    /// </summary>
    public static IReadOnlyList<NpcLoadoutDps> From(NpcReadModel npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        return npc.Encounters.Where(e => e.YourDamage > 0)
            .GroupBy(e => (Character: e.Character.Trim().ToUpperInvariant(),
                Classes: string.IsNullOrWhiteSpace(e.Classes) ? null : e.Classes.Trim().ToUpperInvariant(),
                e.Level))
            .Select(group =>
            {
                var history = group.OrderByDescending(e => e.Start).ThenBy(e => e.Id, StringComparer.Ordinal)
                    .Select(e => new NpcDpsEncounter(e, e.Spells
                        .OrderByDescending(s => s.Damage).ThenBy(s => s.Spell, StringComparer.OrdinalIgnoreCase)
                        .Select(s => new NpcDpsAbility(s.Spell, s.Damage, s.Damage / e.Seconds,
                            100.0 * s.Damage / e.YourDamage, s.Landings, s.Resists)).ToArray())).ToArray();
                var completed = group.Where(e => e.Outcome == NpcEncounterOutcome.NpcDied).ToArray();
                var rates = completed.Select(e => e.YourDps).Order().ToArray();
                long damage = completed.Sum(e => e.YourDamage);
                double seconds = completed.Sum(e => e.Seconds);
                return new NpcLoadoutDps(history[0].Encounter.Character, group.Key.Classes, group.Key.Level,
                    history.Length, completed.Length, damage, seconds,
                    seconds > 0 ? damage / seconds : null,
                    rates.Length > 0 ? rates[^1] : null, history,
                    rates.Length > 0 ? rates.Average() : null,
                    NearestRank(rates, 0.90), NearestRank(rates, 0.99));
            })
            .OrderByDescending(group => group.AverageDps)
            .ThenBy(group => group.Character, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Classes, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Level).ToArray();
    }

    private static double? NearestRank(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
        {
            return null;
        }
        return sorted[(int)Math.Ceiling(percentile * sorted.Length) - 1];
    }
}
