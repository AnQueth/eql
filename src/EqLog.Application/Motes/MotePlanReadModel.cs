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

public abstract record MotePlanBlock;

public sealed record MotePlanHeading(string Text, bool Detail = false, bool Subsequent = false) : MotePlanBlock;

public sealed record MotePlanLine(
    string Text, bool Indent = false, bool Muted = false, bool Bold = false) : MotePlanBlock;

public sealed record MotePlanTable(
    IReadOnlyList<string>? Headers, IReadOnlyList<IReadOnlyList<string>> Rows,
    string? ToolTip = null, bool RowLabels = false) : MotePlanBlock;

public sealed record MotePlanComparison(string Heading, IReadOnlyList<string> Lines) : MotePlanBlock;

public sealed record MotePlanComplete(string Text) : MotePlanBlock;

public sealed record MotePlanReadModel(
    string Summary, string SummaryToolTip, IReadOnlyList<MotePlanBlock> Blocks);
