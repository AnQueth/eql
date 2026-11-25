using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace EqLog.Overlay.Mvvm;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can be reconciled against a new projection. Unchanged leading and
/// trailing items keep their instances (so containers, selection and expansion survive); large rewrites raise a
/// single reset instead of one notification per item.
/// </summary>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>More changed items than this produce one reset notification.</summary>
    public const int ResetThreshold = 64;

    /// <summary>Bounds the search for moved items so huge rewrites stay linear.</summary>
    private const long MaxMatchComparisons = 65_536;

    /// <summary>Replaces every item and raises one reset notification.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        var copy = items.ToList();
        Items.Clear();
        foreach (var item in copy)
        {
            Items.Add(item);
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    /// Makes this collection match <paramref name="source"/>. Items for which <paramref name="matches"/> is true are
    /// kept; others are created with <paramref name="create"/>.
    /// </summary>
    /// <returns><see langword="true"/> when the collection changed.</returns>
    public bool Reconcile<TSource>(IReadOnlyList<TSource> source, Func<T, TSource, bool> matches, Func<TSource, T> create)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(create);
        int prefix = 0;
        while (prefix < Count && prefix < source.Count && matches(this[prefix], source[prefix]))
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < Count - prefix && suffix < source.Count - prefix &&
            matches(this[Count - 1 - suffix], source[source.Count - 1 - suffix]))
        {
            suffix++;
        }

        int remove = Count - prefix - suffix;
        int insert = source.Count - prefix - suffix;
        if (remove == 0 && insert == 0)
        {
            return false;
        }

        // Rows that only shifted (a new entry at the top of a "recent" list, one dropped at the bottom) are moved
        // rather than recreated, so a busy list doesn't reset and rebuild every container on each refresh.
        int reused = 0;
        if ((long)remove * insert <= MaxMatchComparisons)
        {
            reused = CountReusable(source, prefix, remove, insert, matches);
        }

        if (Math.Max(remove, insert) - reused > ResetThreshold)
        {
            var merged = new List<T>(source.Count);
            for (int i = 0; i < prefix; i++)
            {
                merged.Add(this[i]);
            }
            for (int i = prefix; i < source.Count - suffix; i++)
            {
                merged.Add(create(source[i]));
            }
            for (int i = Count - suffix; i < Count; i++)
            {
                merged.Add(this[i]);
            }
            ReplaceAll(merged);
            return true;
        }

        if (reused > 0)
        {
            ReconcileMiddle(source, prefix, suffix, matches, create);
            return true;
        }

        int replace = Math.Min(remove, insert);
        for (int i = 0; i < replace; i++)
        {
            this[prefix + i] = create(source[prefix + i]);
        }
        for (int i = replace; i < remove; i++)
        {
            RemoveAt(prefix + replace);
        }
        for (int i = replace; i < insert; i++)
        {
            Insert(prefix + i, create(source[prefix + i]));
        }
        return true;
    }

    private int CountReusable<TSource>(IReadOnlyList<TSource> source, int prefix, int remove, int insert,
        Func<T, TSource, bool> matches)
    {
        var used = new bool[remove];
        int reused = 0;
        for (int i = 0; i < insert; i++)
        {
            for (int j = 0; j < remove; j++)
            {
                if (!used[j] && matches(this[prefix + j], source[prefix + i]))
                {
                    used[j] = true;
                    reused++;
                    break;
                }
            }
        }
        return reused;
    }

    private void ReconcileMiddle<TSource>(IReadOnlyList<TSource> source, int prefix, int suffix,
        Func<T, TSource, bool> matches, Func<TSource, T> create)
    {
        int end = source.Count - suffix;
        for (int i = prefix; i < end; i++)
        {
            int oldEnd = Count - suffix;
            if (i < oldEnd && matches(this[i], source[i]))
            {
                continue;
            }

            int found = -1;
            for (int j = i + 1; j < oldEnd; j++)
            {
                if (matches(this[j], source[i]))
                {
                    found = j;
                    break;
                }
            }

            if (found >= 0)
            {
                Move(found, i);
            }
            else
            {
                Insert(i, create(source[i]));
            }
        }

        while (Count > source.Count)
        {
            RemoveAt(end);
        }
    }
}
