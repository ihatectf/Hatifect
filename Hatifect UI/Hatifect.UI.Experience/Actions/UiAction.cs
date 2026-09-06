namespace Hatifect.UI.Experience;

/// <summary>Immutable action description. Requests and successful values must be immutable consumer
/// snapshots. Execution state belongs to a host session, never to this reusable description.</summary>
public sealed class UiAction<TRequest, TResult>
{
    private readonly Func<TRequest, CancellationToken, ValueTask<UiActionResult<TResult>>> _execute;
    private readonly Func<UiActionAvailability> _availability;

    public UiAction(UiSymbolId id, string title,
        Func<TRequest, CancellationToken, ValueTask<UiActionResult<TResult>>> execute,
        UiActionConcurrency concurrency, Func<UiActionAvailability>? availability = null)
    {
        if (!id.IsValid) throw new ArgumentException("A stable action ID is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("An action title is required.", nameof(title));
        Id = id;
        Title = title;
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        Concurrency = concurrency ?? throw new ArgumentNullException(nameof(concurrency));
        _availability = availability ?? (() => UiActionAvailability.Available);
    }

    public UiSymbolId Id { get; }
    public string Title { get; }
    public UiActionConcurrency Concurrency { get; }
    internal UiActionAvailability ReadAvailability() => _availability()
        ?? throw new InvalidOperationException("An action must return an availability result.");
    internal ValueTask<UiActionResult<TResult>> Execute(TRequest request, CancellationToken cancellation)
        => _execute(request, cancellation);
}

public enum UiActionState { Available, Disabled, Running, Completed, Rejected, Failed, Cancelled }
public enum UiActionConcurrencyKind { RejectWhileRunning, RestartLatest, Queue }

/// <summary>RestartLatest cancels the active operation and retains only the latest waiting request.
/// It starts that request after the active operation actually finishes, so an executor that ignores
/// cancellation can delay the restart but cannot create unbounded concurrent work.</summary>
public sealed class UiActionConcurrency
{
    public const int MaximumQueueCapacity = 128;
    private UiActionConcurrency(UiActionConcurrencyKind kind, int capacity) { Kind = kind; Capacity = capacity; }
    public UiActionConcurrencyKind Kind { get; }
    /// <summary>Waiting requests, excluding the one active operation.</summary>
    public int Capacity { get; }
    public static UiActionConcurrency RejectWhileRunning { get; } = new(UiActionConcurrencyKind.RejectWhileRunning, 0);
    public static UiActionConcurrency RestartLatest { get; } = new(UiActionConcurrencyKind.RestartLatest, 1);
    public static UiActionConcurrency Queue(int capacity)
    {
        if (capacity < 1 || capacity > MaximumQueueCapacity) throw new ArgumentOutOfRangeException(nameof(capacity));
        return new(UiActionConcurrencyKind.Queue, capacity);
    }
}
