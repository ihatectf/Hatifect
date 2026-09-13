using System;

namespace Hatifect.UI.Experience;

public sealed class UiActionDefinition
{
    private readonly Action? _execute;
    private readonly Func<bool>? _canExecute;
    internal IUiActionBindingDescription? Binding { get; }

    internal UiActionDefinition(UiSymbolId id, string title, IUiActionBindingDescription binding)
    { Id = id; Title = title; Binding = binding; }

    public UiActionDefinition(UiSymbolId id, string title, Action execute, Func<bool>? canExecute = null)
    {
        if (!id.IsValid) throw new ArgumentException("A stable action ID is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("An action title is required.", nameof(title));
        Id = id;
        Title = title;
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute ?? (() => true);
    }

    public UiSymbolId Id { get; }
    public string Title { get; }
    // A typed definition needs a host to resolve availability and concurrency. Its reusable
    // description alone cannot promise that any particular host will admit input.
    public bool CanExecute => _canExecute?.Invoke() ?? false;

    public bool TryExecute() => TryExecute(null);

    // Host input must recheck ownership after consumer availability, before the domain effect.
    // A direct legacy invocation has no host owner and retains its existing behavior.
    internal bool TryExecute(Func<long>? ensureOwnerActive)
    {
        if (Binding is not null)
            throw new InvalidOperationException("A typed action must be invoked through its owning UI host.");
        long? version = ensureOwnerActive?.Invoke();
        bool available = CanExecute;
        if (ensureOwnerActive?.Invoke() != version)
            throw new InvalidOperationException("The accepted UI frame changed during action admission.");
        if (!available) return false;
        _execute!();
        return true;
    }

    // Runtime already checked availability and lifetime under its admission fence.
    internal void ExecuteAdmitted() => _execute!();
}
