using System.Collections.ObjectModel;

namespace ClashSuki.Utilities;

internal static class CollectionSync
{
    public static void Sync<T>(IList<T> target, IReadOnlyList<T> desired) where T : class
    {
        var desiredSet = desired.ToHashSet();
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            var current = target.IndexOf(item);
            if (current == i)
            {
                continue;
            }

            if (current >= 0)
            {
                if (target is ObservableCollection<T> observable)
                {
                    observable.Move(current, i);
                }
                else
                {
                    target.RemoveAt(current);
                    target.Insert(i, item);
                }
            }
            else
            {
                target.Insert(i, item);
            }
        }
    }
}
