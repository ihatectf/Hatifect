namespace Hatifect.UI.Experience;

public sealed partial class UiPublicationBatch
{
    private readonly HashSet<UiSymbolId> _explicitCollectionChanges = new();

    public IUiSemanticCollectionSnapshot Read<T>(UiPublishedCollection<T> source)
        => (IUiSemanticCollectionSnapshot)ReadSource(source);

    /// <summary>Replaces a collection and preserves selection if its stable ID survives.
    /// Select may follow in the same batch to provide the consumer's explicit fallback.</summary>
    public UiPublicationBatch Replace<T>(UiPublishedCollection<T> source, IReadOnlyList<T> values)
    {
        var current = (UiPublishedCollectionSnapshot<T>)ReadSource(source);
        if (!CanPrepare(source)) return this;
        var previous = source.Snapshot(BaseView);
        try
        {
            long version = checked(previous.Version + 1);
            var data = source.Prepare(values, null, previous, version, null);
            UiSymbolId? selection = current.SelectedItemId is { } selected && data.TryGetIndex(selected, out _) ? selected : null;
            var change = new UiCollectionChange<T>(previous.Version, version,
                new[] { new UiCollectionReset<T>(data.Value) }, selection);
            var snapshot = source.Select(data, selection, previous, version, change);
            Stage(source, snapshot, Equivalent(previous, snapshot));
        }
        catch (Exception error) { Reject(source, "UIP010", error.Message); }
        return this;
    }

    public UiPublicationBatch Select<T>(UiPublishedSelectableCollection<T> source, UiSymbolId? selectedItemId)
    {
        var current = (UiPublishedCollectionSnapshot<T>)ReadSource(source);
        if (!CanPrepare(source)) return this;
        var previous = source.Snapshot(BaseView);
        try
        {
            long version = checked(previous.Version + 1);
            bool sameContent = UiSemanticCollectionSnapshot.Equivalent(previous.Items, current.Items);
            IReadOnlyList<UiCollectionOperation<T>> operations = sameContent ? Array.Empty<UiCollectionOperation<T>>()
                : new UiCollectionOperation<T>[] { new UiCollectionReset<T>(current.Value) };
            var change = new UiCollectionChange<T>(previous.Version, version, operations, selectedItemId);
            var snapshot = source.Select(current, selectedItemId, previous, version, change);
            Stage(source, snapshot, Equivalent(previous, snapshot));
        }
        catch (Exception error) { Reject(source, "UIP010", error.Message); }
        return this;
    }

    /// <summary>Applies one explicit versioned change to this source. Do not combine it with Replace/Select
    /// for the same source in this publication batch; put the final selection and all operations in change.
    /// Update-only changes project final values at addressed indices. Use Reset/Replace when a shared
    /// formatting policy changes, so unchanged payloads also receive the new projection.</summary>
    public UiPublicationBatch Apply<T>(UiPublishedCollection<T> source, UiCollectionChange<T> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var previous = (UiPublishedCollectionSnapshot<T>)ReadSource(source);
        if (!AdmitExplicitChange(source)) return this;
        if (change.BaseVersion < 0 || change.Version <= change.BaseVersion)
        { Reject(source, "UIP011", "Collection change versions must be nonnegative and strictly increasing."); return this; }
        if (change.Version <= previous.Version)
        {
            ResolveRetainedChange(source, previous, change);
            return this;
        }
        if (!change.IsReset && (change.BaseVersion != previous.Version || change.Version != previous.Version + 1))
        { Reject(source, "UIP013", "A collection version gap requires a full Reset."); return this; }
        try
        {
            StageExplicitChange(source, previous, change);
        }
        catch (Exception error) { Reject(source, "UIP010", error.Message); }
        return this;
    }

    private bool AdmitExplicitChange(IUiPublishedSource source)
    {
        if (!_changes.ContainsKey(source.SourceId) && _explicitCollectionChanges.Add(source.SourceId)) return true;
        Reject(source, "UIP015", "Use one explicit collection change per source and publication batch.");
        return false;
    }

