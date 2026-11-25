using System.Windows;
using System.Windows.Controls;

namespace EqLog.Overlay.Views;

/// <summary>Shows a bindable <see cref="AbilityBreakdown"/> in a compact <see cref="AbilityDetailsView"/>, updating it in place.</summary>
public sealed class AbilityDetailsHost : ContentControl
{
    /// <summary>The breakdown to show.</summary>
    public static readonly DependencyProperty BreakdownProperty = DependencyProperty.Register(
        nameof(Breakdown), typeof(AbilityBreakdown), typeof(AbilityDetailsHost),
        new PropertyMetadata(null, (target, _) => ((AbilityDetailsHost)target).Refresh()));

    private AbilityDetailsView? _view;

    /// <summary>The breakdown to show; null clears the view.</summary>
    public AbilityBreakdown? Breakdown
    {
        get
        {
            return (AbilityBreakdown?)GetValue(BreakdownProperty);
        }
        set
        {
            SetValue(BreakdownProperty, value);
        }
    }

    private void Refresh()
    {
        if (Breakdown is not { } breakdown)
        {
            _view = null;
            Content = null;
            return;
        }
        if (_view is not null && _view.TryUpdate(breakdown))
        {
            return;
        }
        _view = new AbilityDetailsView(breakdown, compact: true);
        _view.Root.Height = 180;
        Content = _view.Root;
    }
}
