using System;
using System.Collections.Generic;
using Hatifect.UI.Runtime.Accessibility;
using Hatifect.UI.Runtime.Diagnostics;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Rendering;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Runtime.Platform;

/// <summary>
/// Provisional friend-assembly boundary. The Stardew adapter supplies exact text metrics and drawing,
/// while Runtime retains ownership of composition, layout, input state, and frame reuse.
/// </summary>
internal interface IUiPlatformBridge : IUiTextMetrics, IUiRenderBackend { }

internal sealed record UiHostUpdate(bool LayoutChanged, bool FrameChanged, UiSceneDiff Diff);
internal sealed record UiHostScrollUpdate(bool Consumed, bool LayoutChanged, bool FrameChanged, float Offset);
internal readonly record struct UiHostRuntimePerformanceSnapshot(long LayoutBuilds, long FrameBuilds);

internal sealed class UiHostRuntimeSession
{
    private readonly IUiPlatformBridge _platform;
    private readonly UiSceneLayoutEngine _layoutEngine;
    private readonly UiSceneRenderPlanner _renderPlanner = new();
    private readonly UiAccessibilitySnapshotBuilder _accessibilityBuilder = new();
    private readonly UiCompositor _compositor = new();
    private readonly UiSceneReconciler _reconciler = new();
    private readonly UiCollectionViewportState _collections = new();
    private readonly Func<UiInteractionSnapshot, UiScene>? _composeInteraction;
    private UiScene _scene;
    private UiHostPlacementContext _placement;
    private UiRenderFrame _frame;
    private long _layoutBuilds;
    private long _frameBuilds;

    public UiHostRuntimeSession(
        UiScene scene,
        UiRect viewport,
        IUiPlatformBridge platform,
        UiInteractionSnapshot? interaction = null,
        Func<UiInteractionSnapshot, UiScene>? composeInteraction = null)
        : this(scene, new UiHostPlacementContext(viewport), platform, interaction, composeInteraction) { }

