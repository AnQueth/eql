using System.Collections.ObjectModel;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;
using EqLog.Overlay.Mvvm;

namespace EqLog.Overlay.ViewModels;

/// <summary>
/// Live ability table plus damage graphs for an <see cref="AbilityBreakdown"/>. Updates keep row instances, the selected
/// tab and unchanged chart models, so the view keeps column widths, sorting, selection and damage-type scrolling.
/// </summary>
public sealed class AbilityDetailsViewModel : ObservableObject
{
    /// <summary>The most bars the damage timeline shows; longer fights group seconds per bar.</summary>
    public const int MaxTimelineBars = 90;

    public const int AbilitiesTab = 0;
    public const int TimelineTab = 1;
    public const int TypesTab = 2;
    public const int EventsTab = 3;
    public const int IncomingTab = 4;

    private AbilityBreakdown? _displayed;
    private string _rateLabel = "";
    private bool _hasTabs;
    private AbilityStats? _selectedAbility;
    private int _selectedTabIndex;
    private DamageTimelineChartViewModel? _timeline;
    private DamageTimelinePoint[] _timelinePoints = [];
    private AbilityTimeline[] _laneInputs = [];
    private bool _chartsBuilt;
    private double _chartSeconds;
    private DateTime? _chartStart;
    private bool _hasLanes;
    private string _lanesDescription = "";
    private string? _lanesDescriptionToolTip;
    private TimeAxisViewModel? _laneAxis;
    private bool _hasEvents;
    private bool _hasIncoming;
    private string _incomingSummary = "";
    private string? _incomingSummaryToolTip;
    private AbilityDetailsViewModel? _incoming;

    public AbilityDetailsViewModel(AbilityBreakdown breakdown, bool compact)
    {
        Compact = compact;
        Update(breakdown);
    }

    /// <summary>Whether to use the compact layout for inline overlays.</summary>
    public bool Compact
    {
        get;
    }

    /// <summary>The rate column header, such as DPS or HPS.</summary>
    public string RateLabel
    {
        get
        {
            return _rateLabel;
        }
        private set
        {
            if (SetProperty(ref _rateLabel, value))
            {
                OnPropertyChanged(nameof(ShowOverheal));
            }
        }
    }

    /// <summary>Whether the overheal column is shown.</summary>
    public bool ShowOverheal
    {
        get
        {
            return _rateLabel == "HPS";
        }
    }

    /// <summary>Whether the breakdown has a timeline, so the table is shown with graph tabs.</summary>
    public bool HasTabs
    {
        get
        {
            return _hasTabs;
        }
        private set
        {
            SetProperty(ref _hasTabs, value);
        }
    }

    /// <summary>The ability rows, updated in place by name.</summary>
    public ObservableCollection<AbilityStats> Rows { get; } = [];

    /// <summary>The selected ability row; restored by name when a refresh replaces it.</summary>
    public AbilityStats? SelectedAbility
    {
        get
        {
            return _selectedAbility;
        }
        set
        {
            SetProperty(ref _selectedAbility, value);
        }
    }

    /// <summary>The selected tab; falls back to the ability table when the selected tab is hidden.</summary>
    public int SelectedTabIndex
    {
        get
        {
            return _selectedTabIndex;
        }
        set
        {
            SetProperty(ref _selectedTabIndex, value);
        }
    }

    /// <summary>The damage-per-bucket chart; replaced only when its inputs change.</summary>
    public DamageTimelineChartViewModel? Timeline
    {
        get
        {
            return _timeline;
        }
        private set
        {
            SetProperty(ref _timeline, value);
        }
    }

    /// <summary>Whether the damage types tab is shown.</summary>
    public bool HasLanes
    {
        get
        {
            return _hasLanes;
        }
        private set
        {
            SetProperty(ref _hasLanes, value);
        }
    }

    /// <summary>Explains the damage type lanes.</summary>
    public string LanesDescription
    {
        get
        {
            return _lanesDescription;
        }
        private set
        {
            SetProperty(ref _lanesDescription, value);
        }
    }

    /// <summary>The full lanes explanation when the compact description is shortened.</summary>
    public string? LanesDescriptionToolTip
    {
        get
        {
            return _lanesDescriptionToolTip;
        }
        private set
        {
            SetProperty(ref _lanesDescriptionToolTip, value);
        }
    }

