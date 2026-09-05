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

    public bool TryExecute()
    {
        if (!CanExecute) return false;
        _execute();
        return true;
    }
}
