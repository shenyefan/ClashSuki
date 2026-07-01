using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ClashSuki.Utilities;

public sealed class BoundedObservableCollection<T> : ObservableCollection<T>
{
    public BoundedObservableCollection(int capacity)
    {
        Capacity = capacity;
    }

    public int Capacity { get; }

    public void AddNewest(T item)
    {
        Add(item);
        while (Count > Capacity)
        {
            RemoveAt(0);
        }
    }

    public void InsertNewestFirst(T item)
    {
        Insert(0, item);
        while (Count > Capacity)
        {
            RemoveAt(Count - 1);
        }
    }

    public void AddRangeNewest(IReadOnlyList<T> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        while (Count + items.Count > Capacity && Count > 0)
        {
            RemoveAt(0);
        }

        var startIndex = Count;
        foreach (var item in items.TakeLast(Capacity))
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add,
            items.TakeLast(Capacity).ToList(),
            startIndex));
    }
}
