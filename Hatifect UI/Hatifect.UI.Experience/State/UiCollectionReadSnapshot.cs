namespace Hatifect.UI.Experience;

/// <summary>An immutable collection view, including its selection and content revision.
/// Values must be immutable application payloads. Reading a view never executes selection commands.</summary>
public interface IUiSemanticCollectionSnapshot : IUiSemanticCollectionSource, IUiSemanticCollectionMetadata, IUiVersionedSemanticSource
{
    UiSymbolId? SelectedItemId { get; }
}

/// <summary>Optional constant-time capture capability. The returned view remains readable after
/// subsequent source changes. Repeated capture of an unchanged source should reuse the same view.</summary>
public interface IUiSemanticCollectionSnapshotSource : IUiSemanticCollectionSource
{
    IUiSemanticCollectionSnapshot CaptureSnapshot();
}

internal class UiCollectionReadSnapshot<T> : IUiSemanticCollectionSnapshot, IUiSemanticSource<IReadOnlyList<T>>, IUiVersionedSemanticSource
{
    private readonly IReadOnlyList<UiSemanticCollectionItem> _items;
    private readonly IReadOnlyDictionary<UiSymbolId, int> _indices;
    internal IReadOnlyList<UiSemanticCollectionItem> Items => _items;
    internal IReadOnlyDictionary<UiSymbolId, int> Indices => _indices;

    // All collections supplied here are framework-owned immutable snapshots; no per-capture copying.
    internal UiCollectionReadSnapshot(IReadOnlyList<T> values, IReadOnlyList<UiSemanticCollectionItem> items,
        IReadOnlyDictionary<UiSymbolId, int> indices, bool hasSupportingText, long revision, UiSymbolId? selected = null, long? version = null)
    {
        Value = values;
        _items = items;
        _indices = indices;
        HasSupportingText = hasSupportingText;
        Revision = revision;
        Version = version ?? revision;
        SelectedItemId = selected;
    }

    public IReadOnlyList<T> Value { get; }
    public Type ValueType => typeof(IReadOnlyList<T>);
    public object UntypedValue => Value;
    public int Count => _items.Count;
    public long Revision { get; }
    public long Version { get; }
    public bool HasSupportingText { get; }
    public UiSymbolId? SelectedItemId { get; }
    public UiSemanticCollectionItem GetItem(int index) => _items[index];
    public bool TryGetIndex(UiSymbolId item, out int index) => _indices.TryGetValue(item, out index);
    public event Action? Changed { add { } remove { } }
}
