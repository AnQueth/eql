using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;
using EqLog.Application.Presentation;

namespace EqLog.Overlay;

/// <summary>
/// Live ability table plus damage graphs. Refreshes update rows in place so column widths,
/// sorting, selection, the selected tab, and damage-type scrolling survive.
/// </summary>
public sealed class AbilityDetailsView
{
    private readonly ObservableCollection<AbilityStats> _rows = [];
    private readonly ObservableCollection<DamageEvent> _eventRows = [];
    private readonly DataGrid _table;
    private readonly DataGrid _eventsTable;
    private readonly TabControl? _tabs;
    private readonly TabItem? _timelineTab;
    private TabItem? _lanesTab;
    private TabItem? _eventsTab;
    private TabItem? _incomingTab;
    private TextBlock? _incomingSummary;
    private ContentControl? _incomingContent;
    private AbilityDetailsView? _incomingView;
    private AbilityBreakdown? _latestBreakdown;
    private readonly string _rateLabel;
    private DamageTimelinePoint[] _timelinePoints = [];
    private AbilityTimeline[] _abilityTimelines = [];
    private double _chartSeconds;
    private DateTime? _chartStart;

    public AbilityDetailsView(AbilityBreakdown breakdown)
    {
        _rateLabel = breakdown.RateLabel;
        SyncRows(breakdown.Abilities);
        _table = AbilityBreakdownWindow.BuildAbilityTable(breakdown, _rows);
        _eventsTable = AbilityBreakdownWindow.BuildDamageEventsTable(_eventRows);
        if (breakdown.Timeline is null)
        {
            Root = _table;
            return;
        }

        _tabs = new TabControl();
        _tabs.Items.Add(new TabItem { Header = "Abilities", Content = _table });
        _timelineTab = new TabItem
        {
            Header = "Damage timeline",
        };
        _tabs.Items.Add(_timelineTab);
        UpdateCharts(breakdown);
        Root = _tabs;
    }

    public FrameworkElement Root { get; }

    /// <summary>Applies a newer breakdown. Returns false when the layout differs and the view must be rebuilt.</summary>
    public bool TryUpdate(AbilityBreakdown breakdown)
    {
        if (breakdown.RateLabel != _rateLabel || (breakdown.Timeline is null) != (_tabs is null))
        {
            return false;
        }

        var selectedName = (_table.SelectedItem as AbilityStats)?.Name;
        SyncRows(breakdown.Abilities);
        if (selectedName is not null && _table.SelectedItem is null)
        {
            _table.SelectedItem = _rows.FirstOrDefault(r => r.Name == selectedName);
        }

        UpdateCharts(breakdown);

        return true;
    }

    private void UpdateCharts(AbilityBreakdown breakdown)
    {
        if (_timelineTab is null)
        {
            return;
        }

        var points = breakdown.Timeline ?? [];
        var lanes = breakdown.AbilityTimelines ?? [];
        var spanChanged = _chartSeconds != breakdown.TimelineSeconds || _chartStart != breakdown.TimelineStart;
        if (_timelineTab.Content is null || spanChanged || !_timelinePoints.SequenceEqual(points))
        {
            _timelineTab.Content = AbilityBreakdownWindow.BuildTimelineContent(breakdown);
            _timelinePoints = points.ToArray();
        }

        if (spanChanged || !LanesEqual(lanes))
        {
            UpdateLanes(breakdown);
            _abilityTimelines = lanes.Select(lane => new AbilityTimeline(lane.Name, lane.Points.ToArray())).ToArray();
        }

        UpdateDamageEvents(breakdown);
        UpdateIncomingDamage(breakdown);
        _chartSeconds = breakdown.TimelineSeconds;
        _chartStart = breakdown.TimelineStart;
    }

    private void UpdateIncomingDamage(AbilityBreakdown breakdown)
    {
        _latestBreakdown = breakdown;
        if (_tabs is null)
        {
            return;
        }

        if (breakdown.IncomingDamageEvents is null)
        {
            if (_incomingTab is not null)
            {
                _tabs.Items.Remove(_incomingTab);
                _incomingTab = null;
                _incomingView = null;
            }

            return;
        }

        if (_incomingTab is null)
        {
            var panel = new DockPanel { Margin = new Thickness(8) };
            _incomingSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
            DockPanel.SetDock(_incomingSummary, Dock.Top);
            panel.Children.Add(_incomingSummary);
            _incomingContent = new ContentControl
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            };
            panel.Children.Add(_incomingContent);
            _incomingTab = new TabItem { Header = "Incoming damage", Content = panel };
            _tabs.Items.Add(_incomingTab);
        }

