using Hatifect.UI.Experience;

namespace Hatifect.UI.Runtime.Actions;

internal sealed class UiActionSubmission<TResult>
{
    internal UiActionSubmission(bool accepted, Guid sessionId, Guid generationId, UiActionResult<TResult>? result = null)
    { Accepted = accepted; SessionId = sessionId; GenerationId = generationId; Result = result; }
    internal bool Accepted { get; }
    internal Guid SessionId { get; }
    internal Guid GenerationId { get; }
    internal UiActionResult<TResult>? Result { get; private set; }
    internal void Complete(UiActionResult<TResult> result) => Result ??= result;
}

internal sealed class UiActionExecution<TRequest, TResult> : IUiActionExecution
{
    private sealed record Pending(TRequest Request, UiActionSubmission<TResult> Submission);
    private readonly UiActionDispatcher _owner;
    private readonly UiAction<TRequest, TResult> _action;
    private readonly Queue<Pending> _waiting = new();
    private Action<TRequest, UiActionResult<TResult>>? _completed;
    private UiActionOperation<TResult>? _operation;
    private Pending? _active;
    private bool _dispatching;
    private UiActionResult<TResult>? _availabilityFailure;
    internal UiActionExecution(UiActionDispatcher owner, UiAction<TRequest, TResult> action,
        Action<TRequest, UiActionResult<TResult>>? completed)
    { _owner = owner; _action = action; _completed = completed; }

    internal UiActionState State { get; private set; } = UiActionState.Available;
    internal UiActionAvailability Availability { get; private set; } = UiActionAvailability.Available;
    internal UiActionResult<TResult>? LastResult { get; private set; }
    internal Exception? LastObserverError { get; private set; }
    internal int WaitingCount => _waiting.Count;

    internal UiActionSubmission<TResult> Invoke(TRequest request)
    {
        _owner.RequireOwner();
        if (_owner.IsDisposed) return Reject("UIA001", "The action owner has retired.");
        if (_dispatching) return Reject("UIA002", "Reentrant action dispatch is unavailable.");
        _dispatching = true;
        try
        {
            if (!ReadAvailability()) return _owner.IsDisposed
                ? Reject("UIA001", "The action owner has retired.")
                : new(false, _owner.SessionId, _owner.GenerationId, UnavailableResult());
            if (_owner.IsDisposed) return Reject("UIA001", "The action owner has retired.");
            if (_operation is not null && _action.Concurrency.Kind == UiActionConcurrencyKind.RejectWhileRunning)
                return Reject("UIA003", "The action is already running.");
            if ((_operation is not null || _waiting.Count != 0) && _action.Concurrency.Kind == UiActionConcurrencyKind.Queue
                && _waiting.Count >= _action.Concurrency.Capacity)
                return Reject("UIA004", "The action queue is full.");
            var submission = new UiActionSubmission<TResult>(true, _owner.SessionId, _owner.GenerationId);
            var pending = new Pending(request, submission);
            if (_operation is null && _waiting.Count == 0) { Start(pending); ObserveCompletion(); }
            else
            {
                if (_action.Concurrency.Kind == UiActionConcurrencyKind.RestartLatest)
                {
                    CancelWaiting(UiActionCancellationReason.Superseded);
                    _active!.Submission.Complete(UiActionResult<TResult>.Cancelled(UiActionCancellationReason.Superseded));
                    RecordCancellationError(_operation?.Cancel());
                    if (_owner.IsDisposed)
                    { submission.Complete(UiActionResult<TResult>.Cancelled(UiActionCancellationReason.OwnerRetired)); return submission; }
                }
                _waiting.Enqueue(pending);
            }
            return submission;
        }
        finally { _dispatching = false; }
    }

