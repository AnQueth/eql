using System.Globalization;
using EqLog.Core;
using EqLog.Core.Tracking;

namespace EqLog.Application.Motes;

/// <summary>Plans upgrades using the domain rules and prepares all explanations and arithmetic for presentation.</summary>
public static class MotePlanningService
{
    public static MotePlanReadModel Prepare(
        MotePlanningRequest request, IReadOnlyDictionary<int, int> owned, IEnumerable<InventoryExportItem> inventory)
    {
        bool equipment = request.Kind == MoteUpgradeKind.Equipment;
        var kind = equipment ? UpgradeKind.Equipment : UpgradeKind.Spell;
        int startRank = request.StartRank;
        int targetRank = request.TargetRank;
        bool fodderOk = ParsePoints(request.Fodder, 1023, out int fodder);
        int fodderRank = Math.Clamp(request.FodderRank, 0, MoteValues.MaxRank - 1);
        FeedOptions? feed = equipment
            ? new FeedOptions(request.Feeding, request.FeedUsableMotes,
                fodderOk && request.Page == MotePlanPage.Farm ? fodder : 0, fodderRank)
            : null;
        int maxPoints = MotePlanningQueries.MaximumProgress(startRank);
        bool pointsOk = ParsePoints(request.Points, maxPoints, out int points);
        int progress = pointsOk ? points : 0;
        var selection = equipment
            ? MotePlanningQueries.SelectCopies(inventory, request.Name, startRank)
            : new MoteCopySelection([], false, []);
        var copies = selection.Spares;
        bool hypothetical = equipment && request.Page == MotePlanPage.WhatIf && request.UseCopies &&
            !request.HypotheticalCopies.SequenceEqual(copies);
        IReadOnlyList<int> spare = request.UseCopies
            ? (request.Page == MotePlanPage.WhatIf ? request.HypotheticalCopies : copies)
            : [];
        var plannerMotes = request.UseMotes ? owned : new Dictionary<int, int>();
        var plan = MoteUpgradePlanner.Plan(kind, startRank, progress, plannerMotes, spare, feed, targetRank);

        var notes = new List<string>();
        if (hypothetical)
        {
            int originalPoints = copies.Sum(MoteValues.CopyPoints);
            int hypotheticalPoints = spare.Sum(MoteValues.CopyPoints);
            notes.Add($"WHAT IF: spare merge value {originalPoints:N0} to {hypotheticalPoints:N0} points " +
                $"before feeding; main item stays +{startRank}. Inventory is unchanged.");
        }

        if (!pointsOk)
        {
            notes.Add($"Points must be a whole number from 0 to {maxPoints} at +{startRank}; using 0.");
        }

        if (!fodderOk && request.Page == MotePlanPage.Farm)
        {
            notes.Add("Extra copies must be a whole number from 0 to 1023; using 0.");
        }

        if (equipment)
        {
            if (!request.HasInventoryExport)
            {
                notes.Add("No inventory export for this character yet, so no copies are counted. Run /outputfile inventory in game.");
            }
            else if (request.Name.Length > 0)
            {
                notes.Add(copies.Count == 0
                    ? (selection.RemovedTarget ? "No spare copies in inventory." : "No copies of this item in inventory.")
                    : $"{copies.Count} spare cop{(copies.Count == 1 ? "y" : "ies")} in inventory: {string.Join(", ", copies.Select(c => $"+{c}"))}" +
                        (selection.RemovedTarget ? $" (one +{startRank} is the item being upgraded)." : "."));
                if (selection.HigherCopies.Count > 0)
                {
                    int highest = selection.HigherCopies.Max();
                    notes.Add($"You already have a +{highest} copy, so it isn't counted as a spare. Set Current to +{highest} to plan from it.");
                }
            }

            if (feed is { Enabled: true } && plan.Baseline is not null && plan.FedCopies.Count == 0)
            {
                notes.Add("Feeding copies wouldn't save anything here, so it isn't used.");
            }
        }

        string tip = equipment
            ? "Each step uses its matching mote tier. Bigger motes are saved until a higher rank needs them. " +
                "Spare copies are merged after motes. Condensing and Void-Touched motes are not used."
            : "Spell motes double in value each tier (1 to 512 points) and work at any +level. " +
                "Smallest motes are spent first. Condensing and Void-Touched motes are not used.";
        var blocks = new List<MotePlanBlock>();
        if (plan.Steps.Count == 0)
        {
            blocks.Add(new MotePlanComplete($"Already at the +{targetRank} target. No points needed."));
        }
        else if (request.Page is MotePlanPage.Upgrade or MotePlanPage.WhatIf)
        {
            AddGuide(blocks, plan, startRank);
        }
        else
        {
            if (request.Page == MotePlanPage.Farm && feed is not null)
            {
                AddFarmGuide(blocks, startRank, progress, targetRank, plannerMotes, spare, feed);
            }

            AddMath(blocks, plan, startRank, targetRank, points, progress);
        }

        return new MotePlanReadModel(string.Join(Environment.NewLine, notes), tip, blocks.AsReadOnly());
    }

