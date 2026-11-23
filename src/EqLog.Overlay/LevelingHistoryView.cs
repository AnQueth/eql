using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using EqLog.Application.Sessions;

namespace EqLog.Overlay;

internal sealed record LevelingGridRow(DateTime Start, int? Level, string StartText, string EndText,
    string Build, int Kills, double Minutes, bool IsCurrent, string Details);

internal sealed class LevelingDateRangePicker : WrapPanel
{
    private bool _changing;
    private string? _validationError;

    internal ComboBox Preset { get; } = new()
    {
        ItemsSource = new[] { "Today", "Yesterday", "Last 7 days", "Last week (Mon-Sun)", "Custom dates", "All history" },
        SelectedIndex = (int)LevelingPeriod.AllHistory,
        MinWidth = 140,
    };

    internal DatePicker From { get; } = new() { Width = 130, SelectedDateFormat = DatePickerFormat.Short };
    internal DatePicker To { get; } = new() { Width = 130, SelectedDateFormat = DatePickerFormat.Short };

    internal LevelingDateRangePicker()
    {
        Language = XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);
        ToolTip = "Dates are inclusive and use the local game-log clock. Pick a date to use a custom range. " +
            "All history leaves both dates unrestricted.";
        foreach (var control in new FrameworkElement[] { Preset, new TextBlock { Text = "From" }, From,
            new TextBlock { Text = "To" }, To })
        {
            control.Margin = new Thickness(0, 1, 6, 1);
            control.VerticalAlignment = VerticalAlignment.Center;
            Children.Add(control);
        }
        Preset.SelectionChanged += (_, _) =>
        {
            if (!_changing)
            {
                ApplyPreset(DateTime.Now);
            }
        };
        foreach (var picker in new[] { From, To })
        {
            picker.SelectedDateChanged += (_, _) =>
            {
                _validationError = null;
                EditDate(picker);
            };
            picker.PreviewKeyDown += (_, _) => _validationError = null;
            picker.PreviewMouseDown += (_, _) => _validationError = null;
            picker.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => EditDate(picker)));
            picker.DateValidationError += (_, error) =>
            {
                error.ThrowException = false;
                EditDate(picker);
                _validationError = $"Invalid date: {error.Text}. Choose a valid date from the calendar.";
            };
        }
        ApplyPreset(DateTime.Now);
    }

    internal bool TryGetRange(DateTime now, out (DateTime Start, DateTime End) range, out string? error)
    {
        var period = (LevelingPeriod)Preset.SelectedIndex;
        if (_validationError is not null)
        {
            range = default;
            error = _validationError;
            return false;
        }
        if (period != LevelingPeriod.Custom)
        {
            ApplyPreset(now);
            range = LevelingQueries.Period(period, now);
            error = null;
            return true;
        }
        if (!DateTime.TryParse(From.Text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var from) ||
            !DateTime.TryParse(To.Text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var to) ||
            to.Date < from.Date || to.Date == DateTime.MaxValue.Date)
        {
            range = default;
            error = "Choose valid From and To dates; To must be on or after From.";
            return false;
        }
        range = LevelingQueries.Period(LevelingPeriod.Custom, now, from, to);
        error = null;
        return true;
    }

    private void ApplyPreset(DateTime now)
    {
        var period = (LevelingPeriod)Preset.SelectedIndex;
        if (period == LevelingPeriod.Custom)
        {
            return;
        }
        var range = LevelingQueries.Period(period, now);
        _changing = true;
        _validationError = null;
        From.SelectedDate = period == LevelingPeriod.AllHistory ? null : range.Start.Date;
        To.SelectedDate = period == LevelingPeriod.AllHistory ? null : range.End.AddTicks(-1).Date;
        _changing = false;
    }

    private void EditDate(DatePicker edited)
    {
        if (_changing || Preset.SelectedIndex == (int)LevelingPeriod.Custom)
        {
            return;
        }
        _changing = true;
        Preset.SelectedIndex = (int)LevelingPeriod.Custom;
        var other = ReferenceEquals(edited, From) ? To : From;
        if (other.SelectedDate is null && string.IsNullOrWhiteSpace(other.Text))
        {
            other.SelectedDate = ReferenceEquals(other, To) ? DateTime.Today : edited.SelectedDate ?? DateTime.Today;
        }
        _changing = false;
    }
}

