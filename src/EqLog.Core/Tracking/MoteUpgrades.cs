namespace EqLog.Core.Tracking;

/// <summary>What is being upgraded. Equipment and spells use different mote values.</summary>
public enum UpgradeKind
{
    Equipment,
    Spell,
}

/// <summary>Mote values from the EQLWiki Mote Guide and Item/Spell Upgrade System pages.</summary>
public static class MoteValues
{
    /// <summary>Highest upgrade rank (+10).</summary>
    public const int MaxRank = 10;

    /// <summary>Tier names from rank 1 to 10. Rank 4 is the plain "Mote of Potential".</summary>
    public static readonly IReadOnlyList<string> Tiers =
    [
        "Infinitesimal", "Minor", "Lesser", MoteTiers.PlainTier, "Major", "Greater", "Superior", "Grand", "Ascendant", "Infinite",
    ];

    private static readonly int[] ItemPointsByRank = [1, 1, 2, 4, 5, 6, 7, 8, 9, 10];

    /// <summary>The mote's rank (1-10), or null for a tier the guide doesn't list.</summary>
    public static int? RankOf(string tier)
    {
        for (int i = 0; i < Tiers.Count; i++)
        {
            if (Tiers[i].Equals(tier, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return null;
    }

    /// <summary>Points one mote of <paramref name="rank"/> adds to equipment.</summary>
    public static int ItemPoints(int rank)
    {
        return ItemPointsByRank[rank - 1];
    }

    /// <summary>Highest equipment +rank the mote can be used on (a rank 5 Major mote works up to +4).</summary>
    public static int ItemMaxTier(int rank)
    {
        return rank - 1;
    }

    /// <summary>Points one mote of <paramref name="rank"/> adds to a spell (doubles each rank; no tier limit).</summary>
    public static int SpellPoints(int rank)
    {
        return 1 << (rank - 1);
    }

    public static int Points(UpgradeKind kind, int rank)
    {
        return kind == UpgradeKind.Spell ? SpellPoints(rank) : ItemPoints(rank);
    }

    /// <summary>Points needed to go from +<paramref name="rank"/> to the next rank (1, 2, 4 ... 512).</summary>
    public static int PointsToNext(int rank)
    {
        return 1 << rank;
    }

    /// <summary>Points an equipment copy at +<paramref name="rank"/> adds when merged, assuming no partial progress.</summary>
    public static int CopyPoints(int rank)
    {
        return 1 << rank;
    }
}

/// <summary>Something spent on one upgrade step: <paramref name="Count"/> motes of <paramref name="Rank"/>, or item copies at +Rank.</summary>
public sealed record UpgradeUse(int Rank, int Count, bool IsCopy);

/// <summary>
/// One +N to +N+1 step. <paramref name="Missing"/> counts motes of the matching tier (rank N+1) still needed after
/// owned motes and copies run out. <paramref name="CarryAfter"/> is the progress toward the next rank once this step completes.
/// </summary>
public sealed record UpgradeStep(int FromRank, int Needed, int StartPoints, IReadOnlyList<UpgradeUse> Used, int Missing, int CarryAfter)
{
    public int ToRank
    {
        get
        {
            return FromRank + 1;
        }
    }

    /// <summary>The tier name of the mote counted in <see cref="Missing"/>.</summary>
    public string MissingTier
    {
        get
        {
            return MoteValues.Tiers[FromRank];
        }
    }
}

/// <summary>How spare equipment copies are fed with motes before they are merged into the item being upgraded.</summary>
/// <param name="Enabled">Feed copies at all.</param>
/// <param name="FeedUsableMotes">Also feed motes the main item could still use; otherwise only motes above its rank limit.</param>
/// <param name="FodderCopies">Extra copies available to farm; these can be merged as-is or fed first.</param>
/// <param name="FodderRank">The current rank of extra copies available to farm.</param>
public sealed record FeedOptions(bool Enabled = true, bool FeedUsableMotes = true, int FodderCopies = 0, int FodderRank = 0);

/// <summary>
/// A spare copy raised from <paramref name="StartRank"/> to <paramref name="EndRank"/> with <paramref name="Motes"/> before merging.
/// <paramref name="Wasted"/> is the progress past <paramref name="EndRank"/>, which merging doesn't pass on.
/// </summary>
public sealed record FedCopy(int StartRank, int EndRank, bool IsFodder, IReadOnlyList<UpgradeUse> Motes, int Wasted)
{
    public int MergePoints
    {
        get
        {
            return MoteValues.CopyPoints(EndRank);
        }
    }
}

/// <summary>
/// The upgrade plan. <paramref name="Fed"/> lists copies fed with motes first (their end ranks are the copies used in
/// <paramref name="Steps"/>). <paramref name="Baseline"/> is the plan without feeding, for comparison, when feeding was tried.
/// </summary>
public sealed record UpgradePlan(UpgradeKind Kind, IReadOnlyList<UpgradeStep> Steps,
    IReadOnlyList<FedCopy>? Fed = null, UpgradePlan? Baseline = null)
{
    public IReadOnlyList<FedCopy> FedCopies
    {
        get
        {
            return Fed ?? [];
        }
    }

    /// <summary>Owned motes spent per rank, including motes fed to copies.</summary>
    public IReadOnlyDictionary<int, int> MotesUsed
    {
        get
        {
            return Steps.SelectMany(s => s.Used)
                .Concat(FedCopies.SelectMany(f => f.Motes))
                .Where(u => !u.IsCopy)
                .GroupBy(u => u.Rank)
                .ToDictionary(g => g.Key, g => g.Sum(u => u.Count));
        }
    }

    /// <summary>Copies merged per +rank (after feeding) across all steps.</summary>
    public IReadOnlyDictionary<int, int> CopiesUsed
    {
        get
        {
            return Steps.SelectMany(s => s.Used)
                .Where(u => u.IsCopy)
                .GroupBy(u => u.Rank)
                .ToDictionary(g => g.Key, g => g.Sum(u => u.Count));
        }
    }

    /// <summary>Missing motes per rank across all steps.</summary>
    public IReadOnlyDictionary<int, int> Missing
    {
        get
        {
            return Steps.Where(s => s.Missing > 0)
                .GroupBy(s => s.FromRank + 1)
                .ToDictionary(g => g.Key, g => g.Sum(s => s.Missing));
        }
    }

    /// <summary>Points still to be found as missing motes.</summary>
    public long MissingPoints
    {
        get
        {
            return Steps.Sum(s => (long)s.Missing * MoteValues.Points(Kind, s.FromRank + 1));
        }
    }

    /// <summary>Owned motes spent, each weighted by 2^rank, so spending small motes instead of big ones scores lower.</summary>
    public long UsedWeight
    {
        get
        {
            return MotesUsed.Sum(kv => (long)kv.Value << kv.Key);
        }
    }

    /// <summary>Fewer missing points wins; on a tie, the plan that spends smaller owned motes wins.</summary>
    public bool IsBetterThan(UpgradePlan other)
    {
        if (MissingPoints != other.MissingPoints)
        {
            return MissingPoints < other.MissingPoints;
        }

        return UsedWeight < other.UsedWeight;
    }

    /// <summary>Per rank, how many fewer motes (used or missing) this plan needs than <see cref="Baseline"/>; negative means more.</summary>
    public IReadOnlyDictionary<int, int> SavedVersusBaseline
    {
        get
        {
            var saved = new Dictionary<int, int>();
            if (Baseline is null)
            {
                return saved;
            }

            for (int rank = 1; rank <= MoteValues.MaxRank; rank++)
            {
                int before = Baseline.MotesUsed.GetValueOrDefault(rank) + Baseline.Missing.GetValueOrDefault(rank);
                int after = MotesUsed.GetValueOrDefault(rank) + Missing.GetValueOrDefault(rank);
                if (before != after)
                {
                    saved[rank] = before - after;
                }
            }

            return saved;
        }
    }
}

/// <summary>
/// Plans upgrading an item or spell to +10 with owned motes and spare item copies.
/// Motes and copies add fixed points; points past a rank carry into the next one.
/// Equipment steps spend only motes matching the step's tier, preserving higher tiers for later ranks, then copies
/// (which have no tier limit). Spells spend the lowest motes first. Nothing is condensed: once owned motes and copies
/// run out, the rest of each step is listed as missing motes of the step's matching tier.
/// <para>
/// With feeding, spare copies are raised first with small motes (a copy at +0 still takes motes the main item has
/// outgrown), then merged. Raising a copy to +N costs 2^N - 1 points and merging it gives 2^N, so the points carry over
/// while small motes stand in for big ones. The fed plan is only kept if it leaves fewer points missing, or the same with smaller motes spent.
/// </para>
/// </summary>
public static class MoteUpgradePlanner
{
    /// <param name="ownedMotes">Owned motes by rank (1-10).</param>
    /// <param name="copies">+rank of each spare equipment copy to merge in (ignored for spells).</param>
    /// <param name="feed">Feed spare copies with motes first (equipment only); null plans without feeding.</param>
    public static UpgradePlan Plan(UpgradeKind kind, int startRank, int startPoints,
        IReadOnlyDictionary<int, int> ownedMotes, IReadOnlyList<int> copies, FeedOptions? feed = null,
        int targetRank = MoteValues.MaxRank)
    {
        if (startRank is < 0 or > MoteValues.MaxRank)
        {
            throw new ArgumentOutOfRangeException(nameof(startRank), "Rank must be 0 to 10.");
        }

        if (targetRank < startRank || targetRank > MoteValues.MaxRank)
        {
            throw new ArgumentOutOfRangeException(nameof(targetRank), "Target rank must be between the current rank and 10.");
        }

        var motes = new int[MoteValues.MaxRank + 1];
        foreach (var (rank, count) in ownedMotes)
        {
            if (rank is >= 1 and <= MoteValues.MaxRank && count > 0)
            {
                motes[rank] += count;
            }
        }

        var spareCopies = kind == UpgradeKind.Equipment
            ? copies.Where(c => c is >= 0 and <= MoteValues.MaxRank).OrderBy(c => c).ToList()
            : [];
        int extraCopyCount = kind == UpgradeKind.Equipment ? Math.Max(0, feed?.FodderCopies ?? 0) : 0;
        int extraCopyRank = feed?.FodderRank ?? 0;
        if (extraCopyCount > 0 && extraCopyRank is < 0 or > MoteValues.MaxRank)
        {
            throw new ArgumentOutOfRangeException(nameof(feed), "Farmed copy rank must be 0 to 10.");
        }

        var availableCopies = spareCopies
            .Concat(Enumerable.Repeat(extraCopyRank, extraCopyCount))
            .OrderBy(c => c)
            .ToList();
        int points = startRank < MoteValues.MaxRank
            ? Math.Clamp(startPoints, 0, MoteValues.PointsToNext(startRank) - 1)
            : 0;
        var baseline = new UpgradePlan(kind, Simulate(kind, startRank, points, (int[])motes.Clone(), [.. availableCopies], targetRank));
        if (kind != UpgradeKind.Equipment || feed is not { Enabled: true } || startRank >= targetRank)
        {
            return baseline;
        }

        int remaining = (1 << targetRank) - (1 << startRank) - points;
        var fedMotes = (int[])motes.Clone();
        var fed = Feed(fedMotes, spareCopies, extraCopyCount, extraCopyRank, feed.FeedUsableMotes, startRank, remaining);
        var fedSteps = Simulate(kind, startRank, points, fedMotes, fed.Select(f => f.EndRank).OrderBy(r => r).ToList(), targetRank);
        var fedPlan = new UpgradePlan(kind, fedSteps, fed.Where(f => f.Motes.Count > 0).ToArray(), baseline);
        return fedPlan.FedCopies.Count > 0 && fedPlan.IsBetterThan(baseline)
            ? fedPlan
            : baseline with
            {
                Baseline = baseline,
            };
    }

    /// <summary>
    /// Finds the fewest additional copies of the selected rank needed to reach the target using the supplied motes and copies.
    /// Any extra copies already listed in <paramref name="feed"/> are included in the plan.
    /// </summary>
    /// <param name="startRank">The current equipment rank.</param>
    /// <param name="startPoints">Points already invested toward the next rank.</param>
    /// <param name="ownedMotes">Owned motes by rank (1-10).</param>
    /// <param name="copies">Available spare equipment copies by rank.</param>
    /// <param name="feed">Feeding settings and any extra copies already available to farm.</param>
    /// <returns>The minimum number of additional copies of the selected farm rank required to reach the target.</returns>
    public static int MinimumAdditionalCopiesToFinish(int startRank, int startPoints,
        IReadOnlyDictionary<int, int> ownedMotes, IReadOnlyList<int> copies, FeedOptions feed,
        int targetRank = MoteValues.MaxRank)
    {
        ArgumentNullException.ThrowIfNull(ownedMotes);
        ArgumentNullException.ThrowIfNull(copies);
        ArgumentNullException.ThrowIfNull(feed);

        if (startRank is < 0 or > MoteValues.MaxRank)
        {
            throw new ArgumentOutOfRangeException(nameof(startRank), "Rank must be 0 to 10.");
        }

        if (feed.FodderRank is < 0 or >= MoteValues.MaxRank)
        {
            throw new ArgumentOutOfRangeException(nameof(feed), "Farmed copy rank must be 0 to 9.");
        }

        int effectivePoints = startRank < MoteValues.MaxRank
            ? Math.Clamp(startPoints, 0, MoteValues.PointsToNext(startRank) - 1)
            : 0;
        int pointsRemaining = Math.Max(0, (1 << targetRank) - (1 << startRank) - effectivePoints);
        int farmCopyPoints = MoteValues.CopyPoints(feed.FodderRank);
        int high = CeilDiv(pointsRemaining, farmCopyPoints);
        var current = Plan(UpgradeKind.Equipment, startRank, effectivePoints, ownedMotes, copies, feed, targetRank);
        if (current.MissingPoints == 0 || high == 0)
        {
            return 0;
        }

        int low = 1;
        while (low < high)
        {
            int additional = low + (high - low) / 2;
            var candidateFeed = feed with
            {
                FodderCopies = Math.Max(0, feed.FodderCopies) + additional,
            };
            var candidate = Plan(UpgradeKind.Equipment, startRank, effectivePoints, ownedMotes, copies, candidateFeed, targetRank);
            if (candidate.MissingPoints == 0)
            {
                high = additional;
            }
            else
            {
                low = additional + 1;
            }
        }

        return low;
    }

    private sealed class Feeder(int rank, bool isFodder)
    {
        public int StartRank { get; } = rank;
        public bool IsFodder { get; } = isFodder;
        public int Rank { get; set; } = rank;
        public int Progress { get; set; }
        public bool Active { get; set; } = true;
        public List<UpgradeUse> Motes { get; } = [];
    }

    /// <summary>
    /// Raises spare copies one rank at a time, always the lowest copy first so each small mote goes where it is still
    /// allowed. Only motes matching the copy's next rank are used, so higher tiers are reserved for later ranks. A rank is
    /// only started if owned motes can finish it. Stops once the copies hold enough points to reach +10.
    /// </summary>
    private static List<FedCopy> Feed(int[] motes, IReadOnlyList<int> copies, int fodder, int fodderRank,
        bool feedUsable, int targetRank, int remaining)
    {
        var feeders = copies.Select(c => new Feeder(c, isFodder: false))
            .Concat(Enumerable.Range(0, fodder).Select(_ => new Feeder(fodderRank, isFodder: true)))
            .ToList();
        while (true)
        {
            long held = feeders.Sum(f => (long)MoteValues.CopyPoints(f.Rank));
            if (held >= remaining)
            {
                break;
            }

            var next = feeders.Where(f => f.Active)
                .OrderBy(f => f.Rank)
                .ThenBy(f => f.IsFodder)
                .FirstOrDefault();
            if (next is null)
            {
                break;
            }

            if (next.Rank >= MoteValues.MaxRank || !TryRaise(next, motes, feedUsable, targetRank))
            {
                next.Active = false;
            }
        }

        return feeders.Select(f => new FedCopy(f.StartRank, f.Rank, f.IsFodder, f.Motes, f.Progress))
            .ToList();
    }

    private static bool TryRaise(Feeder feeder, int[] motes, bool feedUsable, int targetRank)
    {
        int needed = MoteValues.PointsToNext(feeder.Rank);
        int points = feeder.Progress;
        var take = new List<UpgradeUse>();
        for (int moteRank = 1; moteRank <= MoteValues.MaxRank && points < needed; moteRank++)
        {
            bool allowed = moteRank == feeder.Rank + 1 &&
                (feedUsable || MoteValues.ItemMaxTier(moteRank) < targetRank);
            if (motes[moteRank] == 0 || !allowed)
            {
                continue;
            }

            int value = MoteValues.ItemPoints(moteRank);
            int count = Math.Min(motes[moteRank], CeilDiv(needed - points, value));
            points += count * value;
            take.Add(new UpgradeUse(moteRank, count, IsCopy: false));
        }

        if (points < needed)
        {
            return false;
        }

        foreach (var use in take)
        {
            motes[use.Rank] -= use.Count;
            int index = feeder.Motes.FindIndex(u => u.Rank == use.Rank);
            if (index < 0)
            {
                feeder.Motes.Add(use);
            }
            else
            {
                feeder.Motes[index] = use with { Count = feeder.Motes[index].Count + use.Count };
            }
        }

        while (points >= MoteValues.PointsToNext(feeder.Rank) && feeder.Rank < MoteValues.MaxRank)
        {
            points -= MoteValues.PointsToNext(feeder.Rank);
            feeder.Rank++;
        }

        feeder.Progress = points;
        return true;
    }

    private static List<UpgradeStep> Simulate(UpgradeKind kind, int startRank, int points, int[] motes, List<int> spareCopies,
        int targetRank)
    {
        var steps = new List<UpgradeStep>();
        for (int rank = startRank; rank < targetRank; rank++)
        {
            int needed = MoteValues.PointsToNext(rank);
            int start = points;
            var used = new List<UpgradeUse>();
            for (int moteRank = 1; moteRank <= MoteValues.MaxRank && points < needed; moteRank++)
            {
                if (motes[moteRank] == 0 || (kind == UpgradeKind.Equipment && moteRank != rank + 1))
                {
                    continue;
                }

                int value = MoteValues.Points(kind, moteRank);
                int count = Math.Min(motes[moteRank], CeilDiv(needed - points, value));
                motes[moteRank] -= count;
                points += count * value;
                used.Add(new UpgradeUse(moteRank, count, IsCopy: false));
            }

            while (points < needed && spareCopies.Count > 0)
            {
                int copy = spareCopies[0];
                spareCopies.RemoveAt(0);
                points += MoteValues.CopyPoints(copy);
                AddCopy(used, copy);
            }

            int missing = 0;
            if (points < needed)
            {
                int value = MoteValues.Points(kind, rank + 1);
                missing = CeilDiv(needed - points, value);
                points += missing * value;
            }

            points -= needed;
            steps.Add(new UpgradeStep(rank, needed, start, used, missing, points));
        }

        return steps;
    }

    private static void AddCopy(List<UpgradeUse> used, int rank)
    {
        int index = used.FindIndex(u => u.IsCopy && u.Rank == rank);
        if (index < 0)
        {
            used.Add(new UpgradeUse(rank, 1, IsCopy: true));
        }
        else
        {
            used[index] = used[index] with { Count = used[index].Count + 1 };
        }
    }

    private static int CeilDiv(int value, int divisor)
    {
        return (value + divisor - 1) / divisor;
    }
}
