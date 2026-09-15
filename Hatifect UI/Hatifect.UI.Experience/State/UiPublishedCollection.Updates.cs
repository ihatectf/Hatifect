namespace Hatifect.UI.Experience;

public partial class UiPublishedCollection<T>
{
    /// <summary>A short-lived proof bound to the registered source and its validated base capture.
    /// Only this factory creates the candidate; neither the proof nor its base is retained at commit.</summary>
    internal sealed class PreparedUpdates
    {
        private PreparedUpdates(UiPublishedCollection<T> source, UiPublishedCollectionSnapshot<T> previous,
            UiPublishedCollectionSnapshot<T> snapshot)
        { Source = source; Previous = previous; Snapshot = snapshot; }

        internal UiPublishedCollection<T> Source { get; }
        internal UiPublishedCollectionSnapshot<T> Previous { get; }
        internal UiPublishedCollectionSnapshot<T> Snapshot { get; }

        internal static PreparedUpdates Create(UiPublishedCollection<T> source,
            UiPublishedCollectionSnapshot<T> previous, UiCollectionChange<T> change)
        {
            if (change.BaseVersion != previous.Version || change.Version != checked(previous.Version + 1))
                throw new ArgumentException("Updates must immediately follow their captured source version.", nameof(change));
            if (change.SelectedItemId is { } selection && !previous.TryGetIndex(selection, out _))
                throw new ArgumentException($"Selected collection item '{selection}' is absent from the candidate.", nameof(change));
            if (change.SelectedItemId is not null && source is not IUiSelectableCollectionSource)
                throw new ArgumentException("This collection does not support selection.", nameof(change));

            List<KeyValuePair<int, T>> values = ValidateAndCoalesceUpdates(source, previous, change);
            var items = new List<KeyValuePair<int, UiSemanticCollectionItem>>(values.Count);
            int supportingCount = previous.SupportingItemCount;
            foreach (var update in values)
            {
                var old = previous.GetItem(update.Key);
                var item = UiSemanticCollectionSnapshot.ProjectItem(old.Id, update.Value,
                    source._label, source._supportingText, source._icon, source._tooltip);
                if (!UiSourceTypeValidation.IsValidCollectionItem(item.Value, source._type.Descriptor.ItemType!, typeof(T)))
                    throw new ArgumentException($"Collection item {update.Key} violates its CLR type or required nullability.");
                if (UiSemanticCollectionSnapshot.ItemEquivalent(old, item)) continue;
                if (!string.IsNullOrWhiteSpace(old.SupportingText)) supportingCount--;
                if (!string.IsNullOrWhiteSpace(item.SupportingText)) supportingCount++;
                items.Add(new(update.Key, item with { ItemRevision = change.Version }));
            }

            bool changed = items.Count != 0;
            var snapshot = new UiPublishedCollectionSnapshot<T>(
                changed ? UiChunkedList<T>.Copy(previous.Value).With(values) : previous.Value,
                changed ? UiChunkedList<UiSemanticCollectionItem>.Copy(previous.Items).With(items) : previous.Items,
                previous.Indices, supportingCount, changed ? checked(previous.Revision + 1) : previous.Revision,
                change.SelectedItemId, change.Version, source.History(previous, change));
            return new(source, previous, snapshot);
        }

        private static List<KeyValuePair<int, T>> ValidateAndCoalesceUpdates(
            UiPublishedCollection<T> source,
            UiPublishedCollectionSnapshot<T> previous,
            UiCollectionChange<T> change)
        {
            var final = new Dictionary<int, T>();
            foreach (var operation in change.Operations)
            {
                if (operation is not UiCollectionUpdate<T> update)
                    throw new ArgumentException("Prepared updates cannot change collection structure.", nameof(change));
                if (!update.ItemId.IsValid || update.Index < 0 || update.Index >= previous.Count
                    || previous.GetItem(update.Index).Id != update.ItemId)
                    throw new ArgumentException($"Collection index {update.Index} does not contain the addressed item '{update.ItemId}'.");
                // Validate every sequential operation, even when a later update replaces it again.
                if (source.Identify(update.Value) != update.ItemId)
                    throw new ArgumentException("Update must preserve the addressed stable item ID.");
                final[update.Index] = update.Value;
            }

            List<KeyValuePair<int, T>> values = final.ToList();
            values.Sort(static (left, right) => left.Key.CompareTo(right.Key));
            return values;
        }
    }
}
