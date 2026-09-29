using System.Collections.ObjectModel;

namespace PCBoost.Presentation.Common;

/// <summary>
/// Synchronise une <see cref="ObservableCollection{T}"/> avec une liste cible en déplaçant, insérant et retirant
/// uniquement ce qui change (préserve la sélection et le défilement de la liste affichée).
/// </summary>
public static class CollectionSync
{
    public static void SyncTo<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(desired);

        var desiredSet = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(target[i])) target.RemoveAt(i);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            if (i < target.Count && ReferenceEquals(target[i], item)) continue;

            var existing = IndexOf(target, item, i + 1);
            if (existing >= 0) target.Move(existing, i);
            else target.Insert(i, item);
        }

        while (target.Count > desired.Count) target.RemoveAt(target.Count - 1);
    }

    public static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(items);
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private static int IndexOf<T>(ObservableCollection<T> list, T item, int start)
        where T : class
    {
        for (var i = start; i < list.Count; i++)
            if (ReferenceEquals(list[i], item)) return i;
        return -1;
    }
}
