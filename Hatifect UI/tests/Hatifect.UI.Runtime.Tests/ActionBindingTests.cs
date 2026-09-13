using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Actions;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Rendering;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Theming;
using Hatifect.UI.Semantics;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed partial class ActionBindingTests
{
    [Fact]
    public void HostSessionForwardsActionOwnershipFromItsLiveDispatcher()
    {
        UiActionDefinition action = Action((_, _) => new(UiActionResult<int>.Success(1))).Bind(() => 1);
        var host = Host(Scene(action));
        try
        {
            UiActionDispatcherOwnershipSnapshot before = host.ActionOwnership;
            Assert.False(before.Terminal);
            UiActionOwnershipEntry entry = Assert.Single(before.Actions);
            Assert.Equal(action.Id, entry.Action);
            Assert.Equal(UiActionState.Available, entry.State);
            Assert.False(entry.IsRetired);

            Assert.True(host.Interactions.Submit().ActionInvoked);
            Assert.Equal(UiActionState.Completed, Assert.Single(host.ActionOwnership.Actions).State);

            host.Deactivate();
            Assert.True(host.ActionOwnership.Terminal);
            Assert.Empty(host.ActionOwnership.Actions);
        }
        finally { host.Deactivate(); }
    }

    [Fact]
    public void SharedDescriptionHasIndependentHostStateAndOwningThreadCompletions()
    {
        var pending = new[] { Completion(), Completion() };
        int captured = 0, started = 0;
        int ownerThread = Environment.CurrentManagedThreadId;
        var results = new List<(int Request, int Value, int Thread)>();
        UiActionDefinition action = Action((_, _) => new(pending[started++].Task)).Bind(
            () => ++captured, (request, result) => results.Add((request, result.Value, Environment.CurrentManagedThreadId)));
        UiScene scene = Scene(action);
        var first = Host(scene);
        var second = Host(scene);
        try
        {
            Assert.True(first.Interactions.Submit().ActionInvoked);
            Assert.False(first.Actions.CanInvoke(action));
            Assert.True(second.Actions.CanInvoke(action));
            Assert.True(second.Interactions.Submit().ActionInvoked);
            Assert.False(first.Interactions.Submit().ActionInvoked);
            Assert.Equal(2, captured);
            Assert.Equal(2, started);
            Assert.Same(action, Assert.Single(Nodes(first.Scene.Root).OfType<UiButtonSceneNode>()).Action);
            Assert.Same(action, Assert.Single(Nodes(second.Scene.Root).OfType<UiButtonSceneNode>()).Action);

            OnWorker(() => pending[0].SetResult(UiActionResult<int>.Success(11)));
            first.Render();
            first.CaptureDiagnostics();
            first.Update(scene, Viewport);
            Assert.Empty(results);
            PumpUntil(first, () => results.Count == 1);
            Assert.Equal((1, 11, ownerThread), results[0]);
            Assert.True(first.Actions.CanInvoke(action));
            Assert.False(second.Actions.CanInvoke(action));
            OnWorker(() => pending[1].SetResult(UiActionResult<int>.Success(22)));
            PumpUntil(second, () => results.Count == 2);
            Assert.Equal((2, 22, ownerThread), results[1]);
        }
        finally { first.Deactivate(); second.Deactivate(); }
    }

    [Fact]
    public void TypedDescriptionRequiresAHostWithoutCapturingOrExecutingDirectly()
    {
        int captures = 0, effects = 0;
        var definition = Action((request, _) => { effects++; return new(UiActionResult<int>.Success(request)); })
            .Bind(() => ++captures);
        Assert.False(definition.CanExecute);
        Assert.Throws<InvalidOperationException>(() => definition.TryExecute());
        Assert.Throws<ArgumentNullException>(() => Action((request, _) => new(UiActionResult<int>.Success(request))).Bind(null!));
        Assert.Equal(0, captures);
        Assert.Equal(0, effects);
    }

    [Fact]
    public void FailedScenePreparationKeepsPendingWorkAndRetiresOnlyTheCandidate()
    {
        var pending = Completion();
        CancellationToken cancellation = default;
        int completed = 0, candidateEffects = 0;
        var original = Action((_, token) => { cancellation = token; return new(pending.Task); })
            .Bind(() => 7, (_, result) => completed = result.Value);
        var candidate = Action((request, _) => { candidateEffects++; return new(UiActionResult<int>.Success(request)); })
            .Bind(() => 99);
        var platform = new Platform();
        var host = Host(Scene(original), platform);
        try
        {
            Assert.True(host.Interactions.Submit().ActionInvoked);
            UiScene scene = host.Scene;
            var layout = host.Layout;
            var frame = host.Frame;
            var accessibility = host.Accessibility;
            var input = host.Interactions.Snapshot;
            var error = new InvalidOperationException("candidate text metrics");
            platform.OnMeasure = () => throw error;
            Assert.Same(error, Assert.Throws<InvalidOperationException>(() => host.Update(Scene(candidate), new UiRect(0, 0, 500, 240))));
            Assert.Same(scene, host.Scene);
            Assert.Same(layout, host.Layout);
            Assert.Same(frame, host.Frame);
            Assert.Same(accessibility, host.Accessibility);
            Assert.Same(input, host.Interactions.Snapshot);
            Assert.False(cancellation.IsCancellationRequested);
            Assert.Equal(UiActionState.Running, host.Actions.Status(original)!.State);
            Assert.Null(host.Actions.Status(candidate));
            Assert.Equal(0, candidateEffects);
            platform.OnMeasure = null;
            pending.SetResult(UiActionResult<int>.Success(71));
            PumpUntil(host, () => completed != 0);
            Assert.Equal(71, completed);
            Assert.True(host.Update(Scene(candidate), Viewport).FrameChanged);
            FocusAction(host, candidate);
            Assert.True(host.Interactions.Submit().ActionInvoked);
            Assert.Equal(1, candidateEffects);
        }
        finally { host.Deactivate(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedReplacementOrRemovalCancelsOldWorkBeforeItCanAffectSuccessor(bool remove)
    {
        var pending = Completion();
        int completions = 0, successorEffects = 0;
        CancellationToken token = default;
        var old = Action((_, cancellation) => { token = cancellation; return new(pending.Task); })
            .Bind(() => 1, (_, _) => completions++);
        var successor = Action((request, _) => { successorEffects++; return new(UiActionResult<int>.Success(request)); })
            .Bind(() => 2);
        var host = Host(Scene(old));
        try
        {
            Assert.True(host.Interactions.Submit().ActionInvoked);
            UiScene accepted = remove ? Scene() : Scene(successor);
            host.Update(accepted, Viewport);
            Assert.True(token.IsCancellationRequested);
            Assert.Equal(remove ? 0 : 1, host.ActionCount);
            Assert.Null(host.Actions.Status(old));
            pending.SetException(new InvalidOperationException("late noncooperative domain fault"));
            host.PumpActions();
            Assert.Equal(0, completions);
            Assert.Same(accepted, host.Scene);
            if (!remove)
            {
                Assert.True(host.Interactions.Submit().ActionInvoked);
                Assert.Equal(1, successorEffects);
            }
        }
        finally { host.Deactivate(); }
    }

    [Fact]
    public void PublicationRetirementFencesAllBindingsBeforeCancellationCallbacksReenter()
    {
        using var publication = new UiPublication(Id("publication"));
        var pending = Completion();
        UiHostRuntimeSession? host = null;
        UiActionDefinition? sibling = null;
        int siblingEffects = 0, callbacks = 0, cancellationCallbacks = 0;
        var first = Action((_, token) =>
        {
            token.Register(() =>
            {
                cancellationCallbacks++;
                Assert.False(host!.Actions.Invoke(sibling!));
                host.PumpActions();
            });
            return new(pending.Task);
        }).Bind(() => 1, (_, _) => callbacks++, publication);
        sibling = Action((request, _) => { siblingEffects++; return new(UiActionResult<int>.Success(request)); }, id: Id("sibling"))
            .Bind(() => 2, (_, _) => callbacks++, publication);
        var independent = Action((request, _) => new(UiActionResult<int>.Success(request)), id: Id("independent"))
            .Bind(() => 3, (_, _) => callbacks++);
        host = Host(Scene(first, sibling, independent));
        try
        {
            Assert.True(host.Actions.Invoke(first));
            publication.Dispose();
            Assert.Equal(1, cancellationCallbacks);
            Assert.False(host.Actions.CanInvoke(first));
            Assert.False(host.Actions.CanInvoke(sibling));
            Assert.Equal(0, siblingEffects);
            Assert.Equal(0, callbacks);
            pending.SetResult(UiActionResult<int>.Success(11));
            host.PumpActions();
            Assert.False(FindAccessibility(host, first).Enabled);
            Assert.False(FindAccessibility(host, sibling).Enabled);
            Assert.True(host.Actions.Invoke(independent));
            Assert.Equal(1, callbacks);
            host.Update(Scene(first, sibling, independent), Viewport);
            Assert.False(host.Actions.CanInvoke(first));
            Assert.False(host.Actions.CanInvoke(sibling));
        }
        finally { host.Deactivate(); }
    }

    [Fact]
    public void HostRetirementCancelsRetainedBindingsAndRefusesNewEffects()
    {
        var pending = Completion();
        CancellationToken cancellation = default;
        int effects = 0, captures = 0, callbacks = 0;
        var action = Action((_, token) => { effects++; cancellation = token; return new(pending.Task); })
            .Bind(() => ++captures, (_, _) => callbacks++);
        var host = Host(Scene(action));
        IUiActionResolver retained = host.Actions;
        Assert.True(host.Interactions.Submit().ActionInvoked);
        host.Deactivate();
        host.Deactivate();
        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(retained.CanInvoke(action));
        Assert.False(retained.Invoke(action));
        Assert.Throws<ObjectDisposedException>(() => host.Interactions.Submit());
        pending.SetResult(UiActionResult<int>.Success(10));
        Assert.False(host.PumpActions());
        Assert.Equal(1, captures);
        Assert.Equal(1, effects);
        Assert.Equal(0, callbacks);
        Assert.Equal(0, host.ActionCount);
    }

    [Fact]
    public void QueuedInputCapturesDraftOnceAndPreservesAdmissionOrderAcrossRecomposition()
    {
        var pending = Completion();
        int draft = 11, captures = 0;
        var started = new List<int>();
        var results = new List<int>();
        var action = Action((request, _) =>
        {
            started.Add(request);
            return started.Count == 1 ? new(pending.Task) : new(UiActionResult<int>.Success(request));
        }, concurrency: UiActionConcurrency.Queue(1)).Bind(() => { captures++; return draft; }, (_, result) => results.Add(result.Value));
        var host = Host(Scene(action));
        try
        {
            Assert.True(host.Interactions.Submit().ActionInvoked);
            draft = 22;
            Assert.True(host.Interactions.Submit().ActionInvoked);
            draft = 33;
            Assert.False(host.Interactions.Submit().ActionInvoked);
            Assert.Equal(2, captures);
            host.Update(Scene(action), Viewport);
            Assert.Equal(1, host.Actions.Status(action)!.WaitingCount);
            pending.SetResult(UiActionResult<int>.Success(11));
            PumpUntil(host, () => results.Count == 2);
            Assert.Equal(new[] { 11, 22 }, started);
            Assert.Equal(new[] { 11, 22 }, results);
            Assert.Equal(2, captures);
        }
        finally { host.Deactivate(); }
    }

    [Fact]
    public void AvailabilityAndRequestFailuresAreObservedWithoutDomainEffects()
    {
        int mode = 0, effects = 0, captures = 0;
        var error = new InvalidOperationException("availability");
        var captureError = new InvalidOperationException("draft");
        var action = Action((request, _) => { effects++; return new(UiActionResult<int>.Success(request)); }, availability: () => mode switch
        {
            0 => UiActionAvailability.Disabled(new("target.missing", "Select a target.", Id("target"))),
            1 => throw error,
            _ => UiActionAvailability.Available
        }).Bind(() => { captures++; if (mode == 2) throw captureError; return 13; });
        var host = Host(Scene(action));
        try
        {
            Assert.False(host.Actions.Invoke(action));
            Assert.Equal(0, captures);
            Assert.Equal("Select a target.", host.Actions.Status(action)!.Reason!.Message);
            Assert.Equal(Id("target"), host.Actions.Status(action)!.Reason!.Field);
            mode = 1;
            host.PumpActions();
            Assert.Same(error, host.Actions.Status(action)!.Error);
            Assert.Equal(UiActionState.Failed, host.Actions.Status(action)!.State);
            mode = 2;
            host.PumpActions();
            Assert.True(host.Interactions.MoveFocus(UiNavigationDirection.Next).Consumed);
            Assert.False(host.Interactions.Submit().ActionInvoked);
            Assert.Same(captureError, host.Actions.Status(action)!.Error);
            Assert.Equal(1, captures);
            Assert.Equal(0, effects);
            mode = 3;
            Assert.True(host.Interactions.Submit().ActionInvoked);
            Assert.Equal(UiActionState.Completed, host.Actions.Status(action)!.State);
            Assert.Equal(2, captures);
            Assert.Equal(1, effects);
        }
        finally { host.Deactivate(); }
    }

    [Fact]
    public void LegacyAdmissionReadsAvailabilityOnceAndObservesEffectFailure()
    {
        int reads = 0, effects = 0;
        var error = new InvalidOperationException("legacy effect");
        var action = new UiActionDefinition(Id("action"), "Legacy", () => { effects++; throw error; }, () => { reads++; return true; });
        var host = Host(Scene(action));
        try
        {
            reads = 0;
            host.Render();
            host.CaptureDiagnostics();
            Assert.Equal(0, reads);
            Assert.True(host.Interactions.Submit().ActionInvoked);
            Assert.Equal(1, reads);
            Assert.Equal(1, effects);
            Assert.Equal(UiActionState.Failed, host.Actions.Status(action)!.State);
            Assert.Same(error, host.Actions.Status(action)!.Error);
            Assert.Same(error, Assert.Throws<InvalidOperationException>(() => action.TryExecute()));
            Assert.Equal(2, reads);
            Assert.Equal(2, effects);
        }
        finally { host.Deactivate(); }
    }

    [Fact]
    public void CompletionCanAcceptAReplacementWhileDispatcherEnumeratesItsPreviousMap()
    {
        var pending = Completion();
        UiHostRuntimeSession? host = null;
        int effects = 0, callbacks = 0;
        var successor = Action((request, _) => { effects++; return new(UiActionResult<int>.Success(request)); }).Bind(() => 9);
        UiScene next = Scene(successor);
        var old = Action((_, _) => new(pending.Task)).Bind(() => 1, (_, _) =>
        {
            callbacks++;
            host!.Update(next, Viewport);
        });
        host = Host(Scene(old));
        try
        {
            Assert.True(host.Interactions.Submit().ActionInvoked);
            pending.SetResult(UiActionResult<int>.Success(1));
            PumpUntil(host, () => callbacks == 1);
            Assert.Same(next, host.Scene);
            Assert.Null(host.Actions.Status(old));
            Assert.Null(host.Actions.Status(successor)!.ObserverError);
            FocusAction(host, successor);
            Assert.True(host.Interactions.Submit().ActionInvoked);
            Assert.Equal(1, effects);
        }
        finally { host.Deactivate(); }
    }

    [Theory]
    [InlineData("availability")]
    [InlineData("capture")]
    public void TypedAdmissionCannotExecuteAfterACallbackAcceptsAnotherFrame(string phase)
    {
        UiHostRuntimeSession? host = null;
        UiScene? scene = null;
        bool armed = false;
        int captures = 0, effects = 0;
        void Replace()
        {
            if (!armed) return;
            armed = false;
            host!.Update(scene!, Viewport);
        }
        var definition = Action((request, _) => { effects++; return new(UiActionResult<int>.Success(request)); }, availability: () =>
        {
            if (phase == "availability") Replace();
            return UiActionAvailability.Available;
        }).Bind(() => { captures++; if (phase == "capture") Replace(); return 1; });
        scene = Scene(definition);
        host = Host(scene);
        try
        {
            armed = true;
            Assert.Throws<InvalidOperationException>(() => host.Interactions.Submit());
            Assert.Equal(1, host.AcceptedVersion);
            Assert.Equal(phase == "capture" ? 1 : 0, captures);
            Assert.Equal(0, effects);
            Assert.True(host.Interactions.Submit().ActionInvoked);
            Assert.Equal(1, effects);
        }
        finally { host.Deactivate(); }
    }

    [Fact]
    public void RunningPresentationUsesHostEligibilityAndRetainsMeasuredGeometry()
    {
        var pending = Completion();
        int reads = 0;
        var definition = Action((_, _) => new(pending.Task), availability: () => { reads++; return UiActionAvailability.Available; })
            .Bind(() => 1);
        var host = Host(Scene(definition));
        try
        {
            var layout = host.Layout;
            var activeSurface = host.Frame.Primitives.OfType<UiSurfacePrimitive>().Single(p => p.Node == definition.Id.Child("scene/button"));
            Assert.True(host.Interactions.Submit().ActionInvoked);
            host.PumpActions();
            var disabledSurface = host.Frame.Primitives.OfType<UiSurfacePrimitive>().Single(p => p.Node == definition.Id.Child("scene/button"));
            Assert.NotEqual(activeSurface, disabledSurface);
            Assert.NotEqual(activeSurface.Opacity, disabledSurface.Opacity);
            Assert.False(FindAccessibility(host, definition).Enabled);
            Assert.Same(layout, host.Layout);
            var diagnostic = Assert.Single(host.CaptureDiagnostics().Nodes, n => n.Id == definition.Id.Child("scene/button"));
            Assert.False(diagnostic.Semantic.Enabled);
            Assert.Equal(UiActionState.Running, diagnostic.Action!.State);
            Assert.False(diagnostic.Action.Enabled);
            int before = reads;
            host.Render();
            host.CaptureDiagnostics();
            Assert.Equal(before, reads);
            pending.SetResult(UiActionResult<int>.Success(1));
            PumpUntil(host, () => host.Actions.Status(definition)!.State == UiActionState.Completed);
            Assert.True(FindAccessibility(host, definition).Enabled);
            Assert.Same(layout, host.Layout);
        }
        finally { host.Deactivate(); }
    }

    [Fact]
    public void FailedActionPresentationIsRetriedWithoutAnotherAvailabilityChange()
    {
        bool enabled = true;
        var definition = Action((request, _) => new(UiActionResult<int>.Success(request)),
            availability: () => enabled ? UiActionAvailability.Available :
                UiActionAvailability.Disabled(new("target.missing", "Select a target."))).Bind(() => 1);
        UiScene scene = SearchScene(definition);
        var platform = new Platform();
        var host = new UiHostRuntimeSession(scene, Viewport, platform,
            new UiInteractionSnapshot(Focused: Assert.Single(Nodes(scene.Root).OfType<UiTextInputSceneNode>()).Id));
        try
        {
            var frame = host.Frame;
            var accessibility = host.Accessibility;
            var enabledSurface = ButtonSurface(host, definition);
            Assert.True(FindAccessibility(host, definition).Enabled);
            enabled = false;
            var error = new InvalidOperationException("action presentation text metrics");
            platform.OnMeasure = () => throw error;
            Assert.Same(error, Assert.Throws<InvalidOperationException>(() => host.PumpActions()));
            Assert.Same(frame, host.Frame);
            Assert.Same(accessibility, host.Accessibility);
            Assert.Null(FindAccessibility(host, definition).Value);
            Assert.DoesNotContain(host.Frame.Primitives.OfType<UiTextPrimitive>(), p => p.Text == "Select a target.");
            Assert.False(host.Actions.CanInvoke(definition));

            platform.OnMeasure = null;
            Assert.True(host.PumpActions());
            Assert.NotSame(frame, host.Frame);
            Assert.False(FindAccessibility(host, definition).Enabled);
            Assert.Equal("Select a target.", FindAccessibility(host, definition).Value);
            Assert.Contains(host.Frame.Primitives.OfType<UiTextPrimitive>(), p => p.Text == "Select a target.");
            Assert.NotEqual(enabledSurface.Opacity, ButtonSurface(host, definition).Opacity);
            Assert.False(host.PumpActions());
        }
        finally { host.Deactivate(); }
    }

    [Fact]
    public void ActionPresentationCannotOverwriteASceneAcceptedByTextMetrics()
    {
        bool enabled = true;
        var definition = Action((request, _) => new(UiActionResult<int>.Success(request)),
            availability: () => enabled ? UiActionAvailability.Available :
                UiActionAvailability.Disabled(new("target.missing", "Select a target."))).Bind(() => 1);
        var successor = Action((request, _) => new(UiActionResult<int>.Success(request)), id: Id("successor")).Bind(() => 2);
        UiScene scene = SearchScene(definition);
        UiScene next = Scene(successor);
        var platform = new Platform();
        var host = new UiHostRuntimeSession(scene, Viewport, platform,
            new UiInteractionSnapshot(Focused: Assert.Single(Nodes(scene.Root).OfType<UiTextInputSceneNode>()).Id));
        try
        {
            int callbacks = 0;
            Exception? rejection = null;
            UiRenderFrame? acceptedFrame = null;
            enabled = false;
            platform.OnMeasure = () =>
            {
                callbacks++;
                platform.OnMeasure = null;
                rejection = Record.Exception(() => host.Update(next, Viewport));
                if (rejection == null) acceptedFrame = host.Frame;
            };
            Assert.True(host.PumpActions());
            Assert.Equal(1, callbacks);
            if (rejection == null)
            {
                Assert.Same(next, host.Scene);
                Assert.Same(acceptedFrame, host.Frame);
                Assert.True(FindAccessibility(host, successor).Enabled);
                Assert.DoesNotContain(host.Frame.Primitives, p => p.Node == definition.Id.Child("scene/button"));
            }
            else
            {
                Assert.IsType<InvalidOperationException>(rejection);
                Assert.Same(scene, host.Scene);
                Assert.False(FindAccessibility(host, definition).Enabled);
            }
        }
        finally { host.Deactivate(); }
    }

    [Fact]
    public void RegistrationCapacityIsCurrentSetBoundedAndFailedCandidatesCannotLeakSlots()
    {
        using var owner = new UiHostActionBindings(() => 0);
        UiScene template = Scene(Action((request, _) => new(UiActionResult<int>.Success(request))).Bind(() => 1));
        var definitions = Enumerable.Range(0, UiActionDispatcher.MaximumActions)
            .Select(index => Action((request, _) => new(UiActionResult<int>.Success(request)), id: Id("bounded/" + index)).Bind(() => 1)).ToArray();
        UiScene RegistrationScene(IEnumerable<UiActionDefinition> actions) => new(template.Experience, template.DisplayName,
            new UiHostSceneNode(template.Root.Id, template.Root.Role, template.Root.Visual, template.Root.Policy,
                actions.Select(action => (UiSceneNode)new UiButtonSceneNode(action.Id.Child("button"), UiSceneRoles.Button,
                    template.Root.Visual, action)).ToArray()), template.MeasurementContext);
        using (var candidate = owner.Prepare(RegistrationScene(definitions.Take(definitions.Length - 1)))) candidate.Commit();
        Assert.Equal(UiActionDispatcher.MaximumActions - 1, owner.Count);
        using (var candidate = owner.Prepare(RegistrationScene(definitions))) candidate.Commit();
        Assert.Equal(UiActionDispatcher.MaximumActions, owner.Count);
        var extra = Action((request, _) => new(UiActionResult<int>.Success(request)), id: Id("extra")).Bind(() => 2);
        Assert.Throws<InvalidOperationException>(() => owner.Prepare(RegistrationScene(definitions.Append(extra))));
        Assert.Equal(UiActionDispatcher.MaximumActions, owner.Count);
        Assert.Null(owner.Current.Status(extra));
        var replacements = definitions.Select((definition, index) =>
            Action((request, _) => new(UiActionResult<int>.Success(request)), id: definition.Id).Bind(() => index)).ToArray();
        using (var candidate = owner.Prepare(RegistrationScene(replacements))) candidate.Commit();
        Assert.Equal(UiActionDispatcher.MaximumActions, owner.Count);
        Assert.Null(owner.Current.Status(definitions[0]));
        Assert.True(owner.Current.Invoke(replacements[0]));
        using (var candidate = owner.Prepare(RegistrationScene(Array.Empty<UiActionDefinition>()))) candidate.Commit();
        Assert.Equal(0, owner.Count);
        using (var candidate = owner.Prepare(RegistrationScene(new[] { extra }))) candidate.Commit();
        Assert.True(owner.Current.Invoke(extra));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedActionPresentationRetainsInputAndCanRecoverThroughARegularUpdate(bool update)
    {
        bool enabled = true;
        var disabled = UiActionAvailability.Disabled(new("target.missing", "Select a target."));
        var definition = Action((request, _) => new(UiActionResult<int>.Success(request)),
            availability: () => enabled ? UiActionAvailability.Available : disabled).Bind(() => 1);
        UiScene scene = SearchScene(definition);
        UiSymbolId button = definition.Id.Child("scene/button");
        UiSymbolId search = Assert.Single(Nodes(scene.Root).OfType<UiTextInputSceneNode>()).Id;
        var platform = new Platform();
        var host = new UiHostRuntimeSession(scene, Viewport, platform,
            new UiInteractionSnapshot(Hovered: button, Pressed: button, Focused: search));
        try
        {
            var input = host.Interactions.Snapshot;
            var frame = host.Frame;
            var accessibility = host.Accessibility;
            var layout = host.Layout;
            long version = host.AcceptedVersion;
            enabled = false;
            platform.OnMeasure = () => throw new InvalidOperationException("presentation metrics");

            Assert.Throws<InvalidOperationException>(() => host.PumpActions());

            Assert.Same(input, host.Interactions.Snapshot);
            Assert.Same(frame, host.Frame);
            Assert.Same(accessibility, host.Accessibility);
            Assert.Same(layout, host.Layout);
            Assert.Equal(version, host.AcceptedVersion);
            platform.OnMeasure = null;
            if (update)
            {
                var changed = host.Update(scene, Viewport);
                Assert.True(changed.FrameChanged);
                Assert.False(changed.LayoutChanged);
            }
            else Assert.True(host.PumpActions());
            Assert.Null(host.Interactions.Snapshot.Hovered);
            Assert.Null(host.Interactions.Snapshot.Pressed);
            Assert.Equal(search, host.Interactions.Snapshot.Focused);
            Assert.False(FindAccessibility(host, definition).Enabled);
            Assert.NotSame(frame, host.Frame);
            Assert.Same(layout, host.Layout);
            Assert.False(host.PumpActions());
        }
        finally { host.Deactivate(); }
    }

    [Fact]
    public void FailedInitialHostCannotReactivateDisposedPublication()
    {
        using var publication = new UiPublication(Id("publication"));
        int effects = 0;
        var definition = Action((request, _) => { effects++; return new(UiActionResult<int>.Success(request)); }).Bind(() => 1, owner: publication);
        var platform = new Platform { OnMeasure = () => throw new InvalidOperationException("initial geometry") };
        Assert.Throws<InvalidOperationException>(() => Host(Scene(definition), platform));
        publication.Dispose();
        var replacement = Host(Scene(definition));
        try
        {
            Assert.False(replacement.Actions.CanInvoke(definition));
            Assert.False(replacement.Actions.Invoke(definition));
            Assert.Equal(UiActionState.Cancelled, replacement.Actions.Status(definition)!.State);
            Assert.Equal(0, effects);
        }
        finally { replacement.Deactivate(); }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void UnchangedActionPumpsDoNotAllocatePerFrameAndKeepAcceptedFrame(bool running, bool legacyEnabled)
    {
        var pending = Completion();
        var definition = Action((_, _) => new(pending.Task)).Bind(() => 1);
        var legacy = new UiActionDefinition(Id("legacy"), "Legacy", () => { }, () => legacyEnabled);
        var host = Host(Scene(definition, legacy));
        try
        {
            if (running) Assert.True(host.Interactions.Submit().ActionInvoked);
            for (int i = 0; i < 100; i++) host.PumpActions();
            var frame = host.Frame;
            var layout = host.Layout;
            var performance = host.Performance;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) host.PumpActions();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            // Allow a one-time runtime bookkeeping allocation; one object per pump would
            // exceed this entire 1000-tick allowance by orders of magnitude.
            Assert.InRange(allocated, 0, 64);
            Assert.Same(frame, host.Frame);
            Assert.Same(layout, host.Layout);
            Assert.Equal(performance, host.Performance);
        }
        finally { host.Deactivate(); pending.SetResult(UiActionResult<int>.Success(1)); }
    }

    [Fact]
    public void WrongThreadRetirementOrInputCannotMutateTheAcceptedHost()
    {
        int effects = 0;
        var definition = Action((request, _) => { effects++; return new(UiActionResult<int>.Success(request)); }).Bind(() => 1);
        var host = Host(Scene(definition));
        try
        {
            var frame = host.Frame;
            var input = host.Interactions.Snapshot;
            OnWorker(() =>
            {
                Assert.Throws<InvalidOperationException>(() => host.Deactivate());
                Assert.Throws<InvalidOperationException>(() => host.Actions.Invoke(definition));
                Assert.Throws<InvalidOperationException>(() => host.Interactions.Submit());
            });
            Assert.True(host.IsActive);
            Assert.Same(frame, host.Frame);
            Assert.Same(input, host.Interactions.Snapshot);
            Assert.Equal(0, effects);
            Assert.True(host.Interactions.Submit().ActionInvoked);
            Assert.Equal(1, effects);
        }
        finally { host.Deactivate(); }
    }

    [Fact]
    public void RestartCancellationCannotQueueInputFromTheFrameItsCallbackReplaced()
    {
        var pending = Completion();
        UiHostRuntimeSession? host = null;
        UiScene? scene = null;
        int captures = 0, effects = 0, cancellations = 0;
        var definition = Action((request, token) =>
        {
            effects++;
            if (request == 1)
            {
                token.Register(() => { cancellations++; host!.Update(scene!, Viewport); });
                return new(pending.Task);
            }
            return new(UiActionResult<int>.Success(request));
        }, concurrency: UiActionConcurrency.RestartLatest).Bind(() => ++captures);
        scene = Scene(definition);
        host = Host(scene);
        try
        {
            Assert.True(host.Interactions.Submit().ActionInvoked);
            Assert.Throws<InvalidOperationException>(() => host.Interactions.Submit());
            Assert.Equal(1, host.AcceptedVersion);
            Assert.Equal(1, cancellations);
            Assert.Equal(0, host.Actions.Status(definition)!.WaitingCount);
            Assert.Equal(2, captures);
            Assert.Equal(1, effects);
            pending.SetResult(UiActionResult<int>.Success(1));
            PumpUntil(host, () => host.Actions.Status(definition)!.State == UiActionState.Cancelled);
            Assert.Equal(1, effects);
            Assert.Same(scene, host.Scene);
            Assert.True(host.Interactions.Submit().ActionInvoked);
            Assert.Equal(2, effects);
            Assert.Equal(3, captures);
        }
        finally { host.Deactivate(); }
    }

    [Fact]
    public void ReplacingAllActionsFencesRemovedSiblingsBeforeAnyCancellationCallback()
    {
        var pending = Completion();
        IUiActionResolver? retained = null;
        UiActionDefinition? sibling = null;
        int effects = 0, cancellations = 0;
        var first = Action((_, token) =>
        {
            token.Register(() => { cancellations++; Assert.False(retained!.Invoke(sibling!)); });
            return new(pending.Task);
        }).Bind(() => 1);
        sibling = Action((request, _) => { effects++; return new(UiActionResult<int>.Success(request)); }, id: Id("sibling"))
            .Bind(() => 2);
        var host = Host(Scene(first, sibling));
        try
        {
            retained = host.Actions;
            Assert.True(host.Interactions.Submit().ActionInvoked);
            host.Update(Scene(), Viewport);
            Assert.Equal(1, cancellations);
            Assert.Equal(0, effects);
            Assert.False(retained.CanInvoke(sibling));
            Assert.Null(retained.Status(first)!.ObserverError);
            Assert.Equal(0, host.ActionCount);
        }
        finally { host.Deactivate(); pending.SetResult(UiActionResult<int>.Success(1)); }
    }

    [Fact]
    public void ChangedDisabledRecipeUpdatesRunningPrimitivesWithoutMeasuringAgain()
    {
        var pending = Completion();
        var definition = Action((_, _) => new(pending.Task)).Bind(() => 1);
        var host = Host(SceneWithVisual(definition, "Action.Primary@Disabled\n    opacity = Opacity.Disabled"));
        try
        {
            Assert.True(host.Interactions.Submit().ActionInvoked);
            host.PumpActions();
            var layout = host.Layout;
            var before = ButtonSurface(host, definition);
            UiScene next = SceneWithVisual(definition, "Action.Primary@Disabled\n    opacity = Opacity.Hidden");
            var update = host.Update(next, Viewport);
            Assert.False(update.LayoutChanged);
            Assert.True(update.FrameChanged);
            Assert.Same(layout, host.Layout);
            Assert.NotEqual(before.Opacity, ButtonSurface(host, definition).Opacity);
            Assert.Equal(new UiOpacity(0), ButtonSurface(host, definition).Opacity);
            Assert.False(FindAccessibility(host, definition).Enabled);
            Assert.Equal(UiActionState.Running, host.Actions.Status(definition)!.State);
        }
        finally { host.Deactivate(); pending.SetResult(UiActionResult<int>.Success(1)); }
    }

    [Fact]
    public void StagedLegacyEligibilityRebuildsTheFrameWhenTheSceneIdentityIsUnchanged()
    {
        bool enabled = true;
        var definition = new UiActionDefinition(Id("action"), "Legacy", () => { }, () => enabled);
        UiScene scene = Scene(definition);
        var host = Host(scene);
        try
        {
            var layout = host.Layout;
            var before = ButtonSurface(host, definition);
            enabled = false;
            var disabled = host.Update(scene, Viewport);
            Assert.True(disabled.FrameChanged);
            Assert.False(disabled.LayoutChanged);
            Assert.NotEqual(before.Opacity, ButtonSurface(host, definition).Opacity);
            Assert.False(FindAccessibility(host, definition).Enabled);
            Assert.Same(layout, host.Layout);
            enabled = true;
            var available = host.Update(scene, Viewport);
            Assert.True(available.FrameChanged);
            Assert.False(available.LayoutChanged);
            Assert.Equal(before.Opacity, ButtonSurface(host, definition).Opacity);
            Assert.True(FindAccessibility(host, definition).Enabled);
            Assert.Same(layout, host.Layout);
        }
        finally { host.Deactivate(); }
    }

    [Theory]
    [InlineData("Disabled")]
    [InlineData("Focused")]
    public void TypedButtonGeometryRecipeIsRejectedBeforeReplacingTheAcceptedScene(string state)
    {
        var pending = Completion();
        var definition = Action((_, _) => new(pending.Task)).Bind(() => 1);
        var host = Host(Scene(definition));
        try
        {
            Assert.True(host.Interactions.Submit().ActionInvoked);
            host.PumpActions();
            var scene = host.Scene;
            var frame = host.Frame;
            var layout = host.Layout;
            var error = Assert.Throws<InvalidOperationException>(() => host.Update(
                SceneWithVisual(definition, $"Action.Primary@{state}\n    padding = Space.S", state == "Focused"), Viewport));
            Assert.Contains("layout geometry", error.Message);
            Assert.Same(scene, host.Scene);
            Assert.Same(frame, host.Frame);
            Assert.Same(layout, host.Layout);
            Assert.Equal(UiActionState.Running, host.Actions.Status(definition)!.State);
        }
        finally { host.Deactivate(); pending.SetResult(UiActionResult<int>.Success(1)); }
    }

    [Theory]
    [InlineData("Disabled")]
    [InlineData("Focused")]
    public void LegacyGeometryStatesStillApplyThroughSceneComposition(string state)
    {
        bool enabled = true;
        var definition = new UiActionDefinition(Id("action"), "Legacy", () => { }, () => enabled);
        string recipe = $"Action.Primary@{state}\n    padding = Space.S";
        var host = Host(SceneWithVisual(definition, recipe));
        try
        {
            UiSymbolId button = definition.Id.Child("scene/button");
            Assert.True(host.Layout.TryGetEntry(button, out var before));
            var initialLayout = host.Layout;
            host.RefreshTextEditingVisuals();
            Assert.Same(initialLayout, host.Layout);
            Assert.Equal(before!.Bounds, ButtonSurface(host, definition).Bounds);
            if (state == "Disabled") enabled = false;
            var update = host.Update(SceneWithVisual(definition, recipe, state == "Focused"), Viewport);
            Assert.True(update.LayoutChanged);
            Assert.True(host.Layout.TryGetEntry(button, out var after));
            Assert.True(after!.DesiredSize.Height < before.DesiredSize.Height);
            Assert.Equal(after.Bounds, ButtonSurface(host, definition).Bounds);
            Assert.Equal(enabled, FindAccessibility(host, definition).Enabled);
        }
        finally { host.Deactivate(); }
    }

    [Theory]
    [InlineData("discard")]
    [InlineData("replace")]
    [InlineData("dispose")]
    public void RetiredBindingsReleaseTheirPublicationSubscription(string retirement)
    {
        using var publication = new UiPublication(Id("publication"));
        WeakReference binding = RetireBinding(publication, retirement);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(binding.IsAlive, "A live publication retained its retired host binding.");
        GC.KeepAlive(publication);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference RetireBinding(UiPublication publication, string retirement)
    {
        using var owner = new UiHostActionBindings(() => 0);
        var definition = Action((request, _) => new(UiActionResult<int>.Success(request))).Bind(() => 1, owner: publication);
        WeakReference reference;
        using (var candidate = owner.Prepare(Scene(definition)))
        {
            reference = new WeakReference(Assert.Single(candidate.Map.Bindings).Value);
            if (retirement != "discard") candidate.Commit();
        }
        if (retirement == "replace")
        {
            using var replacement = owner.Prepare(Scene());
            replacement.Commit();
        }
        return reference;
    }

    private static void FocusAction(UiHostRuntimeSession host, UiActionDefinition definition)
    {
        UiSymbolId button = definition.Id.Child("scene/button");
        if (host.Interactions.Snapshot.Focused != button)
            Assert.True(host.Interactions.MoveFocus(UiNavigationDirection.Next).Consumed);
        Assert.Equal(button, host.Interactions.Snapshot.Focused);
    }

    private static UiSurfacePrimitive ButtonSurface(UiHostRuntimeSession host, UiActionDefinition definition)
        => Assert.Single(host.Frame.Primitives.OfType<UiSurfacePrimitive>(), p => p.Node == definition.Id.Child("scene/button"));

    private static UiScene SceneWithVisual(UiActionDefinition definition, string recipes, bool focused = false)
    {
        UiSymbolId id = Id("window");
        var experience = new UiExperienceBuilder(id, "Host actions").Actions("Actions", definition).VisualRole("Action.Primary").Build();
        var registry = new UiRegistryBuilder().Window(id, "Host actions", () => experience).Freeze();
        var invocation = new UiInvocationService(registry).Invoke(id, UiPresentationProfiles.Wide);
        var catalog = UiSemanticCatalog.CreateFoundation();
        var compilation = new UiCompiler(catalog).Compile("visual Actions\n\n" + recipes, experience.CreateBindingContext(), "Actions#visual");
        Assert.True(compilation.IsValid, string.Join(Environment.NewLine, compilation.Diagnostics.Select(d => d.Message)));
        return new UiSceneComposer(UiThemePresets.Dark(), registry, catalog).Compose(invocation,
            Assert.IsType<UiVisualDefinition>(compilation.Definition),
            focused ? new UiInteractionSnapshot(Focused: definition.Id.Child("scene/button")) : null);
    }

    private static UiScene SearchScene(UiActionDefinition action)
    {
        UiSymbolId id = Id("window");
        var experience = new UiExperienceBuilder(id, "Host actions")
            .Search("Search", new UiState<string>("x")).Actions("Actions", action).Build();
        var registry = new UiRegistryBuilder().Window(id, "Host actions", () => experience).Freeze();
        var invocation = new UiInvocationService(registry).Invoke(id, UiPresentationProfiles.Wide);
        return new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(invocation);
    }

    private static UiScene Scene(params UiActionDefinition[] actions)
    {
        UiSymbolId id = Id("window");
        var builder = new UiExperienceBuilder(id, "Host actions");
        if (actions.Length != 0) builder.Actions("Actions", actions);
        else builder.Search("Search", new UiState<string>("No actions"));
        var experience = builder.Build();
        var registry = new UiRegistryBuilder().Window(id, "Host actions", () => experience).Freeze();
        var invocation = new UiInvocationService(registry).Invoke(id, UiPresentationProfiles.Wide);
        return new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(invocation);
    }
    private static readonly UiRect Viewport = new(0, 0, 420, 240);
    private static UiHostRuntimeSession Host(UiScene scene, Platform? platform = null)
        => new(scene, Viewport, platform ?? new Platform(),
            new UiInteractionSnapshot(Focused: Nodes(scene.Root).OfType<UiButtonSceneNode>().FirstOrDefault()?.Id));
    private static UiAction<int, int> Action(Func<int, CancellationToken, ValueTask<UiActionResult<int>>> execute,
        UiSymbolId? id = null, UiActionConcurrency? concurrency = null, Func<UiActionAvailability>? availability = null)
        => new(id ?? Id("action"), "Action", execute, concurrency ?? UiActionConcurrency.RejectWhileRunning, availability);
    private static UiSymbolId Id(string path) => new("Tests", "host-actions/" + path);
    private static TaskCompletionSource<UiActionResult<int>> Completion() => new();
    private static IEnumerable<UiSceneNode> Nodes(UiSceneNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        foreach (var descendant in Nodes(child)) yield return descendant;
    }
    private static Hatifect.UI.Runtime.Accessibility.UiAccessibilityNodeSnapshot FindAccessibility(UiHostRuntimeSession host, UiActionDefinition action)
    {
        var pending = new Stack<Hatifect.UI.Runtime.Accessibility.UiAccessibilityNodeSnapshot>();
        pending.Push(host.Accessibility.Root);
        while (pending.TryPop(out var node))
        {
            if (node.Id == action.Id.Child("scene/button")) return node;
            foreach (var child in node.Children) pending.Push(child);
        }
        throw new Xunit.Sdk.XunitException("Action is missing from accessibility.");
    }
    private static void PumpUntil(UiHostRuntimeSession host, Func<bool> completed)
        => Assert.True(SpinWait.SpinUntil(() => { host.PumpActions(); return completed(); }, TimeSpan.FromSeconds(5)), "Action did not complete.");
    private static void OnWorker(System.Action action)
    {
        Exception? error = null;
        var worker = new Thread(() => { try { action(); } catch (Exception caught) { error = caught; } });
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(error);
    }
    private sealed class Platform : IUiPlatformBridge
    {
        internal System.Action? OnMeasure { get; set; }
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
        {
            OnMeasure?.Invoke();
            return new(Math.Min(text.Length * typography.Size * 0.6f, availableWidth), typography.Size * typography.LineHeight);
        }
        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }
}
