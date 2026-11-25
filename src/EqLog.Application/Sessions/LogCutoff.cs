using EqLog.Core.Parsing;

namespace EqLog.Application.Sessions;

/// <summary>
/// Drops a log's lines until the first one stamped at or after the cutoff. Lines are only filtered by
/// position, so the kept part of a log is numbered the same on every replay that uses the same cutoff.
/// </summary>
internal sealed class LogCutoff
{
    private readonly DateTime _since;
    private bool _reached;

    public LogCutoff(DateTime? ignoreBefore)
    {
        _reached = ignoreBefore is null;
        if (ignoreBefore is DateTime value)
        {
            // Log stamps have whole-second precision; keep lines written in the same second as the cutoff.
            _since = value.AddTicks(-(value.Ticks % TimeSpan.TicksPerSecond));
        }
    }

    /// <summary>Returns the lines to process. Called sequentially by one reader.</summary>
    public IReadOnlyList<string> Filter(IReadOnlyList<string> lines)
    {
        if (_reached)
        {
            return lines;
        }
        for (int i = 0; i < lines.Count; i++)
        {
            if (LogLine.TryParse(lines[i], out var line) && line.Timestamp >= _since)
            {
                _reached = true;
                if (i == 0)
                {
                    return lines;
                }
                return lines.Skip(i).ToArray();
            }
        }
        return [];
    }
}
