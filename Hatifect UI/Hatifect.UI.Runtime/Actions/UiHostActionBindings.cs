using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Scene;

namespace Hatifect.UI.Runtime.Actions;

internal static class UiLegacyActionAvailability
{
    internal static UiActionAvailability Disabled { get; } =
        UiActionAvailability.Disabled(new("UIA006", "The action is unavailable."));
}

internal interface IUiActionResolver
{
    bool CanInvoke(UiActionDefinition definition);
    bool Invoke(UiActionDefinition definition);
    UiHostActionStatus? Status(UiActionDefinition definition);
}

internal sealed record UiHostActionStatus(UiActionState State, bool Enabled, int WaitingCount,
    UiActionRejection? Reason, UiActionOutcome? Outcome, Exception? Error, Exception? ObserverError);

internal abstract class UiHostActionBinding
{
    protected UiHostActionBinding(UiActionDefinition definition) => Definition = definition;
    internal UiActionDefinition Definition { get; }
    internal abstract IUiActionExecution Execution { get; }
    internal abstract bool CanInvoke { get; }
    internal abstract bool IsRetired { get; }
    internal abstract bool PresentationChanged { get; }
    internal abstract void AcceptLegacyAvailability(bool available);
    internal abstract UiHostActionStatus Status { get; }
    internal abstract bool Invoke();
    internal abstract bool RefreshAvailability();
    internal abstract void Retire();
}

internal sealed class UiHostActionBinding<TRequest, TResult> : UiHostActionBinding
{
    private readonly UiActionExecution<TRequest, TResult> _execution;
    private Func<TRequest>? _capture;
    private UiPublication? _publication;
    private bool _dirty;
    private Func<long>? _acceptedVersion;

    internal UiHostActionBinding(UiActionDispatcher dispatcher, UiActionDefinition definition,
        UiAction<TRequest, TResult> action, Func<TRequest> capture,
        Action<TRequest, UiActionResult<TResult>>? completed, UiPublication? publication,
        Func<long> acceptedVersion) : base(definition)
    {
        publication?.EnsureOwner();
        _execution = new(dispatcher, action, completed, publication);
        _capture = capture;
        _acceptedVersion = acceptedVersion;
        if (publication?.IsDisposed == true) Retire();
        else if (publication is not null)
        { _publication = publication; publication.Retired += Retire; }
    }

    internal override IUiActionExecution Execution => _execution;
    internal override bool CanInvoke => _execution.CanInvoke;
    internal override bool IsRetired => _execution.IsRetired;
    internal override bool PresentationChanged => _dirty;
    internal override void AcceptLegacyAvailability(bool available) => _execution.AcceptLegacyAvailability(available);
    internal override UiHostActionStatus Status => new(_execution.State, CanInvoke, _execution.WaitingCount,
        _execution.Availability.Reason ?? _execution.LastResult?.Rejection, _execution.LastResult?.Outcome,
        _execution.LastResult?.Error, _execution.LastObserverError);
    internal override bool Invoke()
    {
        Execution.Owner.RequireOwner();
        if (_capture is not { } capture) return false;
        Func<long> acceptedVersion = _acceptedVersion!;
        long version = acceptedVersion();
        _dirty = true;
        return _execution.InvokeCaptured(capture, () => acceptedVersion() == version).Accepted;
    }
    internal override bool RefreshAvailability()
    {
        bool changed = _execution.RefreshAvailability() || _dirty;
        _dirty = false;
        return changed;
    }
    internal override void Retire()
    {
        Execution.Owner.RequireOwner();
        _capture = null;
        _acceptedVersion = null;
        _dirty = true;
        if (_publication is { } publication)
        { _publication = null; publication.Retired -= Retire; }
        _execution.Retire();
    }
}

