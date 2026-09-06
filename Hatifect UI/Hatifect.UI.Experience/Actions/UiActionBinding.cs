namespace Hatifect.UI.Experience;

// Generic double dispatch preserves request/result types without a Runtime dependency,
// reflection, dynamic invocation or boxed request payloads in the reusable description.
internal interface IUiActionBindingFactory<TBinding>
{
    TBinding Create<TRequest, TResult>(UiAction<TRequest, TResult> action, Func<TRequest> capture,
        Action<TRequest, UiActionResult<TResult>>? completed, UiPublication? owner);
}

internal interface IUiActionBindingDescription
{
    TBinding Create<TBinding>(IUiActionBindingFactory<TBinding> factory);
}

internal sealed record UiTypedActionBinding<TRequest, TResult>(UiAction<TRequest, TResult> Action,
    Func<TRequest> Capture, Action<TRequest, UiActionResult<TResult>>? Completed, UiPublication? Owner)
    : IUiActionBindingDescription
{
    public TBinding Create<TBinding>(IUiActionBindingFactory<TBinding> factory)
        => factory.Create(Action, Capture, Completed, Owner);
}
