using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;

namespace EqLog.Application.Sessions;

/// <summary>Detached query results used by presenters; no rendering code receives the live engine.</summary>
public sealed record EngineQuerySnapshot(string? SpawnZone, IReadOnlyList<SpawnCandidate> SpawnCandidates,
    IReadOnlyList<SpellDurationInfo> SpellDurations, IReadOnlyList<ChatEvent> Chat)
{
    public static EngineQuerySnapshot Capture(LogEngine engine, ISet<ChatChannel>? channels = null, bool includeChat = true,
        bool includeSpawns = true, bool includeSpellDurations = true)
    {
        return new(engine.SpawnZone, includeSpawns ? Array.AsReadOnly(engine.SpawnCandidates().ToArray()) : [],
            includeSpellDurations ? Array.AsReadOnly(engine.SpellDurations(highestRanksOnly: true).ToArray()) : [],
            includeChat ? Array.AsReadOnly(engine.RecentChat(60, channels).ToArray()) : []);
    }
}
