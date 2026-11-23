using System.Windows;
using System.Windows.Controls;
using EqLog.Core;
using EqLog.Core.Tracking;

namespace EqLog.Overlay;

public partial class MainWindow
{
    private DpsFightList? _dpsFightList;

    internal static CheckBox ConfigureDpsToolbar(OverlayPanel panel, bool expanded, Action<bool> viewChanged)
    {
        var fullDetails = new CheckBox
        {
            Content = "Full details",
            IsChecked = expanded,
            Margin = new Thickness(0, 3, 0, 3),
            ToolTip = "Show ability tables and damage graphs under expanded fights.",
        };
        fullDetails.Checked += (_, _) => viewChanged(true);
        fullDetails.Unchecked += (_, _) => viewChanged(false);
        panel.SetToolbar(fullDetails);
        return fullDetails;
    }
}

internal sealed class DpsFightList : ScrollViewer
{
    private readonly OverlayPanel _panel;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _empty = new() { Text = "No fight yet" };
    private readonly Dictionary<(string Target, DateTime Start, int Occurrence), FightRow> _fights = [];
    private bool _fullDetails;

    internal DpsFightList(OverlayPanel panel, bool fullDetails)
    {
        _panel = panel;
        _fullDetails = fullDetails;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Margin = new Thickness(0, 2, 0, 0);
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        Content = _rows;
    }

    internal void SetFullDetails(bool fullDetails)
    {
        if (_fullDetails == fullDetails)
        {
            return;
        }
        _fullDetails = fullDetails;
        foreach (var row in _fights.Values)
        {
            row.SetFullDetails(fullDetails);
        }
    }

    internal void Update(EngineSnapshot snapshot)
    {
        IReadOnlyList<FightSnapshot> fights = snapshot.RecentFights;
        if (fights.Count == 0 && snapshot.CurrentFight is { } current)
        {
            fights = [current];
        }
        var counts = new Dictionary<(string Target, DateTime Start), int>();
        var orderedKeys = fights.Select(fight =>
        {
            var identity = (fight.Target.ToUpperInvariant(), fight.Start);
            int occurrence = counts.GetValueOrDefault(identity);
            counts[identity] = occurrence + 1;
            return (identity.Item1, identity.Start, occurrence);
        }).ToArray();
        var keys = orderedKeys.ToHashSet();
        foreach (var key in _fights.Keys.Where(key => !keys.Contains(key)).ToArray())
        {
            _rows.Children.Remove(_fights[key].Root);
            _fights.Remove(key);
        }
        if (fights.Count == 0)
        {
            if (!_rows.Children.Contains(_empty))
            {
                _rows.Children.Add(_empty);
            }
            return;
        }
        _rows.Children.Remove(_empty);
        for (int index = 0; index < fights.Count; index++)
        {
            var fight = fights[index];
            var key = orderedKeys[index];
            if (!_fights.TryGetValue(key, out var row))
            {
                row = new FightRow(_panel, fight, _fullDetails);
                _fights.Add(key, row);
            }
            row.Update(fight);
            int currentIndex = _rows.Children.IndexOf(row.Root);
            if (currentIndex != index)
            {
                if (currentIndex >= 0)
                {
                    _rows.Children.RemoveAt(currentIndex);
                }
                _rows.Children.Insert(index, row.Root);
            }
        }
    }

    private sealed class FightRow
    {
        private readonly OverlayPanel _panel;
        private readonly TextBlock _name = new() { TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _summary = new() { TextAlignment = TextAlignment.Right, Margin = new Thickness(8, 0, 0, 0) };
        private readonly ContentControl _details = new() { Margin = new Thickness(4, 2, 0, 4), Visibility = Visibility.Collapsed };
        private FightSnapshot _fight;
        private IReadOnlyList<OverlayRow> _compactRows = [];
        private AbilityDetailsView? _fullView;
        private bool _fullDetails;
        private bool _expanded;

        internal FightRow(OverlayPanel panel, FightSnapshot fight, bool fullDetails)
        {
            _panel = panel;
            _fight = fight;
            _fullDetails = fullDetails;
            var header = new DockPanel();
            DockPanel.SetDock(_summary, Dock.Right);
            header.Children.Add(_summary);
            header.Children.Add(_name);
            var button = new Button
            {
                Content = header,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(3, 0, 3, 0),
                Margin = new Thickness(0, 0, 0, 1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ToolTip = "Expand or collapse this fight",
            };
            button.Click += (_, _) =>
            {
                _expanded = !_expanded;
                _details.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
                if (_expanded)
                {
                    RefreshDetails();
                }
            };
            Root = new StackPanel();
            Root.Children.Add(button);
            Root.Children.Add(_details);
        }

        internal StackPanel Root { get; }

        internal void Update(FightSnapshot fight)
        {
            _fight = fight;
            string summary = $"{OverlayPresenters.Short(EqLog.Application.Presentation.CombatQueries.PersonalDps(fight))} dps  {OverlayPresenters.Clock(fight.DurationSeconds)}";
            if (_name.Text != fight.Target)
            {
                _name.Text = fight.Target;
                _name.ToolTip = fight.Target;
            }
            if (_summary.Text != summary)
            {
                _summary.Text = summary;
            }
            if (_expanded)
            {
                RefreshDetails();
            }
        }

        internal void SetFullDetails(bool fullDetails)
        {
            if (_fullDetails == fullDetails)
            {
                return;
            }
            _fullDetails = fullDetails;
            _fullView = null;
            _compactRows = [];
            _details.Content = null;
            if (_expanded)
            {
                RefreshDetails();
            }
        }

        private void RefreshDetails()
        {
            if (_fullDetails)
            {
                var breakdown = OverlayPresenters.FightBreakdown(_fight);
                if (_fullView is null || !_fullView.TryUpdate(breakdown))
                {
                    _fullView = new AbilityDetailsView(breakdown, compact: true);
                    _fullView.Root.Height = 180;
                    _details.Content = _fullView.Root;
                }
                return;
            }

            var rows = OverlayPresenters.DpsAttackerRows(_fight);
            if (_details.Content is not null && _compactRows.SequenceEqual(rows))
            {
                return;
            }
            var content = new StackPanel();
            foreach (var row in rows)
            {
                content.Children.Add(_panel.BuildRow(row));
            }
            if (rows.Count == 0)
            {
                content.Children.Add(new TextBlock { Text = "No attackers recorded." });
            }
            _compactRows = rows;
            _details.Content = content;
        }
    }
}
