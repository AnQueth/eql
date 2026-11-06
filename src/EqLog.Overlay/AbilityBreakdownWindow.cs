using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;

namespace EqLog.Overlay;

public sealed class AbilityBreakdownWindow : Window
{
    public AbilityBreakdownWindow(AbilityBreakdown breakdown)
    {
        Title = $"{breakdown.Title} - ability breakdown";
        Width = 850;
        Height = 480;
        MinWidth = 540;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "NightBackground");
        SetResourceReference(ForegroundProperty, "NightText");
        var root = new DockPanel { Margin = new Thickness(16) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var title = new TextBlock { Text = breakdown.Title, FontSize = 21, FontWeight = FontWeights.Bold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Pumpkin");
        header.Children.Add(title);
        var context = new TextBlock { Text = breakdown.Context, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        context.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        header.Children.Add(context);
        header.Children.Add(new TextBlock
        {
            Text = $"Total {breakdown.Abilities.Sum(a => a.Total):N0} · {breakdown.Abilities.Sum(a => a.PerSecond):N1} {breakdown.RateLabel}",
            Margin = new Thickness(0, 8, 0, 0),
        });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var note = new TextBlock
        {
            Text = "Snapshot captured when clicked. Rates use the displayed fight/session healing span, not the rolling 30-second meter. Melee and Ranged are separate; spell and proc damage uses each logged effect name and rank. The log may not distinguish a proc from a cast of the same spell.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        DockPanel.SetDock(note, Dock.Bottom);
        root.Children.Add(note);
        root.Children.Add(BuildDetails(breakdown));

        Content = root;
    }

    public const int MaxTimelineBars = 90;

    public static FrameworkElement BuildDetails(AbilityBreakdown breakdown)
    {
        return new AbilityDetailsView(breakdown).Root;
    }

    internal static DataGrid BuildAbilityTable(AbilityBreakdown breakdown, System.Collections.IEnumerable rows)
    {
        var table = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            ItemsSource = rows,
            SelectionMode = DataGridSelectionMode.Single,
        };
        AddColumn(table, "Ability", "Name", null, new DataGridLength(1, DataGridLengthUnitType.Star));
        AddColumn(table, "Total", "Total", "N0");
        AddColumn(table, breakdown.RateLabel, "PerSecond", "N1");
        AddColumn(table, "Share %", "Percent", "N1");
        AddColumn(table, "Hits", "Hits", "N0");
        AddColumn(table, "Crits", "Crits", "N0");
        AddColumn(table, "Max", "MaxHit", "N0");
        if (breakdown.RateLabel == "HPS")
        {
            AddColumn(table, "Overheal", "Overheal", "N0");
        }

        return table;
    }

    internal static DataGrid BuildDamageEventsTable(System.Collections.IEnumerable rows)
    {
        var table = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            ItemsSource = rows,
        };
        table.Columns.Add(new DataGridTextColumn
        {
            Header = "Time",
            Binding = new Binding(nameof(DamageEvent.Time)) { StringFormat = "HH:mm:ss.fff" },
            Width = DataGridLength.Auto,
            MinWidth = 85,
        });
        AddColumn(table, "Attacker", nameof(DamageEvent.Attacker), null);
        AddColumn(table, "Target", nameof(DamageEvent.Target), null);
        AddColumn(table, "Type", nameof(DamageEvent.Kind), null);
        AddColumn(table, "Source", nameof(DamageEvent.Source), null);
        AddColumn(table, "Damage", nameof(DamageEvent.Amount), "N0");
        AddColumn(table, "Modifier", nameof(DamageEvent.Modifier), null);
        return table;
    }

    internal static FrameworkElement BuildTimelineContent(AbilityBreakdown breakdown)
    {
        return BuildTimeline(breakdown.Timeline ?? [], breakdown.TimelineSeconds, breakdown.TimelineStart);
    }

    internal static DockPanel BuildLanesContent(AbilityBreakdown breakdown, DockPanel? panel = null)
    {
        return BuildLanes(breakdown.AbilityTimelines ?? [], breakdown.TimelineSeconds, breakdown.TimelineStart, panel);
    }

    /// <summary>One row per damage type; each row spans the whole fight, with a bar from the first to last hit of every stretch.</summary>
    private static DockPanel BuildLanes(IReadOnlyList<AbilityTimeline> lanes, double seconds, DateTime? start, DockPanel? panel)
    {
        int total = Math.Max(1, (int)Math.Ceiling(seconds));
        total = Math.Max(total, lanes.Max(l => l.Points.Max(p => p.Second)) + 1);
        var slices = BuildTimeSlices(total, TimelineTickInterval(total));
        if (panel is null)
        {
            panel = new DockPanel { Margin = new Thickness(8) };
            var description = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            };
            description.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
            DockPanel.SetDock(description, Dock.Top);
            panel.Children.Add(description);
            var tracks = new Grid();
            tracks.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto), MaxWidth = 220 });
            tracks.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.Children.Add(new ScrollViewer { Content = tracks, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }

        var info = (TextBlock)panel.Children[0];
        info.Text = $"Each row spans the full fight ({OverlayPresenters.Clock(total)}). Bars run from when a damage type started landing to when it finished; " +
                    $"a gap over {DamageTimeline.SegmentGapSeconds} seconds starts a new bar. Vertical marks scale with fight length; hover a bar for times and damage.";
        // Keep the scroll viewer and its content grid alive so refreshes retain the user's offset.
        var grid = (Grid)((ScrollViewer)panel.Children[1]).Content;
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        int row = 0;
        foreach (var lane in lanes)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
            var segments = CombatQueries.Segments(lane.Points);
            long damage = lane.Points.Sum(p => p.Amount);
            var name = new TextBlock
            {
                Text = lane.Name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 10, 0),
                ToolTip = $"{lane.Name}: {damage:N0} damage over {lane.Points.Count:N0} active seconds, {segments.Count} stretch{(segments.Count == 1 ? "" : "es")}",
            };
            Grid.SetRow(name, row);
            grid.Children.Add(name);
            Grid.SetRow(BuildLane(segments, total, slices, grid), row);
            row++;
        }

        var axis = BuildLaneTimeAxis(slices, start);
        axis.Margin = new Thickness(0, 4, 0, 0);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(axis, row);
        Grid.SetColumn(axis, 1);
        grid.Children.Add(axis);

        return panel;
    }

    internal static int TimelineTickInterval(int totalSeconds)
    {
        double targetInterval = Math.Max(1, totalSeconds) / 6.0;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(targetInterval)));
        double normalized = targetInterval / magnitude;
        double multiplier = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return Math.Max(1, (int)Math.Ceiling(multiplier * magnitude));
    }

    private static IReadOnlyList<int> BuildTimeSlices(int totalSeconds, int interval)
    {
        var slices = new List<int>();
        int remaining = totalSeconds;
        while (remaining > interval)
        {
            slices.Add(interval);
            remaining -= interval;
        }

        slices.Add(remaining);
        return slices;
    }

    private static Grid BuildLaneTimeAxis(IReadOnlyList<int> slices, DateTime? start)
    {
        var axis = new Grid();
        for (int i = 0; i < slices.Count; i++)
        {
            axis.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(slices[i], GridUnitType.Star) });
        }

        int elapsed = 0;
        var zero = new TextBlock
        {
            Text = start is { } value ? $"0:00 ({value:HH:mm:ss})" : "0:00",
        };
        zero.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        axis.Children.Add(zero);
        for (int i = 0; i < slices.Count; i++)
        {
            elapsed += slices[i];
            if (i == slices.Count - 1)
            {
                var end = new TextBlock
                {
                    Text = OverlayPresenters.Clock(elapsed),
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                end.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
                Grid.SetColumn(end, i);
                axis.Children.Add(end);
                continue;
            }

            var tick = new TextBlock
            {
                Text = OverlayPresenters.Clock(elapsed),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            tick.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
            Grid.SetColumn(tick, i);
            axis.Children.Add(tick);
        }

        return axis;
    }

    private static FrameworkElement BuildLane(IReadOnlyList<DamageSegment> segments, int totalSeconds, IReadOnlyList<int> slices, Grid grid)
    {
        var track = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        var background = new Border { Opacity = 0.18 };
        background.SetResourceReference(Border.BackgroundProperty, "NightMuted");
        track.Children.Add(background);
        foreach (var segment in segments)
        {
            int end = segment.EndSecond + 1;
            var span = new Grid();
            span.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(segment.StartSecond, GridUnitType.Star) });
            span.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(end - segment.StartSecond, GridUnitType.Star) });
            span.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, totalSeconds - end), GridUnitType.Star) });
            var bar = new Grid
            {
                Background = Brushes.Transparent,
                ToolTip = $"{OverlayPresenters.Clock(segment.StartSecond)} - {OverlayPresenters.Clock(segment.EndSecond)}: " +
                          $"{segment.Amount:N0} damage, {segment.ActiveSeconds:N0} active second{(segment.ActiveSeconds == 1 ? "" : "s")}",
            };
            var fill = new Border { Opacity = 0.55, Margin = new Thickness(0, 4, 0, 4) };
            fill.SetResourceReference(Border.BackgroundProperty, "Pumpkin");
            bar.Children.Add(fill);
            bar.Children.Add(Mark(HorizontalAlignment.Left));
            bar.Children.Add(Mark(HorizontalAlignment.Right));
            Grid.SetColumn(bar, 1);
            span.Children.Add(bar);
            track.Children.Add(span);
        }

        var guides = new Grid { IsHitTestVisible = false };
        for (int i = 0; i < slices.Count; i++)
        {
            guides.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(slices[i], GridUnitType.Star) });
            if (i < slices.Count - 1)
            {
                var line = new Border
                {
                    BorderThickness = new Thickness(0, 0, 1, 0),
                    Opacity = 0.55,
                };
                line.SetResourceReference(Border.BorderBrushProperty, "NightMuted");
                Grid.SetColumn(line, i);
                guides.Children.Add(line);
            }
        }

        track.Children.Add(guides);
        Grid.SetColumn(track, 1);
        grid.Children.Add(track);
        return track;
    }

    private static FrameworkElement Mark(HorizontalAlignment side)
    {
        var mark = new Border { Width = 2, HorizontalAlignment = side };
        mark.SetResourceReference(Border.BackgroundProperty, "NightText");
        return mark;
    }

    private static FrameworkElement BuildTimeline(IReadOnlyList<DamageTimelinePoint> points, double seconds, DateTime? start)
    {
        var buckets = CombatQueries.Buckets(points, seconds, MaxTimelineBars);
        int width = buckets.Count == 0 ? 1 : buckets[0].EndSecond - buckets[0].StartSecond;
        long peak = buckets.Count == 0 ? 0 : buckets.Max(b => b.Amount);
        var panel = new DockPanel { Margin = new Thickness(8) };
        var peakBucket = buckets.MaxBy(b => b.Amount);
        string summary = peakBucket is null || peak == 0
            ? "No damage recorded."
            : $"Each bar is {width} second{(width == 1 ? "" : "s")}. Peak {peak:N0} at {OverlayPresenters.Clock(peakBucket.StartSecond)} ({peakBucket.PerSecond:N0} dps). " +
              $"Active {points.Count:N0} of {Math.Max(1, (int)Math.Ceiling(seconds)):N0} seconds.";
        var info = new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        info.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        DockPanel.SetDock(info, Dock.Top);
        panel.Children.Add(info);

        var axis = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        var left = new TextBlock { Text = start is { } s0 ? $"0:00 ({s0:HH:mm:ss})" : "0:00" };
        var right = new TextBlock { Text = OverlayPresenters.Clock(buckets.Count == 0 ? 0 : buckets[^1].EndSecond), HorizontalAlignment = HorizontalAlignment.Right };
        left.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        right.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        axis.Children.Add(left);
        axis.Children.Add(right);
        DockPanel.SetDock(axis, Dock.Bottom);
        panel.Children.Add(axis);

        var peakLabel = new TextBlock { Text = $"{peak:N0}", FontSize = 11 };
        peakLabel.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        DockPanel.SetDock(peakLabel, Dock.Top);
        panel.Children.Add(peakLabel);

        var bars = new UniformGrid { Rows = 1, Columns = Math.Max(1, buckets.Count) };
        foreach (var bucket in buckets)
        {
            var cell = new Grid { Background = Brushes.Transparent, Margin = new Thickness(buckets.Count > 60 ? 0.5 : 1, 0, buckets.Count > 60 ? 0.5 : 1, 0) };
            double fraction = peak == 0 ? 0 : (double)bucket.Amount / peak;
            cell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - fraction, GridUnitType.Star) });
            cell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(fraction, GridUnitType.Star) });
            var bar = new Border();
            bar.SetResourceReference(Border.BackgroundProperty, "Pumpkin");
            Grid.SetRow(bar, 1);
            cell.Children.Add(bar);
            string span = width == 1
                ? OverlayPresenters.Clock(bucket.StartSecond)
                : $"{OverlayPresenters.Clock(bucket.StartSecond)}-{OverlayPresenters.Clock(bucket.EndSecond)}";
            cell.ToolTip = $"{span}: {bucket.Amount:N0} damage ({bucket.PerSecond:N0} dps)";
            bars.Children.Add(cell);
        }

        var frame = new Border { BorderThickness = new Thickness(1, 0, 0, 1), Child = bars, MinHeight = 120 };
        frame.SetResourceReference(Border.BorderBrushProperty, "NightMuted");
        panel.Children.Add(frame);
        return panel;
    }

    private static void AddColumn(DataGrid grid, string header, string path, string? format, DataGridLength? width = null)
    {
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path) { StringFormat = format },
            Width = width ?? DataGridLength.Auto,
            MinWidth = path == "Name" ? 160 : 65,
        });
    }
}
