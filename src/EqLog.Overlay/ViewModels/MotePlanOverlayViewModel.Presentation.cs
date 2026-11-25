using EqLog.Application.Motes;
using EqLog.Core;
using EqLog.Core.Tracking;
using EqLog.Overlay.Mvvm;

namespace EqLog.Overlay.ViewModels;

public sealed partial class MotePlanOverlayViewModel
{
    /// <summary>Detailed guide, farm and math content for the selected planning page.</summary>
    public BulkObservableCollection<MotePlanBlock> PlanBlocks { get; } = [];

    private void RefreshDisplayBlocks()
    {
        PlanBlocks.ReplaceAll(BuildBlocks(Plan, Page));
    }

    private static IReadOnlyList<MotePlanBlock> BuildBlocks(MotePlanReadModel? plan, MotePlanPage page)
    {
        if (plan is null)
        {
            return [];
        }

        var blocks = new List<MotePlanBlock>();
        if (plan.Steps.Count == 0)
        {
            blocks.Add(new(MotePlanBlockKind.Complete,
                $"Already at the +{plan.TargetRank} target. No points needed."));
            return blocks;
        }

        if (page == MotePlanPage.Farm && plan.Kind == MoteUpgradeKind.Equipment)
        {
            AddFarmGuide(blocks, plan);
            AddMath(blocks, plan);
        }
        else if (page is MotePlanPage.Upgrade or MotePlanPage.WhatIf)
        {
            AddGuide(blocks, plan);
        }
        else
        {
            AddMath(blocks, plan);
        }
        return blocks;
    }

    private static string FarmTargetSummary(int targetRank, int farmRank, int additional)
    {
        return additional == 0
            ? $"Reach +{targetRank}: no more copies needed."
            : $"Reach +{targetRank}: farm {additional:N0} more +{farmRank} cop{(additional == 1 ? "y" : "ies")}.";
    }

    private static void AddFarmGuide(List<MotePlanBlock> blocks, MotePlanReadModel plan)
    {
        int fodderRank = plan.FodderRank;
        blocks.Add(new(MotePlanBlockKind.Heading, $"How many +{fodderRank} copies should I farm?"));
        blocks.Add(new(MotePlanBlockKind.Line,
            $"Your item starts at +{plan.StartRank}. Pick the level you want:", Muted: true));
        foreach (var target in plan.FarmTargets)
        {
            blocks.Add(new(MotePlanBlockKind.Line,
                FarmTargetSummary(target.TargetRank, fodderRank, target.AdditionalCopies), Bold: true));
        }

        blocks.Add(new(MotePlanBlockKind.Line,
            "Each number is the TOTAL to farm from where you are now. Do not add the numbers together.", Muted: true));
        blocks.Add(new(MotePlanBlockKind.Line,
            "These numbers use the motes, fragments, spare copies and progress selected above.", Muted: true));
        if (plan.FodderCopies > 0)
        {
            blocks.Add(new(MotePlanBlockKind.Line,
                $"The {plan.FodderCopies:N0} extra copies you entered are already counted. Only copies beyond those are listed here.",
                Muted: true));
        }

        if (plan.FeedingStatus is MoteFeedingStatus.Used or MoteFeedingStatus.NotBeneficial or MoteFeedingStatus.NotNeeded)
        {
            blocks.Add(new(MotePlanBlockKind.Line,
                "Feeding is on: the plan may spend your motes or fragments to raise farmed copies before merging them.",
                Muted: true));
        }

        var comparison = new List<string>
        {
            $"No motes or fragments, no spare copies, no feeding, and no saved progress. Just merge +{fodderRank} copies for ONE level at a time:",
        };
        foreach (var step in plan.CopyOnlySteps)
        {
            comparison.Add($"+{step.FromRank} to +{step.ToRank}: {step.Copies:N0} cop{(step.Copies == 1 ? "y" : "ies")} at +{fodderRank}.");
        }

        blocks.Add(new(MotePlanBlockKind.Comparison, "Compare: copies only, one level at a time",
            Lines: comparison.AsReadOnly()));
    }

