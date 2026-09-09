using Hatifect.UI.Semantics;

namespace Hatifect.UI.Experience;

/// <summary>A collection stored in an atomic publication. Capture and lookup reuse committed immutable storage.</summary>
public partial class UiPublishedCollection<T> : IUiSemanticSource<IReadOnlyList<T>>, IUiSemanticCollectionSnapshotSource,
    IUiSemanticCollectionMetadata, IUiPublicationParticipant
{
    private readonly UiSourceType<IReadOnlyList<T>> _type;
    private readonly Func<T, UiSymbolId> _identify;
    private readonly Func<T, string>? _label;
    private readonly Func<T, string?>? _supportingText;
    private readonly Func<T, UiSymbolId?>? _icon;
    private Action? _changed;

    internal UiPublishedCollection(UiPublication publication, UiSymbolId id, IReadOnlyList<T> values,
        UiSourceType<T> itemType, Func<T, UiSymbolId> identify, Func<T, string>? label,
        Func<T, string?>? supportingText, Func<T, UiSymbolId?>? icon, UiSymbolId? selected, int historyCapacity)
    {
        publication.EnsureCanRegister(id);
        ArgumentNullException.ThrowIfNull(itemType);
        if (historyCapacity is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(historyCapacity), "History capacity must be between 1 and 64 batches.");
        Publication = publication;
        SourceId = id;
        _type = UiSourceTypes.Collection(itemType);
        _identify = identify ?? throw new ArgumentNullException(nameof(identify));
        _label = label;
        _supportingText = supportingText;
        _icon = icon;
        HistoryCapacity = historyCapacity;
        Publication.Register(this, Prepare(values, selected, null, 0, null));
    }

    public UiPublication Publication { get; }
    public UiSymbolId SourceId { get; }
    public int HistoryCapacity { get; }
    public long Version => Current.Version;
    public long Revision => Current.Revision;
    public IReadOnlyList<T> Value => Current.Value;
    public Type ValueType => typeof(IReadOnlyList<T>);
    public object UntypedValue => Value;
    public int Count => Current.Count;
    public bool HasSupportingText => Current.HasSupportingText;
    public UiSemanticCollectionItem GetItem(int index) => Current.GetItem(index);
    public bool TryGetIndex(UiSymbolId item, out int index) => Current.TryGetIndex(item, out index);
    public IUiSemanticCollectionSnapshot CaptureSnapshot() => Current;
    public IUiSemanticCollectionSnapshot Read(UiPublicationView view) => Snapshot(view);
    public UiCollectionChanges<T> ReadChanges(long afterVersion)
    {
        var current = Current;
        if (afterVersion < 0 || afterVersion > current.Version) throw new ArgumentOutOfRangeException(nameof(afterVersion));
        if (afterVersion == current.Version) return new(current.Version, Array.Empty<UiCollectionChange<T>>());
        int start = -1;
        for (int index = 0; index < current.History.Count; index++)
            if (current.History[index].BaseVersion == afterVersion) { start = index; break; }
        if (start >= 0) return new(current.Version, current.History.Skip(start));
        return new(current.Version, new[] { new UiCollectionChange<T>(afterVersion, current.Version,
            new[] { new UiCollectionReset<T>(current.Value) }, current.SelectedItemId) });
    }

    public void Replace(IReadOnlyList<T> values)
        => Require(Publication.BeginUpdate().Replace(this, values).Commit());

    public event Action? Changed
    {
        add { Publication.EnsureOwner(); if (Publication.IsDisposed) throw new ObjectDisposedException(nameof(UiPublication)); _changed += value; }
        remove { Publication.EnsureOwner(); _changed -= value; }
    }
    UiDataType IUiPublicationParticipant.DataType => _type.Descriptor;
    void IUiPublicationParticipant.Notify(List<Exception> errors) => Publication.Notify(_changed, errors);
    void IUiPublicationParticipant.ClearSubscriptions() => _changed = null;
    internal UiPublishedCollectionSnapshot<T> Current => (UiPublishedCollectionSnapshot<T>)Publication.Read(this).Snapshot;
    internal UiPublishedCollectionSnapshot<T> Snapshot(UiPublicationView view) => (UiPublishedCollectionSnapshot<T>)view.Read(this);
    internal UiSymbolId Identify(T value)
    {
        if (value is null && !_type.Descriptor.ItemType!.Nullable)
            throw new ArgumentException("A required collection item contains null.", nameof(value));
        return _identify(value);
    }

    internal UiPublishedCollectionSnapshot<T> Prepare(IReadOnlyList<T> values, UiSymbolId? selected,
        UiPublishedCollectionSnapshot<T>? previous, long version, UiCollectionChange<T>? change)
    {
        var data = UiSemanticCollectionSnapshot.Create(values, Identify, _label, _supportingText, _icon);
        var indices = UiSemanticCollectionSnapshot.Indices(data.Items);
        if (selected is { } selection && !indices.ContainsKey(selection))
            throw new ArgumentException($"Selected collection item '{selection}' is absent from the candidate.", nameof(selected));
        if (selected is not null && this is not IUiSelectableCollectionSource)
            throw new ArgumentException("This collection does not support selection.", nameof(selected));
        bool sameContent = previous is not null && UiSemanticCollectionSnapshot.Equivalent(previous.Items, data.Items);
        long revision = previous is null ? 0 : sameContent ? previous.Revision : checked(previous.Revision + 1);
        IReadOnlyList<UiSemanticCollectionItem> items;
        int supportingCount = 0;
        if (sameContent) { items = previous!.Items; supportingCount = previous.SupportingItemCount; }
        else
        {
            var revised = new UiSemanticCollectionItem[data.Items.Count];
            for (int index = 0; index < revised.Length; index++)
            {
                var item = data.Items[index];
                UiSemanticCollectionItem? old = previous is not null && previous.TryGetIndex(item.Id, out int oldIndex)
                    ? previous.GetItem(oldIndex) : null;
                revised[index] = old is not null && UiSemanticCollectionSnapshot.ItemEquivalent(old, item)
                    ? old : item with { ItemRevision = version };
                if (!string.IsNullOrWhiteSpace(revised[index].SupportingText)) supportingCount++;
            }
            items = Array.AsReadOnly(revised);
        }
        return new(sameContent ? previous!.Value : data.Values, items, sameContent ? previous!.Indices : indices,
            supportingCount,
            revision, selected, version, History(previous, change));
    }

    internal UiPublishedCollectionSnapshot<T> Select(UiPublishedCollectionSnapshot<T> data, UiSymbolId? selected,
        UiPublishedCollectionSnapshot<T> previous, long version, UiCollectionChange<T> change)
    {
        if (selected is { } selection && !data.TryGetIndex(selection, out _))
            throw new ArgumentException($"Selected collection item '{selection}' is absent from the candidate.", nameof(selected));
        return new(data.Value, data.Items, data.Indices, data.SupportingItemCount, data.Revision, selected,
            version, History(previous, change));
    }

    private IReadOnlyList<UiCollectionChange<T>> History(UiPublishedCollectionSnapshot<T>? previous, UiCollectionChange<T>? change)
    {
        if (change is null) return Array.Empty<UiCollectionChange<T>>();
        if (previous is null || change.IsReset) return Array.AsReadOnly(new[] { change });
        int count = Math.Min(previous.History.Count, HistoryCapacity - 1);
        var history = new UiCollectionChange<T>[count + 1];
        for (int index = 0; index < count; index++) history[index] = previous.History[previous.History.Count - count + index];
        history[count] = change;
        return Array.AsReadOnly(history);
    }

    internal static void Require(UiPublicationResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Diagnostics.Select(item => item.Message)));
    }
}

