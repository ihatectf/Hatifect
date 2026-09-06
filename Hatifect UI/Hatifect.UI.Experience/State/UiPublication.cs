using Hatifect.UI.Semantics;

namespace Hatifect.UI.Experience;

public interface IUiVersionedSemanticSource : IUiSemanticSource
{
    long Version { get; }
}

/// <summary>Read capability for publication sources and consumer request facades. Null Publication
/// means a legacy source without atomic ownership. ReadSnapshot must use only the supplied view.</summary>
public interface IUiPublicationReadableSource : IUiSemanticSource
{
    UiPublication? Publication { get; }
    IUiSemanticSource ReadSnapshot(UiPublicationView view);
}

/// <summary>A registered source whose committed value belongs to one immutable publication view.</summary>
public interface IUiPublishedSource : IUiVersionedSemanticSource, IUiPublicationReadableSource
{
    new UiPublication Publication { get; }
    UiSymbolId SourceId { get; }
    IUiSemanticSource IUiPublicationReadableSource.ReadSnapshot(UiPublicationView view) => view.Read(this);
}

public enum UiPublicationStatus { Committed, Unchanged, Invalid, Conflict, Reentrant, WrongThread, Disposed }

public sealed record UiPublicationDiagnostic(string Code, string Message, UiSymbolId Source);

public sealed class UiPublicationResult
{
    internal UiPublicationResult(UiPublicationStatus status, Guid publicationId, long version,
        IEnumerable<UiPublicationDiagnostic>? diagnostics = null, IEnumerable<Exception>? observerErrors = null)
    {
        Status = status;
        PublicationId = publicationId;
        Version = version;
        Diagnostics = Array.AsReadOnly((diagnostics ?? Array.Empty<UiPublicationDiagnostic>()).ToArray());
        ObserverErrors = Array.AsReadOnly((observerErrors ?? Array.Empty<Exception>()).ToArray());
    }
    public UiPublicationStatus Status { get; }
    public Guid PublicationId { get; }
    public long Version { get; }
    public bool Succeeded => Status is UiPublicationStatus.Committed or UiPublicationStatus.Unchanged;
    public IReadOnlyList<UiPublicationDiagnostic> Diagnostics { get; }
    public IReadOnlyList<Exception> ObserverErrors { get; }
}

internal sealed record UiPublicationEntry(IUiPublishedSource Source, IUiSemanticSource Snapshot, long Version);

/// <summary>Retains one complete revision. Captured views remain readable after later commits or disposal.</summary>
public sealed class UiPublicationView
{
    private readonly UiPublication _owner;
    private readonly IReadOnlyDictionary<UiSymbolId, UiPublicationEntry> _entries;
    internal UiPublicationView(UiPublication owner, long version, Dictionary<UiSymbolId, UiPublicationEntry> entries)
    { _owner = owner; Version = version; _entries = entries; }
    public Guid PublicationId => _owner.Id;
    public long Version { get; }
    public IUiSemanticSource Read(IUiPublishedSource source) => Entry(source).Snapshot;
    public long SourceVersion(IUiPublishedSource source) => Entry(source).Version;
    internal UiPublicationEntry Entry(IUiPublishedSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!ReferenceEquals(source.Publication, _owner) || !_entries.TryGetValue(source.SourceId, out var entry)
            || !ReferenceEquals(entry.Source, source))
            throw new ArgumentException("The source does not belong to this captured publication view.", nameof(source));
        return entry;
    }
    internal Dictionary<UiSymbolId, UiPublicationEntry> CopyEntries() => new(_entries);
}

internal interface IUiPublicationParticipant : IUiPublishedSource
{
    UiDataType DataType { get; }
    void Notify(List<Exception> errors);
    void ClearSubscriptions();
}

