using System.Globalization;

namespace EqLog.Core.Parsing;

/// <summary>A single raw log line split into its timestamp and message text.</summary>
public readonly record struct LogLine(DateTime Timestamp, string Message)
{
    private static readonly string[] TimestampFormats =
    [
        "ddd MMM dd HH:mm:ss yyyy",
        "ddd MMM d HH:mm:ss yyyy",
    ];

    /// <summary>Parses lines of the form "[Wed Oct 21 17:07:04 2026] message".</summary>
    public static bool TryParse(string raw, out LogLine line)
    {
        line = default;
        if (raw.Length < 3 || raw[0] != '[')
        {
            return false;
        }

        int close = raw.IndexOf(']');
        if (close < 10)
        {
            return false;
        }

        var stamp = raw.AsSpan(1, close - 1);
        if (!DateTime.TryParseExact(stamp, TimestampFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowInnerWhite | DateTimeStyles.AssumeLocal, out var ts))
        {
            return false;
        }

        int start = close + 1;
        if (start < raw.Length && raw[start] == ' ')
        {
            start++;
        }

        line = new LogLine(ts, raw[start..].TrimEnd('\r', '\n', ' '));
        return true;
    }
}
