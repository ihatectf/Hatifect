using StardewModdingAPI.Events;

namespace Hatifect.UI.Stardew.Semantic;

/// <summary>Owns the actual SMAPI subscriptions for one screen; foreign events never enter UI code.</summary>
internal sealed class UiSemanticOverlayEventBinding : IDisposable
{
    private readonly List<Subscription> _subscriptions = new();
    private readonly Func<int> _currentScreen;
    private readonly int _screen;
    private bool _enabled;

    internal UiSemanticOverlayEventBinding(
        IModEvents events, int screen, Func<int> currentScreen,
        UiSemanticStardewOverlayRenderLayer layer,
        EventHandler<UpdateTickedEventArgs> update,
        EventHandler<ReturnedToTitleEventArgs> title,
        EventHandler<ButtonPressedEventArgs> pressed,
        EventHandler<ButtonReleasedEventArgs> released,
        EventHandler<MouseWheelScrolledEventArgs> wheel,
        EventHandler<WindowResizedEventArgs> resize,
        EventHandler<RenderedHudEventArgs> hud,
        EventHandler<RenderedActiveMenuEventArgs> menu)
    {
        ArgumentNullException.ThrowIfNull(events);
        _screen = screen;
        _currentScreen = currentScreen ?? throw new ArgumentNullException(nameof(currentScreen));
        if (!Enum.IsDefined(typeof(UiSemanticStardewOverlayRenderLayer), layer))
            throw new ArgumentOutOfRangeException(nameof(layer));
        Bind(update, h => events.GameLoop.UpdateTicked += h, h => events.GameLoop.UpdateTicked -= h);
        Bind(title, h => events.GameLoop.ReturnedToTitle += h, h => events.GameLoop.ReturnedToTitle -= h);
        Bind(pressed, h => events.Input.ButtonPressed += h, h => events.Input.ButtonPressed -= h);
        Bind(released, h => events.Input.ButtonReleased += h, h => events.Input.ButtonReleased -= h);
        Bind(wheel, h => events.Input.MouseWheelScrolled += h, h => events.Input.MouseWheelScrolled -= h);
        Bind(resize, h => events.Display.WindowResized += h, h => events.Display.WindowResized -= h);
        if (layer == UiSemanticStardewOverlayRenderLayer.Hud)
            Bind(hud, h => events.Display.RenderedHud += h, h => events.Display.RenderedHud -= h);
        else
            Bind(menu, h => events.Display.RenderedActiveMenu += h, h => events.Display.RenderedActiveMenu -= h);
    }

    internal bool IsCurrentScreen => _currentScreen() == _screen;

    internal void Activate()
    {
        if (_enabled) return;
        if (_subscriptions.Any(s => s.Attached))
            throw new InvalidOperationException("The previous UI event subscriptions still require cleanup.");
        _enabled = true;
        try
        {
            foreach (Subscription subscription in _subscriptions) subscription.Attach();
        }
        catch (Exception activationFailure)
        {
            try { Dispose(); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("UI event activation and cleanup failed.", activationFailure, cleanupFailure);
            }
            throw;
        }
    }

    public void Dispose()
    {
        _enabled = false;
        List<Exception>? failures = null;
        foreach (Subscription subscription in _subscriptions)
        {
            try { subscription.Detach(); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        if (failures != null)
            throw new AggregateException("Semantic Stardew overlay event teardown failed.", failures);
    }

    private void Bind<T>(EventHandler<T> callback, Action<EventHandler<T>> add, Action<EventHandler<T>> remove)
    {
        EventHandler<T> handler = (sender, args) =>
        {
            if (_enabled && IsCurrentScreen) callback(sender, args);
        };
        _subscriptions.Add(new Subscription(() => add(handler), () => remove(handler)));
    }

    private sealed class Subscription
    {
        private readonly Action _add;
        private readonly Action _remove;
        internal bool Attached { get; private set; }
        internal Subscription(Action add, Action remove) { _add = add; _remove = remove; }
        internal void Attach()
        {
            // Retain ownership if an event accessor throws after attaching the delegate.
            Attached = true;
            _add();
        }
        internal void Detach()
        {
            if (!Attached) return;
            _remove();
            Attached = false;
        }
    }
}
