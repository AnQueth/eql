using System.Collections.ObjectModel;
using System.Windows.Threading;
using EqLog.Application.Inventory;
using EqLog.Application.Motes;
using EqLog.Core;
using EqLog.Core.Tracking;
using EqLog.Overlay.Messaging;
using EqLog.Overlay.Mvvm;

namespace EqLog.Overlay.ViewModels;

/// <summary>Presentation state and commands for planning item and spell mote upgrades.</summary>
public sealed partial class MotePlanOverlayViewModel : OverlayViewModel
{
    private readonly Func<InventorySnapshot> _snapshot;
    private readonly InventoryFilterWork _filterWork;
    private InventorySnapshot? _currentSnapshot;
    private IReadOnlyList<InventoryExportItem> _inventoryRows = [];
    private string _inventorySignature = "";
    private string? _character;
    private string _name = "";
    private string? _selectedInventoryName;
    private string _points = "0";
    private bool _useMotes = true;
    private bool _preferFragments = true;
    private bool _useCopies = true;
    private bool _feeding;
    private bool _feedUsableMotes = true;
    private string _fodder = "0";
    private int _fodderRank;
    private MoteUpgradeKind _kind;
    private int _startRank;
    private int _targetRank = MotePlanningQueries.MaxRank;
    private IReadOnlyList<int> _targetRanks = MotePlanningQueries.Ranks;
    private MotePlanPage _page;
    private MotePlanReadModel? _plan;
    private string _summary = "";
    private bool _updatingSelection;
    private int[] _hypotheticalCopies = [];
    private string _copyContext = "";

