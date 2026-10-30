using System.Windows;
using System.Windows.Controls;
using EqLog.Core;
using EqLog.Core.Tracking;

namespace EqLog.Overlay;

/// <summary>One entry in the DPS overlay fight picker; a null key follows the current fight.</summary>
public sealed record DpsFightChoice(DpsFightKey? Key, string Label)
{
    public const int MaxFights = 10;

    public override string ToString()
    {
        return Label;
    }

    /// <summary>"Current fight" followed by up to ten recent fights, newest first.</summary>
    public static IReadOnlyList<DpsFightChoice> Build(IEnumerable<FightSnapshot> fights)
    {
        var choices = new List<DpsFightChoice> { new(null, "Current fight (auto)") };
        foreach (var fight in fights.OrderByDescending(f => f.Start).Take(MaxFights))
        {
            choices.Add(new(new DpsFightKey(fight.Target, fight.Start), $"{fight.Target}  ({fight.Start:HH:mm:ss})"));
        }

        return choices;
    }
}

public partial class MainWindow
{
    private ComboBox? _dpsFightPicker;
    private ComboBox? _dpsViewPicker;
    private DpsFightKey? _dpsFight;
    private bool _dpsPickerUpdating;
    private DateTime _lastDpsDetailsRender;
    private bool _dpsDetailsShown;
    private int _dpsDetailsSelectedTab;
    private AbilityDetailsView? _dpsDetailsView;
    private const double DpsDetailsRefreshSeconds = 1;

    private void ConfigureDpsToolbar(OverlayPanel panel)
    {
        _dpsFightPicker = new ComboBox
        {
            Width = 180,
            Margin = new Thickness(0, 5, 0, 5),
            ToolTip = "Show one of your last 10 fights (newest first) instead of the current fight.",
        };
        _dpsFightPicker.SelectionChanged += (_, _) =>
        {
            if (_dpsPickerUpdating)
            {
                return;
            }

            _dpsFight = (_dpsFightPicker.SelectedItem as DpsFightChoice)?.Key;
        };
        _dpsPickerUpdating = true;
        _dpsFightPicker.ItemsSource = DpsFightChoice.Build([]);
        _dpsFightPicker.SelectedIndex = 0;
        _dpsPickerUpdating = false;
        _dpsViewPicker = new ComboBox
        {
            Width = 110,
            ItemsSource = new[] { "Compact", "Full details" },
            SelectedIndex = _settings.DpsExpandedView ? 1 : 0,
            Margin = new Thickness(6, 5, 0, 5),
            ToolTip = "Choose compact DPS rows or the full ability table and damage graphs.",
        };
        _dpsViewPicker.SelectionChanged += (_, _) =>
        {
            _settings.DpsExpandedView = _dpsViewPicker.SelectedIndex == 1;
            _lastDpsDetailsRender = default;
            SaveSettingsInBackground();
        };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
        toolbar.Children.Add(_dpsFightPicker);
        toolbar.Children.Add(_dpsViewPicker);
        panel.SetToolbar(toolbar);
    }

    private void RefreshDpsChoices(EngineSnapshot snap)
    {
        if (_dpsFightPicker is null || _dpsFightPicker.IsDropDownOpen)
        {
            return;
        }

        var choices = DpsFightChoice.Build(snap.RecentFights);
        if (_dpsFightPicker.ItemsSource is IReadOnlyList<DpsFightChoice> existing && existing.SequenceEqual(choices))
        {
            return;
        }

        if (_dpsFight is not null && !choices.Any(c => c.Key == _dpsFight))
        {
            _dpsFight = null;
        }

        _dpsPickerUpdating = true;
        _dpsFightPicker.ItemsSource = choices;
        _dpsFightPicker.SelectedItem = choices.First(c => c.Key == _dpsFight);
        _lastDpsDetailsRender = default;
        _dpsPickerUpdating = false;
    }

    private void UpdateDpsDetails(EngineSnapshot snapshot, OverlayPanel panel)
    {
        if (!_settings.DpsExpandedView)
        {
            if (_dpsDetailsShown)
            {
                panel.SetAlternateBody(null);
                _dpsDetailsShown = false;
                _dpsDetailsView = null;
                _dpsDetailsSelectedTab = 0;
            }

            return;
        }

        if (_dpsDetailsShown &&
            (DateTime.Now - _lastDpsDetailsRender).TotalSeconds < DpsDetailsRefreshSeconds)
        {
            return;
        }

        var fight = OverlayPresenters.SelectedFight(snapshot, _dpsFight);
        var details = fight is null
            ? new AbilityBreakdown("No fight yet", "No fight data has been recorded.", "DPS", [])
            : OverlayPresenters.FightBreakdown(fight);
        _lastDpsDetailsRender = DateTime.Now;
        if (_dpsDetailsShown && _dpsDetailsView is not null && _dpsDetailsView.TryUpdate(details))
        {
            return;
        }

        _dpsDetailsView = new AbilityDetailsView(details);
        if (_dpsDetailsView.Root is TabControl tabs)
        {
            tabs.SelectedIndex = Math.Min(_dpsDetailsSelectedTab, tabs.Items.Count - 1);
            tabs.SelectionChanged += (_, e) =>
            {
                if (ReferenceEquals(e.OriginalSource, tabs))
                {
                    _dpsDetailsSelectedTab = tabs.SelectedIndex;
                }
            };
        }

        panel.SetAlternateBody(_dpsDetailsView.Root);
        _dpsDetailsShown = true;
    }
}
