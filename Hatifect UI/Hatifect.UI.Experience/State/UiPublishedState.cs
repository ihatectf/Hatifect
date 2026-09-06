using Hatifect.UI.Semantics;

namespace Hatifect.UI.Experience;

/// <summary>A typed state value stored in its publication's immutable view.</summary>
public sealed class UiPublishedState<T> : IUiMutableSemanticSource<T>, IUiPublicationParticipant
{
    private readonly UiSourceType<T> _type;
    private Action? _changed;
    internal UiPublishedState(UiPublication publication, UiSymbolId id, T initial, UiSourceType<T> type)
    {
        Publication = publication;
        SourceId = id;
        _type = type ?? throw new ArgumentNullException(nameof(type));
        Publication.Register(this, new UiPublishedValueSnapshot<T>(initial, 0));
    }
    public UiPublication Publication { get; }
    public UiSymbolId SourceId { get; }
    public long Version => Publication.Read(this).Version;
    public T Value
    {
        get => ((IUiSemanticSource<T>)Publication.Read(this).Snapshot).Value;
        set
        {
            UiPublicationResult result = Publication.BeginUpdate().Set(this, value).Commit();
            if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Diagnostics.Select(item => item.Message)));
        }
    }
    public Type ValueType => typeof(T);
    public object? UntypedValue => Value;
    UiDataType IUiPublicationParticipant.DataType => _type.Descriptor;
    public T Read(UiPublicationView view) => ((IUiSemanticSource<T>)view.Read(this)).Value;
    public event Action? Changed
    {
        add { Publication.EnsureOwner(); if (Publication.IsDisposed) throw new ObjectDisposedException(nameof(UiPublication)); _changed += value; }
        remove { Publication.EnsureOwner(); _changed -= value; }
    }
    void IUiPublicationParticipant.Notify(List<Exception> errors) => Publication.Notify(_changed, errors);
    void IUiPublicationParticipant.ClearSubscriptions() => _changed = null;
}

internal sealed class UiPublishedValueSnapshot<T> : IUiSemanticSource<T>, IUiVersionedSemanticSource
{
    internal UiPublishedValueSnapshot(T value, long version) { Value = value; Version = version; }
    public T Value { get; }
    public long Version { get; }
    public Type ValueType => typeof(T);
    public object? UntypedValue => Value;
    public event Action? Changed { add { } remove { } }
}
