using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
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

public sealed class PortalHostTests
{
    [Fact]
    public void PortalRendersAfterOwnerAndHandleRetiresItSynchronously()
    {
        UiScene root = Scene("portal-root", UiHostPolicies.Window, actionCount: 1);
        UiScene popup = Scene("portal-popup", UiHostPolicies.Popup, actionCount: 1);
        var platform = new RecordingPlatform();
        var session = Host(root, platform);
        UiSymbolId owner = Assert.Single(Nodes(root.Root).OfType<UiButtonSceneNode>()).Id;
        UiSymbolId portalId = RegistryTests.Id("portal/inspect");

        using UiPortalHandle handle = session.Present(Request(portalId, owner, popup));
        session.Render();

        HashSet<UiSymbolId> rootNodes = Nodes(root.Root).Select(node => node.Id).ToHashSet();
        HashSet<UiSymbolId> popupNodes = Nodes(popup.Root).Select(node => node.Id).ToHashSet();
        int lastRootPrimitive = platform.Drawn.FindLastIndex(rootNodes.Contains);
        int firstPopupPrimitive = platform.Drawn.FindIndex(popupNodes.Contains);

        Assert.True(lastRootPrimitive >= 0);
        Assert.True(firstPopupPrimitive > lastRootPrimitive);
        Assert.Contains(platform.Drawn[^1], popupNodes);
        Assert.Contains(portalId, session.ActivePortals);
        handle.Dispose();
        Assert.Empty(session.ActivePortals);
    }

    [Fact]
    public void MissingOwnerAndDuplicatePortalFailClosed()
    {
        UiScene root = Scene("portal-root", UiHostPolicies.Window, actionCount: 1);
        UiScene popup = Scene("portal-popup", UiHostPolicies.Popup, actionCount: 1);
        var session = Host(root, new RecordingPlatform());
        UiSymbolId portalId = RegistryTests.Id("portal/inspect");
        UiSymbolId owner = Assert.Single(Nodes(root.Root).OfType<UiButtonSceneNode>()).Id;

        Assert.Throws<InvalidOperationException>(() =>
            session.Present(Request(portalId, RegistryTests.Id("missing-owner"), popup)));
        using UiPortalHandle handle = session.Present(Request(portalId, owner, popup));
        Assert.Throws<InvalidOperationException>(() => session.Present(Request(portalId, owner, popup)));
    }

    [Fact]
    public void RetiringOwnerAlsoRetiresNestedPortalTree()
    {
        UiScene before = Scene("portal-root", UiHostPolicies.Window, actionCount: 1);
        UiScene after = Scene("portal-root", UiHostPolicies.Window, actionCount: 0);
        UiScene popup = Scene("portal-popup", UiHostPolicies.Popup, actionCount: 1);
        UiScene nested = Scene("portal-nested", UiHostPolicies.Context, actionCount: 1);
        var session = Host(before, new RecordingPlatform());
        UiSymbolId rootOwner = Assert.Single(Nodes(before.Root).OfType<UiButtonSceneNode>()).Id;
        UiSymbolId popupId = RegistryTests.Id("portal/inspect");
        UiSymbolId nestedId = RegistryTests.Id("portal/context");
        using UiPortalHandle popupHandle = session.Present(Request(popupId, rootOwner, popup));
        using UiPortalHandle nestedHandle = session.Present(new UiPortalRequest(
            nestedId,
            new UiPortalOwner(popup.Root.Id, popupId),
            nested,
            new UiHostPlacementContext(Viewport, pointer: new UiPoint(500, 300))));

        session.UpdateRoot(after, new UiHostPlacementContext(Viewport));

        Assert.Empty(session.ActivePortals);
    }