        RenderIncomingDamage();
    }

    private void RenderIncomingDamage()
    {
        if (_latestBreakdown?.IncomingDamageEvents is not { } events ||
            _incomingSummary is null || _incomingContent is null)
        {
            return;
        }

        if (_latestBreakdown.TimelineStart is not { } start)
        {
            _incomingSummary.Text = "Incoming damage cannot be displayed because the fight start time is unavailable.";
            _incomingContent.Content = null;
            _incomingView = null;
            return;
        }

        var details = CombatQueries.IncomingDamage(events, Names.You, start, _latestBreakdown.TimelineSeconds);
        _incomingSummary.Text = details.Events.Count == 0
            ? $"No incoming damage logged for {Names.You} from this enemy. Only observed hits with a named attacker can be matched to the fight."
            : $"{Names.You}: {details.Events.Sum(e => e.Amount):N0} incoming damage, " +
              $"{details.Abilities.Sum(a => a.PerSecond):N1} DTPS over {OverlayPresenters.Clock(details.Seconds)}.";
        if (details.Events.Count == 0)
        {
            _incomingContent.Content = null;
            _incomingView = null;
            return;
        }

        var breakdown = new AbilityBreakdown(Names.You, "Incoming damage from the selected enemy", "DTPS",
            details.Abilities, details.Timeline, details.Seconds, details.Start, details.Timelines, details.Events);
        if (_incomingView is null || !_incomingView.TryUpdate(breakdown))
        {
            _incomingView = new AbilityDetailsView(breakdown);
            _incomingContent.Content = _incomingView.Root;
        }
    }

    private void UpdateDamageEvents(AbilityBreakdown breakdown)
    {
        if (_tabs is null)
        {
            return;
        }

        var events = (breakdown.DamageEvents ?? []).OrderBy(e => e.Time).ToArray();

        SyncDamageEventRows(events);
        if (events.Length > 0)
        {
            if (_eventsTab is null)
            {
                _eventsTab = new TabItem { Header = "Damage data", Content = _eventsTable };
                _tabs.Items.Add(_eventsTab);
            }
        }
        else if (_eventsTab is not null)
        {
            _tabs.Items.Remove(_eventsTab);
            _eventsTab = null;
        }
    }

    private void SyncDamageEventRows(IReadOnlyList<DamageEvent> next)
    {
        int common = 0;
        while (common < _eventRows.Count && common < next.Count && _eventRows[common] == next[common])
        {
            common++;
        }

        while (_eventRows.Count > common)
        {
            _eventRows.RemoveAt(_eventRows.Count - 1);
        }

        for (int i = common; i < next.Count; i++)
        {
            _eventRows.Add(next[i]);
        }
    }

    private bool LanesEqual(IReadOnlyList<AbilityTimeline> lanes)
    {
        if (_abilityTimelines.Length != lanes.Count)
        {
            return false;
        }

        for (var i = 0; i < lanes.Count; i++)
        {
            if (_abilityTimelines[i].Name != lanes[i].Name ||
                !_abilityTimelines[i].Points.SequenceEqual(lanes[i].Points))
            {
                return false;
            }
        }

        return true;
    }

    private void UpdateLanes(AbilityBreakdown breakdown)
    {
        if (_tabs is null)
        {
            return;
        }

        if (breakdown.AbilityTimelines is { Count: > 0 })
        {
            if (_lanesTab is null)
            {
                _lanesTab = new TabItem { Header = "Damage types" };
                _tabs.Items.Add(_lanesTab);
            }

            _lanesTab.Content = AbilityBreakdownWindow.BuildLanesContent(breakdown, _lanesTab.Content as DockPanel);
        }
        else if (_lanesTab is not null)
        {
            _tabs.Items.Remove(_lanesTab);
            _lanesTab = null;
        }
    }

    private void SyncRows(IReadOnlyList<AbilityStats> next)
    {
        for (var i = 0; i < next.Count; i++)
        {
            var item = next[i];
            var existing = IndexOf(item.Name, i);
            if (existing < 0)
            {
                _rows.Insert(i, item);
                continue;
            }

            if (existing != i)
            {
                _rows.Move(existing, i);
            }

            if (!Equals(_rows[i], item))
            {
                _rows[i] = item;
            }
        }

        while (_rows.Count > next.Count)
        {
            _rows.RemoveAt(_rows.Count - 1);
        }
    }

    private int IndexOf(string name, int start)
    {
        for (var i = start; i < _rows.Count; i++)
        {
            if (string.Equals(_rows[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