    private void ResolveRetainedChange<T>(UiPublishedCollection<T> source,
        UiPublishedCollectionSnapshot<T> previous, UiCollectionChange<T> change)
    {
        try
        {
            if (previous.History.Any(applied => applied.Equivalent(change))) return;
            Reject(source, "UIP012", "The collection change is stale or conflicts with retained history; provide a new Reset.");
        }
        catch (Exception error) { Reject(source, "UIP010", error.Message); }
    }

    private void StageExplicitChange<T>(UiPublishedCollection<T> source,
        UiPublishedCollectionSnapshot<T> previous, UiCollectionChange<T> change)
    {
        if (change.Operations.All(operation => operation is UiCollectionUpdate<T>))
        {
            StagePreparedUpdates(UiPublishedCollection<T>.PreparedUpdates.Create(source, previous, change));
            return;
        }

        IReadOnlyList<T> values = change.IsReset
            ? ((UiCollectionReset<T>)change.Operations[0]).Values
            : ApplyStructuralOperations(source, previous, change.Operations);
        var snapshot = source.Prepare(values, change.SelectedItemId, previous, change.Version, change);
        // Even a content-equivalent explicit change advances its source version, so the next delta has a precise base.
        Stage(source, snapshot, false, change.Version);
    }

    private static IReadOnlyList<T> ApplyStructuralOperations<T>(UiPublishedCollection<T> source,
        UiPublishedCollectionSnapshot<T> previous, IReadOnlyList<UiCollectionOperation<T>> operations)
    {
        var candidate = previous.Value.ToList();
        var ids = new HashSet<UiSymbolId>(previous.Items.Select(item => item.Id));
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case UiCollectionInsert<T> insert:
                    if (insert.Index < 0 || insert.Index > candidate.Count) throw new ArgumentException("Insert index is outside the candidate.");
                    var inserted = source.Identify(insert.Value);
                    if (!inserted.IsValid || !ids.Add(inserted)) throw new ArgumentException("Insert requires a unique stable item ID.");
                    candidate.Insert(insert.Index, insert.Value);
                    break;
                case UiCollectionRemove<T> remove:
                    RequireItem(remove.Index, remove.ItemId);
                    candidate.RemoveAt(remove.Index);
                    ids.Remove(remove.ItemId);
                    break;
                case UiCollectionMove<T> move:
                    RequireItem(move.FromIndex, move.ItemId);
                    if (move.ToIndex < 0 || move.ToIndex >= candidate.Count) throw new ArgumentException("Move target index is outside the candidate.");
                    T moved = candidate[move.FromIndex];
                    candidate.RemoveAt(move.FromIndex);
                    candidate.Insert(move.ToIndex, moved);
                    break;
                case UiCollectionUpdate<T> update:
                    RequireItem(update.Index, update.ItemId);
                    if (source.Identify(update.Value) != update.ItemId) throw new ArgumentException("Update must preserve the addressed stable item ID.");
                    candidate[update.Index] = update.Value;
                    break;
                default:
                    throw new ArgumentException("Unsupported collection operation; Reset must be the only operation.");
            }
        }
        return candidate;

        void RequireItem(int index, UiSymbolId id)
        {
            if (!id.IsValid || index < 0 || index >= candidate.Count || source.Identify(candidate[index]) != id)
                throw new ArgumentException($"Collection index {index} does not contain the addressed item '{id}'.");
        }
    }

    private bool CanPrepare(IUiPublishedSource source)
    {
        if (!_explicitCollectionChanges.Contains(source.SourceId)) return true;
        Reject(source, "UIP015", "An explicit collection change already defines the candidate and final selection.");
        return false;
    }

    private static bool Equivalent<T>(UiPublishedCollectionSnapshot<T> left, UiPublishedCollectionSnapshot<T> right)
        => left.SelectedItemId == right.SelectedItemId && UiSemanticCollectionSnapshot.Equivalent(left.Items, right.Items);

}