/// <summary>Owns atomic UI state publication on its creating thread. Prepare a batch, then exchange
/// one immutable view before notifying any observer. This does not execute semantic graph relations.</summary>
public sealed class UiPublication : IDisposable
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<UiSymbolId, IUiPublicationParticipant> _sources = new();
    private Dictionary<UiSymbolId, Type> _nominalTypes = new();
    private UiPublicationView _view;
    private bool _sealed;
    private bool _notifying;
    private bool _disposed;
    private Action? _changed;

    public UiPublication(UiSymbolId ownerId)
    {
        if (!ownerId.IsValid) throw new ArgumentException("A stable publication owner ID is required.", nameof(ownerId));
        OwnerId = ownerId;
        Id = Guid.NewGuid();
        _view = new(this, 0, new());
        LastResult = new(UiPublicationStatus.Unchanged, Id, 0);
    }

    public UiSymbolId OwnerId { get; }
    public Guid Id { get; }
    public long Version => _view.Version;
    public bool IsDisposed => _disposed;
    public bool IsPublishing => _notifying;
    public UiPublicationResult LastResult { get; private set; }
    public event Action? Changed
    {
        add { EnsureOwner(); if (_disposed) throw new ObjectDisposedException(nameof(UiPublication)); _changed += value; }
        remove { EnsureOwner(); _changed -= value; }
    }

    public UiPublishedState<T> State<T>(UiSymbolId id, T initial, UiSourceType<T> type)
        => new(this, id, initial, type);

    public UiPublishedCollection<T> Collection<T>(UiSymbolId id, IReadOnlyList<T> values, UiSourceType<T> itemType,
        Func<T, UiSymbolId> identify, Func<T, string>? label = null, Func<T, string?>? supportingText = null,
        Func<T, UiSymbolId?>? icon = null, int historyCapacity = 64)
        => new(this, id, values, itemType, identify, label, supportingText, icon, null, historyCapacity);

    public UiPublishedSelectableCollection<T> SelectableCollection<T>(UiSymbolId id, IReadOnlyList<T> values,
        UiSourceType<T> itemType, Func<T, UiSymbolId> identify, Func<T, string>? label = null,
        UiSymbolId? selectedItemId = null, Func<T, string?>? supportingText = null,
        Func<T, UiSymbolId?>? icon = null, int historyCapacity = 64)
        => new(this, id, values, itemType, identify, label, supportingText, icon, selectedItemId, historyCapacity);

    public UiPublicationView Capture()
    {
        EnsureOwner();
        _sealed = true;
        return _view;
    }

    public UiPublicationBatch BeginUpdate() => new(this, Capture());

    internal UiPublicationEntry Read(IUiPublishedSource source)
    { EnsureOwner(); return _view.Entry(source); }

    internal void Register(IUiPublicationParticipant source, IUiSemanticSource initial)
    {
        EnsureCanRegister(source.SourceId);
        var nominalTypes = new Dictionary<UiSymbolId, Type>(_nominalTypes);
        var diagnostics = Validate(source, initial, nominalTypes);
        if (diagnostics.Count != 0) throw new UiGraphValidationException(diagnostics);
        var entries = _view.CopyEntries();
        entries.Add(source.SourceId, new(source, initial, 0));
        _sources.Add(source.SourceId, source);
        _nominalTypes = nominalTypes;
        _view = new(this, 0, entries);
    }

    internal void EnsureCanRegister(UiSymbolId sourceId)
    {
        EnsureOwner();
        if (_disposed) throw new ObjectDisposedException(nameof(UiPublication));
        if (_sealed) throw new InvalidOperationException("Declare all publication sources before capturing or updating the view.");
        if (!UiGraphBinder.IsChild(OwnerId, sourceId)) throw new ArgumentException("Source identity must belong to the publication owner.");
        if (_sources.ContainsKey(sourceId)) throw new InvalidOperationException($"Publication source '{sourceId}' is already declared.");
    }

    internal IReadOnlyList<UiGraphDiagnostic> Validate(IUiPublicationParticipant source, IUiSemanticSource candidate)
        => Validate(source, candidate, new(_nominalTypes));

    private IReadOnlyList<UiGraphDiagnostic> Validate(IUiPublicationParticipant source, IUiSemanticSource candidate,
        Dictionary<UiSymbolId, Type> nominalTypes)
    {
        var node = new UiSemanticNode(source.SourceId, "Value", "Value", source.DataType, Array.Empty<UiSymbolId>());
        var errors = new List<UiGraphDiagnostic>(UiGraphBinder.Validate(new(OwnerId, new[] { node })));
        UiSourceTypeValidation.Validate(new(source.SourceId, "Value", candidate, Array.Empty<UiCapability>()) { DataType = source.DataType },
            source.ValueType, nominalTypes, errors);
        return errors;
    }

    public UiPublicationResult Commit(UiPublicationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (Environment.CurrentManagedThreadId != _thread) return Rejected(UiPublicationStatus.WrongThread, "UIP001", "Commit must run on the owning UI thread.");
        if (_disposed) return Rejected(UiPublicationStatus.Disposed, "UIP002", "The publication has been disposed.");
        if (_notifying) return Rejected(UiPublicationStatus.Reentrant, "UIP003", "A publication observer cannot commit reentrantly.");
        if (!ReferenceEquals(batch.Owner, this)) return Rejected(UiPublicationStatus.Invalid, "UIP004", "The batch belongs to another publication.");
        if (batch.IsCompleted || batch.BaseView.Version != Version) return Rejected(UiPublicationStatus.Conflict, "UIP005", "The batch was already completed or prepared against a different revision.");
        batch.IsCompleted = true;
        if (batch.Diagnostics.Count != 0) return LastResult = new(UiPublicationStatus.Invalid, Id, Version, batch.Diagnostics);
        if (batch.Changes.Count == 0) return LastResult = new(UiPublicationStatus.Unchanged, Id, Version);
        if (Version == long.MaxValue) return Rejected(UiPublicationStatus.Conflict, "UIP006", "The publication version is exhausted.");

        var entries = _view.CopyEntries();
        foreach (var (id, next) in batch.Changes) entries[id] = next;
        _view = new(this, Version + 1, entries);
        var observerErrors = new List<Exception>();
        _notifying = true;
        try
        {
            foreach (UiSymbolId id in batch.Changes.Keys)
            {
                if (_disposed) break;
                _sources[id].Notify(observerErrors);
            }
            Notify(_changed, observerErrors);
        }
        finally { _notifying = false; }
        return LastResult = new(UiPublicationStatus.Committed, Id, Version, observerErrors: observerErrors);

        UiPublicationResult Rejected(UiPublicationStatus status, string code, string message)
            => new(status, Id, Version, new[] { new UiPublicationDiagnostic(code, message, OwnerId) });
    }

    public void Dispose()
    {
        EnsureOwner();
        if (_disposed) return;
        _disposed = true;
        _changed = null;
        foreach (var source in _sources.Values) source.ClearSubscriptions();
    }

    internal void EnsureOwner()
    {
        if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("Publication access must run on the owning UI thread.");
    }
    internal void Notify(Action? observers, List<Exception> errors)
    {
        if (observers is null) return;
        foreach (Action observer in observers.GetInvocationList())
        {
            if (_disposed) break;
            try { observer(); } catch (Exception error) { errors.Add(error); }
        }
    }
}