internal sealed class UiActionBindingMap : IUiActionResolver
{
    private readonly Dictionary<UiSymbolId, bool> _preparedLegacyAvailability = new();
    internal Dictionary<UiSymbolId, UiHostActionBinding> Bindings { get; } = new();
    internal Dictionary<UiSymbolId, IUiActionExecution> Executions { get; } = new();
    internal bool LegacyAvailabilityChanged => _preparedLegacyAvailability.Any(pair => pair.Value != Bindings[pair.Key].CanInvoke);
    public bool CanInvoke(UiActionDefinition definition)
        => Find(definition) is { } binding &&
           (_preparedLegacyAvailability.TryGetValue(definition.Id, out bool available)
               ? available && !binding.IsRetired : binding.CanInvoke);
    public bool Invoke(UiActionDefinition definition)
        => Find(definition)?.Invoke() == true;
    public UiHostActionStatus? Status(UiActionDefinition definition) => Find(definition)?.Status;
    private UiHostActionBinding? Find(UiActionDefinition definition)
        => Bindings.TryGetValue(definition.Id, out var binding) && ReferenceEquals(binding.Definition, definition)
            ? binding : null;

    internal IUiActionResolver ForPreparation() => new PreparationResolver(this);
    internal void AcceptLegacyAvailability()
    {
        foreach (var (id, available) in _preparedLegacyAvailability)
            Bindings[id].AcceptLegacyAvailability(available);
        _preparedLegacyAvailability.Clear();
    }

    // Preserve legacy CanExecute validation in the existing focus/accessibility preparation
    // phases. These reads are staged in this candidate, never in a retained execution.
    private sealed class PreparationResolver : IUiActionResolver
    {
        private readonly UiActionBindingMap _map;
        internal PreparationResolver(UiActionBindingMap map) => _map = map;
        public bool CanInvoke(UiActionDefinition definition)
        {
            if (definition.Binding is not null) return _map.CanInvoke(definition);
            bool available = definition.CanExecute;
            _map._preparedLegacyAvailability[definition.Id] = available;
            return available;
        }
        public bool Invoke(UiActionDefinition definition)
            => throw new InvalidOperationException("A prepared action is not available to input.");
        public UiHostActionStatus? Status(UiActionDefinition definition) => _map.Status(definition);
    }
}

/// <summary>Owns only the current bounded registration set. Preparation neither cancels accepted
/// work nor exposes a new binding to input. A failed candidate retires only its new registrations.</summary>
internal sealed class UiHostActionBindings : IDisposable
{
    private readonly UiActionDispatcher _dispatcher = new(Guid.NewGuid(), Guid.NewGuid());
    private readonly Func<long> _acceptedVersion;
    private UiActionBindingMap _current = new();
    private bool _disposed;
    internal UiHostActionBindings(Func<long> acceptedVersion) => _acceptedVersion = acceptedVersion;
    internal IUiActionResolver Current => _current;
    internal int Count => _current.Bindings.Count;
    internal void RequireOwner() => _dispatcher.RequireOwner();
    internal void FenceRetirement() => _dispatcher.FenceRetirement();

    internal Prepared Prepare(UiScene scene)
    {
        _dispatcher.RequireOwner();
        if (_dispatcher.IsDisposed) throw new ObjectDisposedException(nameof(UiHostActionBindings));
        var prepared = new Prepared(this, _current);
        try { Visit(scene.Root); return prepared; }
        catch { prepared.Dispose(); throw; }

        void Visit(UiSceneNode node)
        {
            if (node is UiButtonSceneNode button) prepared.Add(button.Action);
            foreach (var child in node.Children) Visit(child);
        }
    }

    internal bool Pump() => _dispatcher.Pump();

    internal bool RefreshAvailability()
    {
        _dispatcher.RequireOwner();
        if (_dispatcher.IsDisposed) return false;
        bool changed = false;
        foreach (var binding in _current.Bindings.Values)
        {
            changed |= binding.RefreshAvailability();
            if (_dispatcher.IsDisposed) break;
        }
        return changed;
    }

    public void Dispose()
    {
        _dispatcher.RequireOwner();
        if (_disposed) return;
        _disposed = true;
        // Dispatcher retirement fences all siblings before any cancellation callback runs.
        _dispatcher.Dispose();
        var previous = _current;
        _current = new();
        foreach (var binding in previous.Bindings.Values) binding.Retire();
    }

