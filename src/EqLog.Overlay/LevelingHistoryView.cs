namespace EqLog.Overlay;

/// <summary>One row in the leveling history grid.</summary>
public sealed record LevelingGridRow(DateTime Start, int? Level, string StartText, string EndText,
    string Build, int Kills, double Minutes, bool IsCurrent, string Details);
