using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using EqLog.Application.Inventory;
using EqLog.Core;
using EqLog.Overlay.Mvvm;
using EqLog.Overlay.Messaging;

namespace EqLog.Overlay.ViewModels;

/// <summary>Presentation state for the character inventory and its exported copy locations.</summary>
public sealed class InventoryOverlayViewModel : OverlayViewModel, IDisposable
{
    private readonly InventoryViewModel _inventory;
    private readonly RelayCommand _openItemWikiCommand;
    private readonly RelayCommand _lookupQuestReferencesCommand;
    private readonly RelayCommand _openSelectedQuestCommand;
    private readonly RelayCommand _toggleQuestCompletionCommand;
    private readonly RelayCommand _retryQuestLookupsCommand;
    private readonly RelayCommand<QuestInventoryItem> _selectItemCommand;
    private readonly RelayCommand<string> _lookupQuestItemCommand;
    private readonly RelayCommand<string> _openQuestCommand;
    private readonly Func<string, CancellationToken, Task<QuestLookupResult>>? _lookupQuestReferences;
    private readonly Func<string, Task<string>>? _toggleQuestCompletion;
    private readonly Action? _retryQuestLookups;
    private CancellationTokenSource? _questLookupCancellation;
    private long _questLookupVersion;
    private QuestReference? _selectedQuestReference;
    private string _questStatus = "Select an item to see its quest references.";
    private int _selectedDetailsTabIndex;
    private Task _pendingQuestLookup = Task.CompletedTask;
    private Task _pendingQuestCompletion = Task.CompletedTask;

