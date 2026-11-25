using System.Collections.Specialized;
using EqLog.Overlay.Mvvm;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class BulkObservableCollectionTests
{
    private sealed record Item(string Value);

    [Fact]
    public void Reconcile_ShiftedRowsAreMovedInsteadOfResetting()
    {
        // A wiki-activity style refresh: changing request rows, then a recent list gaining a newest entry and
        // dropping its oldest one.
        var before = new List<string> { "header" };
        before.AddRange(Enumerable.Range(0, 15).Select(i => $"request {i} 1s"));
        before.AddRange(Enumerable.Range(0, 60).Select(i => $"recent {i}"));
        var after = new List<string> { "header" };
        after.AddRange(Enumerable.Range(0, 15).Select(i => $"request {i} 2s"));
        after.Add("recent new");
        after.AddRange(Enumerable.Range(0, 59).Select(i => $"recent {i}"));

        var collection = new BulkObservableCollection<Item>();
        collection.Reconcile(before, static (item, value) => item.Value == value, static value => new Item(value));
        var kept = collection.Where(item => item.Value.StartsWith("recent", StringComparison.Ordinal)).Take(59).ToList();
        var actions = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, e) => actions.Add(e.Action);

        bool changed = collection.Reconcile(after, static (item, value) => item.Value == value,
            static value => new Item(value));

        Assert.True(changed);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
        Assert.Equal(after, collection.Select(item => item.Value));
        Assert.All(kept, item => Assert.Contains(collection, existing => ReferenceEquals(existing, item)));
    }
}
