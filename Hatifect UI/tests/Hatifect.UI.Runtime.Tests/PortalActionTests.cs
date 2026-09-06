using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Rendering;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Theming;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class PortalActionTests
{
    [Fact]
    public void RootRetirementFencesChildInputBeforeRootCancellationCallbacks()
    {
        var pending = new TaskCompletionSource<UiActionResult<int>>();
        UiPortalHostSession? host = null;
        int childEffects = 0, cancellations = 0;
        Exception? cancellationInputError = null;
        var rootAction = new UiAction<int, int>(Id("root-action"), "Run", (_, token) =>
        {
            token.Register(() =>
            {
                cancellations++;
                cancellationInputError = Record.Exception(() => host!.Submit());
            });
            return new(pending.Task);
        }, UiActionConcurrency.RejectWhileRunning).Bind(() => 1);
        var root = Scene("root", UiHostPolicies.Window, rootAction);
        host = Host(root);
        try
        {
            host.MoveFocus(UiNavigationDirection.Next);
            Assert.True(host.Submit().Interaction!.ActionInvoked);
            var child = Scene("child", UiHostPolicies.Popup,
                new UiActionDefinition(Id("child-action"), "Child", () => childEffects++));
            using var handle = host.Present(Request("child", root.Root.Id, child));

            host.Deactivate();

            Assert.Equal(1, cancellations);
            Assert.Equal(0, childEffects);
            Assert.IsType<ObjectDisposedException>(cancellationInputError);
            Assert.Empty(host.ActivePortals);
            Assert.False(host.Root.Actions.CanInvoke(rootAction));
        }
        finally { host.Deactivate(); pending.TrySetResult(UiActionResult<int>.Success(1)); }
    }

    [Fact]
    public void ClosingTreeDetachesDescendantsBeforeCancellationCanUpdateOrReopenPortals()
    {
        var pending = new TaskCompletionSource<UiActionResult<int>>();
        var root = Scene("root", UiHostPolicies.Window);
        var host = Host(root);
        var child = Scene("child", UiHostPolicies.Context);
        UiSymbolId parentId = Id("parent"), childId = Id("child");
        UiPortalHandle? replacement = null;
        Exception? childUpdateError = null, replacementError = null;
        int cancellations = 0;
        var action = new UiAction<int, int>(Id("parent-action"), "Run", (_, token) =>
        {
            token.Register(() =>
            {
                cancellations++;
                childUpdateError = Record.Exception(() => host.UpdatePortal(childId, child, Placement));
                replacementError = Record.Exception(() => replacement = host.Present(
                    Request("parent", root.Root.Id, Scene("replacement", UiHostPolicies.Popup))));
            });
            return new(pending.Task);
        }, UiActionConcurrency.RejectWhileRunning).Bind(() => 1);
        var parent = Scene("parent", UiHostPolicies.Popup, action);
        using var parentHandle = host.Present(Request("parent", root.Root.Id, parent));
        try
        {
            Assert.True(host.Submit().Interaction!.ActionInvoked);
            using var childHandle = host.Present(new UiPortalRequest(childId,
                new UiPortalOwner(parent.Root.Id, parentId), child, Placement));

            parentHandle.Dispose();

            Assert.Equal(1, cancellations);
            Assert.IsType<KeyNotFoundException>(childUpdateError);
            Assert.Null(replacementError);
            Assert.NotNull(replacement);
            Assert.Equal(parentId, Assert.Single(host.ActivePortals));
            childHandle.Dispose();
            parentHandle.Dispose();
            Assert.Equal(parentId, Assert.Single(host.ActivePortals));
            replacement.Dispose();
            Assert.Empty(host.ActivePortals);
        }
        finally { replacement?.Dispose(); host.Deactivate(); pending.TrySetResult(UiActionResult<int>.Success(1)); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AcceptedOwnerRemovalFencesAllOrphansBeforeAnyCancellation(bool inPortal, bool ownerRunning)
    {
        var ownerPending = new TaskCompletionSource<UiActionResult<int>>();
        var childPending = new TaskCompletionSource<UiActionResult<int>>();
        UiPortalHostSession? host = null;
        var cancellationMembership = new List<UiSymbolId[]>();
        var cancellationErrors = new List<Exception?>();
        int orphanEffects = 0, survivorEffects = 0;
        void Cancelled()
        {
            cancellationMembership.Add(host!.ActivePortals.ToArray());
            cancellationErrors.Add(Record.Exception(() => host.Submit()));
        }
        var ownerAction = Bound("owner-action", ownerPending, _ => { }, token => token.Register(Cancelled));
        var childAction = Bound("child-action", childPending, _ => { }, token => token.Register(Cancelled));
        var ownerScene = Scene("owner", inPortal ? UiHostPolicies.Popup : UiHostPolicies.Window, ownerAction);
        var root = inPortal ? Scene("root", UiHostPolicies.Window) : ownerScene;
        host = Host(root);
        UiPortalHandle? ownerHandle = null;
        try
        {
            if (inPortal) ownerHandle = host.Present(Request("owner", root.Root.Id, ownerScene));
            else host.MoveFocus(UiNavigationDirection.Next);
            if (ownerRunning) Assert.True(host.Submit().Interaction!.ActionInvoked);
            var owner = new UiPortalOwner(ownerAction.Id.Child("scene/button"), inPortal ? Id("owner") : null);
            using var first = host.Present(new UiPortalRequest(Id("first"), owner,
                Scene("first", UiHostPolicies.Popup,
                    new UiActionDefinition(Id("first-action"), "First", () => orphanEffects++)), Placement));
            var secondScene = Scene("second", UiHostPolicies.Popup, childAction);
            using var second = host.Present(new UiPortalRequest(Id("second"), owner, secondScene, Placement));
            Assert.True(host.Submit().Interaction!.ActionInvoked);
            using var descendant = host.Present(new UiPortalRequest(Id("descendant"),
                new UiPortalOwner(secondScene.Root.Id, Id("second")),
                Scene("descendant", UiHostPolicies.Popup,
                    new UiActionDefinition(Id("descendant-action"), "Descendant", () => orphanEffects++)), Placement));
            var next = Scene("owner", ownerScene.Root.Policy,
                new UiActionDefinition(Id("survivor"), "Survivor", () => survivorEffects++));

            if (inPortal) host.UpdatePortal(Id("owner"), next, Placement);
            else host.UpdateRoot(next, Placement);

            Assert.Equal(0, orphanEffects);
            Assert.Equal(0, survivorEffects);
            Assert.Equal(ownerRunning ? 2 : 1, cancellationMembership.Count);
            UiSymbolId[] remaining = inPortal ? new[] { Id("owner") } : Array.Empty<UiSymbolId>();
            Assert.All(cancellationMembership, membership => Assert.Equal(remaining, membership));
            Assert.All(cancellationErrors, error => Assert.IsType<InvalidOperationException>(error));
            Assert.Equal(remaining, host.ActivePortals);
            host.MoveFocus(UiNavigationDirection.Next);
            Assert.True(host.Submit().Interaction!.ActionInvoked);
            Assert.Equal(1, survivorEffects);
        }
        finally
        {
            ownerHandle?.Dispose();
            host.Deactivate();
            ownerPending.TrySetResult(UiActionResult<int>.Success(1));
            childPending.TrySetResult(UiActionResult<int>.Success(2));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DetachedOrphansAreCancelledWhenOwnerCancellationRetiresItsHost(bool closeWholeHost)
    {
        var ownerPending = new TaskCompletionSource<UiActionResult<int>>();
        var firstPending = new TaskCompletionSource<UiActionResult<int>>();
        var secondPending = new TaskCompletionSource<UiActionResult<int>>();
        var root = Scene("root", UiHostPolicies.Window);
        var host = Host(root);
        UiPortalHandle? ownerHandle = null;
        CancellationToken firstCancellation = default, secondCancellation = default;
        int cancellations = 0;
        bool nestedPump = true;
        var ownerAction = Bound("owner-action", ownerPending, _ => { }, token => token.Register(() =>
        {
            cancellations++;
            nestedPump = host.PumpActions();
            if (closeWholeHost) host.Deactivate();
            else ownerHandle!.Dispose();
        }));
        var ownerScene = Scene("owner", UiHostPolicies.Popup, ownerAction);
        ownerHandle = host.Present(Request("owner", root.Root.Id, ownerScene));
        try
        {
            Assert.True(host.Submit().Interaction!.ActionInvoked);
            var owner = new UiPortalOwner(ownerAction.Id.Child("scene/button"), Id("owner"));
            using var first = host.Present(new UiPortalRequest(Id("first"), owner,
                Scene("first", UiHostPolicies.Popup, Bound("first-action", firstPending, _ => { },
                    token => firstCancellation = token)), Placement));
            Assert.True(host.Submit().Interaction!.ActionInvoked);
            using var second = host.Present(new UiPortalRequest(Id("second"), owner,
                Scene("second", UiHostPolicies.Popup, Bound("second-action", secondPending, _ => { },
                    token => secondCancellation = token)), Placement));
            Assert.True(host.Submit().Interaction!.ActionInvoked);

            host.UpdatePortal(Id("owner"), Scene("owner", UiHostPolicies.Popup), Placement);

            Assert.Equal(1, cancellations);
            Assert.False(nestedPump);
            Assert.True(firstCancellation.IsCancellationRequested);
            Assert.True(secondCancellation.IsCancellationRequested);
            Assert.Empty(host.ActivePortals);
            Assert.Equal(!closeWholeHost, host.Root.IsActive);
            if (!closeWholeHost)
            {
                using var replacement = host.Present(Request("owner", root.Root.Id, ownerScene));
                Assert.Equal(Id("owner"), Assert.Single(host.ActivePortals));
            }
        }
        finally
        {
            ownerHandle.Dispose();
            host.Deactivate();
            ownerPending.TrySetResult(UiActionResult<int>.Success(1));
            firstPending.TrySetResult(UiActionResult<int>.Success(2));
            secondPending.TrySetResult(UiActionResult<int>.Success(3));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedOwnerRemovalKeepsPortalMembershipAndPendingActions(bool inPortal)
    {
        var ownerPending = new TaskCompletionSource<UiActionResult<int>>();
        var childPending = new TaskCompletionSource<UiActionResult<int>>();
        CancellationToken ownerCancellation = default, childCancellation = default;
        int childCompletions = 0;
        var ownerAction = Bound("owner-action", ownerPending, _ => { }, token => ownerCancellation = token);
        var ownerScene = Scene("owner", inPortal ? UiHostPolicies.Popup : UiHostPolicies.Window, ownerAction);
        var root = inPortal ? Scene("root", UiHostPolicies.Window) : ownerScene;
        var platform = new Platform();
        var host = new UiPortalHostSession(root, Placement, platform);
        UiPortalHandle? ownerHandle = null;
        try
        {
            if (inPortal) ownerHandle = host.Present(Request("owner", root.Root.Id, ownerScene));
            else host.MoveFocus(UiNavigationDirection.Next);
            Assert.True(host.Submit().Interaction!.ActionInvoked);
            var childAction = Bound("child-action", childPending, _ => childCompletions++, token => childCancellation = token);
            using var child = host.Present(new UiPortalRequest(Id("child"),
                new UiPortalOwner(ownerAction.Id.Child("scene/button"), inPortal ? Id("owner") : null),
                Scene("child", UiHostPolicies.Popup, childAction), Placement));
            Assert.True(host.Submit().Interaction!.ActionInvoked);
            var next = Scene("owner", ownerScene.Root.Policy);
            var failure = new InvalidOperationException("Rejected owner metrics");
            platform.OnMeasure = () => throw failure;

            Exception? error = Record.Exception(() =>
            {
                if (inPortal) host.UpdatePortal(Id("owner"), next, Placement);
                else host.UpdateRoot(next, Placement);
            });

            Assert.Same(failure, error);
            Assert.Equal(inPortal ? new[] { Id("owner"), Id("child") } : new[] { Id("child") }, host.ActivePortals);
            Assert.False(ownerCancellation.IsCancellationRequested);
            Assert.False(childCancellation.IsCancellationRequested);
            platform.OnMeasure = null;
            Assert.Null(OnWorker(() => childPending.SetResult(UiActionResult<int>.Success(2))));
            Assert.True(host.PumpActions());
            Assert.Equal(1, childCompletions);
            if (inPortal) host.UpdatePortal(Id("owner"), next, Placement);
            else host.UpdateRoot(next, Placement);
            Assert.True(ownerCancellation.IsCancellationRequested);
            Assert.Equal(inPortal ? new[] { Id("owner") } : Array.Empty<UiSymbolId>(), host.ActivePortals);
        }
        finally
        {
            platform.OnMeasure = null;
            ownerHandle?.Dispose();
            host.Deactivate();
            ownerPending.TrySetResult(UiActionResult<int>.Success(1));
            childPending.TrySetResult(UiActionResult<int>.Success(2));
        }
    }

    [Fact]
    public void InteractionRecompositionRetiresOrphansBeforeRemovedOwnerCancellation()
    {
        var pending = new TaskCompletionSource<UiActionResult<int>>();
        UiPortalHostSession? host = null;
        int orphanEffects = 0, cancellations = 0;
        Exception? cancellationError = null;
        UiSymbolId[]? cancellationMembership = null;
        var ownerAction = Bound("owner-action", pending, _ => { }, token => token.Register(() =>
        {
            cancellations++;
            cancellationMembership = host!.ActivePortals.ToArray();
            cancellationError = Record.Exception(() => host.Submit());
        }));
        var retained = new UiActionDefinition(Id("retained-action"), "Retained", () => { });
        var root = Scene("root", UiHostPolicies.Window, ownerAction, retained);
        var next = Scene("root", UiHostPolicies.Window, retained);
        bool removeOwner = false;
        host = new UiPortalHostSession(root, Placement, new Platform(),
            composeInteraction: _ => removeOwner ? next : root);
        try
        {
            host.MoveFocus(UiNavigationDirection.Next);
            Assert.True(host.Submit().Interaction!.ActionInvoked);
            using var portal = host.Present(Request("child", ownerAction.Id.Child("scene/button"),
                Scene("child", UiHostPolicies.Popup,
                    new UiActionDefinition(Id("child-action"), "Child", () => orphanEffects++))));
            Assert.True(host.Root.Layout.TryGetEntry(retained.Id.Child("scene/button"), out var entry));
            var point = new UiPoint(entry!.Bounds.X + entry.Bounds.Width / 2, entry.Bounds.Y + entry.Bounds.Height / 2);
            removeOwner = true;

            host.MovePointer(point);

            Assert.Same(next, host.Root.Scene);
            Assert.Equal(1, cancellations);
            Assert.Equal(0, orphanEffects);
            Assert.NotNull(cancellationMembership);
            Assert.Empty(cancellationMembership);
            Assert.IsType<InvalidOperationException>(cancellationError);
            Assert.Empty(host.ActivePortals);
        }
        finally { host.Deactivate(); pending.TrySetResult(UiActionResult<int>.Success(1)); }
    }

    [Fact]
    public void RetainedRetiredRuntimeDoesNotKeepItsPortalGraphAlive()
    {
        var (runtime, graph) = RetiredGraph();
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(graph.IsAlive);
        Assert.False(runtime.IsActive);
        GC.KeepAlive(runtime);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (UiHostRuntimeSession Runtime, WeakReference Graph) RetiredGraph()
    {
        var host = Host(Scene("root", UiHostPolicies.Window));
        var graph = new WeakReference(host);
        host.Deactivate();
        return (host.Root, graph);
    }

    [Fact]
    public void WrongThreadHostRetirementCannotInvalidateTheOwnerSession()
    {
        var root = Scene("root", UiHostPolicies.Window);
        var host = Host(root);
        try
        {
            var before = host.Root.Frame;
            Assert.IsType<InvalidOperationException>(OnWorker(host.Deactivate));
            Assert.True(host.Root.IsActive);
            Assert.Same(before, host.Root.Frame);
            host.UpdateRoot(root, Placement);
            using var handle = host.Present(Request("after-refusal", root.Root.Id,
                Scene("child", UiHostPolicies.Popup)));
            Assert.Equal(Id("after-refusal"), Assert.Single(host.ActivePortals));
        }
        finally { host.Deactivate(); host.Root.Deactivate(); }
    }

    [Fact]
    public void WrongThreadHandleDisposalRetainsTheOwnersCloseCapability()
    {
        var root = Scene("root", UiHostPolicies.Window);
        var host = Host(root);
        try
        {
            using var handle = host.Present(Request("child", root.Root.Id,
                Scene("child", UiHostPolicies.Popup)));
            Assert.IsType<InvalidOperationException>(OnWorker(handle.Dispose));
            Assert.Equal(Id("child"), Assert.Single(host.ActivePortals));

            handle.Dispose();

            Assert.Empty(host.ActivePortals);
            Assert.True(host.Root.IsActive);
        }
        finally { host.Deactivate(); }
    }

    [Fact]
    public void OwningPumpCompletesRootAndPortalWithoutInputOrRecomposition()
    {
        var rootPending = new TaskCompletionSource<UiActionResult<int>>();
        var childPending = new TaskCompletionSource<UiActionResult<int>>();
        int ownerThread = Environment.CurrentManagedThreadId;
        var callbacks = new List<(string Action, int Thread, int Result)>();
        var rootAction = Bound("root-action", rootPending,
            result => callbacks.Add(("root", Environment.CurrentManagedThreadId, result.Value)));
        var childAction = Bound("child-action", childPending,
            result => callbacks.Add(("child", Environment.CurrentManagedThreadId, result.Value)));
        var root = Scene("root", UiHostPolicies.Window, rootAction);
        var host = Host(root);
        try
        {
            host.MoveFocus(UiNavigationDirection.Next);
            Assert.True(host.Submit().Interaction!.ActionInvoked);
            using var handle = host.Present(Request("child", root.Root.Id,
                Scene("child", UiHostPolicies.Popup, childAction)));
            Assert.True(host.Submit().Interaction!.ActionInvoked);
            Assert.Null(OnWorker(() =>
            {
                rootPending.SetResult(UiActionResult<int>.Success(7));
                childPending.SetResult(UiActionResult<int>.Success(9));
            }));
            Assert.IsType<InvalidOperationException>(OnWorker(() => host.PumpActions()));
            host.Render();
            _ = host.Accessibility;
            host.UpdateRoot(root, Placement);
            Assert.Empty(callbacks);

            Assert.True(host.PumpActions());

            Assert.Equal(new[] { ("root", ownerThread, 7), ("child", ownerThread, 9) }, callbacks);
            Assert.Equal(UiActionState.Completed, host.Root.Actions.Status(rootAction)!.State);
            Assert.True(host.Root.Actions.CanInvoke(rootAction));
            Assert.False(host.PumpActions());
        }
        finally { host.Deactivate(); }
    }

    [Fact]
    public void RootCompletionMayReplaceAChildButTheNewChildWaitsUntilTheNextPump()
    {
        var rootPending = new TaskCompletionSource<UiActionResult<int>>();
        var oldPending = new TaskCompletionSource<UiActionResult<int>>();
        var newPending = new TaskCompletionSource<UiActionResult<int>>();
        UiPortalHostSession? host = null;
        UiPortalHandle? oldHandle = null, newHandle = null;
        CancellationToken oldCancellation = default;
        int rootCallbacks = 0, oldCallbacks = 0, newCallbacks = 0;
        bool reentrantPump = true;
        Exception? workerError = null;
        var newAction = Bound("new-action", newPending, _ => newCallbacks++);
        var newScene = Scene("new-child", UiHostPolicies.Popup, newAction);
        var oldAction = Bound("old-action", oldPending, _ => oldCallbacks++, token => oldCancellation = token);
        var rootAction = Bound("root-action", rootPending, _ =>
        {
            rootCallbacks++;
            oldHandle!.Dispose();
            newHandle = host!.Present(Request("new-child", host.Root.Scene.Root.Id, newScene));
            host.Submit();
            workerError = OnWorker(() => newPending.SetResult(UiActionResult<int>.Success(3)));
            reentrantPump = host.PumpActions();
        });
        var root = Scene("root", UiHostPolicies.Window, rootAction);
        host = Host(root);
        try
        {
            host.MoveFocus(UiNavigationDirection.Next);
            Assert.True(host.Submit().Interaction!.ActionInvoked);
            oldHandle = host.Present(Request("old-child", root.Root.Id,
                Scene("old-child", UiHostPolicies.Popup, oldAction)));
            Assert.True(host.Submit().Interaction!.ActionInvoked);
            Assert.Null(OnWorker(() =>
            {
                rootPending.SetResult(UiActionResult<int>.Success(1));
            }));

            Assert.True(host.PumpActions());

            Assert.Equal(1, rootCallbacks);
            Assert.True(oldCancellation.IsCancellationRequested);
            Assert.Equal(0, oldCallbacks);
            Assert.Equal(0, newCallbacks);
            Assert.False(reentrantPump);
            Assert.Null(workerError);
            Assert.Null(host.Root.Actions.Status(rootAction)!.ObserverError);
            Assert.Equal(Id("new-child"), Assert.Single(host.ActivePortals));
            Assert.Null(OnWorker(() => oldPending.SetResult(UiActionResult<int>.Success(2))));
            Assert.True(host.PumpActions());
            Assert.Equal(1, newCallbacks);
            Assert.Equal(0, oldCallbacks);
        }
        finally { newHandle?.Dispose(); oldHandle?.Dispose(); host.Deactivate(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RootCompletionRetiresChildrenBeforeTheyCanPublish(bool closeWholeHost, bool ready)
    {
        var rootPending = new TaskCompletionSource<UiActionResult<int>>();
        var childPending = new TaskCompletionSource<UiActionResult<int>>();
        UiPortalHostSession? host = null;
        CancellationToken childCancellation = default;
        int childCallbacks = 0, rootCallbacks = 0;
        var replacement = Scene("root", UiHostPolicies.Window);
        var rootAction = Bound("root-action", rootPending, _ =>
        {
            rootCallbacks++;
            if (closeWholeHost) host!.Deactivate();
            else host!.UpdateRoot(replacement, Placement);
        });
        var root = Scene("root", UiHostPolicies.Window, rootAction);
        host = Host(root);
        try
        {
            host.MoveFocus(UiNavigationDirection.Next);
            Assert.True(host.Submit().Interaction!.ActionInvoked);
            var childAction = Bound("child-action", childPending, _ => childCallbacks++, token => childCancellation = token);
            using var handle = host.Present(Request("child", rootAction.Id.Child("scene/button"),
                Scene("child", UiHostPolicies.Popup, childAction)));
            Assert.True(host.Submit().Interaction!.ActionInvoked);
            Assert.Null(OnWorker(() =>
            {
                rootPending.SetResult(UiActionResult<int>.Success(1));
                if (ready) childPending.SetResult(UiActionResult<int>.Success(2));
            }));

            Assert.True(host.PumpActions());

            Assert.Equal(1, rootCallbacks);
            Assert.Equal(0, childCallbacks);
            // A completed executor has already released its cancellation source. Retirement
            // cancels unfinished work and suppresses both ready and late observer delivery.
            Assert.Equal(!ready, childCancellation.IsCancellationRequested);
            Assert.Empty(host.ActivePortals);
            Assert.Equal(!closeWholeHost, host.Root.IsActive);
            if (!closeWholeHost) Assert.Same(replacement, host.Root.Scene);
            if (!ready) Assert.Null(OnWorker(() => childPending.SetResult(UiActionResult<int>.Success(2))));
            Assert.False(host.PumpActions());
            Assert.Equal(0, childCallbacks);
        }
        finally { host.Deactivate(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnchangedPortalPumpKeepsFramesWithoutAllocatingPerTick(bool running)
    {
        var pending = new TaskCompletionSource<UiActionResult<int>>();
        var root = Scene("root", UiHostPolicies.Window,
            new UiActionDefinition(Id("disabled"), "Unavailable", () => { }, () => false));
        var host = Host(root);
        try
        {
            var childAction = Bound("child-action", pending, _ => { });
            using var handle = host.Present(Request("child", root.Root.Id,
                Scene("child", UiHostPolicies.Popup, childAction)));
            if (running) Assert.True(host.Submit().Interaction!.ActionInvoked);
            for (int i = 0; i < 100; i++) host.PumpActions();
            var frame = host.Root.Frame;
            var layout = host.Root.Layout;
            var counters = host.Performance;
            int changes = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < 1000; i++) if (host.PumpActions()) changes++;

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.InRange(allocated, 0, 64);
            Assert.Equal(0, changes);
            Assert.Equal(counters, host.Performance);
            Assert.Same(frame, host.Root.Frame);
            Assert.Same(layout, host.Root.Layout);
        }
        finally { host.Deactivate(); pending.TrySetResult(UiActionResult<int>.Success(1)); }
    }

    private static UiActionDefinition Bound(string id, TaskCompletionSource<UiActionResult<int>> pending,
        Action<UiActionResult<int>> completed, Action<CancellationToken>? admitted = null)
        => new UiAction<int, int>(Id(id), id, (_, cancellation) =>
        {
            admitted?.Invoke(cancellation);
            return new(pending.Task);
        }, UiActionConcurrency.RejectWhileRunning).Bind(() => 1, (_, result) => completed(result));

    private static readonly UiHostPlacementContext Placement = new(new UiRect(0, 0, 800, 480),
        anchor: new UiRect(520, 180, 40, 30), pointer: new UiPoint(540, 200));
    private static UiSymbolId Id(string path) => new("Tests", "portal-actions/" + path);
    private static UiPortalHostSession Host(UiScene scene) => new(scene, Placement, new Platform());
    private static UiPortalRequest Request(string id, UiSymbolId owner, UiScene scene)
        => new(Id(id), new UiPortalOwner(owner), scene, Placement);
    private static UiScene Scene(string id, UiHostPolicy policy, params UiActionDefinition[] actions)
    {
        var builder = new UiExperienceBuilder(Id(id), id).Monitor("Status", new UiConstantSource<string>(id));
        if (actions.Length != 0) builder.Actions("Actions", actions);
        var experience = builder.Build();
        var registry = new UiRegistryBuilder().Window(Id(id), id, () => experience, policy).Freeze();
        return new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(
            new UiInvocationService(registry).Invoke(Id(id), UiPresentationProfiles.Wide));
    }
    private static Exception? OnWorker(Action action)
    {
        Exception? error = null;
        var worker = new Thread(() => error = Record.Exception(action)) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        return error;
    }
    private sealed class Platform : IUiPlatformBridge
    {
        public Action? OnMeasure { get; set; }
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
        {
            OnMeasure?.Invoke();
            return new(Math.Min(text.Length * typography.Size * 0.6f, availableWidth), typography.Size * typography.LineHeight);
        }
        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }
}
