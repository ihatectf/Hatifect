namespace Hatifect.UI.Stardew.Semantic;

/// <summary>Retired menus become inert immediately; only their owning screen can clear its native slot.</summary>
internal sealed class UiSemanticMenuSlotLease
{
    private readonly object _menu;
    private readonly Func<bool> _isOwner;
    private readonly Func<object?> _read;
    private readonly Action _clear;
    private bool _retired;
    private bool _clearing;

    internal UiSemanticMenuSlotLease(object menu, Func<bool> isOwner, Func<object?> read, Action clear)
    {
        _menu = menu;
        _isOwner = isOwner;
        _read = read;
        _clear = clear;
    }

    internal bool CanDispatch => !_retired && _isOwner();

    internal void Retire()
    {
        _retired = true;
        // Game1's menu setter invokes Dispose before assigning its new slot value.
        // Never write that slot recursively from the disposal callback.
    }

    internal void PollRetirement()
    {
        if (!_retired || _clearing || !_isOwner()) return;
        _clearing = true;
        try { if (ReferenceEquals(_read(), _menu)) _clear(); }
        finally { _clearing = false; }
    }
}