internal sealed class LevelingHistoryGrid : DockPanel
{
    private readonly ObservableCollection<LevelingGridRow> _rows = [];
    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(4, 4, 4, 8),
        Visibility = Visibility.Collapsed,
    };

    internal DataGrid Table { get; } = new()
    {
        IsReadOnly = true,
        AutoGenerateColumns = false,
        CanUserAddRows = false,
        CanUserDeleteRows = false,
        CanUserSortColumns = false,
        CanUserResizeRows = false,
        SelectionMode = DataGridSelectionMode.Single,
        SelectionUnit = DataGridSelectionUnit.FullRow,
        EnableRowVirtualization = true,
        EnableColumnVirtualization = true,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        RowHeight = double.NaN,
        MinColumnWidth = 50,
        AlternationCount = 2,
        RowDetailsVisibilityMode = DataGridRowDetailsVisibilityMode.VisibleWhenSelected,
    };

    internal LevelingHistoryGrid()
    {
        _status.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        SetDock(_status, Dock.Top);
        Children.Add(_status);
        Children.Add(Table);
        Table.ItemsSource = _rows;
        VirtualizingPanel.SetVirtualizationMode(Table, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(Table, true);
        AddColumn("Started", nameof(LevelingGridRow.StartText), new(1, DataGridLengthUnitType.Star), 110);
        AddColumn("Ended", nameof(LevelingGridRow.EndText), new(1, DataGridLengthUnitType.Star), 110);
        AddColumn("Level", nameof(LevelingGridRow.Level), new(60), 60, center: true);
        AddColumn("Build at the time", nameof(LevelingGridRow.Build), new(2, DataGridLengthUnitType.Star), 145);
        AddColumn("Kills", nameof(LevelingGridRow.Kills), new(60), 60, numeric: true);
        AddColumn("Minutes", nameof(LevelingGridRow.Minutes), new(80), 80, numeric: true, format: "0.##");
        Table.Columns[0].SortDirection = ListSortDirection.Descending;
        Table.Columns[2].SortDirection = ListSortDirection.Descending;
        var rowStyle = new Style(typeof(DataGridRow));
        rowStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(LevelingGridRow.Details))));
        var current = new DataTrigger { Binding = new Binding(nameof(LevelingGridRow.IsCurrent)), Value = true };
        current.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        rowStyle.Triggers.Add(current);
        Table.RowStyle = rowStyle;
        var details = new FrameworkElementFactory(typeof(TextBlock));
        details.SetBinding(TextBlock.TextProperty, new Binding(nameof(LevelingGridRow.Details)));
        details.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        details.SetValue(MarginProperty, new Thickness(8, 4, 8, 6));
        Table.RowDetailsTemplate = new DataTemplate { VisualTree = details };
        ToolTip = "Newest level start first, then level descending. Select a row for XP sources and timestamp notes. " +
            "Kills are your logged killing blows. Minutes include logged downtime but exclude gaps over 30 minutes.";
    }

    internal void Update(IReadOnlyList<LevelingGridRow> rows, string status)
    {
        if (_status.Text != status)
        {
            _status.Text = status;
        }
        var visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
        if (_status.Visibility != visibility)
        {
            _status.Visibility = visibility;
        }
        if (_rows.SequenceEqual(rows))
        {
            return;
        }
        var selected = Table.SelectedItem as LevelingGridRow;
        for (int index = 0; index < rows.Count; index++)
        {
            if (index >= _rows.Count)
            {
                _rows.Add(rows[index]);
            }
            else if (_rows[index] != rows[index])
            {
                _rows[index] = rows[index];
            }
        }
        while (_rows.Count > rows.Count)
        {
            _rows.RemoveAt(_rows.Count - 1);
        }
        if (selected is not null)
        {
            Table.SelectedItem = _rows.FirstOrDefault(row => row.Start == selected.Start && row.Level == selected.Level);
        }
    }

    private void AddColumn(string header, string path, DataGridLength width, double minWidth,
        bool numeric = false, bool center = false, string? format = null)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty,
            numeric ? TextAlignment.Right : center ? TextAlignment.Center : TextAlignment.Left));
        Table.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path) { StringFormat = format, TargetNullValue = "Unknown" },
            Width = width,
            MinWidth = minWidth,
            ElementStyle = style,
        });
    }
}