    /// <summary>Creates a planner using detached inventory snapshots.</summary>
    public MotePlanOverlayViewModel(OverlayDefinition definition, IOverlayActions actions,
        Func<InventorySnapshot> snapshot)
        : base(definition, actions)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        _filterWork = new InventoryFilterWork(Dispatcher.CurrentDispatcher);
        KindOptions = Enum.GetValues<MoteUpgradeKind>();
        RankOptions = MotePlanningQueries.Ranks;
        FodderRanks = MotePlanningQueries.FodderRanks;
        InventoryNames = [];
        CopyRanks = [];
        SetUseMotesCommand = new RelayCommand<bool>(value => UseMotes = value);
        SetPreferFragmentsCommand = new RelayCommand<bool>(value => PreferFragments = value);
        SetUseCopiesCommand = new RelayCommand<bool>(value => UseCopies = value);
        SetFeedingCommand = new RelayCommand<bool>(value => Feeding = value);
        SetFeedUsableMotesCommand = new RelayCommand<bool>(value => FeedUsableMotes = value);
        ResetCopyRanksCommand = new RelayCommand(ResetCopyRanks);
    }

    /// <summary>The available equipment and spell planning modes.</summary>
    public IReadOnlyList<MoteUpgradeKind> KindOptions
    {
        get;
    }

    /// <summary>The item ranks selectable as the current rank.</summary>
    public IReadOnlyList<int> RankOptions
    {
        get;
    }

    /// <summary>The rank choices available for additional copies.</summary>
    public IReadOnlyList<int> FodderRanks
    {
        get;
    }

    /// <summary>Inventory names matching the current search.</summary>
    public BulkObservableCollection<string> InventoryNames
    {
        get;
    }

    /// <summary>Hypothetical spare copy ranks for the What if page.</summary>
    public BulkObservableCollection<MotePlanCopyViewModel> CopyRanks
    {
        get;
    }

    /// <summary>Command that updates owned-mote inclusion.</summary>
    public RelayCommand<bool> SetUseMotesCommand
    {
        get;
    }

    /// <summary>Command that updates fragment priority.</summary>
    public RelayCommand<bool> SetPreferFragmentsCommand
    {
        get;
    }

    /// <summary>Command that updates copy inclusion.</summary>
    public RelayCommand<bool> SetUseCopiesCommand
    {
        get;
    }

    /// <summary>Command that updates spare-copy feeding.</summary>
    public RelayCommand<bool> SetFeedingCommand
    {
        get;
    }

    /// <summary>Command that updates whether currently usable resources feed copies.</summary>
    public RelayCommand<bool> SetFeedUsableMotesCommand
    {
        get;
    }

    /// <summary>Command that restores hypothetical copy ranks from the inventory export.</summary>
    public RelayCommand ResetCopyRanksCommand
    {
        get;
    }

    /// <summary>Whether the plan is for equipment rather than a spell.</summary>
    public bool IsEquipment
    {
        get
        {
            return _kind == MoteUpgradeKind.Equipment;
        }
    }

    /// <summary>Selected planning kind.</summary>
    public MoteUpgradeKind Kind
    {
        get
        {
            return _kind;
        }
        set
        {
            if (!SetProperty(ref _kind, value))
            {
                return;
            }
            OnPropertiesChanged(nameof(IsEquipment), nameof(PlanSummaryToolTip));
            if (value == MoteUpgradeKind.Spell && ContainsTarget(Name))
            {
                Name = "";
            }
            RequestNames(false);
            RefreshPlan();
        }
    }

    /// <summary>Manual item or spell name.</summary>
    public string Name
    {
        get
        {
            return _name;
        }
        set
        {
            if (!SetProperty(ref _name, value ?? ""))
            {
                return;
            }
            if (_selectedInventoryName is not null)
            {
                SelectedInventoryName = null;
            }
            RequestNames(true);
            RefreshPlan();
        }
    }

    /// <summary>Selected item name from the separate inventory selector.</summary>
    public string? SelectedInventoryName
    {
        get
        {
            return _selectedInventoryName;
        }
        set
        {
            if (!SetProperty(ref _selectedInventoryName, value))
            {
                return;
            }
            if (value is not null && MotePlanningQueries.BestCopyRank(_inventoryRows, value) is int rank)
            {
                _updatingSelection = true;
                CurrentRank = rank;
                Points = "0";
                _updatingSelection = false;
            }
            RefreshPlan();
        }
    }

    /// <summary>Current rank of the item or spell.</summary>
    public int CurrentRank
    {
        get
        {
            return _startRank;
        }
        set
        {
            int rank = Math.Clamp(value, 0, MotePlanningQueries.MaxRank);
            if (!SetProperty(ref _startRank, rank))
            {
                return;
            }
            UpdateTargetRanks();
            OnPropertyChanged(nameof(CanAddProgress));
            RefreshPlan();
        }
    }

    /// <summary>Selectable target ranks for the current rank.</summary>
    public IReadOnlyList<int> TargetRanks
    {
        get
        {
            return _targetRanks;
        }
        private set
        {
            SetProperty(ref _targetRanks, value);
        }
    }

    /// <summary>Target rank at which planning stops.</summary>
    public int TargetRank
    {
        get
        {
            return _targetRank;
        }
        set
        {
            int rank = Math.Clamp(value, CurrentRank, MotePlanningQueries.MaxRank);
            if (SetProperty(ref _targetRank, rank))
            {
                RefreshPlan();
            }
        }
    }

    /// <summary>Whether progress points can be added at the current rank.</summary>
    public bool CanAddProgress
    {
        get
        {
            return MotePlanningQueries.CanAddProgress(CurrentRank);
        }
    }

    /// <summary>Points already put toward the next rank.</summary>
    public string Points
    {
        get
        {
            return _points;
        }
        set
        {
            if (SetProperty(ref _points, value ?? ""))
            {
                RefreshPlan();
            }
        }
    }

    /// <summary>Whether owned motes and fragments are included.</summary>
    public bool UseMotes
    {
        get
        {
            return _useMotes;
        }
        set
        {
            if (SetProperty(ref _useMotes, value))
            {
                OnPropertyChanged(nameof(CanPreferFragments));
                RefreshPlan();
            }
        }
    }

    /// <summary>Whether fragments are used before normal motes.</summary>
    public bool PreferFragments
    {
        get
        {
            return _preferFragments;
        }
        set
        {
            if (SetProperty(ref _preferFragments, value))
            {
                RefreshPlan();
            }
        }
    }

    /// <summary>Whether spare inventory copies are included.</summary>
    public bool UseCopies
    {
        get
        {
            return _useCopies;
        }
        set
        {
            if (SetProperty(ref _useCopies, value))
            {
                RefreshPlan();
            }
        }
    }

    /// <summary>Whether spare copies may be fed before being merged.</summary>
    public bool Feeding
    {
        get
        {
            return _feeding;
        }
        set
        {
            if (SetProperty(ref _feeding, value))
            {
                OnPropertyChanged(nameof(CanFeedUsableMotes));
                RefreshPlan();
            }
        }
    }

    /// <summary>Whether motes the main item can still use may feed copies.</summary>
    public bool FeedUsableMotes
    {
        get
        {
            return _feedUsableMotes;
        }
        set
        {
            if (SetProperty(ref _feedUsableMotes, value))
            {
                RefreshPlan();
            }
        }
    }

    /// <summary>Whether owned-resource priority can be changed.</summary>
    public bool CanPreferFragments
    {
        get
        {
            return UseMotes;
        }
    }

    /// <summary>Whether the feed-usable-motes option is enabled.</summary>
    public bool CanFeedUsableMotes
    {
        get
        {
            return Feeding;
        }
    }

    /// <summary>Number of additional copies that can be farmed.</summary>
    public string Fodder
    {
        get
        {
            return _fodder;
        }
        set
        {
            if (SetProperty(ref _fodder, value ?? ""))
            {
                RefreshPlan();
            }
        }
    }

    /// <summary>Rank of additional copies that can be farmed.</summary>
    public int FodderRank
    {
        get
        {
            return _fodderRank;
        }
        set
        {
            int rank = Math.Clamp(value, 0, MotePlanningQueries.MaxRank - 1);
            if (SetProperty(ref _fodderRank, rank))
            {
                RefreshPlan();
            }
        }
    }

    /// <summary>Selected planning page.</summary>
    public MotePlanPage Page
    {
        get
        {
            return _page;
        }
        set
        {
            if (SetProperty(ref _page, value))
            {
                RefreshPlan();
            }
        }
    }

    /// <summary>The selected tab index, matching <see cref="MotePlanPage"/>.</summary>
    public int SelectedPageIndex
    {
        get
        {
            return (int)Page;
        }
        set
        {
            int index = Math.Clamp(value, 0, Enum.GetValues<MotePlanPage>().Length - 1);
            Page = (MotePlanPage)index;
        }
    }

    /// <summary>Tooltip describing mote and fragment priority behavior.</summary>
    public string PlanSummaryToolTip
    {
        get
        {
            if (Kind == MoteUpgradeKind.Equipment)
            {
                return "Prefer fragments spends usable fragments before normal motes; off uses the smallest usable tier, normal motes first. " +
                    "Fragments are worth twice their named mote and share its equipment +rank limit. " +
                    "Spare copies are merged after motes and fragments. Condensing and Void-Touched resources are not used.";
            }
            return "Spell motes double in value each tier (1 to 512 points); fragments are worth twice their named mote. " +
                "Both work at any +level. Prefer fragments spends fragments first; off uses the smallest tier, normal motes first. " +
                "Condensing and Void-Touched resources are not used.";
        }
    }

    /// <summary>Whether the plan includes a hypothetical-copy comparison.</summary>
    public bool HasWhatIf
    {
        get
        {
            return Plan?.WhatIf is not null;
        }
    }

    /// <summary>Current calculated plan, or null until the first frame arrives.</summary>
    public MotePlanReadModel? Plan
    {
        get
        {
            return _plan;
        }
        private set
        {
            if (SetProperty(ref _plan, value))
            {
                OnPropertyChanged(nameof(HasWhatIf));
            }
        }
    }

    /// <summary>Input validation and inventory status notes.</summary>
    public string Summary
    {
        get
        {
            return _summary;
        }
        private set
        {
            SetProperty(ref _summary, value);
        }
    }

    /// <summary>The latest inventory item-name filtering task, exposed for tests.</summary>
    public Task PendingTargetFilter
    {
        get
        {
            return _filterWork.Pending;
        }
    }

    /// <inheritdoc />
    protected override void OnFrame(OverlayFrameMessage frame)
    {
        var snapshot = _snapshot();
        string? character = frame.Snapshot.CharacterName;
        bool belongs = snapshot.Export is { } export && character is not null &&
            export.Character.Equals(character, StringComparison.OrdinalIgnoreCase);
        _inventoryRows = belongs ? snapshot.Export!.Rows : [];
        _currentSnapshot = snapshot;
        _character = character;
        string signature = BuildSignature(snapshot, character, _inventoryRows);
        if (signature != _inventorySignature)
        {
            _inventorySignature = signature;
            RequestNames(false);
            ResetCopyRanks();
        }
        Show("Mote upgrade planner", character, []);
        RefreshPlan();
    }

    private void RequestNames(bool debounce)
    {
        var rows = IsEquipment ? _inventoryRows.ToArray() : [];
        string filter = Name.Trim();
        _filterWork.Start(
            token => MotePlanningQueries.TargetNames(rows, filter),
            (names, token) =>
            {
                InventoryNames.ReplaceAll(names);
                if (_selectedInventoryName is not null && !names.Contains(_selectedInventoryName, StringComparer.OrdinalIgnoreCase))
                {
                    SelectedInventoryName = null;
                }
                return Task.CompletedTask;
            },
            error => UsageTelemetry.TrackError(error, UsageErrorSource.InventoryImport),
            debounce);
    }

    private void RefreshPlan()
    {
        if (_currentSnapshot is null || _updatingSelection)
        {
            return;
        }
        UpdateTargetRanks();
        var snapshot = _currentSnapshot;
        var owned = MoteInventoryReadModel.Capture(snapshot.Items, _character, snapshot.Export?.Stamp.Written);
        string name = IsEquipment && SelectedInventoryName is not null ? SelectedInventoryName : Name.Trim();
        var copies = IsEquipment ? MotePlanningQueries.SelectCopies(_inventoryRows, name, CurrentRank).Spares : [];
        var request = new MotePlanningRequest(_kind, CurrentRank, TargetRank, Points, Fodder, FodderRank,
            Feeding, FeedUsableMotes, UseMotes, UseCopies, Page, name,
            snapshot.Export is { } export && _character is not null &&
                export.Character.Equals(_character, StringComparison.OrdinalIgnoreCase),
            _hypotheticalCopies, PreferFragments);
        SynchronizeCopyRanks(name, copies);
        Plan = MotePlanningService.Prepare(request with
        {
            HypotheticalCopies = _hypotheticalCopies
        },
            owned.OwnedByRank, _inventoryRows, owned.OwnedFragmentsByRank);
        Summary = BuildSummary(Plan, Page);
        RefreshDisplayBlocks();
    }

    private void UpdateTargetRanks()
    {
        var ranks = MotePlanningQueries.TargetRanks(CurrentRank, TargetRank);
        TargetRanks = ranks.Ranks;
        if (_targetRank != ranks.Selected)
        {
            _targetRank = ranks.Selected;
            OnPropertyChanged(nameof(TargetRank));
        }
    }

    private void SynchronizeCopyRanks(string name, IReadOnlyList<int> ranks)
    {
        string context = $"{_character}|{name.ToUpperInvariant()}|{CurrentRank}|{_inventorySignature}";
        if (context == _copyContext && ranks.Count == _hypotheticalCopies.Length)
        {
            return;
        }
        _copyContext = context;
        _hypotheticalCopies = ranks.ToArray();
        var copyModels = ranks.Select((rank, index) => new MotePlanCopyViewModel(index, rank,
            updated => UpdateCopyRank(index, updated))).ToArray();
        CopyRanks.ReplaceAll(copyModels);
    }

    private void UpdateCopyRank(int index, int rank)
    {
        if (index >= 0 && index < _hypotheticalCopies.Length)
        {
            _hypotheticalCopies[index] = rank;
            RefreshPlan();
        }
    }

    private void ResetCopyRanks()
    {
        _copyContext = "";
        RefreshPlan();
    }

    private bool ContainsTarget(string name)
    {
        return name.Length > 0 && MotePlanningQueries.ContainsTarget(_inventoryRows, name);
    }

    private static string BuildSignature(InventorySnapshot snapshot, string? character,
        IReadOnlyList<InventoryExportItem> rows)
    {
        string export = snapshot.Export is null ? "none" :
            $"{snapshot.Export.Character}|{snapshot.Export.Stamp.Path}|{snapshot.Export.Stamp.Size}|{snapshot.Export.Stamp.Written.Ticks}";
        var motes = MoteInventoryReadModel.Capture(snapshot.Items, character, snapshot.Export?.Stamp.Written);
        return $"{character}|{snapshot.Revision}|{export}|{rows.Count}|" +
            string.Join(",", motes.OwnedByRank.OrderBy(item => item.Key).Select(item => $"{item.Key}:{item.Value}")) + "|" +
            string.Join(",", motes.OwnedFragmentsByRank.OrderBy(item => item.Key).Select(item => $"{item.Key}:{item.Value}"));
    }

    private static string BuildSummary(MotePlanReadModel? plan, MotePlanPage page)
    {
        if (plan is null)
        {
            return "Waiting for inventory data.";
        }

        var notes = new List<string>();
        if (plan.WhatIf is { } hypothetical)
        {
            notes.Add($"WHAT IF: spare merge value {hypothetical.OriginalMergePoints:N0} to {hypothetical.HypotheticalMergePoints:N0} points before feeding; main item stays +{plan.StartRank}. Inventory is unchanged.");
        }
        if (plan.PointsStatus == MoteInputStatus.Invalid)
        {
            notes.Add($"Points must be a whole number from 0 to {plan.MaximumProgress} at +{plan.StartRank}; using 0.");
        }
        if (plan.FodderStatus == MoteInputStatus.Invalid && page == MotePlanPage.Farm)
        {
            notes.Add($"Extra copies must be a whole number from 0 to {plan.MaximumFodderCopies}; using 0.");
        }
        if (plan.AvailableFragments.Count > 0)
        {
            notes.Add($"Owned fragments available (last inventory export): {DescribeCounts(plan.AvailableFragments)}.");
            notes.Add(plan.PreferFragments
                ? "Usable fragments are spent before normal motes, smallest fragment tier first."
                : "Regular motes are used first within each tier; unused fragments are kept.");
            if (plan.Kind == MoteUpgradeKind.Equipment)
            {
                notes.Add("Fragments have the same equipment rank limit as their named mote.");
            }
            notes.Add("Run /outputfile inventory after looting or spending resources to refresh these counts.");
        }
        if (plan.Kind == MoteUpgradeKind.Equipment)
        {
            var selection = plan.CopySelection;
            if (!plan.HasInventoryExport)
            {
                notes.Add("No inventory export for this character yet, so no copies are counted. Run /outputfile inventory in game.");
            }
            else if (plan.ItemName.Length > 0)
            {
                notes.Add(selection.Spares.Count == 0
                    ? (selection.RemovedTarget ? "No spare copies in inventory." : "No copies of this item in inventory.")
                    : $"{selection.Spares.Count} spare cop{(selection.Spares.Count == 1 ? "y" : "ies")} in inventory: {string.Join(", ", selection.Spares.Select(copy => $"+{copy}"))}" +
                        (selection.RemovedTarget ? $" (one +{plan.StartRank} is the item being upgraded)." : "."));
                if (selection.HigherCopies.Count > 0)
                {
                    int highest = selection.HigherCopies.Max();
                    notes.Add($"You already have a +{highest} copy, so it isn't counted as a spare. Set Current to +{highest} to plan from it.");
                }
            }
            if (plan.FeedingStatus == MoteFeedingStatus.NotBeneficial)
            {
                notes.Add("Feeding copies wouldn't save anything here, so it isn't used.");
            }
        }
        return string.Join(Environment.NewLine, notes);
    }
}

/// <summary>One editable hypothetical copy rank.</summary>
public sealed class MotePlanCopyViewModel : ObservableObject
{
    private readonly Action<int> _changed;
    private int _rank;

    /// <summary>Creates an editable copy rank.</summary>
    public MotePlanCopyViewModel(int index, int rank, Action<int> changed)
    {
        Index = index;
        ExportedRank = rank;
        _rank = rank;
        _changed = changed;
    }

    /// <summary>Zero-based copy index.</summary>
    public int Index
    {
        get;
    }

    /// <summary>The copy rank recorded by the latest inventory export.</summary>
    public int ExportedRank
    {
        get;
    }

    /// <summary>Display label containing the copy index and exported rank.</summary>
    public string Label
    {
        get
        {
            return $"Copy {Index + 1} (export +{ExportedRank})";
        }
    }

    /// <summary>Current simulated copy rank.</summary>
    public int Rank
    {
        get
        {
            return _rank;
        }
        set
        {
            int rank = Math.Clamp(value, 0, MotePlanningQueries.MaxRank);
            if (SetProperty(ref _rank, rank))
            {
                _changed(rank);
            }
        }
    }
}
