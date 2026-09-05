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
    [Fact]
    public void NativeSlotSetterCanDisposeBeforeAssigningReplacementWithoutRecursiveWrites()
    {
        object menu = new();
        object replacement = new();
        object? slot = menu;
        int writes = 0;
        var lease = new UiSemanticMenuSlotLease(menu, () => true, () => slot,
            () => { writes++; throw new InvalidOperationException("Dispose must not write a native setter's slot."); });
        // Match Game1.set_activeClickableMenu: old.Dispose(), then store replacement.
        lease.Retire();
        Assert.False(lease.CanDispatch);
        Assert.Same(menu, slot);
        slot = replacement;
        lease.PollRetirement();
        Assert.Equal(0, writes);
        Assert.Same(replacement, slot);
    }

    [Fact]
    public void DeferredRetirementToleratesNativeSetterReentryAndRetainsFailedClearForRetry()
    {
        object menu = new();
        object? slot = menu;
        int writes = 0;
        bool fail = true;
        UiSemanticMenuSlotLease? lease = null;
        lease = new UiSemanticMenuSlotLease(menu, () => true, () => slot, () =>
        {
            writes++;
            if (writes > 2) throw new InvalidOperationException("Unexpected recursive slot write.");
            lease!.Retire();
            lease.PollRetirement();
            if (fail) throw new InvalidOperationException("Native setter failed before assignment.");
            slot = null;
        });
        lease.Retire();
        Assert.Equal(0, writes);
        Assert.Throws<InvalidOperationException>(lease.PollRetirement);
        Assert.Equal(1, writes);
        Assert.Same(menu, slot);
        Assert.False(lease.CanDispatch);
        fail = false;
        lease.PollRetirement();
        Assert.Equal(2, writes);
        Assert.Null(slot);
        lease.PollRetirement();
        Assert.Equal(2, writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignMenuDisposalBecomesInertAndOwnerRetirementPreservesOtherMenus(bool replaceOwner)
    {
        int screen = 3;
        object menu = new();
        object replacement = new();
        object foreignMenu = new();
        var slots = new Dictionary<int, object?> { [3] = menu, [4] = foreignMenu };
        int slotReads = 0;
        var lease = new UiSemanticMenuSlotLease(menu, () => screen == 3,
            () => { slotReads++; return slots[screen]; }, () => slots[screen] = null);
        Assert.True(lease.CanDispatch);
        screen = 4;
        Assert.False(lease.CanDispatch);
        lease.Retire();
        lease.PollRetirement();
        Assert.Equal(0, slotReads);
        Assert.Same(menu, slots[3]);
        Assert.Same(foreignMenu, slots[4]);
        if (replaceOwner) slots[3] = replacement;
        screen = 3;
        Assert.False(lease.CanDispatch);
        lease.PollRetirement();
        if (replaceOwner) Assert.Same(replacement, slots[3]);
        else Assert.Null(slots[3]);
        Assert.Same(foreignMenu, slots[4]);
        slots[3] = replacement;
        lease.Retire();
        Assert.Same(replacement, slots[3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AssetWatchPartialSubscriptionIsDisabledAndCleanupCanBeRetried(bool failRemove)
    {
        var events = new EventSet();
        int screen = 3;
        int polls = 0;
        using var binding = new UiSemanticAssetWatchBinding(events.Events.GameLoop, () => screen == 3, () => polls++);
        events.LoopHub.FailAdd = "UpdateTicked";
        events.LoopHub.FailRemove = failRemove ? "UpdateTicked" : null;
        if (failRemove) Assert.Throws<AggregateException>(binding.Activate);
        else Assert.Throws<InvalidOperationException>(binding.Activate);
        Assert.Equal(failRemove ? 1 : 0, events.Count);
        events.RaiseAll(this);
        Assert.Equal(0, polls);
        events.LoopHub.FailAdd = null;
        if (failRemove) Assert.Throws<InvalidOperationException>(binding.Activate);
        events.LoopHub.FailRemove = null;
        binding.Dispose();
        Assert.Equal(0, events.Count);
        binding.Activate();
        binding.Activate();
        Assert.Equal(1, events.Count);
        screen = 4;
        events.RaiseAll(this);
        Assert.Equal(0, polls);
        screen = 3;
        events.RaiseAll(this);
        Assert.Equal(1, polls);
        events.LoopHub.FailRemove = "UpdateTicked";
        Assert.Throws<InvalidOperationException>(binding.Dispose);
        events.RaiseAll(this);
        Assert.Equal(1, polls);
        events.LoopHub.FailRemove = null;
        binding.Dispose();
        Assert.Equal(0, events.Count);
    }

    [Fact]
    public void HostedLifecycleKeepsOwnerAliveDuringForeignUpdateMenuAndTitleEvents()
    {
        var events = new EventSet();
        int screen = 3;
        var calls = new List<string>();
        using var binding = new UiSemanticHostEventBinding(events.Events, 3, () => screen,
            (_, _) => calls.Add("watch/update"), (_, _) => calls.Add("close"), (_, _) => calls.Add("menu"));
        binding.Activate();
        Assert.Equal(3, events.Count);
        screen = 4;
        events.RaiseAll(this);
        events.DisplayHub.Raise("MenuChanged", this);
        Assert.Empty(calls);
        Assert.Throws<InvalidOperationException>(binding.RequireOwner);
        Assert.Equal(3, events.Count);
        screen = 3;
        binding.RequireOwner();
        events.LoopHub.Raise("UpdateTicked", this);
        events.DisplayHub.Raise("MenuChanged", this);
        events.LoopHub.Raise("ReturnedToTitle", this);
        Assert.Equal(new[] { "watch/update", "menu", "close" }, calls);
        screen = 4;
        binding.Dispose();
        Assert.Equal(0, events.Count);
    }

    [Fact]
    public void HostedLifecycleRetainsFailedCleanupAndBlocksDispatchUntilRetryCompletes()
    {
        var events = new EventSet();
        int calls = 0;
        using var binding = new UiSemanticHostEventBinding(events.Events, 3, () => 3,
            (_, _) => calls++, (_, _) => calls++, (_, _) => calls++);
        events.DisplayHub.FailAdd = "MenuChanged";
        Assert.Throws<InvalidOperationException>(binding.Activate);
        Assert.Equal(0, events.Count);
        events.DisplayHub.FailAdd = null;
        binding.Activate();
        events.LoopHub.FailRemove = "UpdateTicked";
        Assert.Throws<AggregateException>(binding.Dispose);
        events.RaiseAll(this);
        Assert.Equal(0, calls);
        Assert.Equal(1, events.Count);
        Assert.Throws<InvalidOperationException>(binding.Activate);
        events.LoopHub.FailRemove = null;
        binding.Dispose();
        Assert.Equal(0, events.Count);
        binding.Activate();
        events.RaiseAll(this);
        events.DisplayHub.Raise("MenuChanged", this);
        Assert.Equal(3, calls);
    }

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