    /// <summary>One lane per damage type.</summary>
    public BulkObservableCollection<DamageLaneViewModel> Lanes { get; } = [];

    /// <summary>The time axis under the damage type lanes.</summary>
    public TimeAxisViewModel? LaneAxis
    {
        get
        {
            return _laneAxis;
        }
        private set
        {
            SetProperty(ref _laneAxis, value);
        }
    }

    /// <summary>Whether the damage data tab is shown.</summary>
    public bool HasEvents
    {
        get
        {
            return _hasEvents;
        }
        private set
        {
            SetProperty(ref _hasEvents, value);
        }
    }

    /// <summary>Damage events in time order.</summary>
    public ObservableCollection<DamageEvent> EventRows { get; } = [];

    /// <summary>Whether the incoming damage tab is shown.</summary>
    public bool HasIncoming
    {
        get
        {
            return _hasIncoming;
        }
        private set
        {
            SetProperty(ref _hasIncoming, value);
        }
    }

    /// <summary>Summarizes damage taken by the player.</summary>
    public string IncomingSummary
    {
        get
        {
            return _incomingSummary;
        }
        private set
        {
            SetProperty(ref _incomingSummary, value);
        }
    }

    /// <summary>The full incoming summary in the compact layout, where it may be trimmed.</summary>
    public string? IncomingSummaryToolTip
    {
        get
        {
            return _incomingSummaryToolTip;
        }
        private set
        {
            SetProperty(ref _incomingSummaryToolTip, value);
        }
    }

    /// <summary>Details of the damage the player took, or null when there is none to show.</summary>
    public AbilityDetailsViewModel? Incoming
    {
        get
        {
            return _incoming;
        }
        private set
        {
            SetProperty(ref _incoming, value);
        }
    }

    /// <summary>Applies a newer breakdown in place.</summary>
    public void Update(AbilityBreakdown breakdown)
    {
        ArgumentNullException.ThrowIfNull(breakdown);
        if (_displayed == breakdown)
        {
            return;
        }

        RateLabel = breakdown.RateLabel;
        HasTabs = breakdown.Timeline is not null;
        var selectedName = SelectedAbility?.Name;
        SyncRows(breakdown.Abilities);
        if (selectedName is not null && (SelectedAbility is null || !Rows.Contains(SelectedAbility)))
        {
            SelectedAbility = Rows.FirstOrDefault(row => row.Name == selectedName);
        }

        if (HasTabs)
        {
            UpdateCharts(breakdown);
        }
        else
        {
            ClearCharts();
        }

        if (!IsTabVisible(SelectedTabIndex))
        {
            SelectedTabIndex = AbilitiesTab;
        }

        _displayed = breakdown.Snapshot();
    }

