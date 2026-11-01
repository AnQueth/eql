using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using EqLog.Application.Inventory;
using EqLog.Core;

namespace EqLog.Overlay.ViewModels;

/// <summary>Detached inventory presentation state. Changing filters/selection does not call any adapter.</summary>
internal sealed class InventoryViewModel : INotifyPropertyChanged
{
    private string _filter = "";
    private QuestInventoryItem? _selected;
    private string _importStatus = "Waiting for inventory export.";
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly InventoryFilterWork _filterWork;
    private HashSet<QuestInventoryItem>? _matches;
    private bool _filtersSuspended;
    private bool _refreshingFilter;

    public event PropertyChangedEventHandler? PropertyChanged;

    public InventoryViewModel()
    {
        _filterWork = new InventoryFilterWork(_dispatcher);
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = item => item is QuestInventoryItem row &&
            (_matches is null || _matches.Contains(row));
    }

    public Task PendingFilter
    {
        get
        {
            return _filterWork.Pending;
        }
    }

    public void SuspendFilters()
    {
        _dispatcher.VerifyAccess();
        _filtersSuspended = true;
        _filterWork.Cancel();
    }

    public void ResumeFilters()
    {
        _dispatcher.VerifyAccess();
        _filtersSuspended = false;
        RequestFilter(false);
    }

    private void RequestFilter(bool debounce)
    {
        _dispatcher.VerifyAccess();
        _filterWork.Cancel();
        if (_filtersSuspended)
        {
            return;
        }
        var rows = Items.ToArray();
        var names = rows.Select(row => row.Item).ToArray();
        string filter = Filter;
        _filterWork.Start(
            cancellation => InventoryFilterWork.MatchNames(names, filter, cancellation),
            (indices, cancellation) =>
            {
                _matches = indices.Select(index => rows[index]).ToHashSet();
                _refreshingFilter = true;
                try
                {
                    ItemsView.Refresh();
                }
                finally
                {
                    _refreshingFilter = false;
                }
                Notify(nameof(SelectedItem));
                return Task.CompletedTask;
            },
            error =>
            {
                ImportStatus = $"Inventory filter failed: {error.Message}";
                UsageTelemetry.TrackError(error, UsageErrorSource.InventoryImport);
            },
            debounce);
    }

    public ObservableCollection<QuestInventoryItem> Items { get; } = [];
    public ICollectionView ItemsView
    {
        get;
    }
    public long Revision { get; private set; } = -1;
    public InventoryReadModel State { get; private set; } = InventoryReadModel.Empty;
    public InventoryExportSnapshot? Export
    {
        get; private set;
    }
    public IReadOnlyList<InventoryItemLocation> Copies { get; private set; } = [];
    public string CopyStatus { get; private set; } = "Select an inventory item to see every exported copy and its location.";

    public string ImportStatus
    {
        get
        {
            return _importStatus;
        }
        set
        {
            _importStatus = value;
            Notify(nameof(ImportStatus));
        }
    }

    public string Filter
    {
        get
        {
            return _filter;
        }
        set
        {
            _dispatcher.VerifyAccess();
            if (_filter == value)
            {
                return;
            }
            _filter = value;
            RequestFilter(true);
            Notify(nameof(Filter));
        }
    }

    public QuestInventoryItem? SelectedItem
    {
        get
        {
            return _selected;
        }
        set
        {
            _dispatcher.VerifyAccess();
            // DataGrid clears its visible selection during Refresh. Keep the detached
            // selection/copy details when only the filter hides that item.
            if (value is null && _selected is not null && Items.Contains(_selected) &&
                (_refreshingFilter || (_matches is not null && !_matches.Contains(_selected))))
            {
                return;
            }
            if (ReferenceEquals(_selected, value))
            {
                return;
            }
            _selected = value;
            Notify(nameof(SelectedItem));
            RefreshCopies();
        }
    }

    public void Apply(InventorySnapshot snapshot, string? character)
    {
        _dispatcher.VerifyAccess();
        _filterWork.Cancel();
        Revision = snapshot.Revision;
        State = snapshot.ReadModel;
        Export = snapshot.Export;
        var rows = snapshot.Items.Where(item => item.Character.Equals(character, StringComparison.OrdinalIgnoreCase)).ToArray();
        for (int index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            if (index >= Items.Count)
            {
                Items.Add(DisplayItem(row));
            }
            else if (Items[index].Character.Equals(row.Character, StringComparison.OrdinalIgnoreCase) &&
                ItemPresentation.Comparer.Equals(Items[index].Item, row.Item))
            {
                var existing = Items[index];
                existing.Item = row.Item;
                existing.Owned = row.Owned;
                existing.Historical = row.Historical;
                existing.Carried = row.Carried;
                existing.Bank = row.Bank;
                existing.Hoard = row.Hoard;
                existing.Depot = row.Depot;
                existing.OtherStorage = row.OtherStorage;
                existing.Collection = row.Collection;
                existing.Installed = row.Installed;
                existing.LastLootUtc = row.LastLootUtc;
            }
            else
            {
                Items[index] = DisplayItem(row);
            }
        }
        while (Items.Count > rows.Length)
        {
            Items.RemoveAt(Items.Count - 1);
        }
        if (SelectedItem is not null && !Items.Contains(SelectedItem))
        {
            SelectedItem = null;
        }
        // Initial/clear-filter source display stays immediate; active filters use a detached query.
        if (string.IsNullOrWhiteSpace(Filter))
        {
            _matches = null;
            ItemsView.Refresh();
        }
        else
        {
            RequestFilter(false);
        }
        RefreshCopies();
    }

    public void RefreshCopies()
    {
        _dispatcher.VerifyAccess();
        Copies = [];
        if (SelectedItem is null)
        {
            CopyStatus = "Select an inventory item to see every exported copy and its location.";
        }
        else if (Export is null || !Export.Character.Equals(SelectedItem.Character, StringComparison.OrdinalIgnoreCase))
        {
            CopyStatus = "Waiting for this character's inventory export. Run /outputfile inventory to locate copies.";
        }
        else
        {
            Copies = InventoryQueries.CopyLocations(Export.Rows, SelectedItem.Item);
            string freshness = $"Last export: {Export.Stamp.Written.ToLocalTime():g}. Counts and locations use this export only. Run /outputfile inventory after looting, merging, or moving items.";
            CopyStatus = Copies.Count == 0
                ? $"No copies of {ItemPresentation.Name(SelectedItem.Item)} in the last export. {freshness}"
                : $"{ItemPresentation.Name(SelectedItem.Item)}: {Copies.Sum(copy => copy.Quantity):N0} copies across {Copies.Count:N0} exported entries, including collections and installed augments. {freshness}";
        }
        Notify(nameof(Copies));
        Notify(nameof(CopyStatus));
    }

    private void Notify(string property)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    private static QuestInventoryItem DisplayItem(InventoryItemSummary row)
    {
        return new()
        {
            Character = row.Character,
            Item = row.Item,
            Owned = row.Owned,
            Historical = row.Historical,
            Carried = row.Carried,
            Bank = row.Bank,
            Hoard = row.Hoard,
            Depot = row.Depot,
            OtherStorage = row.OtherStorage,
            Collection = row.Collection,
            Installed = row.Installed,
            LastLootUtc = row.LastLootUtc,
        };
    }
}
