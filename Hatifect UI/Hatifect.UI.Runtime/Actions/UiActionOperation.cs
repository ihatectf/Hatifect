using System.Runtime.CompilerServices;
using Hatifect.UI.Experience;

namespace Hatifect.UI.Runtime.Actions;

/// <summary>The asynchronous observer retains no dispatcher, host, subscription or completion callback.
/// Even after retirement it observes faults and releases its cancellation source.</summary>
internal sealed class UiActionOperation<TResult>
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation = new();
    private UiActionResult<TResult>? _result;
    private bool _cancelling;
    private bool _finished;
    internal UiActionResult<TResult>? Result => Volatile.Read(ref _result);

    internal void Start<TRequest>(UiAction<TRequest, TResult> action, TRequest request)
    {
        CancellationToken token = _cancellation!.Token;
        try
        {
            var awaiter = action.Execute(request, token).ConfigureAwait(false).GetAwaiter();
            if (awaiter.IsCompleted) Complete(awaiter.GetResult());
            else
            {
                var observer = new CompletionObserver(this, awaiter, token);
                awaiter.UnsafeOnCompleted(observer.Observe);
            }
        }
        catch (Exception error) { Complete(Failure(error, token)); }
    }

    internal Exception? Cancel()
    {
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_cancellation is null || _cancellation.IsCancellationRequested) return null;
            cancellation = _cancellation;
            _cancelling = true;
        }
        // Consumer cancellation callbacks must not run under the mailbox lock. A callback can wait
        // for its worker to finish, and that worker must be able to publish the observed completion.
        try { cancellation.Cancel(); return null; }
        catch (Exception error) { return error; }
        finally
        {
            lock (_gate)
            {
                _cancelling = false;
                if (_finished) { _cancellation?.Dispose(); _cancellation = null; }
            }
        }
    }

    private void Complete(UiActionResult<TResult>? result)
    {
        lock (_gate)
        {
            if (_finished) return;
            _finished = true;
            if (!_cancelling) { _cancellation?.Dispose(); _cancellation = null; }
        }
        Volatile.Write(ref _result, result ?? UiActionResult<TResult>.Failure(
            new InvalidOperationException("An action must return a result.")));
    }

    private static UiActionResult<TResult> Failure(Exception error, CancellationToken token)
        => error is OperationCanceledException cancelled && token.IsCancellationRequested && cancelled.CancellationToken == token
            ? UiActionResult<TResult>.Cancelled() : UiActionResult<TResult>.Failure(error);

    // Register directly: the async method builder can rethrow registration failures asynchronously,
    // while ValueTask.AsTask also reads a custom source status outside its late exception boundary.
    private sealed class CompletionObserver
    {
        private readonly UiActionOperation<TResult> _operation;
        private readonly ConfiguredValueTaskAwaitable<UiActionResult<TResult>>.ConfiguredValueTaskAwaiter _awaiter;
        private readonly CancellationToken _token;
        private int _observed;
        internal CompletionObserver(UiActionOperation<TResult> operation,
            ConfiguredValueTaskAwaitable<UiActionResult<TResult>>.ConfiguredValueTaskAwaiter awaiter, CancellationToken token)
        { _operation = operation; _awaiter = awaiter; _token = token; }

        internal void Observe()
        {
            if (Interlocked.Exchange(ref _observed, 1) != 0) return;
            UiActionResult<TResult> result;
            try { result = _awaiter.GetResult(); }
            catch (Exception error) { result = Failure(error, _token); }
            // A source may register this callback and then throw. Observe its eventual fault once,
            // but preserve the already published registration failure (or inline completion).
            _operation.Complete(result);
        }
    }
}
