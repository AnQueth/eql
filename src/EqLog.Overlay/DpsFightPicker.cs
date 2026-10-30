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
    private DpsFightKey? _dpsFight;
    private bool _dpsPickerUpdating;

    private void ConfigureDpsToolbar(OverlayPanel panel)
    {
        _dpsFightPicker = new ComboBox
        {
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
        panel.SetToolbar(_dpsFightPicker);
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
        _dpsPickerUpdating = false;
    }
}
