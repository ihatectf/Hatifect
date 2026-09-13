using Hatifect.UI.Experience;

namespace Hatifect.Flow.UI.Semantic;

/// <summary>Edits and their dependent validation/projection commit through one owner request.</summary>
internal sealed class FlowTextSource : IUiMutableSemanticSource<string>, IUiPublicationReadableSource,
    IUiVersionedSemanticSource
{
    private readonly Action<UiPublishedState<string>, string> _request;
    internal FlowTextSource(UiPublishedState<string> source, Action<UiPublishedState<string>, string> request)
    { Source = source; _request = request; }
    internal UiPublishedState<string> Source { get; }
    public string Value { get => Source.Value; set => _request(Source, value); }
    public Type ValueType => Source.ValueType;
    public object UntypedValue => Value;
    public UiPublication Publication => Source.Publication;
    public long Version => Source.Version;
    public IUiSemanticSource ReadSnapshot(UiPublicationView view) => view.Read(Source);
    public event Action? Changed { add => Source.Changed += value; remove => Source.Changed -= value; }
}
