namespace Hatifect.UI.Experience;

internal enum UiCollectionIndexChangeKind { Insert, Remove, Move, Update }

// Indices address the candidate after preceding operations, just like the typed public batch.
internal readonly record struct UiCollectionIndexChange(
    UiCollectionIndexChangeKind Kind, int Index, int DestinationIndex = -1);

/// <summary>Reads index changes from this immutable capture, never from its live owner.</summary>
internal interface IUiCollectionIndexChangeSnapshot : IUiVersionedSemanticSource
{
    // False means the complete transition cannot be proven. No callback is made in that case.
    bool TryVisitIndexChanges(long afterVersion, Action<UiCollectionIndexChange> visit);
}

internal static class UiCollectionIndexChanges
{
    internal static bool TryVisit<T>(long version, IReadOnlyList<UiCollectionChange<T>> history,
        long afterVersion, Action<UiCollectionIndexChange> visit)
    {
        ArgumentNullException.ThrowIfNull(visit);
        if (afterVersion < 0 || afterVersion > version) return false;
        if (afterVersion == version) return true;
        int start = -1;
        for (int index = 0; index < history.Count; index++)
            if (history[index].BaseVersion == afterVersion) { start = index; break; }
        if (start < 0) return false;

        // Preflight the whole retained chain before exposing even its first operation.
        long expected = afterVersion;
        for (int index = start; index < history.Count; index++)
        {
            UiCollectionChange<T> change = history[index];
            if (change.BaseVersion != expected || change.Version <= expected) return false;
            foreach (UiCollectionOperation<T> operation in change.Operations)
                if (!Project(operation, out _)) return false;
            expected = change.Version;
        }
        if (expected != version) return false;
        for (int index = start; index < history.Count; index++)
            foreach (UiCollectionOperation<T> operation in history[index].Operations)
            {
                Project(operation, out UiCollectionIndexChange projected);
                visit(projected);
            }
        return true;
    }

    private static bool Project<T>(UiCollectionOperation<T> operation, out UiCollectionIndexChange change)
    {
        change = operation switch
        {
            UiCollectionInsert<T> insert => new(UiCollectionIndexChangeKind.Insert, insert.Index),
            UiCollectionRemove<T> remove => new(UiCollectionIndexChangeKind.Remove, remove.Index),
            UiCollectionMove<T> move => new(UiCollectionIndexChangeKind.Move, move.FromIndex, move.ToIndex),
            UiCollectionUpdate<T> update => new(UiCollectionIndexChangeKind.Update, update.Index),
            _ => default
        };
        return operation is UiCollectionInsert<T> or UiCollectionRemove<T> or UiCollectionMove<T> or UiCollectionUpdate<T>;
    }
}
