using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EqLog.Overlay.Views;

/// <summary>
/// Shows an <see cref="OverlayTable"/> as a grid. The table shape depends on the data, so <see cref="Table"/> is turned
/// into an <see cref="OverlayTableLayout"/> that the XAML template lays out.
/// </summary>
public sealed partial class OverlayTableView : UserControl
{
    /// <summary>The table to show.</summary>
    public static readonly DependencyProperty TableProperty = DependencyProperty.Register(
        nameof(Table), typeof(OverlayTable), typeof(OverlayTableView), new PropertyMetadata(null, Relayout));

    /// <summary>The command a link cell runs with its URL.</summary>
    public static readonly DependencyProperty LinkCommandProperty = DependencyProperty.Register(
        nameof(LinkCommand), typeof(ICommand), typeof(OverlayTableView), new PropertyMetadata(null, Relayout));

    private static readonly DependencyPropertyKey LayoutPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Layout), typeof(OverlayTableLayout), typeof(OverlayTableView), new PropertyMetadata(null));

    /// <summary>The grid shape of <see cref="Table"/>.</summary>
    public static readonly DependencyProperty LayoutProperty = LayoutPropertyKey.DependencyProperty;

    /// <summary>Creates an empty table view.</summary>
    public OverlayTableView()
    {
        InitializeComponent();
    }

    /// <summary>The table to show.</summary>
    public OverlayTable? Table
    {
        get
        {
            return (OverlayTable?)GetValue(TableProperty);
        }
        set
        {
            SetValue(TableProperty, value);
        }
    }

    /// <summary>The command a link cell runs with its URL.</summary>
    public ICommand? LinkCommand
    {
        get
        {
            return (ICommand?)GetValue(LinkCommandProperty);
        }
        set
        {
            SetValue(LinkCommandProperty, value);
        }
    }

    /// <summary>The grid shape of <see cref="Table"/>.</summary>
    public OverlayTableLayout? Layout
    {
        get
        {
            return (OverlayTableLayout?)GetValue(LayoutProperty);
        }
    }

    private static void Relayout(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (OverlayTableView)d;
        var layout = view.Table is { } table ? OverlayTableLayout.From(table, links: view.LinkCommand is not null) : null;
        view.SetValue(LayoutPropertyKey, layout);
    }
}
