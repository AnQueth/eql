using System.Windows.Threading;
using EqLog.Application.Sessions;
using EqLog.Overlay.Messaging;
using EqLog.Overlay.Mvvm;

namespace EqLog.Overlay.ViewModels;

/// <summary>Provides leveling history data without exposing live application state to the view model.</summary>
public interface ILevelingOverlayServices
{
    /// <summary>The selected character name.</summary>
    string? CharacterName { get; }

    /// <summary>The history/session revision.</summary>
    long Revision { get; }

    /// <summary>The current history storage error.</summary>
    string? StorageError { get; }

    /// <summary>Runs the leveling report.</summary>
    LevelingReport LevelingReport(LevelingRequest request);
}

/// <summary>The Leveling Analyzer filters, view state, grid rows and rendered frame content.</summary>
public sealed class LevelingOverlayViewModel : OverlayViewModel, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ILevelingOverlayServices _leveling;
    private readonly AsyncTextFilter<LevelingOverlayResult> _work;
    private string? _queryError;
    private int _selectedViewIndex;
    private int _windowIndex = 1;
    private bool _splitByBuild;
    private string _zone = "";
    private string _level = "";
    private int _presetIndex = (int)LevelingPeriod.AllHistory;
    private DateTime? _fromDate;
    private DateTime? _toDate;
    private string _gridStatus = "";
    private LevelingGridRow? _selectedGridRow;
    private TailProgress _progress;

    /// <summary>Creates the leveling view model.</summary>
    public LevelingOverlayViewModel(OverlayDefinition definition, IOverlayActions actions,
        IOverlayViewSource source, ILevelingOverlayServices leveling, Dispatcher dispatcher)
        : base(definition, actions)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(leveling);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _leveling = leveling;
        _dispatcher = dispatcher;
        _work = new AsyncTextFilter<LevelingOverlayResult>(dispatcher, error =>
        {
            UsageTelemetry.TrackError(error, UsageErrorSource.StartupData);
            _queryError = $"Leveling query failed: {error.Message}";
            RefreshNow();
        });
        LevelsCommand = new RelayCommand(() => SelectedViewIndex = 0);
        PlacesCommand = new RelayCommand(() => SelectedViewIndex = 1);
        RefreshCommand = new RelayCommand(RefreshNow);
    }

    /// <summary>Available history presets.</summary>
    public IReadOnlyList<string> PresetOptions { get; } =
        ["Today", "Yesterday", "Last 7 days", "Last week (Mon-Sun)", "Custom dates", "All history"];

    /// <summary>Available run lengths.</summary>
    public IReadOnlyList<string> WindowOptions { get; } =
        ["5 minutes", "15 minutes", "30 minutes", "60 minutes"];

    /// <summary>Available result views.</summary>
    public IReadOnlyList<string> ViewOptions { get; } = ["Levels", "Best places"];

    /// <summary>The Levels/Best places selection index.</summary>
    public int SelectedViewIndex
    {
        get
        {
            return _selectedViewIndex;
        }
        set
        {
            if (SetProperty(ref _selectedViewIndex, Math.Clamp(value, 0, 1)))
            {
                OnPropertyChanged(nameof(IsLevelsView));
                OnPropertyChanged(nameof(IsBestPlacesView));
                RefreshNow();
            }
        }
    }

    /// <summary>Whether Levels is selected.</summary>
    public bool IsLevelsView
    {
        get
        {
            return _selectedViewIndex == 0;
        }
    }

    /// <summary>Whether Best places is selected.</summary>
    public bool IsBestPlacesView
    {
        get
        {
            return _selectedViewIndex == 1;
        }
    }

    /// <summary>The continuous run window index.</summary>
    public int WindowIndex
    {
        get
        {
            return _windowIndex;
        }
        set
        {
            if (SetProperty(ref _windowIndex, Math.Clamp(value, 0, 3)))
            {
                RefreshNow();
            }
        }
    }

    /// <summary>Whether XP sources are split by build.</summary>
    public bool SplitByBuild
    {
        get
        {
            return _splitByBuild;
        }
        set
        {
            if (SetProperty(ref _splitByBuild, value))
            {
                RefreshNow();
            }
        }
    }

    /// <summary>The zone filter.</summary>
    public string Zone
    {
        get
        {
            return _zone;
        }
        set
        {
            string text = value ?? "";
            if (SetProperty(ref _zone, text))
            {
                RefreshNow();
            }
        }
    }

    /// <summary>The character level filter.</summary>
    public string Level
    {
        get
        {
            return _level;
        }
        set
        {
            string text = value ?? "";
            if (SetProperty(ref _level, text))
            {
                RefreshNow();
            }
        }
    }

    /// <summary>The date preset index.</summary>
    public int PresetIndex
    {
        get
        {
            return _presetIndex;
        }
        set
        {
            int index = Math.Clamp(value, 0, 5);
            if (SetProperty(ref _presetIndex, index))
            {
                ApplyPreset(DateTime.Now);
                RefreshNow();
            }
        }
    }

    /// <summary>The custom range start date.</summary>
    public DateTime? FromDate
    {
        get
        {
            return _fromDate;
        }
        set
        {
            if (SetProperty(ref _fromDate, value))
            {
                SwitchToCustom(toEdited: false);
            }
            RefreshNow();
        }
    }

    /// <summary>The custom range end date.</summary>
    public DateTime? ToDate
    {
        get
        {
            return _toDate;
        }
        set
        {
            if (SetProperty(ref _toDate, value))
            {
                SwitchToCustom(toEdited: true);
            }
            RefreshNow();
        }
    }

    /// <summary>Rows shown by the Levels DataGrid.</summary>
    public BulkObservableCollection<LevelingGridRow> GridRows { get; } = [];

    /// <summary>The selected level row, whose details are expanded.</summary>
    public LevelingGridRow? SelectedGridRow
    {
        get
        {
            return _selectedGridRow;
        }
        set
        {
            SetProperty(ref _selectedGridRow, value);
        }
    }

    /// <summary>Status text shown above the grid.</summary>
    public string GridStatus
    {
        get
        {
            return _gridStatus;
        }
        private set
        {
            if (SetProperty(ref _gridStatus, value ?? ""))
            {
                OnPropertyChanged(nameof(HasGridStatus));
            }
        }
    }

    /// <summary>Whether grid status text is visible.</summary>
    public bool HasGridStatus
    {
        get
        {
            return !string.IsNullOrEmpty(_gridStatus);
        }
    }

    /// <summary>Selects Levels.</summary>
    public RelayCommand LevelsCommand { get; }

    /// <summary>Selects Best places.</summary>
    public RelayCommand PlacesCommand { get; }

    /// <summary>Refreshes the report.</summary>
    public RelayCommand RefreshCommand { get; }

    /// <inheritdoc />
    protected override void OnFrame(OverlayFrameMessage frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _progress = frame.Progress;
        RefreshNow();
    }

    /// <summary>Builds the grid row projection for tests and rendering.</summary>
    public static IReadOnlyList<LevelingGridRow> BuildLevelingGridRows(LevelingReport report, LevelingRequest request)
    {
        return MainWindow.BuildLevelingGridRows(report, request);
    }

    /// <summary>Builds row-list content for tests and rendering.</summary>
    public static OverlayContent BuildLevelingContent(LevelingReport report, LevelingRequest request, bool places)
    {
        return MainWindow.BuildLevelingContent(report, request, places);
    }

    /// <summary>Disposes pending queries.</summary>
    public void Dispose()
    {
        _work.Dispose();
    }

    private void ApplyPreset(DateTime now)
    {
        var period = (LevelingPeriod)_presetIndex;
        if (period == LevelingPeriod.Custom)
        {
            return;
        }
        var range = LevelingQueries.Period(period, now);
        DateTime? from = period == LevelingPeriod.AllHistory ? null : range.Start.Date;
        DateTime? to = period == LevelingPeriod.AllHistory ? null : range.End.AddTicks(-1).Date;
        if (_fromDate != from)
        {
            _fromDate = from;
            OnPropertyChanged(nameof(FromDate));
        }
        if (_toDate != to)
        {
            _toDate = to;
            OnPropertyChanged(nameof(ToDate));
        }
    }

    /// <summary>
    /// Editing a date selects Custom; a missing other end becomes the edited date (From) or today (To), so one pick
    /// is already a usable range.
    /// </summary>
    private void SwitchToCustom(bool toEdited)
    {
        if (_presetIndex == (int)LevelingPeriod.Custom)
        {
            return;
        }
        _presetIndex = (int)LevelingPeriod.Custom;
        OnPropertyChanged(nameof(PresetIndex));
        if (toEdited && _fromDate is null)
        {
            _fromDate = _toDate ?? DateTime.Today;
            OnPropertyChanged(nameof(FromDate));
        }
        else if (!toEdited && _toDate is null)
        {
            _toDate = DateTime.Today;
            OnPropertyChanged(nameof(ToDate));
        }
    }

    private void RefreshNow()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(RefreshNow);
            return;
        }

        string? character = _leveling.CharacterName;
        if (string.IsNullOrEmpty(character))
        {
            GridRows.ReplaceAll([]);
            GridStatus = "";
            Show("Leveling Analyzer", null, [new OverlayRow("Select a character log to import leveling history.", Wrap: true)]);
            return;
        }

        if (!TryGetRange(DateTime.Now, out var range, out var dateError))
        {
            GridRows.ReplaceAll([]);
            GridStatus = dateError ?? "";
            Show("Leveling Analyzer", character, [new OverlayRow(dateError ?? "", Wrap: true)]);
            return;
        }

        int? level = null;
        if (!string.IsNullOrWhiteSpace(_level))
        {
            if (!int.TryParse(_level.Trim(), out int parsed) || parsed <= 0)
            {
                GridRows.ReplaceAll([]);
                string message = "Enter a positive character level, or leave Level blank for all levels.";
                GridStatus = message;
                Show("Leveling Analyzer", character, [new OverlayRow(message, Wrap: true)]);
                return;
            }
            level = parsed;
        }

        int window = new[] { 5, 15, 30, 60 }[Math.Clamp(_windowIndex, 0, 3)];
        var request = new LevelingRequest(character, range.Start, range.End, window,
            _splitByBuild, _zone, level);
        bool places = _selectedViewIndex == 1;
        var key = (request, places, _leveling.Revision, Refresh: DateTime.UtcNow.Ticks / (TimeSpan.TicksPerSecond * 2));
        _work.Request(key, token =>
        {
            var report = _leveling.LevelingReport(request);
            token.ThrowIfCancellationRequested();
            return new LevelingOverlayResult(MainWindow.BuildLevelingContent(report, request, places),
                places ? [] : MainWindow.BuildLevelingGridRows(report, request));
        }, result =>
        {
            _queryError = null;
            ApplyResult(result);
        }, context: (request, places, _leveling.Revision));

        if (_work.Result is { } result)
        {
            ApplyResult(result);
        }
    }

    private void ApplyResult(LevelingOverlayResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var content = result.Content;
        if (!_progress.CaughtUp)
        {
            double fraction = _progress.TotalBytes > 0 ? (double)_progress.BytesRead / _progress.TotalBytes : 0;
            content = content with
            {
                Rows = new[] { new OverlayRow($"Importing history {fraction:P0} - {_progress.LinesRead:N0} lines. Results are partial.",
                    Bold: true, Wrap: true) }.Concat(content.Rows).ToArray(),
            };
        }
        string? error = _leveling.StorageError ?? _queryError;
        if (error is not null)
        {
            content = content with
            {
                Rows = new[] { new OverlayRow(error, Wrap: true) }.Concat(content.Rows).ToArray(),
            };
        }

        if (!IsBestPlacesView)
        {
            SetGridRows(result.Levels);
            GridStatus = string.Join("\n", content.Rows.Select(row => row.Left));
        }
        Show(content);
    }

    /// <summary>
    /// Updates the grid in place and keeps the selected level selected (matched by start and level), so its open
    /// details survive live refreshes.
    /// </summary>
    internal void SetGridRows(IReadOnlyList<LevelingGridRow> rows)
    {
        var selected = _selectedGridRow;
        GridRows.Reconcile(rows, (current, next) => current == next, row => row);
        if (selected is not null)
        {
            SelectedGridRow = GridRows.FirstOrDefault(row => row.Start == selected.Start && row.Level == selected.Level);
        }
    }

    /// <summary>The selected date range at <paramref name="now"/>; presets follow the clock across midnight.</summary>
    internal bool TryGetRange(DateTime now, out (DateTime Start, DateTime End) range, out string? error)
    {
        var period = (LevelingPeriod)_presetIndex;
        if (period != LevelingPeriod.Custom)
        {
            ApplyPreset(now);
            range = LevelingQueries.Period(period, now);
            error = null;
            return true;
        }
        if (!_fromDate.HasValue || !_toDate.HasValue || _toDate.Value.Date < _fromDate.Value.Date)
        {
            range = default;
            error = "Choose valid From and To dates; To must be on or after From.";
            return false;
        }
        range = LevelingQueries.Period(LevelingPeriod.Custom, now, _fromDate, _toDate);
        error = null;
        return true;
    }
}