    public static string FarmTargetSummary(int targetRank, int farmRank, int additional)
    {
        return additional == 0
            ? $"Reach +{targetRank}: no more copies needed."
            : $"Reach +{targetRank}: farm {additional:N0} more +{farmRank} cop{(additional == 1 ? "y" : "ies")}.";
    }

    private static bool ParsePoints(string text, int maximum, out int points)
    {
        if (text.Trim().Length == 0)
        {
            points = 0;
            return true;
        }

        return int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out points) &&
            points <= maximum;
    }

    private static void AddFarmGuide(
        List<MotePlanBlock> blocks, int startRank, int points, int targetRank,
        IReadOnlyDictionary<int, int> motes, IReadOnlyList<int> spare, FeedOptions feed)
    {
        int fodderRank = feed.FodderRank;
        blocks.Add(new MotePlanHeading($"How many +{fodderRank} copies should I farm?"));
        blocks.Add(new MotePlanLine($"Your item starts at +{startRank}. Pick the level you want:", Muted: true));
        for (int target = startRank + 1; target <= targetRank; target++)
        {
            int additional = MoteUpgradePlanner.MinimumAdditionalCopiesToFinish(
                startRank, points, motes, spare, feed, targetRank: target);
            blocks.Add(new MotePlanLine(FarmTargetSummary(target, fodderRank, additional), Bold: true));
        }

        blocks.Add(new MotePlanLine("Each number is the TOTAL to farm from where you are now. Do not add the numbers together.",
            Muted: true));
        blocks.Add(new MotePlanLine("These numbers use the motes, spare copies and progress selected above.", Muted: true));
        if (feed.FodderCopies > 0)
        {
            blocks.Add(new MotePlanLine($"The {feed.FodderCopies:N0} extra copies you entered are already counted. " +
                "Only copies beyond those are listed here.", Muted: true));
        }

        if (feed.Enabled)
        {
            blocks.Add(new MotePlanLine("Feeding is on: the plan may spend your motes to raise farmed copies before merging them.",
                Muted: true));
        }

        int copyPoints = MoteValues.CopyPoints(fodderRank);
        var comparison = new List<string>
        {
            $"No motes, no spare copies, no feeding, and no saved progress. " +
                $"Just merge +{fodderRank} copies for ONE level at a time:",
        };
        for (int rank = startRank; rank < targetRank; rank++)
        {
            int count = (MoteValues.PointsToNext(rank) + copyPoints - 1) / copyPoints;
            comparison.Add($"+{rank} to +{rank + 1}: {count:N0} cop{(count == 1 ? "y" : "ies")} at +{fodderRank}.");
        }

        blocks.Add(new MotePlanComparison("Compare: copies only, one level at a time", comparison.AsReadOnly()));
    }

    private static void AddMath(List<MotePlanBlock> blocks, UpgradePlan plan, int startRank, int targetRank, int points, int progress)
    {
        if (plan.FedCopies.Count > 0)
        {
            blocks.Add(new MotePlanHeading("Feed copies", Detail: true));
            var feedRows = plan.FedCopies.Select(f => (IReadOnlyList<string>)Array.AsReadOnly(new[]
            {
                f.IsFodder ? $"new +{f.StartRank}" : $"+{f.StartRank}",
                $"+{f.EndRank}",
                DescribeUses(f.Motes),
                f.MergePoints.ToString("N0"),
                f.Wasted > 0 ? f.Wasted.ToString("N0") : "",
            })).ToArray();
            blocks.Add(Table(["Copy", "To", "Feed", "Merge pts", "Wasted"], feedRows,
                "Feed each copy these motes, using only the tier that matches each rank, then merge it into the main item. " +
                "Wasted: points past the copy's last rank, which merging doesn't pass on."));
            blocks.Add(new MotePlanHeading("Upgrade the main item", Detail: true, Subsequent: true));
        }

        var rows = plan.Steps.Select(s => (IReadOnlyList<string>)Array.AsReadOnly(new[]
        {
            $"+{s.FromRank} → +{s.ToRank}",
            s.Needed.ToString("N0"),
            s.StartPoints.ToString("N0"),
            DescribeUses(s.Used),
            s.Missing > 0 ? $"{s.MissingTier} ×{s.Missing}" : "",
            s.CarryAfter.ToString("N0"),
        })).ToArray();
        blocks.Add(Table(["Step", "Need", "Have", "Use", "Missing", "Carry"], rows,
            "Need: points for the step. Have: points carried in. Use: owned motes and spare copies spent. " +
            "Missing: motes still to get. Carry: extra points that roll into the next step."));

        var totals = new List<(string, string)>
        {
            ("Owned motes used", DescribeCounts(plan.MotesUsed, TierName)),
        };
        if (plan.Kind == UpgradeKind.Equipment)
        {
            totals.Add(("Copies merged", DescribeCounts(plan.CopiesUsed, r => $"+{r}")));
        }

        totals.Add(("Still missing", DescribeCounts(plan.Missing, TierName)));
        if (plan.FedCopies.Count > 0)
        {
            var saved = plan.SavedVersusBaseline;
            var fewer = saved.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
            var more = saved.Where(kv => kv.Value < 0).ToDictionary(kv => kv.Key, kv => -kv.Value);
            totals.Add(("Feeding saves", DescribeCounts(fewer, TierName)));
            totals.Add(("…by spending", DescribeCounts(more, TierName)));
            int fodderUsed = plan.FedCopies.Count(f => f.IsFodder);
            if (fodderUsed > 0)
            {
                totals.Add(("Extra copies fed", fodderUsed.ToString("N0")));
            }

            int wasted = plan.FedCopies.Sum(f => f.Wasted);
            if (wasted > 0)
            {
                totals.Add(("Points wasted feeding", wasted.ToString("N0")));
            }
        }

        totals.Add(($"Points to +{targetRank}", plan.Steps.Sum(s => s.Needed).ToString("N0") +
            (startRank > 0 || points > 0 ? $" (from +{startRank}, {progress:N0} in)" : "")));
        var totalRows = totals.Select(pair => (IReadOnlyList<string>)Array.AsReadOnly(new[] { pair.Item1, pair.Item2 })).ToArray();
        blocks.Add(new MotePlanTable(null, Array.AsReadOnly(totalRows), RowLabels: true));
    }

    private static MotePlanTable Table(string[] headers, IReadOnlyList<string>[] rows, string? tip = null)
    {
        return new MotePlanTable(Array.AsReadOnly(headers), Array.AsReadOnly(rows), tip);
    }

    private static void AddGuide(List<MotePlanBlock> blocks, UpgradePlan plan, int startRank)
    {
        bool equipment = plan.Kind == UpgradeKind.Equipment;
        string thing = equipment ? "item" : "spell";
        int total = plan.Steps.Sum(s => s.Needed) - plan.Steps[0].StartPoints;
        int targetRank = plan.Steps[^1].ToRank;
        blocks.Add(new MotePlanHeading("What to do"));
        blocks.Add(new MotePlanLine($"Your {thing} is +{startRank}. It needs {total:N0} more points to reach +{targetRank}.",
            Muted: true));
        int number = 1;
        if (plan.FedCopies.Count > 0)
        {
            blocks.Add(new MotePlanLine($"{number}. Level up your spare copies first. Small motes stop working on your item once it's too high, " +
                "but a lower copy can still use them, and a higher copy is worth more when merged."));
            number++;
            var groups = plan.FedCopies
                .GroupBy(f => (f.IsFodder, f.StartRank, f.EndRank, Feed: DescribeUses(f.Motes)))
                .OrderBy(g => g.Key.StartRank)
                .ThenBy(g => g.Key.EndRank);
            foreach (var group in groups)
            {
                int count = group.Count();
                string copy = group.Key.IsFodder ? $"new +{group.Key.StartRank} item" : $"+{group.Key.StartRank} item";
                string who = count == 1 ? $"One {copy}" : $"{count} {copy}s";
                string each = count == 1 ? "" : " each";
                blocks.Add(new MotePlanLine($"• {who}: feed {group.Key.Feed}{each} → +{group.Key.EndRank}", Indent: true));
            }
        }

        blocks.Add(new MotePlanLine($"{number}. Level up your {thing}:"));
        foreach (var step in plan.Steps)
        {
            var parts = new List<string>();
            var motes = step.Used.Where(u => !u.IsCopy).ToList();
            var copiesUsed = step.Used.Where(u => u.IsCopy).ToList();
            int copyPoints = copiesUsed.Sum(u => MoteValues.CopyPoints(u.Rank) * u.Count);
            if (motes.Count > 0 && copiesUsed.Count > 0 && step.StartPoints + copyPoints >= step.Needed &&
                step.ToRank < targetRank)
            {
                int motePoints = motes.Sum(u => MoteValues.ItemPoints(u.Rank) * u.Count);
                string copyText = DescribeCopies(copiesUsed);
                blocks.Add(new MotePlanLine($"• +{step.FromRank} → +{step.ToRank}: merge {copyText} (that alone makes +{step.ToRank}). " +
                    $"Use your {DescribeUses(motes)} first anyway: they stop working at +{step.ToRank}, " +
                    $"so their {motePoints:N0} points carry into +{step.ToRank} → +{step.ToRank + 1}", Indent: true));
                continue;
            }

            if (motes.Count > 0)
            {
                parts.Add($"use your {DescribeUses(motes)}");
            }

            if (copiesUsed.Count > 0)
            {
                parts.Add("merge " + DescribeCopies(copiesUsed));
            }

            if (step.Missing > 0)
            {
                parts.Add($"add {step.MissingTier} ×{step.Missing} you don't have yet");
            }

            if (parts.Count == 0)
            {
                parts.Add("leftover points cover it");
            }

            blocks.Add(new MotePlanLine($"• +{step.FromRank} → +{step.ToRank}: {string.Join(", then ", parts)}", Indent: true));
        }

        blocks.Add(new MotePlanLine($"Stop at +{targetRank}. No further upgrades are planned.", Muted: true));
        if (plan.Steps[^1].CarryAfter > 0)
        {
            blocks.Add(new MotePlanLine($"The last mote or merge leaves {plan.Steps[^1].CarryAfter:N0} extra points. " +
                "Resources add fixed amounts; do not add more points after reaching your target.", Muted: true));
        }

        var missingSteps = plan.Steps.Where(s => s.Missing > 0).ToList();
        if (missingSteps.Count > 0)
        {
            var tiers = missingSteps.Select(s => s.MissingTier).Distinct().ToList();
            string why;
            if (!equipment)
            {
                why = $"Why {string.Join(", ", tiers)}? One of that mote is exactly what each step needs, so nothing is wasted.";
            }
            else if (missingSteps.Count == 1)
            {
                var step = missingSteps[0];
                why = step.FromRank == MoteValues.MaxRank - 1
                    ? $"Why {step.MissingTier}? Motes stop working once an item gets too high, and {step.MissingTier} is the only one that still works on a +{step.FromRank} item."
                    : $"Why {step.MissingTier}? Motes stop working once an item gets too high, and {step.MissingTier} is the smallest one that still works on a +{step.FromRank} item.";
            }
            else
            {
                why = "Why these motes? Motes stop working once an item gets too high, so each step asks for the smallest one that still works: " +
                    string.Join(", ", missingSteps.Select(s => $"+{s.FromRank} needs {s.MissingTier}")) + ".";
            }

            blocks.Add(new MotePlanLine(why, Muted: true));
            blocks.Add(new MotePlanLine("If you finish with motes, get: " + DescribeCounts(plan.Missing, TierName), Bold: true));
        }
        else
        {
            blocks.Add(new MotePlanLine("You already have everything you need!", Bold: true));
        }

        if (plan.FedCopies.Count > 0)
        {
            var saved = plan.SavedVersusBaseline;
            var fewer = saved.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
            var more = saved.Where(kv => kv.Value < 0).ToDictionary(kv => kv.Key, kv => -kv.Value);
            if (fewer.Count > 0)
            {
                string instead = more.Count > 0 ? $" by using {DescribeCounts(more, TierName)} instead" : "";
                blocks.Add(new MotePlanLine($"Leveling copies saves you {DescribeCounts(fewer, TierName)}{instead}.", Muted: true));
            }
        }
    }

    private static string TierName(int rank)
    {
        return MoteValues.Tiers[rank - 1];
    }

    private static string DescribeCopies(IReadOnlyList<UpgradeUse> copies)
    {
        int total = copies.Sum(u => u.Count);
        string list = string.Join(", ", copies.Select(u => u.Count > 1 ? $"{u.Count} × +{u.Rank}" : $"+{u.Rank}"));
        return total == 1 ? $"your {list} copy" : $"your copies {list}";
    }

    private static string DescribeUses(IReadOnlyList<UpgradeUse> used)
    {
        return string.Join(", ", used.Select(u => u.IsCopy
            ? $"copy +{u.Rank}{(u.Count > 1 ? $" ×{u.Count}" : "")}"
            : $"{MoteValues.Tiers[u.Rank - 1]} ×{u.Count}"));
    }

    private static string DescribeCounts(IReadOnlyDictionary<int, int> counts, Func<int, string> name)
    {
        if (counts.Count == 0)
        {
            return "none";
        }

        return string.Join(", ", counts.OrderBy(kv => kv.Key).Select(kv => $"{name(kv.Key)} ×{kv.Value:N0}"));
    }
}
