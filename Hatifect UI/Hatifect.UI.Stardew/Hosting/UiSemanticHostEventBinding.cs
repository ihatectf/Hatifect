using StardewModdingAPI.Events;

namespace Hatifect.UI.Stardew;

/// <summary>Screen-owned lifecycle for standalone, Terminal and HUD session coordination.</summary>
internal sealed class UiSemanticHostEventBinding : IDisposable
{
    private readonly IModEvents _events;
    private readonly int _owner;
    private readonly Func<int> _currentScreen;
    private readonly EventHandler<UpdateTickedEventArgs> _update;
    private readonly EventHandler<ReturnedToTitleEventArgs> _title;
    private readonly EventHandler<MenuChangedEventArgs> _menu;
    private int _attached;
    private bool _active;

    internal UiSemanticHostEventBinding(IModEvents events, int owner, Func<int> currentScreen,
        EventHandler<UpdateTickedEventArgs> update, EventHandler<ReturnedToTitleEventArgs> title,
        EventHandler<MenuChangedEventArgs> menu)
    {
        _events = events;
        _owner = owner;
        _currentScreen = currentScreen;
        _update = (sender, args) => { if (_active && IsOwner) update(sender, args); };
        _title = (sender, args) => { if (_active && IsOwner) title(sender, args); };
        _menu = (sender, args) => { if (_active && IsOwner) menu(sender, args); };
    }

    internal bool IsOwner => _currentScreen() == _owner;
    internal void RequireOwner()
    {
        if (!IsOwner) throw new InvalidOperationException("A semantic surface belongs to its creating screen.");
    }

    internal void Activate()
    {
        RequireOwner();
        if (_active) return;
        if (_attached != 0) throw new InvalidOperationException("Complete event cleanup before activation.");
        try
        {
            _attached |= 1;
            _events.GameLoop.UpdateTicked += _update;
            _attached |= 2;
            _events.GameLoop.ReturnedToTitle += _title;
            _attached |= 4;
            _events.Display.MenuChanged += _menu;
            _active = true;
        }
        catch (Exception activation)
        {
            try { Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(activation, cleanup); }
            throw;
        }
    }

    public void Dispose()
    {
        _active = false;
        List<Exception>? failures = null;
        Detach(1, () => _events.GameLoop.UpdateTicked -= _update);
        Detach(2, () => _events.GameLoop.ReturnedToTitle -= _title);
        Detach(4, () => _events.Display.MenuChanged -= _menu);
        if (failures is not null) throw new AggregateException(failures);

        void Detach(int bit, Action remove)
        {
            if ((_attached & bit) == 0) return;
            try { remove(); _attached &= ~bit; }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
    }
}
