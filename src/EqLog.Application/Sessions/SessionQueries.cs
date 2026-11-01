using EqLog.Core.Tracking;

namespace EqLog.Application.Sessions;

/// <summary>Prepares session rates and detached loot ordering for presentation.</summary>
public static class SessionQueries
{
    /// <summary>Returns the domain experience rate after its initial warmup interval.</summary>
    public static double ExperiencePercentPerHour(SessionSnapshot snapshot, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.ExperiencePercentPerHour(now);
    }

    /// <summary>Returns a detached loot list in the selected domain ordering.</summary>
    public static IReadOnlyList<LootGroup> LootGroups(LootSnapshot snapshot, bool recentFirst)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.OrderedGroups(recentFirst).ToArray();
    }
}
