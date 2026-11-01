using System.Globalization;
using EqLog.Core;
using EqLog.Core.Tracking;

namespace EqLog.Application.Motes;

/// <summary>Calculates upgrades, resource consumption and input classifications.</summary>
public static class MotePlanningService
{
    public static MotePlanReadModel Prepare(
        MotePlanningRequest request, IReadOnlyDictionary<int, int> owned, IEnumerable<InventoryExportItem> inventory)
    {
        bool equipment = request.Kind == MoteUpgradeKind.Equipment;
        var kind = equipment ? UpgradeKind.Equipment : UpgradeKind.Spell;
        int startRank = request.StartRank;
        int targetRank = request.TargetRank;
        const int maximumFodderCopies = 1023;
        bool fodderOk = ParsePoints(request.Fodder, maximumFodderCopies, out int fodder);
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
        var steps = plan.Steps.Select(step =>
        {
            var used = Uses(step.Used, equipment);
            int motePoints = used.Where(use => !use.IsCopy).Sum(use => use.Points);
            int copyPoints = used.Where(use => use.IsCopy).Sum(use => use.Points);
            var classification = used.Any(use => !use.IsCopy) && used.Any(use => use.IsCopy) &&
                step.StartPoints + copyPoints >= step.Needed && step.ToRank < targetRank
                ? MoteStepClassification.OutgrownMotesBeforeSufficientCopies
                : MoteStepClassification.Standard;
            var reason = step.Missing == 0
                ? MoteMissingReason.None
                : !equipment
                    ? MoteMissingReason.ExactSpellValue
                    : step.FromRank == MoteValues.MaxRank - 1
                        ? MoteMissingReason.OnlyUsableEquipmentTier
                        : MoteMissingReason.SmallestUsableEquipmentTier;
            return new MoteUpgradeStep(step.FromRank, step.ToRank, step.Needed, step.StartPoints, used,
                step.ToRank, step.MissingTier, step.Missing, step.CarryAfter, motePoints, copyPoints,
                classification, reason);
        }).ToArray();
        var fed = plan.FedCopies.Select(copy => new MoteFedCopy(
            copy.StartRank, copy.EndRank, copy.IsFodder, Uses(copy.Motes, true), copy.MergePoints, copy.Wasted)).ToArray();
        var savings = plan.SavedVersusBaseline;
        var farmTargets = new List<MoteFarmTarget>();
        var copyOnlySteps = new List<MoteCopyOnlyStep>();
        if (request.Page == MotePlanPage.Farm && feed is not null && steps.Length > 0)
        {
            int copyPoints = MoteValues.CopyPoints(fodderRank);
            for (int target = startRank + 1; target <= targetRank; target++)
            {
                int additional = MoteUpgradePlanner.MinimumAdditionalCopiesToFinish(
                    startRank, progress, plannerMotes, spare, feed, targetRank: target);
                farmTargets.Add(new MoteFarmTarget(target, additional));
                int rank = target - 1;
                int count = (MoteValues.PointsToNext(rank) + copyPoints - 1) / copyPoints;
                copyOnlySteps.Add(new MoteCopyOnlyStep(rank, target, count));
            }
        }

        var feedingStatus = !equipment
            ? MoteFeedingStatus.NotApplicable
            : !request.Feeding
                ? MoteFeedingStatus.NotRequested
                : fed.Length > 0
                    ? MoteFeedingStatus.Used
                    : plan.Baseline is not null
                        ? MoteFeedingStatus.NotBeneficial
                        : MoteFeedingStatus.NotNeeded;
        int total = steps.Sum(step => step.Needed);
        return new MotePlanReadModel(request.Kind, startRank, targetRank, progress, points, maxPoints, maximumFodderCopies,
            pointsOk ? MoteInputStatus.Valid : MoteInputStatus.Invalid,
            fodderOk ? MoteInputStatus.Valid : MoteInputStatus.Invalid,
            request.HasInventoryExport, request.Name, selection,
            hypothetical ? new MoteWhatIfCalculation(copies.Sum(MoteValues.CopyPoints), spare.Sum(MoteValues.CopyPoints)) : null,
            feedingStatus, fodderRank, feed?.FodderCopies ?? 0,
            Array.AsReadOnly(steps), Array.AsReadOnly(fed),
            Counts(plan.MotesUsed), Counts(plan.CopiesUsed, copies: true), Counts(plan.Missing),
            Counts(savings.Where(pair => pair.Value > 0).ToDictionary()),
            Counts(savings.Where(pair => pair.Value < 0).ToDictionary(pair => pair.Key, pair => -pair.Value)),
            fed.Count(copy => copy.IsFodder), fed.Sum(copy => copy.Wasted),
            total, steps.Length > 0 ? total - steps[0].StartPoints : 0,
            farmTargets.AsReadOnly(), copyOnlySteps.AsReadOnly(), Array.AsReadOnly(MoteValues.Tiers.ToArray()));
    }

    private static IReadOnlyList<MoteResourceUse> Uses(IEnumerable<UpgradeUse> uses, bool equipment)
    {
        return Array.AsReadOnly(uses.Select(use => new MoteResourceUse(use.Rank, use.Count, use.IsCopy,
            use.Count * (use.IsCopy ? MoteValues.CopyPoints(use.Rank) :
                equipment ? MoteValues.ItemPoints(use.Rank) : MoteValues.SpellPoints(use.Rank)))).ToArray());
    }

    private static IReadOnlyList<MoteResourceCount> Counts(IReadOnlyDictionary<int, int> counts, bool copies = false)
    {
        return Array.AsReadOnly(counts.OrderBy(pair => pair.Key).Select(pair =>
            new MoteResourceCount(pair.Key, copies ? "" : MoteValues.Tiers[pair.Key - 1], pair.Value)).ToArray());
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
}
