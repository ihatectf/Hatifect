using System;

namespace Hatifect.UI.Experience;

public sealed class UiActionDefinition
{
    private readonly Action _execute;
    private readonly Func<bool> _canExecute;

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
    public bool CanExecute => _canExecute();

    public bool TryExecute() => TryExecute(null);

    // Host input must recheck ownership after consumer availability, before the domain effect.
    // A direct legacy invocation has no host owner and retains its existing behavior.
    internal bool TryExecute(Func<long>? captureOwnerVersion)
    {
        long? ownerVersion = captureOwnerVersion?.Invoke();
        bool available = CanExecute;
        if (captureOwnerVersion?.Invoke() != ownerVersion)
            throw new InvalidOperationException("The action's host scene changed while availability was being checked.");
        if (!available) return false;
        _execute();
        return true;
    }
}