    [Fact]
    public void OutsidePressDismissesTopPopupWithoutClickThrough()
    {
        int invoked = 0;
        UiScene root = Scene("portal-root", UiHostPolicies.Window, actionCount: 1, () => invoked++);
        UiScene popup = Scene("portal-popup", UiHostPolicies.Popup, actionCount: 1);
        var session = Host(root, new RecordingPlatform());
        UiButtonSceneNode rootButton = Assert.Single(Nodes(root.Root).OfType<UiButtonSceneNode>());
        UiSymbolId popupId = RegistryTests.Id("portal/inspect");
        using UiPortalHandle handle = session.Present(Request(popupId, rootButton.Id, popup));
        Assert.True(session.Root.Layout.TryGet(rootButton.Id, out UiRect rootBounds));
        UiPoint point = new(rootBounds.X + 1, rootBounds.Y + 1);

        UiPortalDispatch dispatch = session.PressPointer(point);

        Assert.True(dispatch.Consumed);
        Assert.True(dispatch.PortalClosed);
        Assert.Equal(popupId, dispatch.Portal);
        Assert.Equal(0, invoked);
        Assert.Empty(session.ActivePortals);
    }

    [Fact]
    public void ModalBlocksPointerOutsideItsBounds()
    {
        int invoked = 0;
        UiScene root = Scene("portal-root", UiHostPolicies.Window, actionCount: 1, () => invoked++);
        UiScene modal = Scene("portal-modal", UiHostPolicies.Modal, actionCount: 1);
        var session = Host(root, new RecordingPlatform());
        UiButtonSceneNode rootButton = Assert.Single(Nodes(root.Root).OfType<UiButtonSceneNode>());
        using UiPortalHandle handle = session.Present(Request(
            RegistryTests.Id("portal/modal"), rootButton.Id, modal));
        var outside = new UiPoint(13, 13);

        UiPortalDispatch dispatch = session.PressPointer(outside);

        Assert.True(dispatch.Consumed);
        Assert.Equal(0, invoked);
        Assert.Single(session.ActivePortals);
    }