    public UiHostRuntimeSession(
        UiScene scene,
        UiHostPlacementContext placement,
        IUiPlatformBridge platform,
        UiInteractionSnapshot? interaction = null,
        Func<UiInteractionSnapshot, UiScene>? composeInteraction = null)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _placement = placement ?? throw new ArgumentNullException(nameof(placement));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _composeInteraction = composeInteraction;
        _layoutEngine = new UiSceneLayoutEngine(platform);
        Layout = BuildLayout(scene, placement);
        _collections.Synchronize(Layout);
        Interactions = new UiInteractionSession(scene, Layout, interaction, platform);
        _frame = BuildFrame(scene);
        Accessibility = _accessibilityBuilder.Build(scene, Layout, Interactions.Snapshot);
    }

    public UiLayoutSnapshot Layout { get; private set; }
    public UiInteractionSession Interactions { get; }
    public UiAccessibilitySnapshot Accessibility { get; private set; }
    public UiRenderFrame Frame => _frame;
    public UiHostPolicy Policy => _scene.Root.Policy;
    internal UiScene Scene => _scene;
    internal UiHostUpdate? LastUpdate { get; private set; }
    internal UiHostRuntimePerformanceSnapshot Performance
        => new(_layoutBuilds, _frameBuilds);

    internal UiRuntimeDiagnosticSnapshot CaptureDiagnostics()
        => UiRuntimeDiagnosticCapture.Capture(
            _scene,
            Layout,
            Interactions.CaptureDiagnostics(),
            LastUpdate,
            Performance);

    public bool Contains(UiPoint point) => Layout.HostPlacement.Bounds.Contains(point);

    public bool ContainsNode(UiSymbolId id)
    {
        foreach (UiCollectionLayoutWindow window in Layout.CollectionWindows)
        foreach (UiVirtualizedItemLayout item in window.Items)
            if (item.Node == id) return true;
        var pending = new Stack<UiSceneNode>();
        pending.Push(_scene.Root);
        while (pending.Count > 0)
        {
            UiSceneNode node = pending.Pop();
            if (node.Id == id) return true;
            foreach (UiSceneNode child in node.Children) pending.Push(child);
        }
        return false;
    }

    public UiHostUpdate Update(UiScene next, UiRect viewport)
        => Update(next, new UiHostPlacementContext(viewport));

    public UiHostUpdate Update(UiScene next, UiHostPlacementContext placement)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(placement);
        UiSceneDiff diff = _reconciler.Compare(_scene, next);
        bool placementChanged = placement != _placement;
        bool layoutChanged = placementChanged || diff.RequiresLayout;
        if (layoutChanged)
        {
            Layout = BuildLayout(next, placement);
            _collections.Synchronize(Layout);
        }
        Interactions.Reconcile(next, Layout);

        bool frameChanged = layoutChanged || diff.RequiresRender;
        if (frameChanged) _frame = BuildFrame(next);
        _scene = next;
        _placement = placement;
        Accessibility = _accessibilityBuilder.Build(next, Layout, Interactions.Snapshot);
        var update = new UiHostUpdate(layoutChanged, frameChanged, diff);
        LastUpdate = update;
        return update;
    }

    public UiHostScrollUpdate ScrollCollection(UiSymbolId collection, float delta)
    {
        if (!float.IsFinite(delta)) throw new ArgumentOutOfRangeException(nameof(delta));
        if (!Layout.TryGetCollection(collection, out UiCollectionLayoutWindow? window) || window == null)
            return new UiHostScrollUpdate(false, false, false, 0);
        float maximum = Math.Max(0, window.TotalExtent -
            (Layout.TryGetEntry(collection, out UiLayoutEntry? entry) && entry != null
                ? entry.ContentBounds.Height
                : 0));
        float offset = Math.Min(maximum, Math.Max(0, window.ScrollOffset + delta));
        if (!_collections.SetOffset(collection, offset))
            return new UiHostScrollUpdate(true, false, false, window.ScrollOffset);

        Layout = BuildLayout(_scene, _placement);
        _collections.Synchronize(Layout);
        Interactions.Reconcile(_scene, Layout);
        _frame = BuildFrame(_scene);
        Accessibility = _accessibilityBuilder.Build(_scene, Layout, Interactions.Snapshot);
        LastUpdate = new UiHostUpdate(
            LayoutChanged: true,
            FrameChanged: true,
            new UiSceneDiff(
                UiPropertyEffects.Measure | UiPropertyEffects.Arrange | UiPropertyEffects.Render,
                new[] { collection }));
        Layout.TryGetCollection(collection, out UiCollectionLayoutWindow? updated);
        return new UiHostScrollUpdate(true, true, true, updated?.ScrollOffset ?? 0);
    }

    public UiHostScrollUpdate ScrollAt(UiPoint point, float delta)
    {
        foreach (UiCollectionLayoutWindow window in Layout.CollectionWindows)
        {
            if (!Layout.TryGetEntry(window.Collection, out UiLayoutEntry? entry) || entry == null ||
                !entry.ContentBounds.Contains(point) || !entry.Clip.Contains(point))
                continue;
            return ScrollCollection(window.Collection, delta);
        }
        return new UiHostScrollUpdate(false, false, false, 0);
    }

    public UiHostUpdate? RefreshInteractionVisuals(bool textChanged = false)
    {
        if (_composeInteraction == null)
        {
            if (textChanged)
                return RefreshLiveText();
            _frame = BuildFrame(_scene);
            Accessibility = _accessibilityBuilder.Build(_scene, Layout, Interactions.Snapshot);
            UiSymbolId[] changed = Interactions.Snapshot.Focused is { } focused
                ? new[] { focused }
                : Array.Empty<UiSymbolId>();
            var directUpdate = new UiHostUpdate(
                LayoutChanged: false,
                FrameChanged: true,
                new UiSceneDiff(UiPropertyEffects.Render, changed));
            LastUpdate = directUpdate;
            return directUpdate;
        }
        UiScene next = _composeInteraction(Interactions.Snapshot)
            ?? throw new InvalidOperationException("The interaction scene composer returned null.");
        return Update(next, _placement);
    }

    private UiHostUpdate RefreshLiveText()
    {
        Layout = BuildLayout(_scene, _placement);
        _collections.Synchronize(Layout);
        Interactions.Reconcile(_scene, Layout);
        _frame = BuildFrame(_scene);
        Accessibility = _accessibilityBuilder.Build(_scene, Layout, Interactions.Snapshot);
        UiSymbolId[] changed = Interactions.Snapshot.TextEditing is { } editing
            ? new[] { editing.Input }
            : Array.Empty<UiSymbolId>();
        var update = new UiHostUpdate(
            LayoutChanged: true,
            FrameChanged: true,
            new UiSceneDiff(
                UiPropertyEffects.Measure | UiPropertyEffects.Arrange | UiPropertyEffects.Render,
                changed));
        LastUpdate = update;
        return update;
    }

    public UiHostUpdate RefreshTextEditingVisuals()
    {
        _frame = BuildFrame(_scene);
        Accessibility = _accessibilityBuilder.Build(_scene, Layout, Interactions.Snapshot);
        UiSymbolId[] changed = Interactions.Snapshot.TextEditing is { } editing
            ? new[] { editing.Input }
            : Array.Empty<UiSymbolId>();
        var update = new UiHostUpdate(
            LayoutChanged: false,
            FrameChanged: true,
            new UiSceneDiff(UiPropertyEffects.Render, changed));
        LastUpdate = update;
        return update;
    }

    public void Render() => _compositor.Render(_frame, _platform);

    private UiLayoutSnapshot BuildLayout(UiScene scene, UiHostPlacementContext placement)
    {
        UiLayoutSnapshot layout = _layoutEngine.Build(scene, placement, _collections.Snapshot());
        _layoutBuilds++;
        return layout;
    }

    private UiRenderFrame BuildFrame(UiScene scene)
    {
        UiRenderFrame frame = _renderPlanner.Build(scene, Layout, Interactions.Snapshot, _platform);
        _frameBuilds++;
        return frame;
    }
}
