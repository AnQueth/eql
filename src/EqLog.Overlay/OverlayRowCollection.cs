using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace EqLog.Overlay;

/// <summary>Large filter changes notify the virtualized view once instead of once per row.</summary>
internal sealed class OverlayRowCollection : ObservableCollection<OverlayRow>
{
    internal void ReplaceAll(IReadOnlyList<OverlayRow> rows)
    {
        Items.Clear();
        foreach (var row in rows)
        {
            Items.Add(row);
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
