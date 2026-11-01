using EqLog.Core;
using EqLog.Core.Tracking;

namespace EqLog.Application.Motes;

public sealed record MoteCopySelection(IReadOnlyList<int> Spares, bool RemovedTarget, IReadOnlyList<int> HigherCopies);

public sealed record MoteTargetRanks(int Selected, IReadOnlyList<int> Ranks);

/// <summary>Inventory selection and input bounds for the mote planner.</summary>
public static class MotePlanningQueries
{
    public static IReadOnlyList<string> RankLabels
    {
        get;
    } =
        Array.AsReadOnly(Enumerable.Range(0, MoteValues.MaxRank + 1).Select(r => $"+{r}").ToArray());

    public static IReadOnlyList<string> FodderRankLabels
    {
        get;
    } =
        Array.AsReadOnly(Enumerable.Range(0, MoteValues.MaxRank).Select(r => $"+{r}").ToArray());

    public static int MaximumProgress(int startRank)
    {
        return startRank >= MoteValues.MaxRank ? 0 : MoteValues.PointsToNext(startRank) - 1;
    }

    public static bool CanAddProgress(int startRank)
    {
        return startRank < MoteValues.MaxRank;
    }

    public static MoteTargetRanks TargetRanks(int currentRank, int? selected)
    {
        int target = Math.Clamp(selected ?? MoteValues.MaxRank, currentRank, MoteValues.MaxRank);
        var ranks = Enumerable.Range(currentRank, MoteValues.MaxRank - currentRank + 1).ToArray();
        return new MoteTargetRanks(target, Array.AsReadOnly(ranks));
    }

    public static int? TargetRank(IEnumerable<InventoryExportItem> rows, string baseName)
    {
        return rows.Select(row => InventoryExport.SplitRank(row.Name))
            .Where(item => item.BaseName.Equals(baseName, StringComparison.OrdinalIgnoreCase))
            .Select(item => (int?)item.Rank).Max();
    }

    public static int? BestCopyRank(IEnumerable<InventoryExportItem> rows, string baseName)
    {
        int? rank = TargetRank(rows, baseName);
        return rank is int value ? Math.Min(value, MoteValues.MaxRank) : null;
    }

    public static int CurrentRank(int selectedIndex)
    {
        return Math.Max(0, selectedIndex);
    }

    public static string[] TargetNames(IEnumerable<InventoryExportItem> rows, string filter)
    {
        return rows.Select(row => InventoryExport.SplitRank(row.Name).BaseName)
            .Where(name => name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool ContainsTarget(IEnumerable<InventoryExportItem> rows, string baseName)
    {
        return rows.Any(row => InventoryExport.SplitRank(row.Name).BaseName.Equals(
            baseName, StringComparison.OrdinalIgnoreCase));
    }

    public static List<int> CopyRanks(IEnumerable<InventoryExportItem> rows, string baseName)
    {
        var ranks = new List<int>();
        foreach (var row in rows.Where(row => !row.Installed &&
            (!row.Collection || row.Location.Equals("Equipment", StringComparison.OrdinalIgnoreCase))))
        {
            var (name, rank) = InventoryExport.SplitRank(row.Name);
            if (name.Equals(baseName, StringComparison.OrdinalIgnoreCase))
            {
                ranks.AddRange(Enumerable.Repeat(rank, Math.Max(1, row.Count)));
            }
        }

        ranks.Sort();
        return ranks;
    }

    public static MoteCopySelection SelectCopies(IEnumerable<InventoryExportItem> rows, string baseName, int startRank)
    {
        var copies = baseName.Length > 0 ? CopyRanks(rows, baseName) : [];
        bool removed = copies.Remove(startRank);
        var higher = copies.Where(c => c > startRank).ToArray();
        var spares = copies.Where(c => c <= startRank).ToArray();
        return new MoteCopySelection(Array.AsReadOnly(spares), removed, Array.AsReadOnly(higher));
    }
}