    [Fact]
    public void KeyboardSubmitTargetsTopPortalFocusScope()
    {
        int rootInvoked = 0;
        int popupInvoked = 0;
        UiScene root = Scene("portal-root", UiHostPolicies.Window, actionCount: 1, () => rootInvoked++);
        UiScene popup = Scene("portal-popup", UiHostPolicies.Popup, actionCount: 1, () => popupInvoked++);
        var session = Host(root, new RecordingPlatform());
        UiSymbolId owner = Assert.Single(Nodes(root.Root).OfType<UiButtonSceneNode>()).Id;
        UiSymbolId portalId = RegistryTests.Id("portal/keyboard");
        using UiPortalHandle handle = session.Present(Request(portalId, owner, popup));

        UiPortalDispatch dispatch = session.Submit();

        Assert.True(dispatch.Consumed);
        Assert.Equal(portalId, dispatch.Portal);
        Assert.Equal(0, rootInvoked);
        Assert.Equal(1, popupInvoked);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void RootPointerOwnershipHonorsModalityWithoutInvokingOrDismissing(bool modal, bool inside)
    {
        int invoked = 0;
        UiHostPolicy policy = modal ? UiProvisionalHostPolicies.OverlayCentered(false) : UiHostPolicies.Overlay;
        UiScene root = Scene("root-pointer-ownership", policy, actionCount: 1, () => invoked++);
        var session = Host(root, new RecordingPlatform());
        Assert.True(session.Root.Layout.TryGet(root.Root.Id, out UiRect bounds));
        UiPoint point = inside ? new UiPoint(bounds.X + 1, bounds.Y + 1) : new UiPoint(bounds.X - 1, bounds.Y - 1);
        Assert.Equal(inside, session.Root.Contains(point));

        UiPortalDispatch press = session.PressPointer(point);
        UiPortalDispatch release = session.ReleasePointer(point);

        Assert.Equal(modal, press.Consumed);
        Assert.Equal(modal, release.Consumed);
        Assert.False(press.Interaction!.DismissRequested);
        Assert.False(release.Interaction!.ActionInvoked);
        Assert.Equal(0, invoked);
        Assert.Null(session.Root.Interactions.Snapshot.Pressed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RootScrollAndUnfocusedSubmitHonorModality(bool modal)
    {
        int invoked = 0;
        UiScene root = Scene("root-unhandled-input",
            modal ? UiProvisionalHostPolicies.OverlayCentered(false) : UiHostPolicies.Overlay,
            actionCount: 1, () => invoked++);
        var session = Host(root, new RecordingPlatform());
        Assert.Null(session.Root.Interactions.Snapshot.Focused);

        UiPortalScrollDispatch scroll = session.ScrollAt(new UiPoint(13, 13), 120);
        UiPortalDispatch submit = session.Submit();

        Assert.Equal(modal, scroll.Consumed);
        Assert.Equal(modal, submit.Consumed);
        Assert.False(scroll.Scroll!.LayoutChanged);
        Assert.False(submit.Interaction!.ActionInvoked);
        Assert.Equal(0, invoked);
    }

    [Fact]
    public void ModelessPortalDoesNotReleaseModalRootInputOwnership()
    {
        UiScene root = Scene("modal-root", UiProvisionalHostPolicies.OverlayCentered(false), actionCount: 0);
        UiScene overlay = Scene("modeless-child", UiHostPolicies.Overlay, actionCount: 0);
        var session = Host(root, new RecordingPlatform());
        using UiPortalHandle handle = session.Present(Request(
            RegistryTests.Id("portal/modeless-child"), root.Root.Id, overlay));

        Assert.True(session.Submit().Consumed);
        Assert.True(session.ScrollAt(new UiPoint(13, 13), 120).Consumed);
        Assert.True(session.ReleasePointer(new UiPoint(13, 13)).Consumed);
        Assert.Single(session.ActivePortals);
    }

    [Fact]
    public void RootOutsideDismissRetainsInputOwnership()
    {
        UiScene root = Scene("outside-dismiss-root", UiProvisionalHostPolicies.OverlayCentered(true), actionCount: 0);
        var session = Host(root, new RecordingPlatform());

        UiPortalDispatch dispatch = session.PressPointer(new UiPoint(13, 13));

        Assert.True(dispatch.Consumed);
        Assert.True(dispatch.Interaction!.DismissRequested);
        Assert.Null(dispatch.Portal);
        Assert.False(dispatch.PortalClosed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnmappedInputHonorsRootModalityWithoutChangingInteraction(bool modal)
    {
        int invoked = 0;
        UiScene root = Scene("unmapped-root",
            modal ? UiProvisionalHostPolicies.OverlayCentered(false) : UiHostPolicies.Overlay,
            actionCount: 1, () => invoked++);
        var session = Host(root, new RecordingPlatform());
        var before = session.Root.Interactions.Snapshot;

        UiPortalDispatch dispatch = session.UnhandledInput();

        Assert.Equal(modal, dispatch.Consumed);
        Assert.Null(dispatch.Interaction);
        Assert.Null(dispatch.Portal);
        Assert.False(dispatch.PortalClosed);
        Assert.Equal(before, session.Root.Interactions.Snapshot);
        Assert.Equal(0, invoked);
    }

    [Fact]
    public void ModelessChildCannotBypassModalPortalAndRetirementRestoresPassThrough()
    {
        UiScene root = Scene("unmapped-modeless-root", UiHostPolicies.Window, actionCount: 0);
        UiScene modal = Scene("unmapped-modal-portal", UiProvisionalHostPolicies.OverlayCentered(false), actionCount: 0);
        UiScene child = Scene("unmapped-modeless-child", UiHostPolicies.Overlay, actionCount: 0);
        var session = Host(root, new RecordingPlatform());
        UiSymbolId modalId = RegistryTests.Id("portal/unmapped-modal");
        using UiPortalHandle modalHandle = session.Present(Request(modalId, root.Root.Id, modal));
        using UiPortalHandle childHandle = session.Present(new UiPortalRequest(
            RegistryTests.Id("portal/unmapped-child"), new UiPortalOwner(modal.Root.Id, modalId),
            child, new UiHostPlacementContext(Viewport)));

        Assert.True(session.UnhandledInput().Consumed);
        Assert.True(session.Submit().Consumed);
        Assert.True(session.ReleasePointer(new UiPoint(13, 13)).Consumed);
        Assert.Equal(2, session.ActivePortals.Count);

        modalHandle.Dispose();

        Assert.Empty(session.ActivePortals);
        Assert.False(session.UnhandledInput().Consumed);
        Assert.False(session.Submit().Consumed);
        Assert.False(session.ReleasePointer(new UiPoint(13, 13)).Consumed);
    }

    [Fact]
    public void ModalPortalConsumesReleaseOverBlankContentWithoutInvokingAction()
    {
        int invoked = 0;
        UiScene root = Scene("blank-release-root", UiHostPolicies.Window, actionCount: 0);
        UiScene modal = Scene("blank-release-modal", UiProvisionalHostPolicies.OverlayCentered(false),
            actionCount: 1, () => invoked++);
        var session = Host(root, new RecordingPlatform());
        using UiPortalHandle handle = session.Present(Request(RegistryTests.Id("portal/blank-release"), root.Root.Id, modal));

        UiPortalDispatch dispatch = session.ReleasePointer(new UiPoint(400, 240));

        Assert.True(dispatch.Consumed);
        Assert.NotNull(dispatch.Interaction);
        Assert.False(dispatch.Interaction.ActionInvoked);
        Assert.False(dispatch.PortalClosed);
        Assert.Equal(0, invoked);
        Assert.Single(session.ActivePortals);
    }

    [Fact]
    public void PerformanceSnapshotCountsLayoutBuildsAndActivePortalWork()
    {
        UiScene root = Scene("portal-performance-root", UiHostPolicies.Window, actionCount: 1);
        UiScene popup = Scene("portal-performance-popup", UiHostPolicies.Popup, actionCount: 1);
        var session = Host(root, new RecordingPlatform());
        UiHostRuntimePerformanceSnapshot initial = session.Performance;

        session.Render();

        Assert.Equal(initial, session.Performance);
        session.UpdateRoot(
            root,
            new UiHostPlacementContext(new UiRect(0, 0, Viewport.Width - 10, Viewport.Height)));
        UiHostRuntimePerformanceSnapshot reflowed = session.Performance;
        Assert.Equal(initial.LayoutBuilds + 1, reflowed.LayoutBuilds);
        Assert.Equal(initial.FrameBuilds + 1, reflowed.FrameBuilds);

        UiSymbolId owner = Assert.Single(Nodes(root.Root).OfType<UiButtonSceneNode>()).Id;
        using UiPortalHandle portal = session.Present(Request(
            RegistryTests.Id("portal/performance"),
            owner,
            popup));
        UiHostRuntimePerformanceSnapshot withPortal = session.Performance;
        Assert.True(withPortal.LayoutBuilds > reflowed.LayoutBuilds);
        Assert.True(withPortal.FrameBuilds > reflowed.FrameBuilds);

        portal.Dispose();
        Assert.Equal(reflowed, session.Performance);
    }

    private static readonly UiRect Viewport = new(0, 0, 800, 480);

    private static UiPortalHostSession Host(UiScene root, RecordingPlatform platform)
        => new(root, new UiHostPlacementContext(Viewport), platform);

    private static UiPortalRequest Request(UiSymbolId id, UiSymbolId owner, UiScene scene)
        => new(
            id,
            new UiPortalOwner(owner),
            scene,
            new UiHostPlacementContext(
                Viewport,
                anchor: new UiRect(520, 180, 40, 30),
                pointer: new UiPoint(540, 200)));

    private static UiScene Scene(
        string localId,
        UiHostPolicy policy,
        int actionCount,
        Action? onInvoke = null)
    {
        UiSymbolId id = RegistryTests.Id(localId);
        UiExperienceBuilder builder = new UiExperienceBuilder(id, localId)
            .Monitor("Status", new UiConstantSource<string>(localId));
        if (actionCount > 0)
        {
            UiActionDefinition[] actions = Enumerable.Range(0, actionCount)
                .Select(index => new UiActionDefinition(
                    id.Child($"action/{index}"),
                    $"Action {index}",
                    onInvoke ?? (() => { })))
                .ToArray();
            builder.Actions("Actions", actions).VisualRole("Action.Primary");
        }
        UiExperienceDefinition experience = builder.Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, localId, () => experience, policy)
            .Freeze();
        return new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(
            new UiInvocationService(registry).Invoke(id, UiPresentationProfiles.Wide));
    }

    private static IEnumerable<UiSceneNode> Nodes(UiSceneNode node)
    {
        yield return node;
        foreach (UiSceneNode child in node.Children)
        foreach (UiSceneNode descendant in Nodes(child))
            yield return descendant;
    }

    [Fact]
    public void DeactivatedHostRefusesRetainedRootInputBeforeEffects()
    {
        int invoked = 0;
        UiScene root = Scene("retired-root", UiHostPolicies.Window, 1, () => invoked++);
        var session = Host(root, new RecordingPlatform());
        session.MoveFocus(Hatifect.UI.Runtime.Input.UiNavigationDirection.Next);
        Assert.Equal(Assert.Single(Nodes(root.Root).OfType<UiButtonSceneNode>()).Id,
            session.Root.Interactions.Snapshot.Focused);
        var snapshot = session.Root.Interactions.Snapshot;

        session.Deactivate();
        session.Deactivate();

        Assert.Throws<ObjectDisposedException>(() => session.Root.Interactions.Submit());
        Assert.Equal(0, invoked);
        Assert.Same(snapshot, session.Root.Interactions.Snapshot);
    }

    [Fact]
    public void PresentCannotRegisterAfterOwnerRetirementDuringPreparation()
    {
        UiScene root = Scene("present-retired-root", UiHostPolicies.Window, 1);
        UiScene popup = Scene("present-retired-popup", UiHostPolicies.Popup, 1);
        var platform = new RecordingPlatform();
        var session = Host(root, platform);
        UiSymbolId owner = Assert.Single(Nodes(root.Root).OfType<UiButtonSceneNode>()).Id;
        platform.OnMeasure = () => { platform.OnMeasure = null; session.Deactivate(); };

        Assert.Throws<ObjectDisposedException>(() => session.Present(Request(
            RegistryTests.Id("portal/retired-preparation"), owner, popup)));

        Assert.Empty(session.ActivePortals);
        Assert.Same(root, session.Root.Scene);
    }

    [Fact]
    public void ClosedPortalCannotCommitItsPreparedUpdate()
    {
        UiScene root = Scene("update-retired-root", UiHostPolicies.Window, 1);
        UiScene popup = Scene("update-retired-popup", UiHostPolicies.Popup, 1);
        UiScene next = Scene("update-retired-popup", UiHostPolicies.Popup, 2);
        var platform = new RecordingPlatform();
        var session = Host(root, platform);
        UiSymbolId owner = Assert.Single(Nodes(root.Root).OfType<UiButtonSceneNode>()).Id;
        UiSymbolId id = RegistryTests.Id("portal/update-retirement");
        using UiPortalHandle handle = session.Present(Request(id, owner, popup));
        platform.OnMeasure = () => { platform.OnMeasure = null; handle.Dispose(); };

        Assert.Throws<ObjectDisposedException>(() => session.UpdatePortal(id, next,
            new UiHostPlacementContext(Viewport, pointer: new UiPoint(500, 300))));

        Assert.Empty(session.ActivePortals);
        Assert.Same(root, session.Root.Scene);
    }

    [Fact]
    public void RetiredRootCannotUpdateRefreshOrDrawThroughRetainedReferences()
    {
        UiScene root = Scene("retired-root-operations", UiHostPolicies.Window, 1);
        var platform = new RecordingPlatform();
        var session = Host(root, platform);
        UiSymbolId owner = Assert.Single(Nodes(root.Root).OfType<UiButtonSceneNode>()).Id;
        UiScene popup = Scene("retired-root-child", UiHostPolicies.Popup, 1);
        using var handle = session.Present(Request(RegistryTests.Id("portal/retired-root"), owner, popup));
        var frame = session.Root.Frame;
        session.Deactivate();

        Assert.Empty(session.ActivePortals);
        Assert.Throws<ObjectDisposedException>(() => session.Root.Update(root, Viewport));
        Assert.Throws<ObjectDisposedException>(() => session.Root.RefreshInteractionVisuals());
        Assert.Throws<ObjectDisposedException>(() => session.Root.RefreshTextEditingVisuals());
        Assert.Throws<ObjectDisposedException>(() => session.Root.ScrollAt(new UiPoint(1, 1), 10));
        Assert.Throws<ObjectDisposedException>(() => session.Present(Request(RegistryTests.Id("portal/new"), owner, popup)));
        Assert.Throws<ObjectDisposedException>(() => session.UnhandledInput());
        session.Root.Render();
        session.Render();

        Assert.Empty(platform.Drawn);
        Assert.Same(frame, session.Root.Frame);
    }

    [Theory]
    [InlineData("root-update")]
    [InlineData("parent-reopen")]
    [InlineData("duplicate")]
    public void PresentCannotOverwriteAnOwnerOrRegistrationChangedByItsCallback(string change)
    {
        UiScene root = Scene("present-owner-change", UiHostPolicies.Window, 1);
        UiScene popup = Scene("present-owner-popup", UiHostPolicies.Popup, 1);
        var platform = new RecordingPlatform();
        var session = Host(root, platform);
        UiSymbolId owner = Assert.Single(Nodes(root.Root).OfType<UiButtonSceneNode>()).Id;
        UiSymbolId parentId = RegistryTests.Id("portal/parent");
        UiSymbolId candidateId = RegistryTests.Id("portal/candidate");
        using UiPortalHandle parent = session.Present(Request(parentId, owner, popup));
        UiPortalRequest request = change == "parent-reopen"
            ? new UiPortalRequest(candidateId, new UiPortalOwner(popup.Root.Id, parentId), popup,
                new UiHostPlacementContext(Viewport, pointer: new UiPoint(500, 300)))
            : Request(candidateId, owner, popup);
        UiPortalHandle? nested = null;
        platform.OnMeasure = () =>
        {
            platform.OnMeasure = null;
            if (change == "root-update") session.UpdateRoot(root,
                new UiHostPlacementContext(new UiRect(0, 0, 1000, 700)));
            else if (change == "parent-reopen")
            {
                parent.Dispose();
                nested = session.Present(Request(parentId, owner, popup));
            }
            else nested = session.Present(request);
        };
        try
        {
            Assert.Throws<InvalidOperationException>(() => session.Present(request));

            Assert.Contains(parentId, session.ActivePortals);
            Assert.Equal(change == "duplicate", session.ActivePortals.Contains(candidateId));
            Assert.Equal(change == "duplicate" ? 2 : 1, session.ActivePortals.Count);
            using var retry = session.Present(Request(RegistryTests.Id("portal/retry"), owner, popup));
            Assert.Contains(RegistryTests.Id("portal/retry"), session.ActivePortals);
        }
        finally { nested?.Dispose(); }
    }

    [Fact]
    public void ClosingAParentFromANestedActionSkipsRetiredCompositionAndPreservesRootInput()
    {
        UiPortalHandle? parent = null;
        int rootEffects = 0;
        int childEffects = 0;
        int childCompositions = 0;
        UiScene root = Scene("action-retirement-root", UiHostPolicies.Window, 1, () => rootEffects++);
        UiScene popup = Scene("action-retirement-parent", UiHostPolicies.Popup, 1);
        UiScene child = Scene("action-retirement-child", UiHostPolicies.Context, 1,
            () => { childEffects++; parent!.Dispose(); });
        var session = Host(root, new RecordingPlatform());
        UiSymbolId owner = Assert.Single(Nodes(root.Root).OfType<UiButtonSceneNode>()).Id;
        UiSymbolId parentId = RegistryTests.Id("portal/action-parent");
        using (parent = session.Present(Request(parentId, owner, popup)))
        using (session.Present(new UiPortalRequest(RegistryTests.Id("portal/action-child"),
            new UiPortalOwner(popup.Root.Id, parentId), child,
            new UiHostPlacementContext(Viewport, pointer: new UiPoint(500, 300)),
            _ => { childCompositions++; return child; })))
        {
            int before = childCompositions;
            Assert.True(session.Submit().Interaction!.ActionInvoked);
            Assert.Equal(1, childEffects);
            Assert.Equal(before, childCompositions);
            Assert.Empty(session.ActivePortals);
            session.MoveFocus(Hatifect.UI.Runtime.Input.UiNavigationDirection.Next);
            Assert.True(session.Submit().Interaction!.ActionInvoked);
            Assert.Equal(1, rootEffects);
        }
    }

    private sealed class RecordingPlatform : IUiPlatformBridge
    {
        public List<UiSymbolId> Drawn { get; } = new();
        public Action? OnMeasure { get; set; }

        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
        {
            OnMeasure?.Invoke();
            return new(Math.Min(text.Length * typography.Size * 0.6f, availableWidth), typography.Size * typography.LineHeight);
        }

        public void DrawSurface(UiSurfacePrimitive surface) => Drawn.Add(surface.Node);
        public void DrawText(UiTextPrimitive text) => Drawn.Add(text.Node);
    }
}
