using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Actions;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class ActionExecutionTests
{
    [Fact]
    public void AvailabilityRefreshReportsReasonsAndRecoversWithoutDomainEffects()
    {
        int mode = 0, effects = 0;
        var error = new InvalidOperationException("availability refresh");
        using var owner = Dispatcher();
        var execution = owner.Bind(Action((_, _) => { effects++; return new(UiActionResult<int>.Success(1)); },
            availability: () => mode switch
            {
                0 => UiActionAvailability.Disabled(new("domain.field", "Choose a target.", Id("target"))),
                1 => throw error,
                _ => UiActionAvailability.Available
            }));

        Assert.True(execution.RefreshAvailability());
        Assert.False(execution.CanInvoke);
        Assert.Equal(UiActionState.Disabled, execution.State);
        Assert.Equal("domain.field", execution.Availability.Reason!.Code);
        Assert.Equal("Choose a target.", execution.Availability.Reason.Message);
        Assert.Equal(Id("target"), execution.Availability.Reason.Field);
        Assert.False(execution.RefreshAvailability());
        mode = 1;
        Assert.True(execution.RefreshAvailability());
        Assert.False(execution.CanInvoke);
        Assert.Equal(UiActionState.Failed, execution.State);
        Assert.Same(error, execution.LastResult!.Error);
        mode = 2;
        Assert.True(execution.RefreshAvailability());
        Assert.True(execution.CanInvoke);
        Assert.Equal(UiActionState.Available, execution.State);
        Assert.Null(execution.Availability.Reason);
        Assert.False(execution.RefreshAvailability());
        Assert.Equal(0, effects);
    }

    [Fact]
    public void AvailabilityRefreshDoesNotDeliverBackgroundCompletion()
    {
        var pending = Completion();
        int callbacks = 0, callbackThread = 0;
        int owningThread = Environment.CurrentManagedThreadId;
        using var owner = Dispatcher();
        var execution = owner.Bind(Action((_, _) => new(pending.Task)), (_, _) =>
        { callbacks++; callbackThread = Environment.CurrentManagedThreadId; });
        var submission = execution.Invoke(1);
        OnWorker(() => pending.SetResult(UiActionResult<int>.Success(31)));

        Assert.False(execution.RefreshAvailability());
        Assert.False(execution.CanInvoke);
        Assert.Equal(UiActionState.Running, execution.State);
        Assert.Null(submission.Result);
        Assert.Equal(0, callbacks);
        PumpCompletion(owner);
        Assert.Equal(31, submission.Result!.Value);
        Assert.Equal(1, callbacks);
        Assert.Equal(owningThread, callbackThread);
        Assert.True(execution.CanInvoke);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RequestCaptureRunsOnlyForAdmittedInput(int policy)
    {
        bool enabled = false;
        int captures = 0, effects = 0;
        var pending = Completion();
        var concurrency = policy switch
        {
            0 => UiActionConcurrency.RejectWhileRunning,
            1 => UiActionConcurrency.Queue(1),
            _ => UiActionConcurrency.RestartLatest
        };
        using var owner = Dispatcher();
        var execution = owner.Bind(Action((_, _) => { effects++; return new(pending.Task); }, concurrency,
            () => enabled ? UiActionAvailability.Available : UiActionAvailability.Disabled(new("domain.disabled", "Disabled."))));
        int Capture() => ++captures;

        Assert.False(execution.InvokeCaptured(Capture).Accepted);
        Assert.Equal(0, captures);
        enabled = true;
        Assert.True(execution.InvokeCaptured(Capture).Accepted);
        Assert.Equal(policy != 0, execution.CanInvoke);
        var next = execution.InvokeCaptured(Capture);
        Assert.Equal(policy != 0, next.Accepted);
        Assert.Equal(policy == 0 ? 1 : 2, captures);
        var third = execution.InvokeCaptured(Capture);
        Assert.Equal(policy == 2, third.Accepted);
        Assert.Equal(policy == 0 ? 1 : policy == 1 ? 2 : 3, captures);
        Assert.Equal(policy == 2, execution.CanInvoke);
        if (policy < 2) Assert.Equal(policy == 0 ? "UIA003" : "UIA004", third.Result!.Rejection!.Code);
        Assert.Equal(1, effects);
        execution.Retire();
        int before = captures;
        Assert.False(execution.InvokeCaptured(Capture).Accepted);
        Assert.Equal(before, captures);
        Assert.False(execution.CanInvoke);
        pending.SetResult(UiActionResult<int>.Success(1));
        Assert.False(owner.Pump());
    }

    [Fact]
    public void QueuedRequestRetainsExactlyOneAdmissionSnapshot()
    {
        var pending = Completion();
        int draft = 11, captures = 0;
        var started = new List<int>();
        using var owner = Dispatcher();
        var execution = owner.Bind(Action((request, _) =>
        {
            started.Add(request);
            return started.Count == 1 ? new(pending.Task) : new(UiActionResult<int>.Success(request));
        }, UiActionConcurrency.Queue(1)));
        int Capture() { captures++; return draft; }
        var active = execution.InvokeCaptured(Capture);
        draft = 22;
        var queued = execution.InvokeCaptured(Capture);
        draft = 99;
        pending.SetResult(UiActionResult<int>.Success(11));

        PumpCompletion(owner);

        Assert.Equal(new[] { 11, 22 }, started);
        Assert.Equal(11, active.Result!.Value);
        Assert.Equal(22, queued.Result!.Value);
        Assert.Equal(2, captures);
        Assert.Equal(0, execution.WaitingCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequestCaptureFailureIsObservedWithoutReplacingAnActiveOperation(bool running)
    {
        var pending = Completion();
        var error = new InvalidOperationException("request snapshot");
        int effects = 0, callbacks = 0;
        using var owner = Dispatcher();
        var execution = owner.Bind(Action((request, _) =>
        {
            effects++;
            return request == 1 ? new(pending.Task) : new(UiActionResult<int>.Success(request));
        }, UiActionConcurrency.Queue(1)), (_, _) => callbacks++);
        var active = running ? execution.Invoke(1) : null;

        var failed = execution.InvokeCaptured(() => throw error);

        Assert.False(failed.Accepted);
        Assert.Same(error, failed.Result!.Error);
        Assert.Same(failed.Result, execution.LastResult);
        Assert.Equal(running ? UiActionState.Running : UiActionState.Failed, execution.State);
        Assert.Equal(running ? 1 : 0, effects);
        Assert.Equal(0, callbacks);
        Assert.Equal(0, execution.WaitingCount);
        if (running)
        {
            Assert.Null(active!.Result);
            pending.SetResult(UiActionResult<int>.Success(41));
            PumpCompletion(owner);
            Assert.Equal(41, active.Result!.Value);
            Assert.Equal(1, callbacks);
        }
        Assert.Equal(7, execution.InvokeCaptured(() => 7).Result!.Value);
        Assert.Equal(running ? 2 : 1, effects);
        Assert.Equal(running ? 2 : 1, callbacks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequestCaptureCannotBypassRetirementOrReentry(bool retire)
    {
        int effects = 0, captures = 0;
        using var owner = Dispatcher();
        var execution = owner.Bind(Action((request, _) => { effects++; return new(UiActionResult<int>.Success(request)); }));
        UiActionSubmission<int>? nested = null;

        var submission = execution.InvokeCaptured(() =>
        {
            if (retire) execution.Retire();
            nested = execution.InvokeCaptured(() => { captures++; return 99; });
            return 5;
        });

        Assert.NotNull(nested);
        Assert.False(nested.Accepted);
        Assert.Equal(retire ? "UIA001" : "UIA002", nested.Result!.Rejection!.Code);
        Assert.Equal(0, captures);
        Assert.Equal(!retire, submission.Accepted);
        Assert.Equal(retire ? 0 : 1, effects);
        if (retire) Assert.Equal("UIA001", submission.Result!.Rejection!.Code);
        else Assert.Equal(5, submission.Result!.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AvailabilityRefreshCannotPublishAfterBindingOrDispatcherRetirement(bool retireDispatcher)
    {
        int calls = 0;
        using var owner = Dispatcher();
        UiActionExecution<int, int>? execution = null;
        execution = owner.Bind(Action((request, _) => new(UiActionResult<int>.Success(request)), availability: () =>
        {
            calls++;
            if (retireDispatcher) owner.Dispose();
            else execution!.Retire();
            return UiActionAvailability.Available;
        }));

        Assert.True(execution.RefreshAvailability());
        Assert.False(execution.CanInvoke);
        Assert.Equal(UiActionState.Cancelled, execution.State);
        Assert.False(execution.RefreshAvailability());
        Assert.False(execution.InvokeCaptured(() => throw new InvalidOperationException("must not capture")).Accepted);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void RetiringOneBindingBlocksItsEffectsWithoutRetiringOtherActions()
    {
        var pending = Completion();
        int effects = 0, callbacks = 0;
        CancellationToken token = default;
        using var owner = Dispatcher();
        var execution = owner.Bind(Action((_, cancellation) =>
        {
            effects++;
            token = cancellation;
            return new(pending.Task);
        }, UiActionConcurrency.Queue(1)), (_, _) => callbacks++);
        var independent = owner.Bind(new UiAction<int, int>(Id("independent"), "Independent",
            (value, _) => new(UiActionResult<int>.Success(value)), UiActionConcurrency.RejectWhileRunning));
        var active = execution.Invoke(1);
        var queued = execution.Invoke(2);

        execution.Retire();
        execution.Retire();
        var stale = execution.Invoke(3);

        Assert.False(owner.IsDisposed);
        Assert.False(stale.Accepted);
        Assert.Equal("UIA001", stale.Result!.Rejection!.Code);
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(UiActionCancellationReason.OwnerRetired, active.Result!.Cancellation);
        Assert.Equal(UiActionCancellationReason.OwnerRetired, queued.Result!.Cancellation);
        Assert.Equal(1, effects);
        Assert.Equal(77, independent.Invoke(77).Result!.Value);
        OnWorker(() => pending.SetException(new InvalidOperationException("retired binding")));
        Assert.False(owner.Pump());
        Assert.Equal(0, callbacks);
        Assert.Equal(UiActionState.Cancelled, execution.State);
    }

    [Fact]
    public void SharedDescriptionHasIndependentHostStateAndSynchronousTypedResults()
    {
        var action = Action((request, _) => new(UiActionResult<int>.Success(request * 2)));
        using var first = Dispatcher();
        using var second = Dispatcher();
        int callbacks = 0;
        var one = first.Bind(action, (request, result) => { Assert.Equal(7, request); Assert.Equal(14, result.Value); callbacks++; });
        var two = second.Bind(action);

        var submission = one.Invoke(7);

        Assert.True(submission.Accepted);
        Assert.Equal(14, submission.Result!.Value);
        Assert.Equal(first.SessionId, submission.SessionId);
        Assert.Equal(first.GenerationId, submission.GenerationId);
        Assert.Equal(UiActionState.Completed, one.State);
        Assert.Equal(UiActionState.Available, two.State);
        Assert.Equal(1, callbacks);
        Assert.False(first.Pump());
    }

    [Fact]
    public void BackgroundCompletionChangesStateAndCallsConsumerOnlyOnOwningPump()
    {
        var completion = Completion();
        using var owner = Dispatcher();
        int thread = Environment.CurrentManagedThreadId;
        var calls = new List<int>();
        var execution = owner.Bind(Action((_, _) => new(completion.Task)), (_, result) =>
        { Assert.Equal(thread, Environment.CurrentManagedThreadId); calls.Add(result.Value); });
        var submission = execution.Invoke(10);
        OnWorker(() => completion.SetResult(UiActionResult<int>.Success(42)));

        Assert.Null(submission.Result);
        Assert.Empty(calls);
        Assert.Equal(UiActionState.Running, execution.State);
        PumpCompletion(owner);
        Assert.Equal(new[] { 42 }, calls);
        Assert.Equal(42, submission.Result!.Value);
        Assert.False(owner.Pump());
    }

    [Fact]
    public void RejectWhileRunningDoesNotInvokeAgainOrReplaceActiveState()
    {
        var completion = Completion();
        int effects = 0;
        using var owner = Dispatcher();
        var execution = owner.Bind(Action((_, _) => { effects++; return new(completion.Task); }));
        var accepted = execution.Invoke(1);
        var rejected = execution.Invoke(2);

        Assert.True(accepted.Accepted);
        Assert.False(rejected.Accepted);
        Assert.Equal("UIA003", rejected.Result!.Rejection!.Code);
        Assert.Equal(UiActionState.Running, execution.State);
        Assert.Equal(1, effects);
        completion.SetResult(UiActionResult<int>.Success(1));
        PumpCompletion(owner);
        Assert.Equal(1, accepted.Result!.Value);
    }

    [Fact]
    public void RestartLatestCancelsWaitingAndKeepsOneNoncooperativeOperation()
    {
        var first = Completion();
        var started = new List<int>();
        var completed = new List<int>();
        CancellationToken token = default;
        using var owner = Dispatcher();
        var execution = owner.Bind(Action((request, cancellation) =>
        {
            started.Add(request);
            if (request != 1) return new(UiActionResult<int>.Success(request));
            token = cancellation;
            return new(first.Task);
        }, UiActionConcurrency.RestartLatest), (request, _) => completed.Add(request));
        var old = execution.Invoke(1);
        var pending = execution.Invoke(2);
        var latest = execution.Invoke(3);

        Assert.True(token.IsCancellationRequested);
        Assert.Equal(UiActionCancellationReason.Superseded, old.Result!.Cancellation);
        Assert.Equal(UiActionCancellationReason.Superseded, pending.Result!.Cancellation);
        Assert.Null(latest.Result);
        Assert.Equal(1, execution.WaitingCount);
        Assert.Equal(new[] { 1 }, started);
        Assert.False(owner.Pump());
        first.SetException(new InvalidOperationException("late fault"));
        PumpCompletion(owner);
        Assert.Equal(new[] { 1, 3 }, started);
        Assert.Equal(new[] { 3 }, completed);
        Assert.Equal(3, latest.Result!.Value);
        Assert.Equal(0, execution.WaitingCount);
        Assert.Equal(UiActionState.Completed, execution.State);
    }

    [Fact]
    public void QueueIsFifoWithCapacityAndAtMostOneQueuedStartPerPump()
    {
        var completion = Completion();
        var started = new List<int>();
        using var owner = Dispatcher();
        var execution = owner.Bind(Action((request, _) =>
        { started.Add(request); return request == 1 ? new(completion.Task) : new(UiActionResult<int>.Success(request)); },
            UiActionConcurrency.Queue(2)));
        var first = execution.Invoke(1);
        var second = execution.Invoke(2);
        var third = execution.Invoke(3);
        var overflow = execution.Invoke(4);
        Assert.False(overflow.Accepted);
        Assert.Equal("UIA004", overflow.Result!.Rejection!.Code);
        Assert.Equal(2, execution.WaitingCount);
        completion.SetResult(UiActionResult<int>.Success(1));

        PumpCompletion(owner);
        Assert.Equal(new[] { 1, 2 }, started);
        Assert.Equal(1, first.Result!.Value);
        Assert.Equal(2, second.Result!.Value);
        Assert.Null(third.Result);
        var fourth = execution.Invoke(4);
        Assert.Equal(new[] { 1, 2 }, started);
        PumpCompletion(owner);
        Assert.Equal(3, third.Result!.Value);
        Assert.Null(fourth.Result);
        PumpCompletion(owner);
        Assert.Equal(4, fourth.Result!.Value);
        Assert.Equal(new[] { 1, 2, 3, 4 }, started);
    }

    [Fact]
    public void AvailabilityIncludesFieldReasonAndIsRecheckedBeforeQueuedEffects()
    {
        var completion = Completion();
        var field = Id("field");
        var rejection = new UiActionRejection("domain.capacity", "No room remains.", field);
        bool enabled = false;
        int effects = 0;
        using var owner = Dispatcher();
        var execution = owner.Bind(Action((_, _) => { effects++; return new(completion.Task); },
            UiActionConcurrency.Queue(2), () => enabled ? UiActionAvailability.Available : UiActionAvailability.Disabled(rejection)));
        Assert.Same(rejection, execution.Invoke(0).Result!.Rejection);
        Assert.Equal(field, execution.Availability.Reason!.Field);
        Assert.Equal(UiActionState.Disabled, execution.State);
        Assert.Equal(0, effects);
        enabled = true;
        execution.Invoke(1);
        var queued = execution.Invoke(2);
        enabled = false;
        completion.SetResult(UiActionResult<int>.Success(1));
        PumpCompletion(owner);
        Assert.Same(rejection, queued.Result!.Rejection);
        Assert.Equal(UiActionState.Rejected, execution.State);
        Assert.Equal(1, effects);
    }

    [Fact]
    public void CancellationDropsQueueButPreservesSuccessfulCommittedDomainResult()
    {
        var completion = Completion();
        CancellationToken token = default;
        using var owner = Dispatcher();
        var execution = owner.Bind(Action((_, cancellation) => { token = cancellation; return new(completion.Task); }, UiActionConcurrency.Queue(1)));
        var active = execution.Invoke(1);
        var pending = execution.Invoke(2);
        execution.Cancel();
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(UiActionCancellationReason.Requested, pending.Result!.Cancellation);
        Assert.Null(active.Result);
        completion.SetResult(UiActionResult<int>.Success(17));
        PumpCompletion(owner);
        Assert.Equal(17, active.Result!.Value);
        Assert.Equal(UiActionState.Completed, execution.State);
        Assert.Equal(0, execution.WaitingCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CloseOrReplacementFencesLateSuccessAndFault(bool fault)
    {
        var completion = Completion();
        CancellationToken token = default;
        int callbacks = 0;
        var action = Action((_, cancellation) => { token = cancellation; return new(completion.Task); }, UiActionConcurrency.Queue(1));
        var old = Dispatcher();
        var execution = old.Bind(action, (_, _) => callbacks++);
        var active = execution.Invoke(1);
        var pending = execution.Invoke(2);
        old.Dispose();
        old.Dispose();
        using var next = new UiActionDispatcher(old.SessionId, Guid.NewGuid());
        var replacement = next.Bind(action, (_, _) => callbacks++);
        OnWorker(() =>
        {
            if (fault) completion.SetException(new InvalidOperationException("closed"));
            else completion.SetResult(UiActionResult<int>.Success(100));
        });

        Assert.True(token.IsCancellationRequested);
        Assert.Equal(UiActionCancellationReason.OwnerRetired, active.Result!.Cancellation);
        Assert.Equal(UiActionCancellationReason.OwnerRetired, pending.Result!.Cancellation);
        Assert.Equal(0, old.Count);
        Assert.False(old.Pump());
        Assert.False(next.Pump());
        Assert.Equal(0, callbacks);
        Assert.Equal(UiActionState.Available, replacement.State);
        Assert.Equal(UiActionState.Cancelled, execution.State);
        Assert.Equal("UIA001", execution.Invoke(3).Result!.Rejection!.Code);
    }

    [Theory]
    [InlineData(0, UiActionState.Failed)]
    [InlineData(1, UiActionState.Failed)]
    [InlineData(2, UiActionState.Rejected)]
    [InlineData(3, UiActionState.Cancelled)]
    [InlineData(4, UiActionState.Failed)]
    public void SynchronousThrowFaultRejectionCancellationAndNullAreObserved(int kind, UiActionState state)
    {
        var error = new InvalidOperationException("executor");
        using var owner = Dispatcher();
        var execution = owner.Bind(Action((_, _) => kind switch
        {
            0 => throw error,
            1 => new(Task.FromException<UiActionResult<int>>(error)),
            2 => new(UiActionResult<int>.Rejected(new("domain.invalid", "Invalid request."))),
            3 => new(UiActionResult<int>.Cancelled()),
            _ => new((UiActionResult<int>)null!)
        }));
        var result = execution.Invoke(1).Result!;
        Assert.Equal(state, execution.State);
        Assert.NotNull(result);
        if (kind < 2) Assert.Same(error, result.Error);
        if (kind == 4) Assert.IsType<InvalidOperationException>(result.Error);
        if (kind == 2) Assert.Equal("domain.invalid", result.Rejection!.Code);
        if (kind == 3) Assert.Equal(UiActionCancellationReason.Requested, result.Cancellation);
        Assert.False(owner.Pump());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyCancellationFromThisRequestedTokenBecomesCancelled(bool ownToken)
    {
        var completion = Completion();
        CancellationToken token = default;
        using var other = new CancellationTokenSource();
        using var owner = Dispatcher();
        var execution = owner.Bind(Action((_, cancellation) => { token = cancellation; return new(completion.Task); }));
        var submission = execution.Invoke(1);
        execution.Cancel();
        var error = new OperationCanceledException(ownToken ? token : other.Token);
        completion.SetException(error);
        PumpCompletion(owner);
        Assert.Equal(ownToken ? UiActionOutcome.Cancelled : UiActionOutcome.Failure, submission.Result!.Outcome);
        if (!ownToken) Assert.Same(error, submission.Result.Error);
    }

    [Fact]
    public void CallbackFailureAndReentryDoNotLoseQueuedWork()
    {
        var completion = Completion();
        var callbackError = new InvalidOperationException("observer");
        using var owner = Dispatcher();
        UiActionExecution<int, int>? execution = null;
        UiActionSubmission<int>? reentry = null;
        execution = owner.Bind(Action((request, _) => request == 1 ? new(completion.Task) : new(UiActionResult<int>.Success(request)),
            UiActionConcurrency.Queue(1)), (_, _) => { reentry = execution!.Invoke(99); throw callbackError; });
        var one = execution.Invoke(1);
        var two = execution.Invoke(2);
        completion.SetResult(UiActionResult<int>.Success(1));
        PumpCompletion(owner);
        Assert.Equal(1, one.Result!.Value);
        Assert.Equal(2, two.Result!.Value);
        Assert.Equal("UIA002", reentry!.Result!.Rejection!.Code);
        Assert.Same(callbackError, execution.LastObserverError);
        Assert.Equal(UiActionState.Completed, execution.State);
    }

    [Fact]
    public void CallbackCanCloseDispatcherWithoutStartingQueuedWork()
    {
        var completion = Completion();
        var owner = Dispatcher();
        int effects = 0;
        var execution = owner.Bind(Action((_, _) => { effects++; return new(completion.Task); }, UiActionConcurrency.Queue(1)),
            (_, _) => owner.Dispose());
        var one = execution.Invoke(1);
        var two = execution.Invoke(2);
        completion.SetResult(UiActionResult<int>.Success(5));
        PumpCompletion(owner);
        Assert.Equal(5, one.Result!.Value);
        Assert.Equal(UiActionCancellationReason.OwnerRetired, two.Result!.Cancellation);
        Assert.Equal(1, effects);
        Assert.Equal(0, owner.Count);
        Assert.Equal(UiActionState.Cancelled, execution.State);
    }

    [Fact]
    public void AvailabilityFailureAndOwnerRetirementPreventExecutorEffects()
    {
        using var owner = Dispatcher();
        var error = new InvalidOperationException("availability");
        int effects = 0;
        var execution = owner.Bind(Action((_, _) => { effects++; return new(UiActionResult<int>.Success(1)); },
            availability: () => throw error));
        var failed = execution.Invoke(1);
        Assert.False(failed.Accepted);
        Assert.Equal(UiActionOutcome.Failure, failed.Result!.Outcome);
        Assert.Same(error, failed.Result.Error);
        Assert.Same(error, execution.LastResult!.Error);
        Assert.Equal(UiActionState.Failed, execution.State);
        var closing = owner.Bind(Action((_, _) => { effects++; return new(UiActionResult<int>.Success(1)); },
            availability: () => { owner.Dispose(); return UiActionAvailability.Available; }, id: Id("close")));
        Assert.False(closing.Invoke(1).Accepted);
        Assert.Equal(UiActionState.Cancelled, closing.State);
        Assert.Equal(0, effects);
    }

    [Fact]
    public void QueuedAvailabilityFailurePreservesOriginalDiagnosticInCompletion()
    {
        var completion = Completion();
        var error = new InvalidOperationException("queued availability");
        bool fail = false;
        int effects = 0;
        var results = new List<UiActionResult<int>>();
        using var owner = Dispatcher();
        var execution = owner.Bind(Action((_, _) => { effects++; return new(completion.Task); },
            UiActionConcurrency.Queue(1), () => fail ? throw error : UiActionAvailability.Available),
            (_, result) => results.Add(result));
        execution.Invoke(1);
        var queued = execution.Invoke(2);
        fail = true;
        completion.SetResult(UiActionResult<int>.Success(1));
        PumpCompletion(owner);

        Assert.Equal(1, effects);
        Assert.Equal(UiActionOutcome.Failure, queued.Result!.Outcome);
        Assert.Same(error, queued.Result.Error);
        Assert.Equal(2, results.Count);
        Assert.Same(queued.Result, results[1]);
        Assert.Same(queued.Result, execution.LastResult);
        Assert.Equal(UiActionState.Failed, execution.State);
        Assert.Equal("UIA005", execution.Availability.Reason!.Code);
    }

    [Fact]
    public void CancellationCallbackFailureIsObservedAndDoesNotPreventRetirement()
    {
        var completion = Completion();
        var error = new InvalidOperationException("cancellation callback");
        var owner = Dispatcher();
        var execution = owner.Bind(Action((_, token) =>
        { token.Register(() => throw error); return new(completion.Task); }, UiActionConcurrency.Queue(1)));
        var one = execution.Invoke(1);
        var two = execution.Invoke(2);
        owner.Dispose();
        Assert.Contains(error, Assert.IsType<AggregateException>(execution.LastObserverError).InnerExceptions);
        Assert.Equal(UiActionCancellationReason.OwnerRetired, one.Result!.Cancellation);
        Assert.Equal(UiActionCancellationReason.OwnerRetired, two.Result!.Cancellation);
        Assert.Equal(0, owner.Count);
        completion.SetException(new OperationCanceledException());
        Assert.False(owner.Pump());
    }

    [Fact]
    public void WrongThreadAndDuplicateRegistrationHaveNoEffects()
    {
        using var owner = Dispatcher();
        int effects = 0;
        var action = Action((_, _) => { effects++; return new(UiActionResult<int>.Success(1)); });
        var execution = owner.Bind(action);
        OnWorker(() =>
        {
            Assert.Throws<InvalidOperationException>(() => execution.Invoke(1));
            Assert.Throws<InvalidOperationException>(() => owner.Pump());
            Assert.Throws<InvalidOperationException>(() => execution.Cancel());
            Assert.Throws<InvalidOperationException>(() => owner.Dispose());
            Assert.Throws<InvalidOperationException>(() => owner.Bind(action));
        });
        Assert.Throws<ArgumentException>(() => owner.Bind(action));
        Assert.False(owner.IsDisposed);
        Assert.Equal(1, owner.Count);
        Assert.Equal(0, effects);
    }

    [Fact]
    public void QueueCapacityAndResultFactoriesEnforceValidContracts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UiActionConcurrency.Queue(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => UiActionConcurrency.Queue(129));
        Assert.Equal(128, UiActionConcurrency.Queue(128).Capacity);
        Assert.Throws<ArgumentException>(() => new UiActionRejection("", "Reason"));
        Assert.Throws<ArgumentException>(() => new UiActionRejection("code", ""));
        Assert.Throws<ArgumentException>(() => new UiActionRejection("code", "Reason", default(UiSymbolId)));
        Assert.Throws<ArgumentNullException>(() => UiActionAvailability.Disabled(null!));
        Assert.Throws<ArgumentNullException>(() => UiActionResult<int>.Failure(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => UiActionResult<int>.Cancelled((UiActionCancellationReason)99));
        Assert.Throws<InvalidOperationException>(() => UiActionResult<int>.Cancelled().Value);
    }

    [Fact]
    public void CancellationCallbackCanJoinWorkerCompletionWithoutHoldingMailboxLock()
    {
        var completion = Completion();
        var operation = new UiActionOperation<int>();
        operation.Start(Action((_, token) =>
        {
            token.Register(() => OnWorker(() =>
            {
                completion.SetResult(UiActionResult<int>.Success(8));
                Assert.True(SpinWait.SpinUntil(() => operation.Result is not null, TimeSpan.FromSeconds(5)));
            }));
            return new(completion.Task);
        }), 1);

        Assert.Null(operation.Cancel());
        Assert.Equal(8, operation.Result!.Value);
    }

    [Fact]
    public void RetiredPendingOperationDoesNotRetainHostOrCompletionSubscriber()
    {
        var completion = Completion();
        (WeakReference owner, WeakReference subscriber) = RetiredReferences(completion);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(owner.IsAlive);
        Assert.False(subscriber.IsAlive);
        completion.SetException(new InvalidOperationException("late detached failure"));
        GC.KeepAlive(completion);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (WeakReference, WeakReference) RetiredReferences(TaskCompletionSource<UiActionResult<int>> completion)
    {
        var owner = Dispatcher();
        var subscriber = new List<int>();
        var execution = owner.Bind(Action((_, _) => new(completion.Task)), (_, result) => subscriber.Add(result.Value));
        execution.Invoke(1);
        owner.Dispose();
        return (new WeakReference(owner), new WeakReference(subscriber));
    }

    private static void PumpCompletion(UiActionDispatcher owner)
        => Assert.True(SpinWait.SpinUntil(owner.Pump, TimeSpan.FromSeconds(10)), "No completion reached the owning-thread dispatcher.");

    [Fact]
    public void AvailabilityFailureDoesNotLeakIntoLaterDisabledOrSuccessfulAdmission()
    {
        using var owner = Dispatcher();
        var error = new InvalidOperationException("availability");
        var reason = new UiActionRejection("domain.disabled", "Disabled by domain.");
        int mode = 0;
        int effects = 0;
        var execution = owner.Bind(Action((_, _) => { effects++; return new(UiActionResult<int>.Success(7)); },
            availability: () => mode switch
            {
                0 => throw error,
                1 => UiActionAvailability.Disabled(reason),
                _ => UiActionAvailability.Available
            }));
        Assert.Same(error, execution.Invoke(1).Result!.Error);
        mode = 1;
        var disabled = execution.Invoke(2);
        Assert.False(disabled.Accepted);
        Assert.Equal(UiActionOutcome.Rejected, disabled.Result!.Outcome);
        Assert.Same(reason, disabled.Result.Rejection);
        Assert.Null(disabled.Result.Error);
        Assert.Equal(UiActionState.Disabled, execution.State);
        mode = 2;
        Assert.Equal(7, execution.Invoke(3).Result!.Value);
        Assert.Equal(1, effects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValueTaskSourceInspectionFailureCompletesAndAllowsNextInvocation(bool resultFailure)
    {
        using var owner = Dispatcher();
        var error = new InvalidOperationException("value task source");
        var source = new FailingValueTaskSource(error, resultFailure);
        int calls = 0;
        var execution = owner.Bind(Action((_, _) => ++calls == 1
            ? new ValueTask<UiActionResult<int>>(source, 0) : new(UiActionResult<int>.Success(9))));
        var failed = execution.Invoke(1);
        Assert.True(failed.Accepted);
        Assert.Equal(UiActionOutcome.Failure, failed.Result!.Outcome);
        Assert.Same(error, failed.Result.Error);
        Assert.Equal(UiActionState.Failed, execution.State);
        Assert.Equal(9, execution.Invoke(2).Result!.Value);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ValueTaskRegistrationAndLateDuplicateCallbacksHaveOneTerminalDelivery(int mode)
    {
        using var owner = Dispatcher();
        var registration = new InvalidOperationException("registration");
        var late = new InvalidOperationException("late result");
        var source = new CallbackValueTaskSource(mode, registration);
        var results = new List<UiActionResult<int>>();
        int effects = 0;
        var execution = owner.Bind(Action((_, _) => ++effects == 1
            ? new ValueTask<UiActionResult<int>>(source, 0) : new(UiActionResult<int>.Success(99))),
            (_, result) => results.Add(result));
        var submission = execution.Invoke(1);
        if (mode == 0) Assert.Null(submission.Result);
        else if (mode < 3) Assert.Same(registration, submission.Result!.Error);
        else Assert.Equal(12, submission.Result!.Value);

        source.Error = late;
        OnWorker(() => { source.Fire(); source.Fire(); });
        if (mode == 0) PumpCompletion(owner);
        else Assert.False(owner.Pump());

        Assert.Equal(1, source.StatusReads);
        Assert.Equal(mode == 1 ? 0 : 1, source.ResultReads);
        Assert.Single(results);
        Assert.Same(submission.Result, results[0]);
        if (mode == 0) Assert.Same(late, submission.Result!.Error);
        else if (mode < 3) Assert.Same(registration, submission.Result!.Error);
        else Assert.Equal(12, submission.Result!.Value);
        Assert.Equal(99, execution.Invoke(2).Result!.Value);
        Assert.Equal(2, effects);
    }

    private sealed class CallbackValueTaskSource : System.Threading.Tasks.Sources.IValueTaskSource<UiActionResult<int>>
    {
        private readonly int _mode;
        private readonly Exception _registration;
        private Action<object?>? _callback;
        private object? _state;
        internal CallbackValueTaskSource(int mode, Exception registration) { _mode = mode; _registration = registration; }
        internal Exception? Error { get; set; }
        internal int StatusReads { get; private set; }
        internal int ResultReads { get; private set; }
        internal void Fire() => _callback?.Invoke(_state);
        public System.Threading.Tasks.Sources.ValueTaskSourceStatus GetStatus(short token)
        {
            if (++StatusReads != 1) throw new InvalidOperationException("The observer must not re-read late status.");
            return System.Threading.Tasks.Sources.ValueTaskSourceStatus.Pending;
        }
        public UiActionResult<int> GetResult(short token)
        {
            ResultReads++;
            if (Error is not null) throw Error;
            return UiActionResult<int>.Success(12);
        }
        public void OnCompleted(Action<object?> continuation, object? state, short token,
            System.Threading.Tasks.Sources.ValueTaskSourceOnCompletedFlags flags)
        {
            Assert.Equal(System.Threading.Tasks.Sources.ValueTaskSourceOnCompletedFlags.None, flags);
            if (_mode == 1) throw _registration;
            _callback = continuation; _state = state;
            if (_mode >= 3) Fire();
            if (_mode is 2 or 4) throw _registration;
        }
    }

    private sealed class FailingValueTaskSource : System.Threading.Tasks.Sources.IValueTaskSource<UiActionResult<int>>
    {
        private readonly Exception _error;
        private readonly bool _resultFailure;
        internal FailingValueTaskSource(Exception error, bool resultFailure) { _error = error; _resultFailure = resultFailure; }
        public System.Threading.Tasks.Sources.ValueTaskSourceStatus GetStatus(short token)
            => _resultFailure ? System.Threading.Tasks.Sources.ValueTaskSourceStatus.Succeeded : throw _error;
        public UiActionResult<int> GetResult(short token) => throw _error;
        public void OnCompleted(Action<object?> continuation, object? state, short token,
            System.Threading.Tasks.Sources.ValueTaskSourceOnCompletedFlags flags)
            => throw new InvalidOperationException("This completed fixture must not subscribe.");
    }

    private static UiAction<int, int> Action(Func<int, CancellationToken, ValueTask<UiActionResult<int>>> execute,
        UiActionConcurrency? concurrency = null, Func<UiActionAvailability>? availability = null, UiSymbolId? id = null)
        => new(id ?? Id("action"), "Action", execute, concurrency ?? UiActionConcurrency.RejectWhileRunning, availability);
    private static UiActionDispatcher Dispatcher() => new(Guid.NewGuid(), Guid.NewGuid());
    private static UiSymbolId Id(string path) => new("Tests", "actions/" + path);
    private static TaskCompletionSource<UiActionResult<int>> Completion() => new();
    private static void OnWorker(System.Action action)
    {
        Exception? error = null;
        var worker = new Thread(() => { try { action(); } catch (Exception failure) { error = failure; } });
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "Worker did not complete.");
        Assert.Null(error);
    }
}