    private static void AddGuide(List<MotePlanBlock> blocks, MotePlanReadModel plan)
    {
        bool equipment = plan.Kind == MoteUpgradeKind.Equipment;
        string thing = equipment ? "item" : "spell";
        int targetRank = plan.Steps[^1].ToRank;
        blocks.Add(new(MotePlanBlockKind.Heading, "What to do"));
        blocks.Add(new(MotePlanBlockKind.Line,
            $"Your {thing} is +{plan.StartRank}. It needs {plan.RemainingPoints:N0} more points to reach +{targetRank}.",
            Muted: true));

        int number = 1;
        if (plan.FedCopies.Count > 0)
        {
            blocks.Add(new(MotePlanBlockKind.Line,
                $"{number}. Level up your spare copies first. Small motes stop working on your item once it's too high, " +
                    "but a lower copy can still use them, and a higher copy is worth more when merged."));
            number++;
            var groups = plan.FedCopies
                .GroupBy(copy => (copy.IsFodder, copy.StartRank, copy.EndRank,
                    Feed: DescribeUses(copy.Motes, plan.TierIds)))
                .OrderBy(group => group.Key.StartRank)
                .ThenBy(group => group.Key.EndRank);
            foreach (var group in groups)
            {
                int count = group.Count();
                string copy = group.Key.IsFodder
                    ? $"new +{group.Key.StartRank} item"
                    : $"+{group.Key.StartRank} item";
                string who = count == 1 ? $"One {copy}" : $"{count} {copy}s";
                string each = count == 1 ? "" : " each";
                blocks.Add(new(MotePlanBlockKind.Line,
                    $"• {who}: feed {group.Key.Feed}{each} → +{group.Key.EndRank}", Indent: true));
            }
        }

        blocks.Add(new(MotePlanBlockKind.Line, $"{number}. Level up your {thing}:"));
        for (int index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index];
            int toRank = step.ToRank;
            while (index + 1 < plan.Steps.Count &&
                plan.Steps[index + 1].Used.Count == 0 && plan.Steps[index + 1].Missing == 0)
            {
                index++;
                toRank = plan.Steps[index].ToRank;
            }

            var parts = new List<string>();
            var motes = step.Used.Where(use => !use.IsCopy).ToList();
            var copies = step.Used.Where(use => use.IsCopy).ToList();
            if (step.Classification == MoteStepClassification.OutgrownMotesBeforeSufficientCopies)
            {
                string copyText = DescribeCopies(copies);
                blocks.Add(new(MotePlanBlockKind.Line,
                    $"• +{step.FromRank} → +{toRank}: merge {copyText} (that alone makes +{step.ToRank}). " +
                    $"Use your {DescribeUses(motes, plan.TierIds)} first anyway: they stop working at +{step.ToRank}, " +
                    $"so their {step.MotePoints:N0} points carry into +{step.ToRank} → +{Math.Max(toRank, step.ToRank + 1)}",
                    Indent: true));
                continue;
            }

            if (motes.Count > 0)
            {
                parts.Add($"use your {DescribeUses(motes, plan.TierIds)}");
            }

            if (copies.Count > 0)
            {
                parts.Add("merge " + DescribeCopies(copies));
            }

            if (step.Missing > 0)
            {
                parts.Add($"add {step.MissingTier} ×{step.Missing} you don't have yet");
            }

            if (parts.Count == 0)
            {
                parts.Add("leftover points cover it");
            }

            blocks.Add(new(MotePlanBlockKind.Line,
                $"• +{step.FromRank} → +{toRank}: {string.Join(", then ", parts)}", Indent: true));
        }

        blocks.Add(new(MotePlanBlockKind.Line,
            $"Stop at +{targetRank}. No further upgrades are planned.", Muted: true));
        if (plan.Steps[^1].CarryAfter > 0)
        {
            blocks.Add(new(MotePlanBlockKind.Line,
                $"The last resource or merge leaves {plan.Steps[^1].CarryAfter:N0} extra points. " +
                    "Resources add fixed amounts; do not add more points after reaching your target.",
                Muted: true));
        }