    /// <summary>Chooses a tick spacing of 1, 2 or 5 times a power of ten that gives about six ticks.</summary>
    internal static int TimelineTickInterval(int totalSeconds)
    {
        double targetInterval = Math.Max(1, totalSeconds) / 6.0;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(targetInterval)));
        double normalized = targetInterval / magnitude;
        double multiplier = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return Math.Max(1, (int)Math.Ceiling(multiplier * magnitude));
    }

    private bool IsTabVisible(int index)
    {
        return index switch
        {
            AbilitiesTab => true,
            TimelineTab => HasTabs,
            TypesTab => HasLanes,
            EventsTab => HasEvents,
            IncomingTab => HasIncoming,
            _ => false,
        };
    }

    private void UpdateCharts(AbilityBreakdown breakdown)
    {
        var points = breakdown.Timeline ?? [];
        var lanes = breakdown.AbilityTimelines ?? [];
        bool spanChanged = !_chartsBuilt || _chartSeconds != breakdown.TimelineSeconds || _chartStart != breakdown.TimelineStart;
        if (Timeline is null || spanChanged || !_timelinePoints.SequenceEqual(points))
        {
            Timeline = new DamageTimelineChartViewModel(points, breakdown.TimelineSeconds, breakdown.TimelineStart, Compact);
            _timelinePoints = points.ToArray();
        }

        if (spanChanged || !LanesEqual(lanes))
        {
            UpdateLanes(lanes, breakdown.TimelineSeconds, breakdown.TimelineStart);
            _laneInputs = lanes.Select(lane => new AbilityTimeline(lane.Name, lane.Points.ToArray())).ToArray();
        }

        UpdateDamageEvents(breakdown.DamageEvents ?? []);
        UpdateIncomingDamage(breakdown);
        _chartSeconds = breakdown.TimelineSeconds;
        _chartStart = breakdown.TimelineStart;
        _chartsBuilt = true;
    }

    private void ClearCharts()
    {
        Timeline = null;
        _timelinePoints = [];
        _laneInputs = [];
        _chartsBuilt = false;
        HasLanes = false;
        Lanes.ReplaceAll([]);
        LaneAxis = null;
        HasEvents = false;
        EventRows.Clear();
        HasIncoming = false;
        Incoming = null;
    }

    private bool LanesEqual(IReadOnlyList<AbilityTimeline> lanes)
    {
        if (_laneInputs.Length != lanes.Count)
        {
            return false;
        }

        for (var i = 0; i < lanes.Count; i++)
        {
            if (_laneInputs[i].Name != lanes[i].Name || !_laneInputs[i].Points.SequenceEqual(lanes[i].Points))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One row per damage type; each spans the whole fight with a bar from the first to last hit of every stretch.</summary>
    private void UpdateLanes(IReadOnlyList<AbilityTimeline> lanes, double seconds, DateTime? start)
    {
        if (lanes.Count == 0)
        {
            HasLanes = false;
            Lanes.ReplaceAll([]);
            LaneAxis = null;
            return;
        }

        int total = Math.Max(1, (int)Math.Ceiling(seconds));
        total = Math.Max(total, lanes.Max(lane => lane.Points.Max(point => point.Second)) + 1);
        var slices = TimeSlices(total, TimelineTickInterval(total));
        string description = $"Each row spans the full fight ({OverlayPresenters.Clock(total)}). Bars run from when a damage type started landing to when it finished; " +
                             $"a gap over {DamageTimeline.SegmentGapSeconds} seconds starts a new bar. Vertical marks scale with fight length; hover a bar for times and damage.";
        LanesDescription = Compact ? $"Damage types over {OverlayPresenters.Clock(total)}" : description;
        LanesDescriptionToolTip = Compact ? description : null;
        Lanes.ReplaceAll(lanes.Select(lane => new DamageLaneViewModel(lane, total, slices)));
        LaneAxis = Compact
            ? TimeAxisViewModel.StartAndEnd(total, start, Compact)
            : new TimeAxisViewModel(start is { } value ? $"0:00 ({value:HH:mm:ss})" : "0:00", null, slices);
        HasLanes = true;
    }

    private static IReadOnlyList<TimeSlice> TimeSlices(int totalSeconds, int interval)
    {
        var slices = new List<TimeSlice>();
        int start = 0;
        while (totalSeconds - start > interval)
        {
            slices.Add(new TimeSlice(start, interval, totalSeconds, OverlayPresenters.Clock(start + interval), ShowGuide: true));
            start += interval;
        }

        slices.Add(new TimeSlice(start, totalSeconds - start, totalSeconds, OverlayPresenters.Clock(totalSeconds), ShowGuide: false));
        return slices;
    }

    private void UpdateDamageEvents(IReadOnlyList<DamageEvent> damageEvents)
    {
        var next = damageEvents.OrderBy(e => e.Time).ToArray();
        int common = 0;
        while (common < EventRows.Count && common < next.Length && EventRows[common] == next[common])
        {
            common++;
        }

        while (EventRows.Count > common)
        {
            EventRows.RemoveAt(EventRows.Count - 1);
        }

        for (int i = common; i < next.Length; i++)
        {
            EventRows.Add(next[i]);
        }

        HasEvents = next.Length > 0;
    }

    private void UpdateIncomingDamage(AbilityBreakdown breakdown)
    {
        if (breakdown.IncomingDamageEvents is not { } events)
        {
            HasIncoming = false;
            Incoming = null;
            return;
        }

        HasIncoming = true;
        if (breakdown.TimelineStart is not { } start)
        {
            SetIncomingSummary("Incoming damage cannot be displayed because the fight start time is unavailable.");
            Incoming = null;
            return;
        }

        var details = CombatQueries.IncomingDamage(events, Names.You, start, breakdown.TimelineSeconds);
        SetIncomingSummary(details.Events.Count == 0
            ? $"No incoming damage logged for {Names.You} from this enemy. Only observed hits with a named attacker can be matched to the fight."
            : $"{Names.You}: {details.Events.Sum(e => e.Amount):N0} incoming damage, " +
              $"{details.Abilities.Sum(a => a.PerSecond):N1} DTPS over {OverlayPresenters.Clock(details.Seconds)}.");
        if (details.Events.Count == 0)
        {
            Incoming = null;
            return;
        }

        var incoming = new AbilityBreakdown(Names.You, "Incoming damage from the selected enemy", "DTPS",
            details.Abilities, details.Timeline, details.Seconds, details.Start, details.Timelines, details.Events);
        if (Incoming is null)
        {
            Incoming = new AbilityDetailsViewModel(incoming, Compact);
        }
        else
        {
            Incoming.Update(incoming);
        }
    }

    private void SetIncomingSummary(string text)
    {
        IncomingSummary = text;
        IncomingSummaryToolTip = Compact ? text : null;
    }

    private void SyncRows(IReadOnlyList<AbilityStats> next)
    {
        for (var i = 0; i < next.Count; i++)
        {
            var item = next[i];
            var existing = IndexOf(item.Name, i);
            if (existing < 0)
            {
                Rows.Insert(i, item);
                continue;
            }

            if (existing != i)
            {
                Rows.Move(existing, i);
            }

            if (!Equals(Rows[i], item))
            {
                Rows[i] = item;
            }
        }

        while (Rows.Count > next.Count)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }
    }

    private int IndexOf(string name, int start)
    {
        for (var i = start; i < Rows.Count; i++)
        {
            if (string.Equals(Rows[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>A span of a time axis: a guide line at its end and a tick label.</summary>
public sealed record TimeSlice(double Start, double Length, double Total, string Label, bool ShowGuide)
{
    /// <summary>The seconds after this slice, so the span can be laid out as proportional columns.</summary>
    public double Remainder
    {
        get
        {
            return Math.Max(0, Total - Start - Length);
        }
    }
}

/// <summary>A time axis: a start label on the left and tick labels positioned by second.</summary>
public sealed record TimeAxisViewModel(string StartLabel, string? StartToolTip, IReadOnlyList<TimeSlice> Ticks)
{
    /// <summary>An axis labeled only at its start and end.</summary>
    public static TimeAxisViewModel StartAndEnd(int endSecond, DateTime? start, bool compact)
    {
        string startLabel = !compact && start is { } value ? $"0:00 ({value:HH:mm:ss})" : "0:00";
        string? startToolTip = compact && start is { } startTime ? $"Start: {startTime:HH:mm:ss}" : null;
        double total = Math.Max(1, endSecond);
        return new TimeAxisViewModel(startLabel, startToolTip,
            [new TimeSlice(0, total, total, OverlayPresenters.Clock(endSecond), ShowGuide: false)]);
    }
}

/// <summary>One timeline bar: the share of the peak bucket it reaches.</summary>
public sealed record TimelineBar(double Fraction, string ToolTip)
{
    /// <summary>The share of the chart height above the bar.</summary>
    public double Remainder
    {
        get
        {
            return 1 - Fraction;
        }
    }
}

/// <summary>Damage per time bucket with a peak summary.</summary>
public sealed class DamageTimelineChartViewModel
{
    public DamageTimelineChartViewModel(IReadOnlyList<DamageTimelinePoint> points, double seconds, DateTime? start, bool compact)
    {
        Compact = compact;
        var buckets = CombatQueries.Buckets(points, seconds, AbilityDetailsViewModel.MaxTimelineBars);
        int width = buckets.Count == 0 ? 1 : buckets[0].EndSecond - buckets[0].StartSecond;
        long peak = buckets.Count == 0 ? 0 : buckets.Max(b => b.Amount);
        var peakBucket = buckets.MaxBy(b => b.Amount);
        int activeOf = Math.Max(1, (int)Math.Ceiling(seconds));
        string summary = peakBucket is null || peak == 0
            ? "No damage recorded."
            : $"Each bar is {width} second{(width == 1 ? "" : "s")}. Peak {peak:N0} at {OverlayPresenters.Clock(peakBucket.StartSecond)} ({peakBucket.PerSecond:N0} dps). " +
              $"Active {points.Count:N0} of {activeOf:N0} seconds.";
        Summary = compact && peakBucket is not null && peak > 0
            ? $"Peak {peak:N0} at {OverlayPresenters.Clock(peakBucket.StartSecond)} \u00b7 {points.Count:N0}/{activeOf:N0}s active"
            : summary;
        SummaryToolTip = compact ? summary : null;
        PeakLabel = $"{peak:N0}";
        Axis = TimeAxisViewModel.StartAndEnd(buckets.Count == 0 ? 0 : buckets[^1].EndSecond, start, compact);
        BarColumns = Math.Max(1, buckets.Count);
        DenseBars = buckets.Count > 60;
        Bars = buckets.Select(bucket =>
        {
            double fraction = peak == 0 ? 0 : (double)bucket.Amount / peak;
            string span = width == 1
                ? OverlayPresenters.Clock(bucket.StartSecond)
                : $"{OverlayPresenters.Clock(bucket.StartSecond)}-{OverlayPresenters.Clock(bucket.EndSecond)}";
            return new TimelineBar(fraction, $"{span}: {bucket.Amount:N0} damage ({bucket.PerSecond:N0} dps)");
        }).ToArray();
    }

    /// <summary>Whether to use the compact layout.</summary>
    public bool Compact
    {
        get;
    }

    /// <summary>Bar width and peak, shortened in the compact layout.</summary>
    public string Summary
    {
        get;
    }

    /// <summary>The full summary in the compact layout.</summary>
    public string? SummaryToolTip
    {
        get;
    }

    /// <summary>The peak bucket's damage, shown above the chart.</summary>
    public string PeakLabel
    {
        get;
    }

    /// <summary>The axis under the chart.</summary>
    public TimeAxisViewModel Axis
    {
        get;
    }

    /// <summary>The bars, oldest first.</summary>
    public IReadOnlyList<TimelineBar> Bars
    {
        get;
    }

    /// <summary>The number of bar columns.</summary>
    public int BarColumns
    {
        get;
    }

    /// <summary>Whether bars are narrow enough to need thinner gaps.</summary>
    public bool DenseBars
    {
        get;
    }
}

/// <summary>A stretch of one damage type's lane.</summary>
public sealed record LaneSegment(double Start, double Length, double Total, string ToolTip)
{
    /// <summary>The seconds after this stretch, so the lane can be laid out as proportional columns.</summary>
    public double Remainder
    {
        get
        {
            return Math.Max(0, Total - Start - Length);
        }
    }
}

/// <summary>One damage type across the whole fight.</summary>
public sealed class DamageLaneViewModel
{
    public DamageLaneViewModel(AbilityTimeline lane, int totalSeconds, IReadOnlyList<TimeSlice> guides)
    {
        var segments = CombatQueries.Segments(lane.Points);
        long damage = lane.Points.Sum(p => p.Amount);
        Name = lane.Name;
        ToolTip = $"{lane.Name}: {damage:N0} damage over {lane.Points.Count:N0} active seconds, {segments.Count} stretch{(segments.Count == 1 ? "" : "es")}";
        Guides = guides;
        Segments = segments.Select(segment =>
        {
            int end = segment.EndSecond + 1;
            return new LaneSegment(segment.StartSecond, end - segment.StartSecond, totalSeconds,
                $"{OverlayPresenters.Clock(segment.StartSecond)} - {OverlayPresenters.Clock(segment.EndSecond)}: " +
                $"{segment.Amount:N0} damage, {segment.ActiveSeconds:N0} active second{(segment.ActiveSeconds == 1 ? "" : "s")}");
        }).ToArray();
    }

    /// <summary>The damage type.</summary>
    public string Name
    {
        get;
    }

    /// <summary>Total damage, active seconds and stretch count.</summary>
    public string ToolTip
    {
        get;
    }

    /// <summary>Stretches in which the damage type landed.</summary>
    public IReadOnlyList<LaneSegment> Segments
    {
        get;
    }

    /// <summary>Time slices whose ends are marked with guide lines.</summary>
    public IReadOnlyList<TimeSlice> Guides
    {
        get;
    }
}
