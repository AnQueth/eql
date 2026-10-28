using System.Text.RegularExpressions;
using EqLog.Core.Parsing;

namespace EqLog.Core.Triggers;

/// <summary>A user-defined rule: when a log message matches <see cref="Pattern"/>, show <see cref="Text"/>.</summary>
public sealed class AlertTrigger
{
    public bool Enabled { get; set; } = true;

    public string Name { get; set; } = "";

    /// <summary>
    /// Regex matched (case-insensitive) against the message text after the "[timestamp] " prefix.
    /// <c>{C}</c> is replaced with your character's name.
    /// </summary>
    public string Pattern { get; set; } = "";

    /// <summary>Alert text. <c>$1</c> / <c>${name}</c> insert captured groups and <c>{C}</c> your name. Empty shows the matched line.</summary>
    public string Text { get; set; } = "";

    public double Seconds { get; set; } = 8;

    public bool Sound
    {
        get; set;
    }

    /// <summary>A WPF color name or #RRGGBB value.</summary>
    public string Color { get; set; } = "#FF5A5A";

    public AlertTrigger Clone()
    {
        return (AlertTrigger)MemberwiseClone();
    }

    public static List<AlertTrigger> Defaults()
    {
        return [
        new()
        {
            Name = "Feign Death failed",
            Pattern = @"^{C} has fallen to the ground\.$",
            Text = "FEIGN DEATH FAILED!",
            Sound = true,
        },
        new()
        {
            Name = "Feign Death broken",
            Pattern = @"^You are no longer feigning death, because (.+)\.$",
            Text = "Feign broken: $1",
            Color = "Orange",
        },
    ];
    }
}

/// <summary>An alert produced by a trigger match.</summary>
public sealed record TriggerAlert(AlertTrigger Trigger, string Text, DateTime LogTime, string Message);

/// <summary>Compiled set of enabled triggers for one character.</summary>
public sealed class TriggerMatcher
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    private readonly List<(AlertTrigger Trigger, Regex Regex)> _compiled = [];
    private readonly string _characterName;

    public TriggerMatcher(IEnumerable<AlertTrigger> triggers, string? characterName)
    {
        _characterName = characterName ?? "";
        foreach (var t in triggers)
        {
            if (!t.Enabled || string.IsNullOrWhiteSpace(t.Pattern))
            {
                continue;
            }

            if (TryCompile(t.Pattern, characterName, out var regex, out string? error))
            {
                _compiled.Add((t, regex));
            }
            else
            {
                Errors.Add($"{(t.Name.Length > 0 ? t.Name : t.Pattern)}: {error}");
            }
        }
    }

    public static TriggerMatcher Empty { get; } = new([], null);

    /// <summary>Triggers that were skipped because their pattern is not a valid regex.</summary>
    public List<string> Errors { get; } = [];

    public int Count
    {
        get
        {
            return _compiled.Count;
        }
    }

    public static bool TryCompile(string pattern, string? characterName, out Regex regex, out string? error)
    {
        // Without a known name, {C} matches any single-word name.
        string name = string.IsNullOrEmpty(characterName) ? "[A-Za-z]+" : Regex.Escape(characterName);
        try
        {
            regex = new Regex(pattern.Replace("{C}", name, StringComparison.Ordinal),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
            error = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            regex = null!;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Builds a starting regex that matches <paramref name="message"/> exactly, with your name replaced by <c>{C}</c>
    /// and, optionally, numbers generalized to <c>\d+</c>.
    /// </summary>
    public static string PatternFromMessage(string message, string? characterName, bool generalizeNumbers = true)
    {
        string escaped = Regex.Escape(message).Replace(@"\ ", " ", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(characterName))
        {
            escaped = Regex.Replace(escaped, $@"\b{Regex.Escape(characterName)}\b", "{C}", RegexOptions.IgnoreCase);
        }

        if (generalizeNumbers)
        {
            escaped = Regex.Replace(escaped, @"(?<![\\{])\d+", @"\d+");
        }

        return $"^{escaped}$";
    }

    /// <summary>Checks raw log lines ("[timestamp] message"); lines without a timestamp are matched as-is.</summary>
    public List<TriggerAlert> Match(IReadOnlyList<string> rawLines)
    {
        var alerts = new List<TriggerAlert>();
        if (_compiled.Count == 0)
        {
            return alerts;
        }

        foreach (string raw in rawLines)
        {
            var (time, message) = LogLine.TryParse(raw, out var line) ? (line.Timestamp, line.Message) : (DateTime.Now, raw.Trim());
            foreach (var (trigger, regex) in _compiled)
            {
                Match m;
                try
                {
                    m = regex.Match(message);
                }
                catch (RegexMatchTimeoutException)
                {
                    continue;
                }

                if (m.Success)
                {
                    alerts.Add(new TriggerAlert(trigger, Format(trigger, m, message), time, message));
                }
            }
        }

        return alerts;
    }

    private string Format(AlertTrigger trigger, Match m, string message)
    {
        if (string.IsNullOrWhiteSpace(trigger.Text))
        {
            return message;
        }

        string text;
        try
        {
            text = m.Result(trigger.Text);
        }
        catch (ArgumentException)
        {
            text = trigger.Text;
        }

        return text.Replace("{C}", _characterName, StringComparison.Ordinal);
    }
}
