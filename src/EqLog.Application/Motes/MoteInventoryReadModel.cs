using System.Collections.ObjectModel;
using EqLog.Application.Inventory;
using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;

namespace EqLog.Application.Motes;

public sealed record MoteTierReadModel(
    string Tier, string ItemName, string Count, bool HasSessionDrops, string Owned, string Value, string ToolTip);

public sealed record MoteRecentReadModel(string Text, string Time, bool IsYou, string ItemName);

public sealed record MoteRateReadModel(string Mob, string Rate);

public sealed record MoteOverviewReadModel(
    IReadOnlyList<MoteTierReadModel> Tiers, IReadOnlyList<MoteRecentReadModel> Recent,
    IReadOnlyList<MoteRateReadModel> ZoneRates, string Subtitle, string TotalCount,
    string OwnedTotal, string InventoryToolTip, string HeaderToolTip);

/// <summary>Detached, immutable owned mote counts for one character.</summary>
public sealed class MoteInventoryReadModel
{
    private const string ValuesTip =
        "A mote adds points to gear and spells. In the Gear/Spell column, the first number is for gear; the second is for spells.\n" +
        "The ≤+N part means this mote works on gear at +N or lower. Motes work on spells at any rank.\n" +
        "Example: 5/16 ≤+4 means 5 gear points at +4 or lower, or 16 spell points at any rank. " +
        "Values are from the EQLWiki Mote Guide.";

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
        string inventoryTip = (_exportTime is DateTime exported
            ? $"Export {exported.ToLocalTime():g}; owned counts are confirmed by this export."
            : "No inventory export: export inventory to confirm owned counts.") +
            "\nOwned excludes collection, installed items and historical drops." +
            "\nLoot logs do not change Owned. Run /outputfile inventory after looting, spending, or moving motes to refresh.";
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
            string owned = _exportTime.HasValue || _ownedByTier.ContainsKey(tier)
                ? _ownedByTier.GetValueOrDefault(tier).ToString("N0") : "?";
            string value = "?";
            string tip = $"No upgrade value is known for {tier}.\n\n{inventoryTip}";
            if (MoteValues.RankOf(tier) is int rank)
            {
                value = $"{MoteValues.ItemPoints(rank)}/{MoteValues.SpellPoints(rank)} ≤+{MoteValues.ItemMaxTier(rank)}";
                tip = $"{MoteTiers.ItemName(tier)} (rank {rank})\n" +
                    $"Adds {MoteValues.ItemPoints(rank)} points to gear at +{MoteValues.ItemMaxTier(rank)} or lower.\n" +
                    $"Adds {MoteValues.SpellPoints(rank)} points to spells at any rank.\n\n" + inventoryTip;
            }

            rows.Add(new MoteTierReadModel(tier, MoteTiers.ItemName(tier),
                $"{count}  ({allTime.GetValueOrDefault(tier)})", count > 0, owned, value, tip));
        }

        string total = _exportTime.HasValue || _ownedByTier.Count > 0
            ? _ownedByTier.Values.Sum().ToString("N0") : "?";
        var recent = snapshot.Recent.Take(5).Select(d =>
        {
            bool isYou = d.Looter == Names.You;
            string who = isYou ? "" : $"[{d.Looter}] ";
            string quantity = d.Quantity > 1 ? $" x{d.Quantity}" : "";
            return new MoteRecentReadModel($"{who}{d.Tier}{quantity}  {d.Source}", d.Time.ToString("HH:mm"),
                isYou, MoteTiers.ItemName(d.Tier));
        }).ToArray();
        var rates = snapshot.ZoneRates.Take(10).Select(r =>
        {
            string rate = r.DropRate is double value ? $"  {value:P0}" : "";
            return new MoteRateReadModel(r.Mob, $"{r.Drops}/{r.Kills}{rate}");
        }).ToArray();
        return new MoteOverviewReadModel(rows.AsReadOnly(), Array.AsReadOnly(recent),
            Array.AsReadOnly(rates), $"{snapshot.SessionTotal} this session  {snapshot.PerHour(now):0.#}/hr",
            $"{snapshot.SessionTotal}  ({snapshot.AllTimeTotal})", total, inventoryTip, ValuesTip + "\n\n" + inventoryTip);
    }
}
