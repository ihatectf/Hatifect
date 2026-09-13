using Hatifect.UI;
using Hatifect.UI.Experience;

namespace Hatifect.ChestsAnywhereOverlay.UI.Semantic;

/// <summary>Routes selection intent to the session before any committed UI value changes.</summary>
internal sealed class ChestsAnywhereSelectionSource<T> : IUiSemanticSource<IReadOnlyList<T>>,
    IUiSelectableCollectionSource, IUiSemanticCollectionMetadata, IUiSemanticCollectionSnapshotSource,
    IUiPublicationReadableSource, IUiVersionedSemanticSource
{
    private readonly UiPublishedSelectableCollection<T> _source;
    private readonly Func<UiSymbolId, bool> _request;
    internal ChestsAnywhereSelectionSource(UiPublishedSelectableCollection<T> source, Func<UiSymbolId, bool> request)
    { _source = source; _request = request; }
    public UiPublication Publication => _source.Publication;
    public long Version => _source.Version;
    public long Revision => _source.Revision;
    public IReadOnlyList<T> Value => _source.Value;
    public Type ValueType => _source.ValueType;
    public object UntypedValue => Value;
    public int Count => _source.Count;
    public UiSymbolId? SelectedItemId => _source.SelectedItemId;
    public bool HasSupportingText => _source.HasSupportingText;
    public UiSemanticCollectionItem GetItem(int index) => _source.GetItem(index);
    public bool TryGetIndex(UiSymbolId item, out int index) => _source.TryGetIndex(item, out index);
    public bool TrySelect(UiSymbolId item) => _request(item);
    public IUiSemanticCollectionSnapshot CaptureSnapshot() => _source.CaptureSnapshot();
    public IUiSemanticSource ReadSnapshot(UiPublicationView view) => _source.Read(view);
    public event Action? Changed { add => _source.Changed += value; remove => _source.Changed -= value; }
}
