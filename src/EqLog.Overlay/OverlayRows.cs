using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;
using EqLog.Core.Triggers;
using System.Collections.ObjectModel;

namespace EqLog.Overlay;

/// <summary>One line in an overlay: left/right text with an optional proportional bar behind it.</summary>
public sealed record AbilityBreakdown(string Title, string Context, string RateLabel, IReadOnlyList<AbilityStats> Abilities,
    IReadOnlyList<DamageTimelinePoint>? Timeline = null, double TimelineSeconds = 0, DateTime? TimelineStart = null,
    IReadOnlyList<AbilityTimeline>? AbilityTimelines = null, IReadOnlyList<DamageEvent>? DamageEvents = null,
    IReadOnlyList<DamageEvent>? IncomingDamageEvents = null)
{
    public bool Equals(AbilityBreakdown? other)
    {
        if (other is null)
        {
            return false;
        }
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Title == other.Title && Context == other.Context && RateLabel == other.RateLabel &&
            TimelineSeconds == other.TimelineSeconds && TimelineStart == other.TimelineStart &&
            Abilities.SequenceEqual(other.Abilities) &&
            SameItems(Timeline, other.Timeline) &&
            SameTimelines(AbilityTimelines, other.AbilityTimelines) &&
            SameItems(DamageEvents, other.DamageEvents) &&
            SameItems(IncomingDamageEvents, other.IncomingDamageEvents);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Title, Context, RateLabel, TimelineSeconds, TimelineStart);
    }

    internal AbilityBreakdown Snapshot()
    {
        return this with
        {
            Abilities = Abilities.ToArray(),
            Timeline = Timeline?.ToArray(),
            AbilityTimelines = AbilityTimelines?.Select(lane =>
                new AbilityTimeline(lane.Name, lane.Points.ToArray())).ToArray(),
            DamageEvents = DamageEvents?.ToArray(),
            IncomingDamageEvents = IncomingDamageEvents?.ToArray(),
        };
    }

    private static bool SameItems<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }
        return left.SequenceEqual(right);
    }

    private static bool SameTimelines(IReadOnlyList<AbilityTimeline>? left, IReadOnlyList<AbilityTimeline>? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }
        return left.Count == right.Count &&
            left.Zip(right).All(pair => pair.First.Name == pair.Second.Name &&
                SameItems(pair.First.Points, pair.Second.Points));
    }
}

public sealed record SpawnTrackChange(string Zone, string Mob, bool Tracked);

public sealed record SpellTrackChange(string Spell, bool Tracked);

/// <summary>One overlay row. A non-null <paramref name="ValueColumn"/> appears between Left and Right; empty reserves the column.</summary>
public sealed record OverlayRow(string Left, string Right = "", double Fraction = 0, Brush? Bar = null, Brush? Foreground = null,
    bool Wrap = false, bool Bold = false, double Scale = 1, string? LootItem = null, AbilityBreakdown? Breakdown = null,
    bool QuestHeading = false, string? Details = null, bool Stacked = false, string? ExtraColumn = null, string? ToolTip = null,
    string? WatchKey = null, bool Watched = false, OverlayTable? Table = null, string? Link = null, bool HasQuestReferences = false,
    AlertEffect Effect = AlertEffect.None, SpawnTrackChange? SpawnTrack = null, SpellTrackChange? SpellTrack = null,
    string? ValueColumn = null);

/// <summary>
/// A small grid shown inside a row's card, below its title. With <see cref="Header"/> it is a column table
/// (numeric columns right-aligned); with <see cref="RowLabels"/> even columns are labels and odd columns their values.
/// <see cref="Then"/> chains another table underneath. <see cref="Links"/> maps value-cell text to a URL that the
/// cell opens when clicked. Equality is by value so unchanged rows are not rebuilt.
/// </summary>
public sealed record OverlayTable(IReadOnlyList<string>? Header, IReadOnlyList<IReadOnlyList<string>> Rows,
    bool RowLabels = false, OverlayTable? Then = null, IReadOnlyDictionary<string, string>? Links = null)
{
    /// <summary>Label/value table, <paramref name="pairsPerRow"/> pairs side by side.</summary>
    public static OverlayTable Pairs(IEnumerable<(string Label, string Value)> pairs, int pairsPerRow = 1, OverlayTable? then = null)
    {
        var rows = pairs
            .Chunk(Math.Max(1, pairsPerRow))
            .Select(chunk => (IReadOnlyList<string>)chunk.SelectMany(p => new[] { p.Label, p.Value }).ToArray())
            .ToArray();
        return new(null, rows, RowLabels: true, Then: then);
    }

    /// <summary>One header row and one value row.</summary>
    public static OverlayTable Columns(IEnumerable<(string Header, string Value)> columns, OverlayTable? then = null)
    {
        var list = columns.ToArray();
        return new(list.Select(c => c.Header).ToArray(), [list.Select(c => c.Value).ToArray()], Then: then);
    }

    /// <summary>Label/value pairs where a list of values shares one label, shown only on its first line.</summary>
    public static IEnumerable<(string Label, string Value)> Labeled(string label, IEnumerable<string> values)
    {
        bool first = true;
        foreach (string value in values)
        {
            yield return (first ? label : "", value);
            first = false;
        }
    }

    /// <summary>Copy with extra cell-text links merged into <see cref="Links"/>; empty texts are ignored.</summary>
    public OverlayTable WithLinks(IEnumerable<(string Text, string Url)> links)
    {
        var merged = Links is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(Links, StringComparer.Ordinal);
        foreach (var (text, url) in links)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                merged.TryAdd(text, url);
            }
        }
        return this with
        {
            Links = merged
        };
    }

    public bool Equals(OverlayTable? other)
    {
        if (other is null)
        {
            return false;
        }
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return RowLabels == other.RowLabels &&
            SameCells(Header, other.Header) &&
            Rows.Count == other.Rows.Count &&
            Rows.Zip(other.Rows).All(pair => SameCells(pair.First, pair.Second)) &&
            SameLinks(Links, other.Links) &&
            Equals(Then, other.Then);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(RowLabels);
        hash.Add(Rows.Count);
        foreach (var row in Rows)
        {
            foreach (string cell in row)
            {
                hash.Add(cell);
            }
        }
        hash.Add(Then);
        return hash.ToHashCode();
    }

    private static bool SameCells(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        return a.SequenceEqual(b, StringComparer.Ordinal);
    }

    private static bool SameLinks(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        return a.Count == b.Count &&
            a.All(pair => b.TryGetValue(pair.Key, out string? url) && string.Equals(url, pair.Value, StringComparison.Ordinal));
    }
}
