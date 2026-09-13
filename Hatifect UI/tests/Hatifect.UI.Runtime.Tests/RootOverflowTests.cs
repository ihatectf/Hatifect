using System;
using System.Collections.Generic;
using System.Linq;
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

public sealed class RootOverflowTests
{
    private static readonly UiRect Viewport = new(0, 0, 1280, 720);

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1960, 1275)]
    [InlineData(1024, 576)]
    public void SemanticWindowPreservesItsPolicyAndNaturalControlsInABoundedViewport(int width, int height)
    {
        UiScene scene = Scene(count: 40, semanticWindow: true);
        Assert.Equal(UiHostKind.Window, scene.Root.Policy.Kind);
        Assert.Equal(UiWindowChrome.Standard, scene.Root.Policy.Chrome);
        Assert.Equal(UiDismissPolicy.Escape, scene.Root.Policy.Dismiss);
        Assert.Equal(UiModalPolicy.Modeless, scene.Root.Policy.Modal);
        Assert.Equal(UiFocusScopePolicy.Contained, scene.Root.Policy.Focus);
        var engine = new UiSceneLayoutEngine(new Platform());
        UiLayoutSnapshot natural = engine.Build(scene, new UiRect(0, 0, width, 8000));
        UiLayoutSnapshot bounded = engine.Build(scene, new UiRect(0, 0, width, height));
        Assert.NotNull(bounded.RootScroll);
        Assert.True(bounded.RootScroll.Viewport.Width > 0 && bounded.RootScroll.Viewport.Height > 0);
        Assert.Equal(UiHostPlacementKind.Center, bounded.HostPlacement.Kind);
        Assert.True(bounded.HostPlacement.WasClamped);
        Assert.InRange(bounded.HostPlacement.Bounds.X, 12, width - 12);
        Assert.InRange(bounded.HostPlacement.Bounds.Right, 12, width - 12);
        Assert.InRange(bounded.HostPlacement.Bounds.Y, 12, height - 12);
        Assert.InRange(bounded.HostPlacement.Bounds.Bottom, 12, height - 12);
        UiSceneNode[] controls = Nodes(scene.Root)
            .Where(node => node.Kind is UiSceneNodeKind.TextInput or UiSceneNodeKind.Button).ToArray();
        Assert.Equal(41, controls.Length);
        foreach (UiSceneNode control in controls)
        {
            UiLayoutEntry entry = Entry(bounded, control.Id);
            Assert.True(entry.Bounds.Width > 0 && entry.Bounds.Height > 0);
            Assert.Equal(Entry(natural, control.Id).Bounds.Height, entry.Bounds.Height, 3);
            if (entry.Clip.Height > 0)
            {
                Assert.True(entry.Clip.Y >= bounded.RootScroll!.Viewport.Y);
                Assert.True(entry.Clip.Bottom <= bounded.RootScroll.Viewport.Bottom + .01f);
            }
        }
        Assert.Equal(0, Entry(bounded, controls[^1].Id).Clip.Height);
    }

    [Fact]
    public void SemanticWindowWheelAndNavigationReachClippedControlsAndExplicitAction()
    {
        int calls = 0;
        UiScene scene = Scene(execute: () => calls++, semanticWindow: true);
        var runtime = new UiHostRuntimeSession(scene, Viewport, new Platform());
        UiSceneNode[] controls = Nodes(scene.Root)
            .Where(node => node.Kind is UiSceneNodeKind.TextInput or UiSceneNodeKind.Button).ToArray();
        Assert.True(runtime.MoveFocus(UiNavigationDirection.Next).Consumed);
        Assert.Equal(controls[0].Id, runtime.Interactions.Snapshot.Focused);
        Assert.Equal(0, runtime.Layout.RootScroll!.Offset);
        Assert.Equal(0, Entry(runtime.Layout, controls[^1].Id).Clip.Height);
        UiRect content = runtime.Layout.RootScroll!.Viewport;
        UiHostScrollUpdate scroll = runtime.ScrollAt(new UiPoint(content.X + 1, content.Y + 1), float.MaxValue);
        Assert.True(scroll.Consumed && scroll.LayoutChanged && scroll.FrameChanged);
        Assert.True(scroll.Offset > 0);
        Assert.Equal(runtime.Layout.RootScroll.MaximumOffset, scroll.Offset);
        UiLayoutEntry last = Entry(runtime.Layout, controls[^1].Id);
        Assert.Equal(last.Bounds.Height, last.Clip.Height, 3);
        Assert.Equal(controls[0].Id, runtime.Interactions.Snapshot.Focused);
        Assert.Equal(0, Entry(runtime.Layout, controls[0].Id).Clip.Height);
        Assert.Equal(0, calls);
        for (int i = 1; i < controls.Length; i++)
        {
            Assert.True(runtime.MoveFocus(UiNavigationDirection.Next).Consumed);
            Assert.Equal(controls[i].Id, runtime.Interactions.Snapshot.Focused);
            UiLayoutEntry entry = Entry(runtime.Layout, controls[i].Id);
            Assert.Equal(entry.Bounds.Height, entry.Clip.Height, 3);
        }
        Assert.Equal(0, calls);
        Assert.False(runtime.MoveFocus(UiNavigationDirection.Next).Consumed);
        Assert.Equal(controls[^1].Id, runtime.Interactions.Snapshot.Focused);
        for (int i = controls.Length - 2; i >= 0; i--)
        {
            Assert.True(runtime.MoveFocus(UiNavigationDirection.Previous).Consumed);
            Assert.Equal(controls[i].Id, runtime.Interactions.Snapshot.Focused);
            UiLayoutEntry entry = Entry(runtime.Layout, controls[i].Id);
            Assert.Equal(entry.Bounds.Height, entry.Clip.Height, 3);
        }
        Assert.False(runtime.MoveFocus(UiNavigationDirection.Previous).Consumed);
        Assert.Equal(controls[0].Id, runtime.Interactions.Snapshot.Focused);
        UiButtonSceneNode button = Assert.Single(Nodes(scene.Root).OfType<UiButtonSceneNode>());
        for (int i = 0; i < controls.Length && runtime.Interactions.Snapshot.Focused != button.Id; i++)
            Assert.True(runtime.MoveFocus(UiNavigationDirection.Next).Consumed);
        Assert.Equal(button.Id, runtime.Interactions.Snapshot.Focused);
        UiLayoutEntry action = Entry(runtime.Layout, button.Id);
        Assert.Equal(action.Bounds.Height, action.Clip.Height, 3);
        Assert.Equal(0, calls);
        Assert.True(runtime.Interactions.Submit().ActionInvoked);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void SemanticWindowThatFitsKeepsOrdinaryWindowPlacementWithoutRootScroll()
    {
        var engine = new UiSceneLayoutEngine(new Platform());
        UiLayoutSnapshot semantic = engine.Build(Scene(count: 1, semanticWindow: true), Viewport);
        UiLayoutSnapshot ordinary = engine.Build(Scene(UiHostPolicies.Window, count: 1), Viewport);
        Assert.Null(semantic.RootScroll);
        Assert.Equal(ordinary.HostPlacement, semantic.HostPlacement);
    }

    [Theory]
    [InlineData(4, 720)]
    [InlineData(1280, 4)]
    public void SemanticWindowStillRejectsAnUnusableViewport(int width, int height)
        => Assert.Throws<UiLayoutException>(() => new UiSceneLayoutEngine(new Platform())
            .Build(Scene(semanticWindow: true), new UiRect(0, 0, width, height)));

    [Fact]
    public void OversizedCenteredOverlayPreservesControlGeometryInsideAClippedViewport()
    {
        UiScene scene = Scene();
        var engine = new UiSceneLayoutEngine(new Platform());
        UiLayoutSnapshot natural = engine.Build(scene, new UiRect(0, 0, 1280, 8000));
        UiLayoutSnapshot layout = engine.Build(scene, Viewport);
        UiLayoutEntry root = Entry(layout, scene.Root.Id);
        Assert.True(layout.HostPlacement.WasClamped);
        Assert.InRange(root.Bounds.Y, 12, 720);
        Assert.InRange(root.Bounds.Bottom, 12, 708);
        var controls = Nodes(scene.Root).Where(n => n.Kind is UiSceneNodeKind.TextInput or UiSceneNodeKind.Button).ToArray();
        Assert.Equal(21, controls.Length);
        foreach (UiSceneNode node in controls)
        {
            UiLayoutEntry entry = Entry(layout, node.Id);
            Assert.Equal(Entry(natural, node.Id).Bounds.Height, entry.Bounds.Height, 3);
            Assert.True(entry.Bounds.Width > 0 && entry.Bounds.Height > 0);
            if (entry.Clip.Width > 0 && entry.Clip.Height > 0)
            {
                Assert.True(entry.Clip.Y >= root.ContentBounds.Y);
                Assert.True(entry.Clip.Bottom <= root.ContentBounds.Bottom + .01f);
            }
        }
        Assert.True(Entry(layout, controls[^1].Id).Bounds.Bottom > root.ContentBounds.Bottom);
        Assert.Equal(0, Entry(layout, controls[^1].Id).Clip.Height);
    }

    [Fact]
    public void RootWheelClampsAtBothEndsAndDoesNotRevealPassiveFocus()
    {
        UiScene scene = Scene();
        var runtime = new UiHostRuntimeSession(scene, Viewport, new Platform());
        runtime.MoveFocus(UiNavigationDirection.Next);
        UiSymbolId? focus = runtime.Interactions.Snapshot.Focused;
        UiLayoutEntry root = Entry(runtime.Layout, scene.Root.Id);
        var point = new UiPoint(root.ContentBounds.X + 1, root.ContentBounds.Y + 1);
        UiHostScrollUpdate end = runtime.ScrollAt(point, float.MaxValue);
        Assert.True(end.Consumed && end.LayoutChanged && end.FrameChanged);
        Assert.True(end.Offset > 0);
        Assert.Equal(focus, runtime.Interactions.Snapshot.Focused);
        Assert.Equal(0, Entry(runtime.Layout, focus!.Value).Clip.Height);
        UiRenderFrame frame = runtime.Frame;
        UiHostScrollUpdate repeated = runtime.ScrollAt(point, 100);
        Assert.True(repeated.Consumed);
        Assert.False(repeated.LayoutChanged || repeated.FrameChanged);
        Assert.Same(frame, runtime.Frame);
        Assert.Equal(end.Offset, repeated.Offset);
        UiHostScrollUpdate start = runtime.ScrollAt(point, -float.MaxValue);
        Assert.Equal(0, start.Offset);
        Assert.True(start.LayoutChanged);
        Assert.False(runtime.ScrollAt(new UiPoint(0, 0), 100).Consumed);
    }

    [Fact]
    public void ExplicitNavigationRevealsTheLastControlAndWrapsToTheFirst()
    {
        UiScene scene = Scene();
        var runtime = new UiHostRuntimeSession(scene, Viewport, new Platform());
        UiSceneNode[] controls = Nodes(scene.Root).Where(n => n.Kind is UiSceneNodeKind.TextInput or UiSceneNodeKind.Button).ToArray();
        for (int i = 0; i < controls.Length; i++)
        {
            Assert.True(runtime.MoveFocus(UiNavigationDirection.Next).Consumed);
            UiSymbolId focused = runtime.Interactions.Snapshot.Focused!.Value;
            UiLayoutEntry entry = Entry(runtime.Layout, focused);
            Assert.True(entry.Clip.Height >= entry.Bounds.Height - .01f);
        }
        Assert.Equal(controls[^1].Id, runtime.Interactions.Snapshot.Focused);
        Assert.True(runtime.MoveFocus(UiNavigationDirection.Next).Consumed);
        Assert.Equal(controls[0].Id, runtime.Interactions.Snapshot.Focused);
        Assert.True(Entry(runtime.Layout, controls[0].Id).Clip.Height > 0);
    }

    [Fact]
    public void OrdinaryWindowAndImpossibleCenteredWidthStillRejectInsufficientSpace()
    {
        Assert.Throws<UiLayoutException>(() => new UiSceneLayoutEngine(new Platform()).Build(Scene(UiHostPolicies.Window), Viewport));
        Assert.Throws<UiLayoutException>(() => new UiSceneLayoutEngine(new Platform()).Build(Scene(), new UiRect(0, 0, 4, 720)));
        Assert.Throws<UiLayoutException>(() => new UiSceneLayoutEngine(new Platform()).Build(Scene(), new UiRect(0, 0, 1280, 4)));
    }

    [Fact]
    public void RootOffsetSurvivesAnUpdateAndResetsWhenContentFits()
    {
        UiScene scene = Scene();
        var runtime = new UiHostRuntimeSession(scene, Viewport, new Platform());
        UiRect viewport = runtime.Layout.RootScroll!.Viewport;
        var point = new UiPoint(viewport.X + 1, viewport.Y + 1);
        runtime.ScrollAt(point, 60);
        runtime.Update(Scene(), Viewport);
        Assert.Equal(60, runtime.Layout.RootScroll!.Offset);
        runtime.Update(Scene(count: 1), Viewport);
        Assert.Null(runtime.Layout.RootScroll);
        runtime.Update(Scene(), Viewport);
        Assert.Equal(0, runtime.Layout.RootScroll!.Offset);
        runtime.ScrollAt(point, float.MaxValue);
        runtime.Update(Scene(), new UiRect(0, 0, 1280, 780));
        Assert.Equal(runtime.Layout.RootScroll!.MaximumOffset, runtime.Layout.RootScroll.Offset);
        Assert.True(runtime.Layout.RootScroll.Offset < 60);
        runtime.Update(Scene(), new UiRect(0, 0, 1280, 2000));
        Assert.Null(runtime.Layout.RootScroll);
    }

    [Fact]
    public void FailedRootScrollAndSceneUpdatePreserveAcceptedPresentationAndOffset()
    {
        UiScene scene = Scene();
        var platform = new Platform();
        var runtime = new UiHostRuntimeSession(scene, Viewport, platform);
        UiRect viewport = runtime.Layout.RootScroll!.Viewport;
        var point = new UiPoint(viewport.X + 1, viewport.Y + 1);
        runtime.ScrollAt(point, 60);
        UiLayoutSnapshot layout = runtime.Layout;
        UiRenderFrame frame = runtime.Frame;
        var accessibility = runtime.Accessibility;
        platform.FailMeasurement = true;
        Assert.Throws<InvalidOperationException>(() => runtime.ScrollAt(point, -20));
        Assert.Same(layout, runtime.Layout);
        Assert.Same(frame, runtime.Frame);
        Assert.Same(accessibility, runtime.Accessibility);
        platform.FailMeasurement = false;
        Assert.Throws<UiLayoutException>(() => runtime.Update(Scene(), new UiRect(0, 0, 4, 720)));
        Assert.Same(layout, runtime.Layout);
        Assert.Same(frame, runtime.Frame);
        Assert.Equal(60, runtime.Layout.RootScroll!.Offset);
        Assert.Equal(40, runtime.ScrollAt(point, -20).Offset);
    }

    [Fact]
    public void RootScrollRejectsReentrantMutationAndNonFiniteInput()
    {
        UiScene scene = Scene();
        var platform = new Platform();
        var runtime = new UiHostRuntimeSession(scene, Viewport, platform);
        UiRect viewport = runtime.Layout.RootScroll!.Viewport;
        var point = new UiPoint(viewport.X + 1, viewport.Y + 1);
        int callbacks = 0;
        platform.OnMeasure = () =>
        {
            platform.OnMeasure = null;
            callbacks++;
            Assert.Throws<InvalidOperationException>(() => runtime.ScrollAt(point, 10));
        };
        Assert.Equal(30, runtime.ScrollAt(point, 30).Offset);
        Assert.Equal(1, callbacks);
        UiLayoutSnapshot layout = runtime.Layout;
        foreach (float delta in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => runtime.ScrollAt(point, delta));
        Assert.Same(layout, runtime.Layout);
    }

    [Fact]
    public void CollectionWheelKeepsRootOffsetAndNavigationRevealsVirtualItems()
    {
        UiScene scene = Scene(includeCollection: true);
        var runtime = new UiHostRuntimeSession(scene, Viewport, new Platform());
        UiCollectionSceneNode collection = Assert.Single(Nodes(scene.Root).OfType<UiCollectionSceneNode>());
        UiRect rootViewport = runtime.Layout.RootScroll!.Viewport;
        runtime.ScrollAt(new UiPoint(rootViewport.X + 1, rootViewport.Y + 1), float.MaxValue);
        UiSymbolId item = default;
        int index = -1;
        for (int step = 0; step < 122; step++)
        {
            runtime.MoveFocus(UiNavigationDirection.Previous);
            if (runtime.Interactions.TryGetFocusedCollectionItem(out _, out item, out index) && index == 99) break;
        }
        Assert.Equal(99, index);
        Assert.True(runtime.Layout.TryGetCollection(collection.Id, out UiCollectionLayoutWindow? window));
        UiVirtualizedItemLayout target = Assert.Single(window!.Items, row => row.Item.Id == item);
        UiLayoutEntry entry = Entry(runtime.Layout, collection.Id);
        Assert.True(target.Bounds.Y >= entry.Clip.Y - .01f);
        Assert.True(target.Bounds.Bottom <= entry.Clip.Bottom + .01f);
        float rootOffset = runtime.Layout.RootScroll!.Offset;
        float collectionOffset = window.ScrollOffset;
        var point = new UiPoint(entry.ContentBounds.X + 1, Math.Max(entry.ContentBounds.Y, entry.Clip.Y) + 1);
        UiHostScrollUpdate scroll = runtime.ScrollAt(point, -40);
        Assert.True(scroll.Consumed && scroll.LayoutChanged);
        Assert.Equal(collectionOffset - 40, scroll.Offset, 3);
        Assert.Equal(rootOffset, runtime.Layout.RootScroll.Offset);
        Assert.Equal(item, runtime.Interactions.Snapshot.Focused);
        Assert.True(runtime.Layout.TryGetCollection(collection.Id, out window));
        Assert.True(window!.Items.Count < 100);
        runtime.MoveFocus(UiNavigationDirection.Next);
        Assert.False(runtime.Interactions.TryGetFocusedCollectionItem(out _, out _, out _));
        Assert.NotNull(runtime.Interactions.Snapshot.Focused);
    }

    [Fact]
    public void FailedFocusRevealKeepsThePreviouslyAcceptedFocusAndFrame()
    {
        UiScene scene = Scene();
        var platform = new Platform();
        var runtime = new UiHostRuntimeSession(scene, Viewport, platform);
        UiInteractionSnapshot interaction = runtime.Interactions.Snapshot;
        UiLayoutSnapshot layout = runtime.Layout;
        UiRenderFrame frame = runtime.Frame;
        var accessibility = runtime.Accessibility;
        long frameVersion = runtime.FrameVersion;
        float offset = runtime.Layout.RootScroll!.Offset;
        platform.FailMeasurement = true;
        Assert.Throws<InvalidOperationException>(() => runtime.MoveFocus(UiNavigationDirection.Previous));
        Assert.Equal(interaction, runtime.Interactions.Snapshot);
        Assert.Same(layout, runtime.Layout);
        Assert.Same(frame, runtime.Frame);
        Assert.Same(accessibility, runtime.Accessibility);
        Assert.Equal(frameVersion, runtime.FrameVersion);
        Assert.Equal(offset, runtime.Layout.RootScroll!.Offset);
        platform.FailMeasurement = false;
        Assert.True(runtime.MoveFocus(UiNavigationDirection.Previous).Consumed);
        UiSceneNode expected = Nodes(scene.Root).Last(n => n.Kind is UiSceneNodeKind.TextInput or UiSceneNodeKind.Button);
        Assert.Equal(expected.Id, runtime.Interactions.Snapshot.Focused);
        UiLayoutEntry target = Entry(runtime.Layout, runtime.Interactions.Snapshot.Focused!.Value);
        Assert.True(target.Clip.Height >= target.Bounds.Height - .01f);
    }

    [Fact]
    public void ScrolledControlsUseCurrentHitboxesAndKeepTheHeaderAndActionDispatcher()
    {
        int executed = 0;
        UiScene scene = Scene(execute: () => executed++);
        var runtime = new UiHostRuntimeSession(scene, Viewport, new Platform());
        UiLayoutEntry root = Entry(runtime.Layout, scene.Root.Id);
        UiTextPrimitive heading = Assert.Single(runtime.Frame.Primitives.OfType<UiTextPrimitive>(),
            primitive => primitive.Node == scene.Root.Id && primitive.Text == "Network");
        UiSceneNode[] controls = Nodes(scene.Root).Where(n => n.Kind is UiSceneNodeKind.TextInput or UiSceneNodeKind.Button).ToArray();
        var point = new UiPoint(root.ContentBounds.X + 1, root.ContentBounds.Y + 1);
        runtime.ScrollAt(point, float.MaxValue);
        Assert.Equal(root.HeadingBounds, Entry(runtime.Layout, scene.Root.Id).HeadingBounds);
        UiTextPrimitive scrolledHeading = Assert.Single(runtime.Frame.Primitives.OfType<UiTextPrimitive>(),
            primitive => primitive.Node == scene.Root.Id && primitive.Text == "Network");
        Assert.Equal(heading.Bounds, scrolledHeading.Bounds);
        Assert.Equal(heading.Clip, scrolledHeading.Clip);
        UiLayoutEntry last = Entry(runtime.Layout, controls[^1].Id);
        runtime.Interactions.PressPointer(new UiPoint(last.Bounds.X + last.Bounds.Width / 2, last.Bounds.Y + last.Bounds.Height / 2));
        Assert.Equal(controls[^1].Id, runtime.Interactions.Snapshot.Focused);
        var hitboxes = runtime.Interactions.CaptureDiagnostics().HitTestTargets;
        Assert.Contains(controls[^1].Id, hitboxes);
        Assert.DoesNotContain(controls[0].Id, hitboxes);
        UiButtonSceneNode button = Assert.Single(Nodes(scene.Root).OfType<UiButtonSceneNode>());
        for (int step = 0; step < controls.Length && runtime.Interactions.Snapshot.Focused != button.Id; step++)
            runtime.MoveFocus(UiNavigationDirection.Next);
        Assert.Equal(button.Id, runtime.Interactions.Snapshot.Focused);
        Assert.True(runtime.Interactions.Submit().Consumed);
        Assert.Equal(1, executed);
    }

    [Fact]
    public void FailureDuringNestedCollectionRevealDoesNotAcceptThePreparedRootScroll()
    {
        UiScene scene = Scene(includeCollection: true);
        var platform = new Platform();
        var runtime = new UiHostRuntimeSession(scene, Viewport, platform);
        UiCollectionSceneNode collection = Assert.Single(Nodes(scene.Root).OfType<UiCollectionSceneNode>());
        int index = -1;
        for (int step = 0; step < 122; step++)
        {
            runtime.MoveFocus(UiNavigationDirection.Next);
            if (runtime.Interactions.TryGetFocusedCollectionItem(out _, out _, out index) && index == 99) break;
        }
        Assert.Equal(99, index);
        runtime.MoveFocus(UiNavigationDirection.Next);
        Assert.False(runtime.Interactions.TryGetFocusedCollectionItem(out _, out _, out _));
        runtime.ScrollCollection(collection.Id, -float.MaxValue);
        UiRect viewport = runtime.Layout.RootScroll!.Viewport;
        runtime.ScrollAt(new UiPoint(viewport.Right - 1, viewport.Bottom - 1), float.MaxValue);
        Assert.Equal(0, Entry(runtime.Layout, collection.Id).Clip.Height);
        Assert.True(runtime.Layout.TryGetCollection(collection.Id, out UiCollectionLayoutWindow? window));
        Assert.DoesNotContain(window!.Items, row => row.Index == 99);
        UiLayoutSnapshot layout = runtime.Layout;
        UiRenderFrame frame = runtime.Frame;
        var accessibility = runtime.Accessibility;
        UiInteractionSnapshot interaction = runtime.Interactions.Snapshot;
        long version = runtime.FrameVersion;
        int rootMeasures = 0;
        platform.ObserveMeasurement = text =>
        {
            if (text == "Network" && ++rootMeasures == 2)
                throw new InvalidOperationException("Second layout failed.");
        };
        Assert.Throws<InvalidOperationException>(() => runtime.MoveFocus(UiNavigationDirection.Previous));
        Assert.Equal(2, rootMeasures);
        Assert.Same(layout, runtime.Layout);
        Assert.Same(frame, runtime.Frame);
        Assert.Same(accessibility, runtime.Accessibility);
        Assert.Equal(interaction, runtime.Interactions.Snapshot);
        Assert.Equal(version, runtime.FrameVersion);
        platform.ObserveMeasurement = null;
        runtime.MoveFocus(UiNavigationDirection.Previous);
        Assert.True(runtime.Interactions.TryGetFocusedCollectionItem(out _, out UiSymbolId item, out index));
        Assert.Equal(99, index);
        Assert.True(runtime.Layout.RootScroll!.Offset < layout.RootScroll!.Offset);
        Assert.True(runtime.Layout.TryGetCollection(collection.Id, out window));
        UiVirtualizedItemLayout target = Assert.Single(window!.Items, row => row.Item.Id == item);
        Assert.True(target.Clip.Height >= target.Bounds.Height - .01f);
    }

    [Fact]
    public void FocusPreparationFencesLegacyTextSourceReentry()
    {
        var source = new TextSource();
        UiScene scene = Scene(lastInput: source);
        var runtime = new UiHostRuntimeSession(scene, Viewport, new Platform());
        UiRect viewport = runtime.Layout.RootScroll!.Viewport;
        var point = new UiPoint(viewport.X + 1, viewport.Y + 1);
        int calls = 0;
        source.OnRead = () =>
        {
            calls++;
            Assert.Throws<InvalidOperationException>(() => runtime.ScrollAt(point, 10));
            Assert.Throws<InvalidOperationException>(() => runtime.Update(scene, Viewport));
        };
        Assert.True(runtime.MoveFocus(UiNavigationDirection.Previous).Consumed);
        Assert.True(calls > 0);
        UiSceneNode last = Nodes(scene.Root).Last(n => n.Kind is UiSceneNodeKind.TextInput or UiSceneNodeKind.Button);
        Assert.Equal(last.Id, runtime.Interactions.Snapshot.Focused);
        Assert.True(Entry(runtime.Layout, last.Id).Clip.Height > 0);
    }

    [Fact]
    public void RetirementDuringVisibleFocusPreparationCannotAcceptTheCandidate()
    {
        var source = new TextSource();
        UiScene scene = Scene(lastInput: source);
        var runtime = new UiHostRuntimeSession(scene, Viewport, new Platform());
        runtime.MoveFocus(UiNavigationDirection.Previous);
        UiSymbolId target = runtime.Interactions.Snapshot.Focused!.Value;
        runtime.MoveFocus(UiNavigationDirection.Previous);
        UiLayoutEntry entry = Entry(runtime.Layout, target);
        Assert.True(entry.Clip.Height >= entry.Bounds.Height - .01f);
        UiLayoutSnapshot layout = runtime.Layout;
        UiRenderFrame frame = runtime.Frame;
        UiInteractionSnapshot interaction = runtime.Interactions.Snapshot;
        source.OnRead = runtime.Deactivate;
        Assert.Throws<ObjectDisposedException>(() => runtime.MoveFocus(UiNavigationDirection.Next));
        Assert.False(runtime.IsActive);
        Assert.Same(layout, runtime.Layout);
        Assert.Same(frame, runtime.Frame);
        Assert.Equal(interaction, runtime.Interactions.Snapshot);
    }

    private static UiScene Scene(UiHostPolicy? policy = null, int count = 20, bool includeCollection = false, Action? execute = null, IUiSemanticSource<string>? lastInput = null, bool semanticWindow = false)
    {
        UiSymbolId id = RegistryTests.Id("root-overflow");
        var builder = new UiExperienceBuilder(id, "Network");
        for (int i = 0; i < count; i++) builder.Search("Field/" + i,
            i == count - 1 && lastInput != null ? lastInput : new UiState<string>("Value"));
        if (includeCollection) builder.Select("Items", new UiSelectableCollectionState<int>(
            Enumerable.Range(0, 100).ToArray(), item => id.Child("item/" + item), item => "Item " + item));
        UiExperienceDefinition experience = builder.Actions("Actions",
            new UiActionDefinition(id.Child("return"), "Return", execute ?? (() => { }))).Build();
        UiRegistrySnapshot registry = semanticWindow
            ? UiSemanticHostProjection.Register(experience, UiSemanticHostKind.Window)
            : new UiRegistryBuilder().Window(id, "Network", () => experience,
                policy ?? UiProvisionalHostPolicies.OverlayCentered(true)).Freeze();
        return new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(
            new UiInvocationService(registry).Invoke(id, UiPresentationProfiles.Wide));
    }

    private static UiLayoutEntry Entry(UiLayoutSnapshot layout, UiSymbolId id)
    {
        Assert.True(layout.TryGetEntry(id, out UiLayoutEntry? entry));
        return Assert.IsType<UiLayoutEntry>(entry);
    }

    private static IEnumerable<UiSceneNode> Nodes(UiSceneNode node)
    {
        yield return node;
        foreach (UiSceneNode child in node.Children)
        foreach (UiSceneNode descendant in Nodes(child)) yield return descendant;
    }

    private sealed class TextSource : IUiSemanticSource<string>
    {
        public Action? OnRead { get; set; }
        public string Value { get { OnRead?.Invoke(); return "Value"; } }
        public Type ValueType => typeof(string);
        public object UntypedValue => Value;
        public event Action? Changed { add { } remove { } }
    }

    private sealed class Platform : IUiPlatformBridge
    {
        public bool FailMeasurement { get; set; }
        public Action? OnMeasure { get; set; }
        public Action<string>? ObserveMeasurement { get; set; }
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
        {
            OnMeasure?.Invoke();
            ObserveMeasurement?.Invoke(text);
            if (FailMeasurement) throw new InvalidOperationException("Measurement failed.");
            return new(Math.Min(text.Length * 8, availableWidth), typography.Size * typography.LineHeight);
        }
        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }
}
