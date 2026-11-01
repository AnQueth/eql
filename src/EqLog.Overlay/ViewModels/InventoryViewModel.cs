using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using EqLog.Application.Inventory;
using EqLog.Core;

namespace EqLog.Overlay.ViewModels;

/// <summary>Detached inventory presentation state. Changing filters/selection does not call any adapter.</summary>
internal sealed class InventoryViewModel : INotifyPropertyChanged
{
    private string _filter = "";
    private QuestInventoryItem? _selected;
    private string _importStatus = "Waiting for inventory export.";

    public event PropertyChangedEventHandler? PropertyChanged;

    public InventoryViewModel()
    {
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = item => item is QuestInventoryItem row &&
            OverlayPresenters.MatchesItemName(row.Item, Filter);
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
            if (_filter == value)
            {
                return;
            }
            _filter = value;
            ItemsView.Refresh();
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
        ItemsView.Refresh();
        RefreshCopies();
    }

    public void RefreshCopies()
    {
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