public sealed partial class UiPublicationBatch
{
    private readonly Dictionary<UiSymbolId, UiPublicationEntry> _changes = new();
    private readonly List<UiPublicationDiagnostic> _diagnostics = new();
    internal UiPublicationBatch(UiPublication owner, UiPublicationView view) { Owner = owner; BaseView = view; }
    internal UiPublication Owner { get; }
    internal UiPublicationView BaseView { get; }
    internal bool IsCompleted { get; set; }
    internal IReadOnlyDictionary<UiSymbolId, UiPublicationEntry> Changes => _changes;
    internal IReadOnlyList<UiPublicationDiagnostic> Diagnostics => _diagnostics;
    public long BaseVersion => BaseView.Version;

    public T Read<T>(UiPublishedState<T> source)
        => ((IUiSemanticSource<T>)ReadSource(source)).Value;

    public UiPublicationBatch Set<T>(UiPublishedState<T> source, T value)
    {
        EnsureMutable();
        BaseView.Entry(source);
        try
        {
            bool equivalent = EqualityComparer<T>.Default.Equals(source.Read(BaseView), value);
            long version = BaseView.SourceVersion(source);
            var candidate = new UiPublishedValueSnapshot<T>(value, equivalent ? version : checked(version + 1));
            Stage(source, candidate, equivalent);
        }
        catch (Exception error) { Reject(source, "UIP010", error.Message); }
        return this;
    }

    public UiPublicationResult Commit() => Owner.Commit(this);

    internal IUiSemanticSource ReadSource(IUiPublishedSource source)
    {
        EnsureMutable();
        BaseView.Entry(source);
        return _changes.TryGetValue(source.SourceId, out var value) ? value.Snapshot : BaseView.Read(source);
    }

    internal void Stage(IUiPublicationParticipant source, IUiSemanticSource snapshot, bool equivalentToBase, long? version = null)
    {
        EnsureMutable();
        UiPublicationEntry previous = BaseView.Entry(source);
        // Selection-only drafts reuse already validated immutable payload storage. Do not enumerate
        // an entire collection on a selection dispatch merely to validate the same values again.
        bool sameCollectionValues = snapshot is IUiSemanticCollectionSnapshot
            && previous.Snapshot is IUiSemanticCollectionSnapshot
            && ReferenceEquals(snapshot.UntypedValue, previous.Snapshot.UntypedValue);
        IReadOnlyList<UiGraphDiagnostic> invalid = sameCollectionValues
            ? Array.Empty<UiGraphDiagnostic>() : Owner.Validate(source, snapshot);
        foreach (var diagnostic in invalid) _diagnostics.Add(new(diagnostic.Code, diagnostic.Message, source.SourceId));
        if (invalid.Count != 0) return;
        if (equivalentToBase) { _changes.Remove(source.SourceId); return; }
        if (previous.Version == long.MaxValue) { _diagnostics.Add(new("UIP006", "The source version is exhausted.", source.SourceId)); return; }
        _changes[source.SourceId] = new(source, snapshot, version ?? previous.Version + 1);
    }

    internal void Reject(IUiPublishedSource source, string code, string message)
    { EnsureMutable(); BaseView.Entry(source); _diagnostics.Add(new(code, message, source.SourceId)); }

    private void EnsureMutable()
    {
        Owner.EnsureOwner();
        if (IsCompleted) throw new InvalidOperationException("The publication batch is already completed.");
    }
}
