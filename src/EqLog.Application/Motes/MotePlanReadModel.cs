namespace EqLog.Application.Motes;

public enum MoteUpgradeKind
{
    Equipment,
    Spell,
}

public enum MotePlanPage
{
    Upgrade,
    Farm,
    WhatIf,
    Math,
}

public sealed record MotePlanningRequest(
    MoteUpgradeKind Kind,
    int StartRank,
    int TargetRank,
    string Points,
    string Fodder,
    int FodderRank,
    bool Feeding,
    bool FeedUsableMotes,
    bool UseMotes,
    bool UseCopies,
    MotePlanPage Page,
    string Name,
    bool HasInventoryExport,
    IReadOnlyList<int> HypotheticalCopies);

public enum MoteInputStatus
{
    Valid,
    Invalid,
}

public enum MoteFeedingStatus
{
    NotRequested,
    NotApplicable,
    NotNeeded,
    NotBeneficial,
    Used,
}

public enum MoteStepClassification
{
    Standard,
    OutgrownMotesBeforeSufficientCopies,
}

public enum MoteMissingReason
{
    None,
    ExactSpellValue,
    SmallestUsableEquipmentTier,
    OnlyUsableEquipmentTier,
}

public sealed record MoteResourceUse(int Rank, int Count, bool IsCopy, int Points);

public sealed record MoteUpgradeStep(
    int FromRank, int ToRank, int Needed, int StartPoints, IReadOnlyList<MoteResourceUse> Used,
    int MissingRank, string MissingTier, int Missing, int CarryAfter, int MotePoints, int CopyPoints,
    MoteStepClassification Classification, MoteMissingReason MissingReason);

public sealed record MoteFedCopy(
    int StartRank, int EndRank, bool IsFodder, IReadOnlyList<MoteResourceUse> Motes, int MergePoints, int Wasted);

public sealed record MoteResourceCount(int Rank, string Tier, int Count);

public sealed record MoteFarmTarget(int TargetRank, int AdditionalCopies);

public sealed record MoteCopyOnlyStep(int FromRank, int ToRank, int Copies);

public sealed record MoteWhatIfCalculation(int OriginalMergePoints, int HypotheticalMergePoints);

public sealed record MotePlanReadModel(
    MoteUpgradeKind Kind, int StartRank, int TargetRank, int Progress, int ParsedPoints,
    int MaximumProgress, int MaximumFodderCopies,
    MoteInputStatus PointsStatus, MoteInputStatus FodderStatus,
    bool HasInventoryExport, string ItemName, MoteCopySelection CopySelection,
    MoteWhatIfCalculation? WhatIf, MoteFeedingStatus FeedingStatus, int FodderRank, int FodderCopies,
    IReadOnlyList<MoteUpgradeStep> Steps, IReadOnlyList<MoteFedCopy> FedCopies,
    IReadOnlyList<MoteResourceCount> MotesUsed, IReadOnlyList<MoteResourceCount> CopiesUsed,
    IReadOnlyList<MoteResourceCount> Missing, IReadOnlyList<MoteResourceCount> FeedingSavings,
    IReadOnlyList<MoteResourceCount> FeedingCosts, int ExtraCopiesFed, int FeedingWaste,
    int TotalRequiredPoints, int RemainingPoints,
    IReadOnlyList<MoteFarmTarget> FarmTargets, IReadOnlyList<MoteCopyOnlyStep> CopyOnlySteps,
    IReadOnlyList<string> TierIds);