    internal sealed class Prepared : IDisposable
    {
        private readonly UiHostActionBindings _owner;
        private readonly UiActionBindingMap _previous;
        private bool _committed;
        private bool _disposed;
        internal UiActionBindingMap Map { get; } = new();
        internal IUiActionResolver Resolver { get; }
        internal bool RequiresRender => Map.LegacyAvailabilityChanged || Map.Bindings.Count != _previous.Bindings.Count ||
            Map.Bindings.Any(pair => pair.Value.PresentationChanged ||
                !_previous.Bindings.TryGetValue(pair.Key, out var previous) || !ReferenceEquals(previous, pair.Value));
        internal Prepared(UiHostActionBindings owner, UiActionBindingMap previous)
        { _owner = owner; _previous = previous; Resolver = Map.ForPreparation(); }

        internal void Add(UiActionDefinition definition)
        {
            if (_committed || _disposed) throw new InvalidOperationException("The prepared registration is already completed.");
            if (Map.Bindings.TryGetValue(definition.Id, out var duplicate))
            {
                if (!ReferenceEquals(duplicate.Definition, definition))
                    throw new InvalidOperationException("Distinct action definitions share an ID in one host.");
                return;
            }
            if (Map.Bindings.Count == UiActionDispatcher.MaximumActions)
                throw new InvalidOperationException("The host action capacity is exhausted.");
            UiHostActionBinding binding = _previous.Bindings.TryGetValue(definition.Id, out var previous)
                && ReferenceEquals(previous.Definition, definition)
                    ? previous : new Factory(_owner._dispatcher, definition, _owner._acceptedVersion).Create();
            Map.Bindings.Add(definition.Id, binding);
            Map.Executions.Add(definition.Id, binding.Execution);
            if (!ReferenceEquals(binding, previous) && definition.Binding is not null) binding.RefreshAvailability();
        }

        internal void Commit()
        {
            if (_disposed || _committed || !ReferenceEquals(_owner._current, _previous))
                throw new InvalidOperationException("The prepared action registration is no longer current.");
            _owner._dispatcher.Install(Map.Executions);
            Map.AcceptLegacyAvailability();
            _owner._current = Map;
            _committed = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            UiActionBindingMap retired = _committed ? _previous : Map;
            UiActionBindingMap retained = _committed ? Map : _previous;
            foreach (var (id, binding) in retired.Bindings)
                if (!retained.Bindings.TryGetValue(id, out var live) || !ReferenceEquals(binding, live))
                    binding.Retire();
        }
    }

    private readonly record struct Unit;
    private sealed class Factory : IUiActionBindingFactory<UiHostActionBinding>
    {
        private readonly UiActionDispatcher _dispatcher;
        private readonly UiActionDefinition _definition;
        private readonly Func<long> _acceptedVersion;
        internal Factory(UiActionDispatcher dispatcher, UiActionDefinition definition, Func<long> acceptedVersion)
        { _dispatcher = dispatcher; _definition = definition; _acceptedVersion = acceptedVersion; }
        internal UiHostActionBinding Create() => _definition.Binding?.Create(this) ?? Create(
            new UiAction<Unit, Unit>(_definition.Id, _definition.Title, (_, _) =>
            {
                _definition.ExecuteAdmitted();
                return new(UiActionResult<Unit>.Success(default));
            }, UiActionConcurrency.RejectWhileRunning, () => _definition.CanExecute
                ? UiActionAvailability.Available
                : UiLegacyActionAvailability.Disabled),
            static () => default(Unit), null, null);
        public UiHostActionBinding Create<TRequest, TResult>(UiAction<TRequest, TResult> action,
            Func<TRequest> capture, Action<TRequest, UiActionResult<TResult>>? completed, UiPublication? owner)
            => new UiHostActionBinding<TRequest, TResult>(_dispatcher, _definition, action, capture, completed, owner, _acceptedVersion);
    }
}
