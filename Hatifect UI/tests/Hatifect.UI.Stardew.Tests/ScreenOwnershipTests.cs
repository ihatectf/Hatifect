using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI.Events;
using StardewValley;
using Hatifect.UI.Stardew.Semantic;
using Xunit;

namespace Hatifect.UI.Stardew.Tests;

public sealed class ScreenOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignEventsCannotEnterSurfaceAndOwnerEventsRemainSubscribed(bool hud)
    {
        var events = new EventSet();
        int screen = 3;
        var calls = new List<string>();
        object sender = new();
        using var binding = Bind(events, () => screen, hud, (name, source) =>
        {
            Assert.Same(sender, source);
            calls.Add(name);
        });
        binding.Activate();
        binding.Activate();
        Assert.Equal(7, events.Count);
        Assert.Equal(hud ? 1 : 0, events.DisplayHub.Count("RenderedHud"));
        Assert.Equal(hud ? 0 : 1, events.DisplayHub.Count("RenderedActiveMenu"));
        screen = 4;
        events.RaiseAll(sender);
        Assert.Empty(calls);
        Assert.Equal(7, events.Count);
        screen = 3;
        events.RaiseAll(sender);
        Assert.Equal(new[] { "update", "title", "pressed", "released", "wheel", "resize", hud ? "hud" : "menu" }, calls);
        binding.Dispose();
        Assert.Equal(0, events.Count);
        events.RaiseAll(sender);
        Assert.Equal(7, calls.Count);
    }

    [Fact]
    public void OnlyOwnerTitleClosesAndUnsubscribesTheSurface()
    {
        var events = new EventSet();
        int screen = 3;
        UiSemanticOverlayEventBinding? binding = null;
        int closed = 0;
        binding = Bind(events, () => screen, false, (name, _) =>
        {
            if (name != "title") return;
            closed++;
            binding!.Dispose();
        });
        binding.Activate();
        screen = 4;
        events.LoopHub.Raise("ReturnedToTitle", this);
        Assert.Equal(0, closed);
        Assert.Equal(7, events.Count);
        screen = 3;
        events.LoopHub.Raise("ReturnedToTitle", this);
        Assert.Equal(1, closed);
        Assert.Equal(0, events.Count);
        events.RaiseAll(this);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void FailedSubscriptionAndUnsubscriptionRetainRetryableCleanupWithoutDispatch()
    {
        var events = new EventSet();
        int calls = 0;
        using var binding = Bind(events, () => 3, false, (_, _) => calls++);
        events.InputHub.FailAdd = "ButtonReleased";
        Assert.Throws<InvalidOperationException>(() => binding.Activate());
        Assert.Equal(0, events.Count);
        events.RaiseAll(this);
        Assert.Equal(0, calls);
        events.InputHub.FailAdd = null;
        binding.Activate();
        events.InputHub.FailRemove = "ButtonPressed";
        Assert.Throws<AggregateException>(() => binding.Dispose());
        Assert.Equal(1, events.Count);
        events.RaiseAll(this);
        Assert.Equal(0, calls);
        Assert.Throws<InvalidOperationException>(() => binding.Activate());
        events.InputHub.FailRemove = null;
        binding.Dispose();
        Assert.Equal(0, events.Count);
        binding.Activate();
        events.RaiseAll(this);
        Assert.Equal(7, calls);
    }

    [Fact]
    public void KeyboardCleanupUsesCapturedDispatcherAndForeignInputCannotReachOwner()
    {
        int screen = 3;
        var a = new KeyboardSlot();
        var b = new KeyboardSlot();
        IKeyboardSubscriber previous = a.Value!;
        IKeyboardSubscriber other = b.Value!;
        var received = new List<string>();
        var lease = new UiSemanticKeyboardSubscriberLease(
            received.Add, key => received.Add(key.ToString()),
            () => (screen == 3 ? a : b).Capture(), () => screen == 3);
        lease.Acquire();
        lease.Acquire();
        Assert.True(lease.OwnsSubscriber);
        lease.RecieveTextInput('a');
        lease.RecieveTextInput("bc");
        lease.RecieveSpecialInput(Keys.Enter);
        Assert.Equal(new[] { "a", "bc", "Enter" }, received);
        screen = 4;
        lease.RecieveTextInput('x');
        lease.RecieveTextInput("foreign");
        lease.RecieveSpecialInput(Keys.Escape);
        Assert.Equal(3, received.Count);
        Assert.Throws<InvalidOperationException>(() => lease.Acquire());
        lease.Dispose();
        Assert.Same(previous, a.Value);
        Assert.Same(other, b.Value);
        Assert.False(lease.OwnsSubscriber);
    }

    [Fact]
    public void LostNativeMenuCannotRegainKeyboardFocusEvenWhenReleaseNeedsRetry()
    {
        var slot = new KeyboardSlot();
        using var lease = new UiSemanticKeyboardSubscriberLease(_ => { }, _ => { }, slot.Capture, () => true);
        lease.Acquire();
        slot.FailWrite = true;
        Assert.Throws<InvalidOperationException>(() => lease.Release(restorePrevious: false));
        Assert.Same(lease, slot.Value);
        slot.FailWrite = false;
        lease.Dispose();
        Assert.Null(slot.Value);
        Assert.False(lease.OwnsSubscriber);
    }

    [Fact]
    public void KeyboardLeaseDoesNotOverwriteReplacementAndRetainsFailedRelease()
    {
        var slot = new KeyboardSlot();
        IKeyboardSubscriber previous = slot.Value!;
        using var lease = new UiSemanticKeyboardSubscriberLease(_ => { }, _ => { }, slot.Capture, () => true);
        lease.Acquire();
        slot.FailWrite = true;
        Assert.Throws<InvalidOperationException>(() => lease.Release());
        Assert.True(lease.OwnsSubscriber);
        slot.FailWrite = false;
        lease.Release();
        Assert.Same(previous, slot.Value);
        lease.Acquire();
        IKeyboardSubscriber replacement = new Subscriber();
        slot.Value = replacement;
        lease.Release();
        Assert.Same(replacement, slot.Value);
        Assert.False(lease.OwnsSubscriber);
    }

    private static UiSemanticOverlayEventBinding Bind(EventSet e, Func<int> screen, bool hud, Action<string, object?> callback)
        => new(e.Events, 3, screen,
            hud ? UiSemanticStardewOverlayRenderLayer.Hud : UiSemanticStardewOverlayRenderLayer.ActiveMenu,
            (s, _) => callback("update", s), (s, _) => callback("title", s),
            (s, _) => callback("pressed", s), (s, _) => callback("released", s),
            (s, _) => callback("wheel", s), (s, _) => callback("resize", s),
            (s, _) => callback("hud", s), (s, _) => callback("menu", s));

    private sealed class EventSet
    {
        internal IModEvents Events { get; } = DispatchProxy.Create<IModEvents, EventHub>();
        internal EventHub LoopHub { get; }
        internal EventHub InputHub { get; }
        internal EventHub DisplayHub { get; }
        internal int Count => LoopHub.Total + InputHub.Total + DisplayHub.Total;
        internal EventSet()
        {
            var loop = DispatchProxy.Create<IGameLoopEvents, EventHub>();
            var input = DispatchProxy.Create<IInputEvents, EventHub>();
            var display = DispatchProxy.Create<IDisplayEvents, EventHub>();
            ((EventHub)Events).Groups.Add("GameLoop", loop);
            ((EventHub)Events).Groups.Add("Input", input);
            ((EventHub)Events).Groups.Add("Display", display);
            LoopHub = (EventHub)loop;
            InputHub = (EventHub)input;
            DisplayHub = (EventHub)display;
        }
        internal void RaiseAll(object sender)
        {
            LoopHub.Raise("UpdateTicked", sender);
            LoopHub.Raise("ReturnedToTitle", sender);
            InputHub.Raise("ButtonPressed", sender);
            InputHub.Raise("ButtonReleased", sender);
            InputHub.Raise("MouseWheelScrolled", sender);
            DisplayHub.Raise("WindowResized", sender);
            DisplayHub.Raise("RenderedHud", sender);
            DisplayHub.Raise("RenderedActiveMenu", sender);
        }
    }

    // A real event subscription fake, with failures before removal and after addition.
    // Payloads are opaque here; the screen boundary forwards the original sender/args unchanged.
    public class EventHub : DispatchProxy
    {
        private readonly Dictionary<string, Delegate?> _handlers = new();
        internal Dictionary<string, object> Groups { get; } = new();
        internal string? FailAdd { get; set; }
        internal string? FailRemove { get; set; }
        internal int Total => _handlers.Values.Sum(h => h?.GetInvocationList().Length ?? 0);
        internal int Count(string name) => _handlers.GetValueOrDefault(name)?.GetInvocationList().Length ?? 0;
        internal void Raise(string name, object sender) => _handlers.GetValueOrDefault(name)?.DynamicInvoke(sender, null);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            string name = method!.Name;
            if (name.StartsWith("get_", StringComparison.Ordinal)) return Groups[name[4..]];
            bool adding = name.StartsWith("add_", StringComparison.Ordinal);
            string key = name[(adding ? 4 : 7)..];
            if (!adding && key == FailRemove) throw new InvalidOperationException("remove failed");
            _handlers[key] = adding
                ? Delegate.Combine(_handlers.GetValueOrDefault(key), (Delegate)args![0]!)
                : Delegate.Remove(_handlers.GetValueOrDefault(key), (Delegate)args![0]!);
            if (adding && key == FailAdd) throw new InvalidOperationException("add failed after subscription");
            return null;
        }
    }

    private sealed class KeyboardSlot
    {
        internal IKeyboardSubscriber? Value = new Subscriber();
        internal bool FailWrite;
        internal (Func<IKeyboardSubscriber?>, Action<IKeyboardSubscriber?>) Capture() => (() => Value, value =>
        {
            if (FailWrite) throw new InvalidOperationException("subscriber write failed");
            if (Value != null) Value.Selected = false;
            Value = value;
            if (Value != null) Value.Selected = true;
        });
    }
    private sealed class Subscriber : IKeyboardSubscriber
    {
        public bool Selected { get; set; }
        public void RecieveTextInput(char inputChar) { }
        public void RecieveTextInput(string text) { }
        public void RecieveCommandInput(char command) { }
        public void RecieveSpecialInput(Keys key) { }
    }
}
