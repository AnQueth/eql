using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using EqLog.Core.Tracking;

namespace EqLog.Overlay;

/// <summary>
/// Live ability table plus damage graphs. Refreshes update rows in place so column widths,
/// sorting, selection, the selected tab, and damage-type scrolling survive.
/// </summary>
public sealed class AbilityDetailsView
{
    private readonly ObservableCollection<AbilityStats> _rows = [];
    private readonly DataGrid _table;
    private readonly TabControl? _tabs;
    private readonly TabItem? _timelineTab;
    private TabItem? _lanesTab;
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

        _chartSeconds = breakdown.TimelineSeconds;
        _chartStart = breakdown.TimelineStart;
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
