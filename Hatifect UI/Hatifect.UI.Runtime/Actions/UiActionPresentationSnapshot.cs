using Hatifect.UI.Experience;

namespace Hatifect.UI.Runtime.Actions;

/// <summary>Callback-free presentation of the accepted action state. A candidate is published
/// together with its render frame and accessibility, never from a worker completion.</summary>
internal sealed class UiActionPresentationSnapshot : IUiActionResolver
{
    private readonly Dictionary<UiActionDefinition, UiHostActionStatus> _states = new();

    internal static UiActionPresentationSnapshot Capture(IEnumerable<UiHostActionBinding> bindings,
        IUiActionResolver resolver, string locale)
    {
        var snapshot = new UiActionPresentationSnapshot();
        foreach (var binding in bindings)
        {
            UiHostActionStatus state = binding.Status;
            bool enabled = resolver.CanInvoke(binding.Definition);
            snapshot._states.Add(binding.Definition, state with
            {
                Enabled = enabled,
                Message = binding.Definition.Binding is null ? null : Resolve(state, locale)
            });
        }
        return snapshot;
    }

    internal void RequireCurrent(UiActionBindingMap map, string locale)
    {
        foreach (var pair in _states)
        {
            UiHostActionStatus? current = map.Status(pair.Key);
            string? message = pair.Key.Binding is null || current is null ? null : Resolve(current, locale);
            if (current is null || pair.Value.Enabled != map.CanInvoke(pair.Key) || pair.Value.Message != message)
                throw new InvalidOperationException("Action ownership or presentation changed during frame preparation.");
        }
    }

    internal bool HasSamePresentation(UiActionPresentationSnapshot other)
    {
        if (_states.Count != other._states.Count) return false;
        foreach (var pair in _states)
            if (!other._states.TryGetValue(pair.Key, out var prior) ||
                prior.Enabled != pair.Value.Enabled || prior.Message != pair.Value.Message)
                return false;
        return true;
    }

    public bool CanInvoke(UiActionDefinition definition) => Status(definition)?.Enabled == true;
    public bool Invoke(UiActionDefinition definition)
        => throw new InvalidOperationException("An accepted presentation cannot dispatch actions.");
    public UiHostActionStatus? Status(UiActionDefinition definition)
        => _states.TryGetValue(definition, out var state) ? state : null;

    private static string? Resolve(UiHostActionStatus state, string locale)
    {
        bool russian = locale is "ru" or "ru-RU";
        return state.State switch
        {
            UiActionState.Running => russian ? "Выполняется…" : "Running…",
            UiActionState.Completed => russian ? "Выполнено." : "Completed.",
            UiActionState.Disabled or UiActionState.Rejected =>
                state.Reason?.LocalizedMessage?.Text.Resolve(locale) ?? state.Reason?.Message ??
                (russian ? "Действие недоступно." : "The action is unavailable."),
            UiActionState.Failed => state.FailureMessage?.Text.Resolve(locale) ??
                (russian ? "Не удалось выполнить действие." : "The action could not be completed."),
            UiActionState.Cancelled => russian ? "Ожидание отменено." : "Waiting cancelled.",
            _ => null
        };
    }
}
