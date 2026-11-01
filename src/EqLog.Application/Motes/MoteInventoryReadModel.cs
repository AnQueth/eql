using System.Collections.ObjectModel;
using EqLog.Application.Inventory;
using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;

namespace EqLog.Application.Motes;

public sealed record MoteTierReadModel(
    string Tier, string ItemName, int SessionCount, int AllTimeCount, int? Owned,
    int? Rank, int? EquipmentPoints, int? SpellPoints, int? MaximumEquipmentRank);

public sealed record MoteRecentReadModel(
    string Tier, string Looter, int Quantity, string Source, DateTime Time, string ItemName);

public sealed record MoteRateReadModel(string Mob, int Drops, int Kills, double? DropRate);

public sealed record MoteOverviewReadModel(
    IReadOnlyList<MoteTierReadModel> Tiers, IReadOnlyList<MoteRecentReadModel> Recent,
    IReadOnlyList<MoteRateReadModel> ZoneRates, int SessionTotal, int AllTimeTotal, double PerHour,
    int? OwnedTotal, DateTime? ExportTime);

/// <summary>Detached, immutable owned mote counts for one character.</summary>
public sealed class MoteInventoryReadModel
{
    private readonly IReadOnlyDictionary<string, int> _ownedByTier;
    private readonly DateTime? _exportTime;

    private MoteInventoryReadModel(Dictionary<string, int> ownedByTier, Dictionary<int, int> ownedByRank, DateTime? exportTime)
    {
        _ownedByTier = new ReadOnlyDictionary<string, int>(ownedByTier);
        OwnedByRank = new ReadOnlyDictionary<int, int>(ownedByRank);
        _exportTime = exportTime;
    }

    public IReadOnlyDictionary<int, int> OwnedByRank
    {
        get;
    }

    public static MoteInventoryReadModel Empty { get; } = Capture([], null);

    public static MoteInventoryReadModel Capture(
        IEnumerable<InventoryItemSummary> items, string? characterName, DateTime? exportTime = null)
    {
        var byTier = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byRank = new Dictionary<int, int>();
        foreach (var item in items)
        {
            if (!item.Character.Equals(characterName, StringComparison.OrdinalIgnoreCase) ||
                MoteTiers.TierOf(ItemNames.Normalize(item.Item)) is not string tier)
            {
                continue;
            }

            byTier[tier] = byTier.GetValueOrDefault(tier) + item.Owned;
            if (item.Owned > 0 && MoteValues.RankOf(tier) is int rank)
            {
                byRank[rank] = byRank.GetValueOrDefault(rank) + item.Owned;
            }
        }

        return new MoteInventoryReadModel(byTier, byRank, exportTime);
    }

    public MoteOverviewReadModel Prepare(MoteSnapshot snapshot, DateTime now)
    {
        var allTime = snapshot.AllTime.ToDictionary(c => c.Tier, c => c.Count, StringComparer.OrdinalIgnoreCase);
        var session = snapshot.Session.ToDictionary(c => c.Tier, c => c.Count, StringComparer.OrdinalIgnoreCase);
        var tiers = allTime.Keys.Union(session.Keys, StringComparer.OrdinalIgnoreCase)
            .Union(_ownedByTier.Keys, StringComparer.OrdinalIgnoreCase)
            .Union(MoteValues.Tiers, StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => MoteValues.RankOf(t) ?? int.MaxValue)
            .ThenBy(t => t, StringComparer.OrdinalIgnoreCase);
        var rows = new List<MoteTierReadModel>();
        foreach (string tier in tiers)
        {
            int count = session.GetValueOrDefault(tier);
            int? owned = _exportTime.HasValue || _ownedByTier.ContainsKey(tier)
                ? _ownedByTier.GetValueOrDefault(tier) : null;
            int? rank = MoteValues.RankOf(tier);
            rows.Add(new MoteTierReadModel(tier, MoteTiers.ItemName(tier), count,
                allTime.GetValueOrDefault(tier), owned, rank,
                rank is int gearRank ? MoteValues.ItemPoints(gearRank) : null,
                rank is int spellRank ? MoteValues.SpellPoints(spellRank) : null,
                rank is int maxRank ? MoteValues.ItemMaxTier(maxRank) : null));
        }

        int? total = _exportTime.HasValue || _ownedByTier.Count > 0
            ? _ownedByTier.Values.Sum() : null;
        var recent = snapshot.Recent.Take(5).Select(d => new MoteRecentReadModel(
            d.Tier, d.Looter, d.Quantity, d.Source, d.Time, MoteTiers.ItemName(d.Tier))).ToArray();
        var rates = snapshot.ZoneRates.Take(10).Select(r =>
            new MoteRateReadModel(r.Mob, r.Drops, r.Kills, r.DropRate)).ToArray();
        return new MoteOverviewReadModel(rows.AsReadOnly(), Array.AsReadOnly(recent),
            Array.AsReadOnly(rates), snapshot.SessionTotal, snapshot.AllTimeTotal, snapshot.PerHour(now),
            total, _exportTime);
    }
}
