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
    private readonly UiPublication? _publication;
    private readonly Queue<Pending> _waiting = new();
    private Action<TRequest, UiActionResult<TResult>>? _completed;
    private UiActionOperation<TResult>? _operation;
    private Pending? _active;
    private bool _dispatching;
    private bool _retired;
    private bool _registered;
    private UiActionResult<TResult>? _availabilityFailure;
    internal UiActionExecution(UiActionDispatcher owner, UiAction<TRequest, TResult> action,
        Action<TRequest, UiActionResult<TResult>>? completed, UiPublication? publication = null)
    { _owner = owner; _action = action; _completed = completed; _publication = publication; }

    UiActionDispatcher IUiActionExecution.Owner => _owner;
    UiSymbolId IUiActionExecution.Id => _action.Id;
    void IUiActionExecution.Register() => _registered = true;

    internal UiActionState State { get; private set; } = UiActionState.Available;
    internal UiActionAvailability Availability { get; private set; } = UiActionAvailability.Available;
    internal UiActionResult<TResult>? LastResult { get; private set; }
    internal Exception? LastObserverError { get; private set; }
    internal int WaitingCount => _waiting.Count;
    internal bool IsRetired => _retired || _owner.IsDisposed || _publication?.IsDisposed == true ||
                               (_registered && !_owner.Contains(this));
    internal bool CanInvoke => !IsRetired && Availability.CanExecute &&
        (_operation is null || _action.Concurrency.Kind != UiActionConcurrencyKind.RejectWhileRunning) &&
        (_action.Concurrency.Kind != UiActionConcurrencyKind.Queue ||
         _waiting.Count < _action.Concurrency.Capacity);

    internal void AcceptLegacyAvailability(bool available)
    {
        if (IsRetired) return;
        Availability = available ? UiActionAvailability.Available
            : UiLegacyActionAvailability.Disabled;
        if (!available) State = UiActionState.Disabled;
        else if (State == UiActionState.Disabled) State = UiActionState.Available;
    }

    internal UiActionSubmission<TResult> Invoke(TRequest request)
        => InvokeCore(request, capture: null, isCurrent: null);

    internal UiActionSubmission<TResult> InvokeCaptured(Func<TRequest> capture, Func<bool>? isCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return InvokeCore(default!, capture, isCurrent);
    }

    private UiActionSubmission<TResult> InvokeCore(TRequest request, Func<TRequest>? capture, Func<bool>? isCurrent)
    {
        _owner.RequireOwner();
        if (IsRetired) return Reject("UIA001", "The action owner has retired.");
        if (!_registered) return Reject("UIA008", "The action registration has not been accepted.");
        if (_dispatching) return Reject("UIA002", "Reentrant action dispatch is unavailable.");
        _dispatching = true;
        try
        {
            if (!ReadAvailability()) return IsRetired
                ? Reject("UIA001", "The action owner has retired.")
                : new(false, _owner.SessionId, _owner.GenerationId, UnavailableResult());
            if (IsRetired) return Reject("UIA001", "The action owner has retired.");
            if (isCurrent?.Invoke() == false) return Reject("UIA007", "The accepted UI frame changed during action admission.");
            if (_operation is not null && _action.Concurrency.Kind == UiActionConcurrencyKind.RejectWhileRunning)
                return Reject("UIA003", "The action is already running.");
            if ((_operation is not null || _waiting.Count != 0) && _action.Concurrency.Kind == UiActionConcurrencyKind.Queue
                && _waiting.Count >= _action.Concurrency.Capacity)
                return Reject("UIA004", "The action queue is full.");
            // Capture only admitted input, under the same reentry/owner fence as execution.
            // Queued starts retain this request instead of reading a later UI draft.
            if (capture is not null)
            {
                try { request = capture(); }
                catch (Exception error)
                {
                    var failure = UiActionResult<TResult>.Failure(error);
                    if (!IsRetired)
                    {
                        LastResult = failure;
                        if (_operation is null) State = UiActionState.Failed;
                    }
                    return new(false, _owner.SessionId, _owner.GenerationId, failure);
                }
                if (IsRetired) return Reject("UIA001", "The action owner has retired.");
                if (isCurrent?.Invoke() == false) return Reject("UIA007", "The accepted UI frame changed during action admission.");
            }
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
                    if (IsRetired)
                    { submission.Complete(UiActionResult<TResult>.Cancelled(UiActionCancellationReason.OwnerRetired)); return submission; }
                    if (isCurrent?.Invoke() == false)
                        return Reject("UIA007", "The accepted UI frame changed during action admission.");
                }
                _waiting.Enqueue(pending);
            }
            return submission;
        }
        finally { _dispatching = false; }
    }

    // Presentation may refresh eligibility without delivering a completion or starting a queue.
    internal bool RefreshAvailability()
    {
        _owner.RequireOwner();
        if (IsRetired || _dispatching) return false;
        UiActionAvailability before = Availability;
        UiActionState state = State;
        bool enabled = CanInvoke;
        _dispatching = true;
        try
        {
            ReadAvailability();
            return enabled != CanInvoke || state != State ||
                before.Reason?.Code != Availability.Reason?.Code ||
                before.Reason?.Message != Availability.Reason?.Message ||
                before.Reason?.Field != Availability.Reason?.Field ||
                !(before.Reason?.LocalizedMessage is { } message
                    ? message.HasSameContent(Availability.Reason?.LocalizedMessage)
                    : Availability.Reason?.LocalizedMessage is null);
        }
        finally { _dispatching = false; }
    }

    public bool Pump()
    {
        _owner.RequireOwner();
        if (IsRetired || _dispatching) return false;
        _dispatching = true;
        try
        {
            bool changed = ObserveCompletion();
            if (IsRetired) return changed;
            if (_operation is null && _waiting.TryDequeue(out Pending? pending))
            {
                if (ReadAvailability() && !IsRetired) Start(pending);
                else if (!IsRetired)
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
        if (IsRetired || _dispatching) return;
        _dispatching = true;
        try { CancelWaiting(UiActionCancellationReason.Requested); RecordCancellationError(_operation?.Cancel()); }
        finally { _dispatching = false; }
    }

    public void Retire()
    {
        _owner.RequireOwner();
        if (_retired) return;
        _retired = true;
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
        bool recovering = _availabilityFailure is not null;
        _availabilityFailure = null;
        try
        {
            UiActionAvailability availability = _action.ReadAvailability();
            if (IsRetired) return false;
            Availability = availability;
        }
        catch (Exception error)
        {
            if (IsRetired) return false;
            _availabilityFailure = LastResult = UiActionResult<TResult>.Failure(error);
            Availability = UiActionAvailability.Disabled(new("UIA005", "Action availability failed."));
            if (_operation is null) State = UiActionState.Failed;
            return false;
        }
        if (_operation is null && !Availability.CanExecute) State = UiActionState.Disabled;
        else if (_operation is null && (State == UiActionState.Disabled || recovering))
            State = UiActionState.Available;
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
