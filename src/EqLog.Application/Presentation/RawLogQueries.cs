using EqLog.Core.Parsing;
using EqLog.Core.Triggers;

namespace EqLog.Application.Presentation;

public sealed record RawLogSelection(IReadOnlyList<string> Lines, string? Error);

/// <summary>Log-message parsing and trigger syntax stay behind the Application query boundary.</summary>
public static class RawLogQueries
{
    public static string Message(string raw)
    {
        return LogLine.TryParse(raw, out var line) ? line.Message : raw.Trim();
    }

    public static string TriggerPattern(string message, string? character)
    {
        return TriggerMatcher.PatternFromMessage(message, character);
    }

    public static RawLogSelection Filter(IReadOnlyList<string> lines, string filter, bool useRegex, string? character, int maximum)
    {
        Func<string, bool> keep = _ => true;
        string? error = null;
        if (filter.Length > 0)
        {
            if (useRegex)
            {
                if (TriggerMatcher.TryCompile(filter, character, out var regex, out string? compileError))
                {
                    keep = raw =>
                    {
                        try
                        {
                            string message = LogLine.TryParse(raw, out var line) ? line.Message : raw;
                            return regex.IsMatch(message);
                        }
                        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
                        {
                            error = "Regex filter timed out; some lines could not be checked.";
                            return false;
                        }
                    };
                }
                else
                {
                    error = $"Invalid regex: {compileError}";
                }
            }
            else
            {
                keep = raw => raw.Contains(filter, StringComparison.OrdinalIgnoreCase);
            }
        }
        var selected = lines.Where(keep).TakeLast(maximum).ToArray();
        return new(selected, error);
    }
}
