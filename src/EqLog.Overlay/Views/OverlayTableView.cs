using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using EqLog.Overlay.Mvvm;

namespace EqLog.Overlay.Views;

/// <summary>
/// Shows an <see cref="OverlayTable"/> as a grid. The table shape (column count, widths, numeric alignment) depends on
/// the data, so the grid is generated whenever <see cref="Table"/> changes.
/// </summary>
public sealed class OverlayTableView : ContentControl
{
    /// <summary>The table to show.</summary>
    public static readonly DependencyProperty TableProperty = DependencyProperty.Register(
        nameof(Table), typeof(OverlayTable), typeof(OverlayTableView), new PropertyMetadata(null, Rebuild));

    /// <summary>The command a link cell runs with its URL.</summary>
    public static readonly DependencyProperty LinkCommandProperty = DependencyProperty.Register(
        nameof(LinkCommand), typeof(ICommand), typeof(OverlayTableView), new PropertyMetadata(null, Rebuild));

    /// <summary>Creates an empty table view.</summary>
    public OverlayTableView()
    {
        Focusable = false;
        IsTabStop = false;
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

    private static void Rebuild(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (OverlayTableView)d;
        view.Content = view.Table is { } table ? OverlayTableBuilder.Build(table, view.LinkCommand) : null;
    }
}

/// <summary>
/// Builds the grid for an <see cref="OverlayTable"/>. Label/value tables use muted auto-width label columns; column tables
/// get a header rule, zebra rows, and right-aligned numeric columns.
/// </summary>
public static class OverlayTableBuilder
{
    /// <summary>Builds a table at a fixed text size (one smaller than <paramref name="fontSize"/>, at least 10).</summary>
    public static FrameworkElement Build(OverlayTable table, double fontSize, Action<string>? openLink = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        var grid = Build(table, openLink is null ? null : new RelayCommand<string>(openLink));
        grid.SetValue(TextElement.FontSizeProperty, Math.Max(10, fontSize - 1));
        return grid;
    }

    /// <summary>Builds a table that inherits its text size; link cells run <paramref name="linkCommand"/> with their URL.</summary>
    public static Grid Build(OverlayTable table, ICommand? linkCommand)
    {
        ArgumentNullException.ThrowIfNull(table);
        int columns = Math.Max(table.Header?.Count ?? 0, table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.Count));
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        if (columns == 0)
        {
            return grid;
        }

        var numeric = new bool[columns];
        for (int c = 0; c < columns; c++)
        {
            var cells = table.Rows.Where(r => c < r.Count && r[c].Length > 0).Select(r => r[c]).ToArray();
            numeric[c] = !table.RowLabels && cells.Length > 0 && cells.All(IsNumeric);
        }

        bool allNumeric = numeric.All(n => n);
        int stretch = table.RowLabels ? -1 : Array.IndexOf(numeric, false);
        for (int c = 0; c < columns; c++)
        {
            GridLength width;
            if (table.RowLabels)
            {
                width = c % 2 == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            }
            else if (allNumeric || c == stretch)
            {
                width = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                width = GridLength.Auto;
            }
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        }

        int gridRow = 0;
        if (table.Header is { } header)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rule = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
            rule.SetResourceReference(Border.BorderBrushProperty, "GridLine");
            Grid.SetColumnSpan(rule, columns);
            grid.Children.Add(rule);
            for (int c = 0; c < header.Count; c++)
            {
                var cell = TableCell(header[c], c, columns, numeric[c], wrap: false);
                cell.FontWeight = FontWeights.SemiBold;
                cell.TextTrimming = TextTrimming.CharacterEllipsis;
                cell.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
                grid.Children.Add(cell);
            }
            gridRow++;
        }

        for (int r = 0; r < table.Rows.Count; r++, gridRow++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (table.Header is not null && r % 2 == 1)
            {
                var stripe = new Border { CornerRadius = new CornerRadius(2), Opacity = 0.7 };
                stripe.SetResourceReference(Border.BackgroundProperty, "Alternate");
                Grid.SetRow(stripe, gridRow);
                Grid.SetColumnSpan(stripe, columns);
                grid.Children.Add(stripe);
            }

            var cells = table.Rows[r];
            for (int c = 0; c < cells.Count; c++)
            {
                bool label = table.RowLabels && c % 2 == 0;
                var cell = TableCell(cells[c], c, columns, numeric[c], wrap: !label);
                if (label)
                {
                    cell.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
                }
                else if (table.RowLabels)
                {
                    cell.FontWeight = FontWeights.SemiBold;
                }
                if (!label && linkCommand is not null && table.Links is { } links && links.TryGetValue(cells[c], out string? url))
                {
                    MakeLink(cell, url, linkCommand);
                }
                Grid.SetRow(cell, gridRow);
                grid.Children.Add(cell);
            }
        }
        return grid;
    }

    /// <summary>Turns a cell into a link; <see cref="ClickCommand"/> keeps a clickable card around it from also acting.</summary>
    private static void MakeLink(TextBlock cell, string url, ICommand command)
    {
        cell.TextDecorations = TextDecorations.Underline;
        cell.Cursor = Cursors.Hand;
        cell.ToolTip = "Open on EQLWiki";
        cell.HorizontalAlignment = HorizontalAlignment.Left;
        cell.Background = System.Windows.Media.Brushes.Transparent;
        ClickCommand.SetCommandParameter(cell, url);
        ClickCommand.SetCommand(cell, command);
    }

    private static TextBlock TableCell(string text, int column, int columns, bool numeric, bool wrap)
    {
        var cell = new TextBlock
        {
            Text = text,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextAlignment = numeric ? TextAlignment.Right : TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(column == 0 ? 2 : 6, 3, column == columns - 1 ? 2 : 6, 3),
        };
        Grid.SetColumn(cell, column);
        return cell;
    }

    private static bool IsNumeric(string text)
    {
        return text.Length > 0 &&
            (char.IsDigit(text[0]) || (text.Length > 1 && text[0] is '-' or '+' && char.IsDigit(text[1])) || text == "?");
    }
}
