using System;

namespace Hatifect.UI.Experience;

/// <summary>Semantic state rendered by the foundation Status component.</summary>
public enum UiStatusKind
{
    Status,
    Empty,
    Loading,
    Success,
    Error
}

/// <summary>
/// Immutable status content. The kind carries visual and accessibility meaning while the message
/// remains consumer-owned and localizable.
/// </summary>
public sealed record UiStatus
{
    public UiStatus(UiStatusKind kind, string message)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("A status message is required.", nameof(message));
        Kind = kind;
        Message = message;
    }

    public UiStatusKind Kind { get; }
    public string Message { get; }

    public override string ToString() => Message;
}