    /// <summary>Creates the inventory overlay around the shared detached inventory presentation model.</summary>
    public InventoryOverlayViewModel(OverlayDefinition definition, IOverlayActions actions, InventoryViewModel inventory,
        Func<string, CancellationToken, Task<QuestLookupResult>>? lookupQuestReferences = null,
        Func<string, Task<string>>? toggleQuestCompletion = null, Action? retryQuestLookups = null)
        : base(definition, actions)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        _inventory = inventory;
        _lookupQuestReferences = lookupQuestReferences;
        _toggleQuestCompletion = toggleQuestCompletion;
        _retryQuestLookups = retryQuestLookups;
        QuestReferences = [];
        _openItemWikiCommand = new RelayCommand(OpenSelectedItemWiki, () => _inventory.SelectedItem is not null);
        _lookupQuestReferencesCommand = new RelayCommand(LookupSelectedQuestReferences, () => _inventory.SelectedItem is not null);
        _openSelectedQuestCommand = new RelayCommand(OpenSelectedQuest, () => SelectedQuestReference is not null);
        _toggleQuestCompletionCommand = new RelayCommand(ToggleQuestCompletion,
            () => SelectedQuestReference is not null && _toggleQuestCompletion is not null);
        _retryQuestLookupsCommand = new RelayCommand(RetryQuestLookups);
        _selectItemCommand = new RelayCommand<QuestInventoryItem>(item => _inventory.SelectedItem = item);
        _lookupQuestItemCommand = new RelayCommand<string>(LookupQuestItem, item => !string.IsNullOrWhiteSpace(item));
        _openQuestCommand = new RelayCommand<string>(Actions.OpenLink, url => !string.IsNullOrWhiteSpace(url));
        _inventory.PropertyChanged += InventoryPropertyChanged;
    }

    private void InventoryPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(InventoryViewModel.SelectedItem))
        {
            _openItemWikiCommand.RaiseCanExecuteChanged();
            _lookupQuestReferencesCommand.RaiseCanExecuteChanged();
            if (_inventory.SelectedItem is { } selected)
            {
                SelectedDetailsTabIndex = 0;
                if (_lookupQuestReferences is null)
                {
                    Actions.LookupQuestReferences(selected.Item);
                }
                else
                {
                    _pendingQuestLookup = LoadQuestReferencesAsync(selected.Item);
                }
            }
            else
            {
                CancelQuestLookup();
                QuestReferences.Clear();
                QuestStatus = "Select an item to see its quest references.";
                SelectedQuestReference = null;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _inventory.PropertyChanged -= InventoryPropertyChanged;
        CancelQuestLookup();
    }

    /// <summary>The inventory filter, rows, selection, and copy details.</summary>
    public InventoryViewModel Inventory
    {
        get
        {
            return _inventory;
        }
    }

    /// <summary>Opens the selected item's wiki page.</summary>
    public RelayCommand OpenSelectedItemWikiCommand
    {
        get
        {
            return _openItemWikiCommand;
        }
    }

    /// <summary>Quest references for the selected inventory item.</summary>
    public ObservableCollection<QuestReference> QuestReferences
    {
        get;
    }

    /// <summary>The latest quest-reference lookup task, exposed for deterministic tests.</summary>
    public Task PendingQuestLookup
    {
        get
        {
            return _pendingQuestLookup;
        }
    }

    /// <summary>The latest quest completion save task, exposed for deterministic tests.</summary>
    public Task PendingQuestCompletion
    {
        get
        {
            return _pendingQuestCompletion;
        }
    }

    /// <summary>Status text for the selected item's quest lookup.</summary>
    public string QuestStatus
    {
        get
        {
            return _questStatus;
        }
        private set
        {
            SetProperty(ref _questStatus, value);
        }
    }

    /// <summary>The selected quest reference.</summary>
    public QuestReference? SelectedQuestReference
    {
        get
        {
            return _selectedQuestReference;
        }
        set
        {
            if (SetProperty(ref _selectedQuestReference, value))
            {
                _openSelectedQuestCommand.RaiseCanExecuteChanged();
                _toggleQuestCompletionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>The selected inventory detail tab: copy locations or quest references.</summary>
    public int SelectedDetailsTabIndex
    {
        get
        {
            return _selectedDetailsTabIndex;
        }
        set
        {
            SetProperty(ref _selectedDetailsTabIndex, value);
        }
    }

    /// <summary>Opens the selected quest reference.</summary>
    public RelayCommand OpenSelectedQuestCommand
    {
        get
        {
            return _openSelectedQuestCommand;
        }
    }

    /// <summary>Toggles the selected quest reference's completion state.</summary>
    public RelayCommand ToggleQuestCompletionCommand
    {
        get
        {
            return _toggleQuestCompletionCommand;
        }
    }

    /// <summary>Retries background quest lookups.</summary>
    public RelayCommand RetryQuestLookupsCommand
    {
        get
        {
            return _retryQuestLookupsCommand;
        }
    }

    /// <summary>Selects an inventory row.</summary>
    public RelayCommand<QuestInventoryItem> SelectItemCommand
    {
        get
        {
            return _selectItemCommand;
        }
    }

    /// <summary>Looks up quest references for an inventory item.</summary>
    public RelayCommand<string> LookupQuestItemCommand
    {
        get
        {
            return _lookupQuestItemCommand;
        }
    }

    /// <summary>Opens a quest reference URL.</summary>
    public RelayCommand<string> OpenQuestCommand
    {
        get
        {
            return _openQuestCommand;
        }
    }

    /// <summary>Looks up quest references for the selected item.</summary>
    public RelayCommand LookupSelectedQuestReferencesCommand
    {
        get
        {
            return _lookupQuestReferencesCommand;
        }
    }

    /// <inheritdoc />
    protected override void OnFrame(OverlayFrameMessage frame)
    {
        Show("Inventory & loot quests", frame.Snapshot.CharacterName, []);
    }

    /// <inheritdoc />
    protected override void OnShowingChanged(bool showing)
    {
        if (showing)
        {
            _inventory.ResumeFilters();
        }
        else
        {
            _inventory.SuspendFilters();
        }
    }

    private void OpenSelectedItemWiki()
    {
        if (_inventory.SelectedItem is { } selected)
        {
            Actions.OpenItemWiki(selected.Item);
        }
    }

    private void LookupSelectedQuestReferences()
    {
        if (_inventory.SelectedItem is { } selected)
        {
            SelectedDetailsTabIndex = 1;
            LookupQuestItem(selected.Item);
        }
    }

    private void LookupQuestItem(string item)
    {
        SelectedDetailsTabIndex = 1;
        if (_lookupQuestReferences is null)
        {
            Actions.LookupQuestReferences(item);
            return;
        }

        var selected = _inventory.Items.FirstOrDefault(row =>
            ItemPresentation.Comparer.Equals(row.Item, item));
        if (selected is not null && !ReferenceEquals(_inventory.SelectedItem, selected))
        {
            _inventory.SelectedItem = selected;
            SelectedDetailsTabIndex = 1;
            return;
        }

        _pendingQuestLookup = LoadQuestReferencesAsync(item);
    }

    private async Task LoadQuestReferencesAsync(string item)
    {
        CancelQuestLookup();
        var cancellation = new CancellationTokenSource();
        _questLookupCancellation = cancellation;
        long version = ++_questLookupVersion;
        QuestReferences.Clear();
        SelectedQuestReference = null;
        QuestStatus = $"Looking up {item} on EQLWiki...";
        try
        {
            QuestLookupResult result = await _lookupQuestReferences!(item, cancellation.Token);
            if (version != _questLookupVersion)
            {
                return;
            }

            foreach (QuestReference quest in result.Quests)
            {
                QuestReferences.Add(quest);
            }
            QuestStatus = !result.Found
                ? $"EQLWiki has no item page named '{result.Item}'. Cached for 24 hours; try searching the wiki directly."
                : result.Quests.Count == 0
                    ? "No linked quest pages found. This does not prove the item has no quest use; wiki coverage may be incomplete."
                    : $"{result.Quests.Count} quest reference(s). Open a page to check requirements versus rewards.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (version == _questLookupVersion)
            {
                QuestStatus = "Wiki lookup cancelled.";
            }
        }
        catch (Exception error)
        {
            if (version == _questLookupVersion)
            {
                QuestStatus = $"Wiki lookup failed: {error.Message}";
            }
            UsageTelemetry.TrackError(error, UsageErrorSource.Wiki);
        }
    }

    private void OpenSelectedQuest()
    {
        if (SelectedQuestReference is { } quest)
        {
            Actions.OpenLink(quest.Url);
        }
    }

    private void ToggleQuestCompletion()
    {
        _pendingQuestCompletion = ToggleQuestCompletionAsync();
    }

    private async Task ToggleQuestCompletionAsync()
    {
        if (SelectedQuestReference is not { } quest || _toggleQuestCompletion is null)
        {
            return;
        }

        try
        {
            QuestStatus = await _toggleQuestCompletion(quest.Title);
        }
        catch (Exception error)
        {
            QuestStatus = $"Could not update quest completion: {error.Message}";
            UsageTelemetry.TrackError(error, UsageErrorSource.InventoryImport);
        }
    }

    private void RetryQuestLookups()
    {
        _retryQuestLookups?.Invoke();
        if (_inventory.SelectedItem is { } selected && _lookupQuestReferences is not null)
        {
            _pendingQuestLookup = LoadQuestReferencesAsync(selected.Item);
        }
    }

    private void CancelQuestLookup()
    {
        _questLookupVersion++;
        _questLookupCancellation?.Cancel();
        _questLookupCancellation?.Dispose();
        _questLookupCancellation = null;
    }
}

/// <summary>Presentation state for exported item copies that can be merged across different ranks.</summary>
public sealed class MergeOverlayViewModel : OverlayViewModel
{
    private readonly Func<InventorySnapshot> _snapshot;
    private readonly Func<string?> _characterName;
    private readonly InventoryFilterWork _filterWork;
    private string _filter = "";
    private string _summary = "Waiting for this character's inventory export. Run /outputfile inventory in game.";
    private long _revision = -1;
    private string? _lastCharacter;
    private InventoryExportSnapshot? _lastExport;
    private bool _suspended;

    /// <summary>Creates the merge overlay with a detached inventory snapshot provider.</summary>
    public MergeOverlayViewModel(OverlayDefinition definition, IOverlayActions actions,
        Func<InventorySnapshot> snapshot, Func<string?> characterName)
        : base(definition, actions)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(characterName);
        _snapshot = snapshot;
        _characterName = characterName;
        _filterWork = new InventoryFilterWork(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        OpenMergeItemWikiCommand = new RelayCommand<string>(item => Actions.OpenItemWiki(item), item => item.Length > 0);
        LookupMergeQuestReferencesCommand = new RelayCommand<string>(item => Actions.LookupQuestReferences(item), item => item.Length > 0);
    }

    /// <summary>The item/location filter, applied after a short debounce.</summary>
    public string Filter
    {
        get
        {
            return _filter;
        }
        set
        {
            if (SetProperty(ref _filter, value ?? ""))
            {
                Refresh(true);
            }
        }
    }

    /// <summary>Summary text for the current export and filter.</summary>
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

    /// <summary>Groups containing items available at multiple upgrade ranks.</summary>
    public BulkObservableCollection<MergeGroupViewModel> Groups { get; } = [];

    /// <summary>Opens an item's EQLWiki page.</summary>
    public RelayCommand<string> OpenMergeItemWikiCommand
    {
        get;
    }

    /// <summary>Looks up quest references for an item.</summary>
    public RelayCommand<string> LookupMergeQuestReferencesCommand
    {
        get;
    }

    /// <summary>The latest filtering task, exposed for deterministic tests.</summary>
    public Task PendingFilter
    {
        get
        {
            return _filterWork.Pending;
        }
    }

    /// <summary>Suspends work while the overlay is not in use.</summary>
    public void SuspendFiltering()
    {
        _filterWork.Cancel();
    }

    /// <summary>Refreshes against the current inventory export. Does nothing while the overlay is hidden.</summary>
    public void Refresh(bool debounce = false)
    {
        if (_suspended)
        {
            return;
        }
        var snapshot = _snapshot();
        string? character = _characterName();
        var export = snapshot.Export;
        _revision = snapshot.Revision;
        _lastCharacter = character;
        _lastExport = export;
        bool current = export is not null && character is not null &&
            export.Character.Equals(character, StringComparison.OrdinalIgnoreCase);
        if (!current || export!.Rows.Count == 0)
        {
            _filterWork.Cancel();
            Groups.Clear();
            Summary = "Waiting for this character's inventory export. Run /outputfile inventory in game.";
            return;
        }

        var rows = export.Rows.ToArray();
        long revision = snapshot.Revision;
        string? sourceCharacter = character;
        _filterWork.Start(
            cancellation => InventoryFilterWork.QueryMerge(rows, Filter, cancellation),
            async (result, cancellation) => await ApplyAsync(result, cancellation, revision, sourceCharacter),
            error =>
            {
                Summary = $"Merge filter failed: {error.Message}";
                UsageTelemetry.TrackError(error, UsageErrorSource.InventoryImport);
            },
            debounce);
    }

    /// <inheritdoc />
    protected override void OnShowingChanged(bool showing)
    {
        _suspended = !showing;
        if (showing)
        {
            Refresh();
        }
        else
        {
            _filterWork.Cancel();
        }
    }

    /// <inheritdoc />
    protected override void OnFrame(OverlayFrameMessage frame)
    {
        string character = frame.Snapshot.CharacterName;
        var snapshot = _snapshot();
        if (snapshot.Revision != _revision || !string.Equals(character, _lastCharacter, StringComparison.Ordinal) ||
            !ReferenceEquals(snapshot.Export, _lastExport))
        {
            _revision = snapshot.Revision;
            _lastCharacter = character;
            _lastExport = snapshot.Export;
            Show("Ready to merge", character, []);
            Refresh();
            return;
        }
        Show("Ready to merge", character, []);
    }

    private async Task ApplyAsync(InventoryMergeResult result, CancellationToken cancellation, long revision, string? character)
    {
        if (revision != _revision || !string.Equals(character, _characterName(), StringComparison.Ordinal))
        {
            return;
        }

        Summary = result.Total == 0
            ? "Nothing to merge: no item has copies at different +N ranks."
            : $"{result.Total:N0} item(s) have more than one copy at different ranks" +
                (result.Shown.Count == result.Total ? "." : $"; {result.Shown.Count:N0} match the filter.") +
                " Plain copies count as +0. Two copies that were never upgraded are not listed, because they look like stackable items." +
                " Collections and installed augments are left out.";

        var groups = new List<MergeGroupViewModel>();
        foreach (var group in result.Shown)
        {
            cancellation.ThrowIfCancellationRequested();
            var tables = new List<OverlayTable>();
            for (int offset = 0; offset < group.Rows.Count; offset += 32)
            {
                cancellation.ThrowIfCancellationRequested();
                var rows = group.Rows.Skip(offset).Take(32).ToArray();
                tables.Add(new OverlayTable(["Rank", "Qty", "Location"], rows));
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                if (revision != _revision || !string.Equals(character, _characterName(), StringComparison.Ordinal))
                {
                    return;
                }
            }
            groups.Add(new MergeGroupViewModel(group.BaseName, group.Total,
                Actions.HasQuestReferences(group.BaseName), tables));
        }
        Groups.ReplaceAll(groups);
    }
}

/// <summary>One mergeable item group and its virtualizable table chunks.</summary>
public sealed class MergeGroupViewModel
{
    /// <summary>Creates a group row.</summary>
    public MergeGroupViewModel(string baseName, int total, bool hasQuestReferences, IEnumerable<OverlayTable> tables)
    {
        BaseName = baseName;
        Total = total;
        HasQuestReferences = hasQuestReferences;
        Tables = new ObservableCollection<OverlayTable>(tables);
    }

    /// <summary>The item name without its +N rank suffix.</summary>
    public string BaseName
    {
        get;
    }

    /// <summary>Total loose copies in the export.</summary>
    public int Total
    {
        get;
    }

    /// <summary>Whether quest references are available for this item.</summary>
    public bool HasQuestReferences
    {
        get;
    }

    /// <summary>Table chunks keeping each generated table bounded.</summary>
    public ObservableCollection<OverlayTable> Tables
    {
        get;
    }
}
