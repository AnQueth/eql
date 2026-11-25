using System.Windows;

namespace EqLog.Overlay.Views;

/// <summary>How a table cell's text is styled.</summary>
public enum OverlayTableCellKind
{
    /// <summary>A column table value.</summary>
    Value,

    /// <summary>A column header.</summary>
    Header,

    /// <summary>A label in a label/value table.</summary>
    Label,

    /// <summary>A value in a label/value table.</summary>
    LabelValue,
}

/// <summary>An item placed in a table grid cell or row.</summary>
public abstract record OverlayTableItem(int Row, int Column, int ColumnSpan);

/// <summary>The rule under a column table's header.</summary>
public sealed record OverlayTableRule(int ColumnSpan) : OverlayTableItem(0, 0, ColumnSpan);

/// <summary>The background of an alternate column table row.</summary>
public sealed record OverlayTableStripe(int Row, int ColumnSpan) : OverlayTableItem(Row, 0, ColumnSpan);

/// <summary>A table cell's text.</summary>
public sealed record OverlayTableCell(string Text, int Row, int Column, OverlayTableCellKind Kind, bool IsNumeric,
    bool Wraps, Thickness Margin, string? Url) : OverlayTableItem(Row, Column, 1)
{
    /// <summary>Whether clicking the cell opens <see cref="Url"/>.</summary>
    public bool IsLink
    {
        get
        {
            return Url is not null;
        }
    }
}

/// <summary>
/// The grid shape for an <see cref="OverlayTable"/>. Label/value tables use auto-width label columns; column tables
/// get a header rule, zebra rows, and right-aligned numeric columns, with the first text column taking the spare width.
/// </summary>
public sealed class OverlayTableLayout
{
    private OverlayTableLayout(IReadOnlyList<GridLength> columns, int rowCount, IReadOnlyList<OverlayTableItem> items)
    {
        Columns = columns;
        RowCount = rowCount;
        Items = items;
    }

    public IReadOnlyList<GridLength> Columns
    {
        get;
    }

    public int RowCount
    {
        get;
    }

    /// <summary>The rule, stripes, and cells, in drawing order.</summary>
    public IReadOnlyList<OverlayTableItem> Items
    {
        get;
    }

    /// <summary>Lays out <paramref name="table"/>; its link cells are only clickable when <paramref name="links"/> is set.</summary>
    public static OverlayTableLayout From(OverlayTable table, bool links)
    {
        ArgumentNullException.ThrowIfNull(table);
        int columns = Math.Max(table.Header?.Count ?? 0, table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.Count));
        if (columns == 0)
        {
            return new([], 0, []);
        }

        var numeric = new bool[columns];
        for (int c = 0; c < columns; c++)
        {
            var cells = table.Rows.Where(r => c < r.Count && r[c].Length > 0).Select(r => r[c]).ToArray();
            numeric[c] = !table.RowLabels && cells.Length > 0 && cells.All(IsNumeric);
        }

        bool allNumeric = numeric.All(n => n);
        int stretch = table.RowLabels ? -1 : Array.IndexOf(numeric, false);
        var widths = new GridLength[columns];
        for (int c = 0; c < columns; c++)
        {
            if (table.RowLabels)
            {
                widths[c] = c % 2 == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            }
            else if (allNumeric || c == stretch)
            {
                widths[c] = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                widths[c] = GridLength.Auto;
            }
        }

        var items = new List<OverlayTableItem>();
        int gridRow = 0;
        if (table.Header is { } header)
        {
            items.Add(new OverlayTableRule(columns));
            for (int c = 0; c < header.Count; c++)
            {
                items.Add(new OverlayTableCell(header[c], 0, c, OverlayTableCellKind.Header, numeric[c], Wraps: false,
                    CellMargin(c, columns), Url: null));
            }
            gridRow++;
        }

        for (int r = 0; r < table.Rows.Count; r++, gridRow++)
        {
            if (table.Header is not null && r % 2 == 1)
            {
                items.Add(new OverlayTableStripe(gridRow, columns));
            }

            var cells = table.Rows[r];
            for (int c = 0; c < cells.Count; c++)
            {
                bool label = table.RowLabels && c % 2 == 0;
                var kind = label
                    ? OverlayTableCellKind.Label
                    : table.RowLabels ? OverlayTableCellKind.LabelValue : OverlayTableCellKind.Value;
                string? url = null;
                if (links && !label && table.Links is { } urls && urls.TryGetValue(cells[c], out string? link))
                {
                    url = link;
                }
                items.Add(new OverlayTableCell(cells[c], gridRow, c, kind, numeric[c], Wraps: !label,
                    CellMargin(c, columns), url));
            }
        }
        return new(widths, gridRow, items);
    }

    private static Thickness CellMargin(int column, int columns)
    {
        return new Thickness(column == 0 ? 2 : 6, 3, column == columns - 1 ? 2 : 6, 3);
    }

    private static bool IsNumeric(string text)
    {
        return text.Length > 0 &&
            (char.IsDigit(text[0]) || (text.Length > 1 && text[0] is '-' or '+' && char.IsDigit(text[1])) || text == "?");
    }
}