        var missingSteps = plan.Steps.Where(step => step.Missing > 0).ToList();
        if (missingSteps.Count > 0)
        {
            var tiers = missingSteps.Select(step => step.MissingTier).Distinct().ToList();
            string why;
            if (!equipment)
            {
                why = $"Why {string.Join(", ", tiers)}? One of those resources is exactly what each step needs, so nothing is wasted.";
            }
            else if (missingSteps.Count == 1)
            {
                var step = missingSteps[0];
                why = step.MissingReason == MoteMissingReason.OnlyUsableEquipmentTier
                    ? $"Why {step.MissingTier}? Motes stop working once an item gets too high, and {step.MissingTier} is the only one that still works on a +{step.FromRank} item."
                    : $"Why {step.MissingTier}? Motes stop working once an item gets too high, and {step.MissingTier} is the smallest one that still works on a +{step.FromRank} item.";
            }
            else
            {
                why = "Why these resources? Motes and fragments stop working once an item gets too high, so each step asks for the smallest one that still works: " +
                    string.Join(", ", missingSteps.Select(step => $"+{step.FromRank} needs {step.MissingTier}")) + ".";
            }

            blocks.Add(new(MotePlanBlockKind.Line, why, Muted: true));
            blocks.Add(new(MotePlanBlockKind.Line,
                "If you finish with motes, get: " + DescribeCounts(plan.Missing), Bold: true));
        }
        else
        {
            blocks.Add(new(MotePlanBlockKind.Line, "You already have everything you need!", Bold: true));
        }

        if (plan.FedCopies.Count > 0 && plan.FeedingSavings.Count > 0)
        {
            string instead = plan.FeedingCosts.Count > 0
                ? $" by using {DescribeCounts(plan.FeedingCosts)} instead"
                : "";
            blocks.Add(new(MotePlanBlockKind.Line,
                $"Leveling copies saves you {DescribeCounts(plan.FeedingSavings)}{instead}.", Muted: true));
        }
    }

    private static void AddMath(List<MotePlanBlock> blocks, MotePlanReadModel plan)
    {
        if (plan.FedCopies.Count > 0)
        {
            blocks.Add(new(MotePlanBlockKind.Heading, "Feed copies", Detail: true));
            var feedRows = plan.FedCopies.Select(copy => (IReadOnlyList<string>)Array.AsReadOnly(new[]
            {
                copy.IsFodder ? $"new +{copy.StartRank}" : $"+{copy.StartRank}",
                $"+{copy.EndRank}",
                DescribeUses(copy.Motes, plan.TierIds),
                copy.MergePoints.ToString("N0"),
                copy.Wasted > 0 ? copy.Wasted.ToString("N0") : "",
            })).ToArray();
            blocks.Add(new(MotePlanBlockKind.Table,
                Table: new OverlayTable(["Copy", "To", "Feed", "Merge pts", "Wasted"], feedRows),
                ToolTip: "Feed each copy these motes or fragments, using only tiers that work at each rank, then merge it into the main item. Wasted: points past the copy's last rank, which merging doesn't pass on."));
            blocks.Add(new(MotePlanBlockKind.Heading, "Upgrade the main item", Detail: true, Subsequent: true));
        }

        var rows = plan.Steps.Select(step => (IReadOnlyList<string>)Array.AsReadOnly(new[]
        {
            $"+{step.FromRank} → +{step.ToRank}",
            step.Needed.ToString("N0"),
            step.StartPoints.ToString("N0"),
            DescribeUses(step.Used, plan.TierIds),
            step.Missing > 0 ? $"{step.MissingTier} ×{step.Missing}" : "",
            step.CarryAfter.ToString("N0"),
        })).ToArray();
        blocks.Add(new(MotePlanBlockKind.Table,
            Table: new OverlayTable(["Step", "Need", "Have", "Use", "Missing", "Carry"], rows),
            ToolTip: "Need: points for the step. Have: points carried in. Use: owned motes, fragments and spare copies spent. Missing: motes still to get. Carry: extra points that roll into the next step."));

        var totals = new List<(string Label, string Value)>
        {
            ("Owned motes used", DescribeCounts(plan.MotesUsed)),
        };
        if (plan.FragmentsUsed.Count > 0)
        {
            totals.Add(("Owned fragments used", DescribeCounts(plan.FragmentsUsed)));
        }
        if (plan.Kind == MoteUpgradeKind.Equipment)
        {
            totals.Add(("Copies merged", DescribeCounts(plan.CopiesUsed, true)));
        }

        totals.Add(("Still missing", DescribeCounts(plan.Missing)));
        if (plan.FedCopies.Count > 0)
        {
            totals.Add(("Feeding saves", DescribeCounts(plan.FeedingSavings)));
            totals.Add(("…by spending", DescribeCounts(plan.FeedingCosts)));
            if (plan.ExtraCopiesFed > 0)
            {
                totals.Add(("Extra copies fed", plan.ExtraCopiesFed.ToString("N0")));
            }
            if (plan.FeedingWaste > 0)
            {
                totals.Add(("Points wasted feeding", plan.FeedingWaste.ToString("N0")));
            }
        }

        totals.Add(($"Points to +{plan.TargetRank}", plan.TotalRequiredPoints.ToString("N0") +
            (plan.StartRank > 0 || plan.ParsedPoints > 0
                ? $" (from +{plan.StartRank}, {plan.Progress:N0} in)"
                : "")));
        var totalRows = totals.Select(pair => (IReadOnlyList<string>)Array.AsReadOnly(new[]
        {
            pair.Label,
            pair.Value,
        })).ToArray();
        blocks.Add(new(MotePlanBlockKind.Table,
            Table: new OverlayTable(null, totalRows, RowLabels: true)));
    }

    private static string DescribeCopies(IReadOnlyList<MoteResourceUse> copies)
    {
        int total = copies.Sum(copy => copy.Count);
        string ranks = string.Join(", ", copies.Select(copy => copy.Count > 1 ? $"{copy.Count} × +{copy.Rank}" : $"+{copy.Rank}"));
        return total == 1 ? $"your {ranks} copy" : $"your copies {ranks}";
    }

    private static string DescribeUses(IReadOnlyList<MoteResourceUse> uses, IReadOnlyList<string> tiers)
    {
        return string.Join(", ", uses.Select(use => use.IsCopy
            ? $"copy +{use.Rank}{(use.Count > 1 ? $" ×{use.Count}" : "")}"
            : $"{MotePlanningQueries.ResourceName(tiers, use.Rank, use.IsFragment)} ×{use.Count}"));
    }

    private static string DescribeCounts(IReadOnlyList<MoteResourceCount> counts, bool copies = false)
    {
        if (counts.Count == 0)
        {
            return "none";
        }
        return string.Join(", ", counts.Select(count =>
            $"{(copies ? $"+{count.Rank}" : MotePlanningQueries.ResourceName(count.Tier, count.IsFragment))} ×{count.Count:N0}"));
    }
}