public sealed class UiPublishedSelectableCollection<T> : UiPublishedCollection<T>, IUiSelectableCollectionSource
{
    internal UiPublishedSelectableCollection(UiPublication publication, UiSymbolId id, IReadOnlyList<T> values,
        UiSourceType<T> itemType, Func<T, UiSymbolId> identify, Func<T, string>? label,
        Func<T, string?>? supportingText, Func<T, UiSymbolId?>? icon, UiSymbolId? selected, int historyCapacity)
        : base(publication, id, values, itemType, identify, label, supportingText, icon, selected, historyCapacity) { }

    public UiSymbolId? SelectedItemId => Current.SelectedItemId;
    public bool TrySelect(UiSymbolId item)
    {
        if (!item.IsValid) throw new ArgumentException("A stable semantic item ID is required.", nameof(item));
        if (!TryGetIndex(item, out _)) return false;
        return Publication.BeginUpdate().Select(this, item).Commit().Succeeded;
    }
    public bool ClearSelection()
    {
        if (SelectedItemId is null) return false;
        return Publication.BeginUpdate().Select(this, null).Commit().Succeeded;
    }
}

internal sealed class UiPublishedCollectionSnapshot<T> : UiCollectionReadSnapshot<T>, IUiCollectionIndexChangeSnapshot
{
    internal UiPublishedCollectionSnapshot(IReadOnlyList<T> values, IReadOnlyList<UiSemanticCollectionItem> items,
        IReadOnlyDictionary<UiSymbolId, int> indices, int supportingCount, long revision, UiSymbolId? selected,
        long version, IReadOnlyList<UiCollectionChange<T>> history)
        : base(UiChunkedList<T>.Copy(values), UiChunkedList<UiSemanticCollectionItem>.Copy(items), indices,
            supportingCount > 0, revision, selected, version)
    { History = history; SupportingItemCount = supportingCount; }
    internal int SupportingItemCount { get; }
    internal IReadOnlyList<UiCollectionChange<T>> History { get; }
    public bool TryVisitIndexChanges(long afterVersion, Action<UiCollectionIndexChange> visit)
        => UiCollectionIndexChanges.TryVisit(Version, History, afterVersion, visit);
}
