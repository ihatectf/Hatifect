using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Stardew.Semantic;

namespace Hatifect.UI.Stardew;

internal sealed partial class UiAutomatedAcceptanceController
{
    private object? _retiredOverlayInput;

    private void ExecuteRetiredOverlayInput()
    {
        _dogfood.CloseOverlay();
        _dogfood.Close();
        RequireAction(Game1.activeClickableMenu == null, "Retired input acceptance requires the isolated empty menu slot.");
        var cover = new ActionPumpCoverMenu();
        Game1.activeClickableMenu = cover;
        using var runtime = new UiSemanticStardewRuntime(Game1.graphics.GraphicsDevice,
            typography => typography.Family == "Display" ? Game1.dialogueFont : Game1.smallFont,
            asset => throw new InvalidOperationException($"Retired input acceptance has no texture '{asset}'."));
        var owner = ActionId("retired-input");
        using var publication = new UiPublication(owner);
        var query = publication.State(owner.Child("query"), string.Empty, UiSourceTypes.String);
        var experience = new UiExperienceBuilder(owner, "Retired input").Search("Query", query).Build();
        var registry = new UiRegistryBuilder().Window(owner, "Retired input", () => experience, UiHostPolicies.Overlay).Freeze();
        UiScene scene = new UiSceneComposer(UiSemanticStardewTheme.Default, registry).Compose(
            new UiInvocationService(registry).Invoke(owner, UiPresentationProfiles.Wide));
        var viewport = UiSemanticStardewMenu.CaptureViewport();
        using var host = runtime.CreateHost(scene, new UiHostPlacementContext(viewport));
        RequireAction(host.Session.MoveFocus(UiNavigationDirection.Next).Consumed && host.Input.IsTextEditing,
            "The real overlay must own a focused text input before acceptance.");

        // Forward every real SMAPI subscription except one injected removal failure. The same
        // event binding must retain that handler inertly, then remove it on a successful retry.
        IGameLoopEvents loop = RetiredInputForwarder.Wrap(_helper.Events.GameLoop);
        var loopFault = (RetiredInputForwarder)loop;
        IModEvents events = RetiredInputForwarder.Wrap(_helper.Events);
        ((RetiredInputForwarder)events).Properties.Add("GameLoop", loop);
        IModHelper helper = RetiredInputForwarder.Wrap(_helper);
        ((RetiredInputForwarder)helper).Properties.Add("Events", events);
        int closed = 0;
        var overlay = new UiSemanticStardewOverlaySession(runtime, helper, host, viewport,
            UiSemanticStardewOverlayRenderLayer.ActiveMenu, () => closed++);
        IKeyboardSubscriber? previous = Game1.keyboardDispatcher.Subscriber;
        try
        {
            overlay.Show();
            IKeyboardSubscriber? subscriber = Game1.keyboardDispatcher.Subscriber;
            RequireAction(subscriber is UiSemanticKeyboardSubscriberLease && !ReferenceEquals(subscriber, previous),
                "The actual overlay did not acquire the native keyboard subscriber.");
            publication.Changed += overlay.Hide;
            loopFault.FailRemove = "UpdateTicked";
            subscriber!.RecieveTextInput("x");
            bool restoredAfterInput = ReferenceEquals(previous, Game1.keyboardDispatcher.Subscriber);
            bool retainedForRetry = overlay.Visible && !host.Session.Root.IsActive &&
                publication.LastResult.Succeeded && publication.LastResult.ObserverErrors.Count == 1 &&
                query.Value == "x" && loopFault.ActiveHandlers == 1 && loopFault.RemovalFailures == 1 && closed == 0;
            Record("semantic.input.retired-overlay.keyboard", retainedForRetry && restoredAfterInput,
                $"Publication committed through failed Hide observer; retainedForRetry={retainedForRetry}, keyboardRestored={restoredAfterInput}.");

            loopFault.FailRemove = null;
            overlay.Hide();
            subscriber.RecieveTextInput("late");
            bool retryClean = !overlay.Visible && loopFault.ActiveHandlers == 0 && closed == 1 &&
                ReferenceEquals(previous, Game1.keyboardDispatcher.Subscriber) && query.Value == "x";
            Record("semantic.input.retired-overlay.retry", retryClean,
                "Retry removed the retained handler, notified close once and kept the retired subscriber inert.");
            _retiredOverlayInput = new
            {
                completed = true, retainedForRetry, restoredAfterInput, retryClean,
                publicationVersion = publication.Version,
                observerErrors = publication.LastResult.ObserverErrors.Count,
                removalFailures = loopFault.RemovalFailures, retainedHandlers = loopFault.ActiveHandlers,
                closed, text = query.Value
            };
        }
        finally
        {
            loopFault.FailRemove = null;
            publication.Changed -= overlay.Hide;
            overlay.Dispose();
            if (ReferenceEquals(Game1.activeClickableMenu, cover)) Game1.activeClickableMenu = null;
        }
    }

    // Test-only fault at the actual subscription boundary, without replacing the UI runtime,
    // keyboard dispatcher, publication, input adapter or event binding implementation.
    public class RetiredInputForwarder : DispatchProxy
    {
        private object _target = null!;
        internal Dictionary<string, object> Properties { get; } = new();
        private readonly Dictionary<string, List<Delegate>> _handlers = new();
        internal IReadOnlyList<Exception> ReplayRetained(string eventName)
        {
            var errors = new List<Exception>();
            if (!_handlers.TryGetValue(eventName, out var handlers)) return errors;
            foreach (Delegate handler in handlers.ToArray())
            {
                try { handler.DynamicInvoke(_target, null); }
                catch (Exception error) { errors.Add(error is TargetInvocationException { InnerException: { } inner } ? inner : error); }
            }
            return errors;
        }
        internal string? FailRemove { get; set; }
        internal int ActiveHandlers { get; private set; }
        internal int RemovalFailures { get; private set; }
        internal static T Wrap<T>(T target) where T : class
        {
            T proxy = Create<T, RetiredInputForwarder>();
            ((RetiredInputForwarder)(object)proxy)._target = target;
            return proxy;
        }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            string name = method!.Name;
            if (name.StartsWith("get_", StringComparison.Ordinal) && Properties.TryGetValue(name[4..], out var value))
                return value;
            if (name.StartsWith("remove_", StringComparison.Ordinal) && name[7..] == FailRemove)
            {
                RemovalFailures++;
                throw new InvalidOperationException("Controlled overlay event removal failure.");
            }
            try
            {
                object? result = method.Invoke(_target, args);
                if (name.StartsWith("add_", StringComparison.Ordinal))
                {
                    ActiveHandlers++;
                    if (!_handlers.TryGetValue(name[4..], out var handlers)) _handlers.Add(name[4..], handlers = new());
                    handlers.Add((Delegate)args![0]!);
                }
                if (name.StartsWith("remove_", StringComparison.Ordinal))
                {
                    ActiveHandlers--;
                    if (_handlers.TryGetValue(name[7..], out var handlers)) handlers.Remove((Delegate)args![0]!);
                }
                return result;
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }
}