    public bool Pump()
    {
        _owner.RequireOwner();
        if (_owner.IsDisposed || _dispatching) return false;
        _dispatching = true;
        try
        {
            bool changed = ObserveCompletion();
            if (_owner.IsDisposed) return changed;
            if (_operation is null && _waiting.TryDequeue(out Pending? pending))
            {
                if (ReadAvailability() && !_owner.IsDisposed) Start(pending);
                else if (!_owner.IsDisposed)
                {
                    Complete(pending, UnavailableResult());
                }
                else pending.Submission.Complete(UiActionResult<TResult>.Cancelled(UiActionCancellationReason.OwnerRetired));
                changed = true;
                // At most one queued start/completion per pump, including synchronous executors.
                ObserveCompletion();
            }
            return changed;
        }
        finally { _dispatching = false; }
    }

    internal void Cancel()
    {
        _owner.RequireOwner();
        if (_owner.IsDisposed || _dispatching) return;
        _dispatching = true;
        try { CancelWaiting(UiActionCancellationReason.Requested); RecordCancellationError(_operation?.Cancel()); }
        finally { _dispatching = false; }
    }

    public void Retire()
    {
        _owner.RequireOwner();
        _completed = null;
        CancelWaiting(UiActionCancellationReason.OwnerRetired);
        _active?.Submission.Complete(UiActionResult<TResult>.Cancelled(UiActionCancellationReason.OwnerRetired));
        UiActionOperation<TResult>? operation = _operation;
        _operation = null; _active = null;
        State = UiActionState.Cancelled;
        RecordCancellationError(operation?.Cancel());
    }

    private bool ReadAvailability()
    {
        _availabilityFailure = null;
        try
        {
            UiActionAvailability availability = _action.ReadAvailability();
            if (_owner.IsDisposed) return false;
            Availability = availability;
        }
        catch (Exception error)
        {
            if (_owner.IsDisposed) return false;
            _availabilityFailure = LastResult = UiActionResult<TResult>.Failure(error);
            Availability = UiActionAvailability.Disabled(new("UIA005", "Action availability failed."));
            if (_operation is null) State = UiActionState.Failed;
            return false;
        }
        if (_operation is null && !Availability.CanExecute) State = UiActionState.Disabled;
        return Availability.CanExecute;
    }

    private UiActionResult<TResult> UnavailableResult()
        => _availabilityFailure ?? UiActionResult<TResult>.Rejected(Availability.Reason!);

    private void Start(Pending pending)
    {
        _active = pending;
        _operation = new();
        State = UiActionState.Running;
        _operation.Start(_action, pending.Request);
    }

    private bool ObserveCompletion()
    {
        if (_operation?.Result is not { } result) return false;
        Pending pending = _active!;
        _operation = null; _active = null;
        if (pending.Submission.Result is null) Complete(pending, result);
        else if (_waiting.Count == 0) State = UiActionState.Cancelled;
        return true;
    }

    private void Complete(Pending pending, UiActionResult<TResult> result)
    {
        pending.Submission.Complete(result);
        LastResult = result;
        State = result.Outcome switch
        {
            UiActionOutcome.Success => UiActionState.Completed,
            UiActionOutcome.Rejected => UiActionState.Rejected,
            UiActionOutcome.Failure => UiActionState.Failed,
            _ => UiActionState.Cancelled
        };
        try { _completed?.Invoke(pending.Request, result); }
        catch (Exception error) { LastObserverError = error; }
    }

    private void CancelWaiting(UiActionCancellationReason reason)
    {
        while (_waiting.TryDequeue(out Pending? pending)) pending.Submission.Complete(UiActionResult<TResult>.Cancelled(reason));
    }
    private void RecordCancellationError(Exception? error) { if (error is not null) LastObserverError = error; }
    private UiActionSubmission<TResult> Reject(string code, string message) => Reject(new UiActionRejection(code, message));
    private UiActionSubmission<TResult> Reject(UiActionRejection rejection)
        => new(false, _owner.SessionId, _owner.GenerationId, UiActionResult<TResult>.Rejected(rejection));
}
