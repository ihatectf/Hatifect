using Hatifect.UI;
using Hatifect.UI.Experience;

namespace Hatifect.Flow.UI.Semantic;

/// <summary>Selection intent reaches the projection owner before committed state changes.</summary>
internal sealed class FlowSelectionSource<T> : IUiSemanticSource<IReadOnlyList<T>>,
    IUiSelectableCollectionSource, IUiSemanticCollectionMetadata, IUiSemanticCollectionSnapshotSource,
    IUiPublicationReadableSource, IUiVersionedSemanticSource
{
    private readonly Func<UiSymbolId, bool> _request;
    internal FlowSelectionSource(UiPublishedSelectableCollection<T> source, Func<UiSymbolId, bool> request)
    { Source = source; _request = request; }
    internal UiPublishedSelectableCollection<T> Source { get; }
    public UiPublication Publication => Source.Publication;
    public long Version => Source.Version;
    public long Revision => Source.Revision;
    public IReadOnlyList<T> Value => Source.Value;
    public Type ValueType => Source.ValueType;
    public object UntypedValue => Value;
    public int Count => Source.Count;
    public UiSymbolId? SelectedItemId => Source.SelectedItemId;
    public bool HasSupportingText => Source.HasSupportingText;
    public UiSemanticCollectionItem GetItem(int index) => Source.GetItem(index);
    public bool TryGetIndex(UiSymbolId item, out int index) => Source.TryGetIndex(item, out index);
    public bool TrySelect(UiSymbolId item) => _request(item);
    public IUiSemanticCollectionSnapshot CaptureSnapshot() => Source.CaptureSnapshot();
    public IUiSemanticSource ReadSnapshot(UiPublicationView view) => Source.Read(view);
    public event Action? Changed { add => Source.Changed += value; remove => Source.Changed -= value; }
}
