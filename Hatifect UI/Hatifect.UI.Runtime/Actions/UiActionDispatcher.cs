using Hatifect.UI.Experience;

namespace Hatifect.UI.Runtime.Actions;

internal interface IUiActionExecution
{
    UiActionDispatcher Owner { get; }
    UiSymbolId Id { get; }
    void Register();
    bool Pump();
    void Retire();
}

/// <summary>One host generation owns this dispatcher. Recomposition reuses it; close, reload and
/// session replacement retire it. No callback from an old dispatcher is delivered to its successor.</summary>
internal sealed class UiActionDispatcher : IDisposable
{
    internal const int MaximumActions = 4096;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private Dictionary<UiSymbolId, IUiActionExecution> _actions = new();
    private bool _pumping;
    private bool _retired;
    internal UiActionDispatcher(Guid sessionId, Guid generationId)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A session identity is required.", nameof(sessionId));
        if (generationId == Guid.Empty) throw new ArgumentException("A generation identity is required.", nameof(generationId));
        SessionId = sessionId; GenerationId = generationId;
    }
    internal Guid SessionId { get; }
    internal Guid GenerationId { get; }
    internal bool IsDisposed { get; private set; }
    internal int Count => _actions.Count;
    internal void RequireOwner()
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Action dispatch must run on the owning UI thread.");
    }

    internal UiActionExecution<TRequest, TResult> Bind<TRequest, TResult>(UiAction<TRequest, TResult> action,
        Action<TRequest, UiActionResult<TResult>>? completed = null)
    {
        RequireOwner();
        if (IsDisposed) throw new ObjectDisposedException(nameof(UiActionDispatcher));
        ArgumentNullException.ThrowIfNull(action);
        if (_pumping) throw new InvalidOperationException("Action registration cannot change during dispatch.");
        if (_actions.Count >= MaximumActions) throw new InvalidOperationException("The host action capacity is exhausted.");
        if (_actions.ContainsKey(action.Id)) throw new ArgumentException("An action is already bound in this host.", nameof(action));
        var execution = new UiActionExecution<TRequest, TResult>(this, action, completed);
        _actions.Add(action.Id, execution);
        ((IUiActionExecution)execution).Register();
        return execution;
    }

    internal bool Pump()
    {
        RequireOwner();
        if (IsDisposed || _pumping) return false;
        _pumping = true;
        bool changed = false;
        try
        {
            foreach (IUiActionExecution execution in _actions.Values)
            {
                changed |= execution.Pump();
                if (IsDisposed) break;
            }
        }
        finally { _pumping = false; if (IsDisposed) _actions.Clear(); }
        return changed;
    }

    // Prepared maps are private and never mutated after installation. Pump can therefore
    // finish enumerating its old map if a completion accepts a replacement scene.
    internal void Install(Dictionary<UiSymbolId, IUiActionExecution> actions)
    {
        RequireOwner();
        if (IsDisposed) throw new ObjectDisposedException(nameof(UiActionDispatcher));
        if (actions.Count > MaximumActions) throw new InvalidOperationException("The host action capacity is exhausted.");
        foreach (var (id, execution) in actions)
            if (!ReferenceEquals(execution.Owner, this) || execution.Id != id)
                throw new ArgumentException("The registration belongs to another action owner.", nameof(actions));
        foreach (IUiActionExecution execution in actions.Values) execution.Register();
        _actions = actions;
    }

    internal bool Contains(IUiActionExecution execution)
        => _actions.TryGetValue(execution.Id, out var current) && ReferenceEquals(current, execution);

    internal void FenceRetirement()
    {
        RequireOwner();
        IsDisposed = true;
    }

    public void Dispose()
    {
        RequireOwner();
        if (_retired) return;
        _retired = true;
        IsDisposed = true;
        foreach (IUiActionExecution execution in _actions.Values) execution.Retire();
        if (!_pumping) _actions.Clear();
    }
}