/// <summary>Display block kind for the planner's XAML template.</summary>
public enum MotePlanBlockKind
{
    /// <summary>A section heading.</summary>
    Heading,

    /// <summary>A line of guide or status text.</summary>
    Line,

    /// <summary>A calculation table.</summary>
    Table,

    /// <summary>An expandable comparison.</summary>
    Comparison,

    /// <summary>A completed-plan message.</summary>
    Complete,
}

/// <summary>Detached content for one block in the mote planner results.</summary>
/// <param name="Kind">The visual block kind.</param>
/// <param name="Text">The displayed text or table heading.</param>
/// <param name="Indent">Whether line text is indented.</param>
/// <param name="Muted">Whether line text uses muted styling.</param>
/// <param name="Bold">Whether line text is emphasized.</param>
/// <param name="Detail">Whether a heading uses secondary spacing.</param>
/// <param name="Subsequent">Whether the heading follows another detail block.</param>
/// <param name="ToolTip">Optional explanatory text for the block.</param>
/// <param name="Table">Optional calculation table.</param>
/// <param name="Lines">Optional comparison lines.</param>
public sealed record MotePlanBlock(
    MotePlanBlockKind Kind,
    string Text = "",
    bool Indent = false,
    bool Muted = false,
    bool Bold = false,
    bool Detail = false,
    bool Subsequent = false,
    string? ToolTip = null,
    OverlayTable? Table = null,
    IReadOnlyList<string>? Lines = null);
