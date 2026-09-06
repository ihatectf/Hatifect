namespace Hatifect.UI.Experience;

public sealed class UiActionRejection
{
    public UiActionRejection(string code, string message, UiSymbolId? field = null)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("A rejection code is required.", nameof(code));
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("A rejection message is required.", nameof(message));
        if (field is { IsValid: false }) throw new ArgumentException("A valid field identity is required.", nameof(field));
        Code = code; Message = message; Field = field;
    }
    public string Code { get; }
    public string Message { get; }
    public UiSymbolId? Field { get; }
}

public sealed class UiActionAvailability
{
    private UiActionAvailability(UiActionRejection? reason) => Reason = reason;
    public bool CanExecute => Reason is null;
    public UiActionRejection? Reason { get; }
    public static UiActionAvailability Available { get; } = new(null);
    public static UiActionAvailability Disabled(UiActionRejection reason)
        => new(reason ?? throw new ArgumentNullException(nameof(reason)));
}

public enum UiActionOutcome { Success, Rejected, Failure, Cancelled }
public enum UiActionCancellationReason { Requested, Superseded, OwnerRetired }

/// <summary>A cancelled UI wait does not roll back a domain effect. An active executor may still
/// finish successfully after cancellation was requested; its typed result remains authoritative.</summary>
public sealed class UiActionResult<T>
{
    private readonly T? _value;
    private UiActionResult(UiActionOutcome outcome, T? value = default, UiActionRejection? rejection = null,
        Exception? error = null, UiActionCancellationReason? cancellation = null)
    { Outcome = outcome; _value = value; Rejection = rejection; Error = error; Cancellation = cancellation; }
    public UiActionOutcome Outcome { get; }
    public T Value => Outcome == UiActionOutcome.Success ? _value! : throw new InvalidOperationException("The action did not succeed.");
    public UiActionRejection? Rejection { get; }
    public Exception? Error { get; }
    public UiActionCancellationReason? Cancellation { get; }
    public static UiActionResult<T> Success(T value) => new(UiActionOutcome.Success, value);
    public static UiActionResult<T> Rejected(UiActionRejection reason)
        => new(UiActionOutcome.Rejected, rejection: reason ?? throw new ArgumentNullException(nameof(reason)));
    public static UiActionResult<T> Failure(Exception error)
        => new(UiActionOutcome.Failure, error: error ?? throw new ArgumentNullException(nameof(error)));
    public static UiActionResult<T> Cancelled(UiActionCancellationReason reason = UiActionCancellationReason.Requested)
    {
        if (!Enum.IsDefined(typeof(UiActionCancellationReason), reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new(UiActionOutcome.Cancelled, cancellation: reason);
    }
}
