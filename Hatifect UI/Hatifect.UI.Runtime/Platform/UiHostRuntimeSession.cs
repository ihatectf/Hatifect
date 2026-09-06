using System;
using System.Collections.Generic;
using Hatifect.UI.Runtime.Accessibility;
using Hatifect.UI.Runtime.Actions;
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

// Every scene acceptance path of a graph-owned runtime uses the same ownership transaction.
// FenceAcceptedScene publishes inert membership only; EndSceneUpdate performs cancellation.
internal interface IUiHostSceneOwner
{
    void BeginSceneUpdate();
    void FenceAcceptedScene();
    void EndSceneUpdate();
}

internal sealed class UiHostRuntimeSession
{
    private readonly IUiPlatformBridge _platform;
    private IUiHostSceneOwner? _sceneOwner;
    private readonly UiSceneLayoutEngine _layoutEngine;
    private readonly UiSceneRenderPlanner _renderPlanner = new();
    private readonly UiAccessibilitySnapshotBuilder _accessibilityBuilder = new();
    private readonly UiCompositor _compositor = new();
    private readonly UiSceneReconciler _reconciler = new();
    private UiCollectionViewportState _collections = new();
    private readonly Func<UiInteractionSnapshot, UiScene>? _composeInteraction;
    private UiScene _scene;
    private UiHostPlacementContext _placement;
    private UiRenderFrame _frame;
    private long _layoutBuilds;
    private long _frameBuilds;
    private bool _preparingUpdate;
    private bool _active = true;
    private bool _pumpingActions;
    private bool _actionPresentationDirty;
    private readonly UiHostActionBindings _actions;

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
        Func<UiInteractionSnapshot, UiScene>? composeInteraction = null,
        IUiHostSceneOwner? sceneOwner = null)
    {
        _sceneOwner = sceneOwner;
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _placement = placement ?? throw new ArgumentNullException(nameof(placement));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _composeInteraction = composeInteraction;
        _layoutEngine = new UiSceneLayoutEngine(platform);
        _actions = new(() => AcceptedVersion);
        try
        {
            using var actions = _actions.Prepare(scene);
            Layout = BuildLayout(scene, placement);
            _collections.Synchronize(Layout);
            Interactions = new UiInteractionSession(scene, Layout, interaction, platform, CaptureInputOwnerVersion, actions.Resolver);
            Accessibility = BuildAccessibility(scene, Layout, Interactions.Snapshot, actions.Resolver);
            _frame = BuildFrame(scene, Layout, Interactions.Snapshot, actions.Map);
            actions.Commit();
            Interactions.SetActionResolver(actions.Map);
        }
        catch { Deactivate(); throw; }
    }

    public UiLayoutSnapshot Layout { get; private set; }
    public UiInteractionSession Interactions { get; }
    public UiAccessibilitySnapshot Accessibility { get; private set; }
    public UiRenderFrame Frame => _frame;
    public UiHostPolicy Policy => _scene.Root.Policy;
    internal UiScene Scene => _scene;
    internal UiHostUpdate? LastUpdate { get; private set; }
    internal bool IsActive => _active;
    internal long AcceptedVersion { get; private set; }
    internal void RequireOwner() => _actions.RequireOwner();
    // No callbacks: portal ownership can fence a whole tree before cancelling any operation.
    internal void FenceRetirement()
    {
        _actions.RequireOwner();
        _active = false;
        _sceneOwner = null;
        _actions.FenceRetirement();
    }
    internal void Deactivate()
    {
        FenceRetirement();
        _actions.Dispose();
    }
    internal int ActionCount => _actions.Count;
    internal IUiActionResolver Actions => _actions.Current;

    internal bool PumpActions()
    {
        if (!_active || _pumpingActions) return false;
        EnsureNotPreparing();
        _pumpingActions = true;
        try
        {
            bool changed = _actions.Pump();
            if (!_active) return changed;
            changed |= _actions.RefreshAvailability();
            _actionPresentationDirty |= changed;
            if (!_active || !_actionPresentationDirty) return changed;
            _preparingUpdate = true;
            try
            {
                // Availability/execution may already have advanced. Retain the presentation
                // debt until all callback-bearing preparation succeeds, just like scene Update.
                UiInteractionSession interaction = Interactions.PrepareReconcile(_scene, Layout);
                UiAccessibilitySnapshot accessibility = BuildAccessibility(_scene, Layout, interaction.Snapshot);
                UiRenderFrame frame = BuildFrame(_scene, Layout, interaction.Snapshot);
                EnsureActive();
                Interactions.CommitReconcile(interaction);
                _frame = frame;
                Accessibility = accessibility;
                _actionPresentationDirty = false;
                return true;
            }
            finally { _preparingUpdate = false; }
        }
        finally { _pumpingActions = false; }
    }
    internal UiHostRuntimePerformanceSnapshot Performance
        => new(_layoutBuilds, _frameBuilds);

    internal UiRuntimeDiagnosticSnapshot CaptureDiagnostics()
        => UiRuntimeDiagnosticCapture.Capture(
            _scene,
            Layout,
            Interactions.CaptureDiagnostics(),
            LastUpdate,
            Performance, _actions.Current);

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
        EnsureNotPreparing();
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(placement);
        IUiHostSceneOwner? sceneOwner = _sceneOwner;
        sceneOwner?.BeginSceneUpdate();
        _preparingUpdate = true;
        bool attemptedLayout = false;
        try
        {
            using var actions = _actions.Prepare(next);
            UiSceneDiff diff = _reconciler.Compare(_scene, next, Interactions.Snapshot, _actions.Current, actions.Map);
            bool layoutChanged = placement != _placement || diff.RequiresLayout;
            attemptedLayout = layoutChanged;
            UiLayoutSnapshot layout = layoutChanged ? BuildLayout(next, placement) : Layout;
            UiInteractionSession interaction = Interactions.PrepareReconcile(next, layout, actions.Resolver);
            EnsureActive();
            UiAccessibilitySnapshot accessibility = BuildAccessibility(next, layout, interaction.Snapshot, actions.Resolver);
            bool frameChanged = _actionPresentationDirty || layoutChanged || diff.RequiresRender || actions.RequiresRender ||
                                interaction.Snapshot != Interactions.Snapshot;
            UiRenderFrame frame = frameChanged ? BuildFrame(next, layout, interaction.Snapshot, actions.Map) : _frame;
            UiCollectionViewportState collections = _collections;
            if (layoutChanged)
            {
                collections = new UiCollectionViewportState();
                collections.Synchronize(layout);
            }
            var update = new UiHostUpdate(layoutChanged, frameChanged, diff);
            EnsureActive();

            // All callback-bearing work succeeded. Publish the prepared state without calling
            // consumers between assignments, retaining the input session's existing identity.
            actions.Commit();
            interaction.SetActionResolver(actions.Map);
            Layout = layout;
            Interactions.CommitReconcile(interaction);
            _frame = frame;
            Accessibility = accessibility;
            _collections = collections;
            _scene = next;
            _placement = placement;
            LastUpdate = update;
            AcceptedVersion++;
            _actionPresentationDirty = false;
            sceneOwner?.FenceAcceptedScene();
            return update;
        }
        catch
        {
            if (attemptedLayout) _layoutEngine.RestoreCollectionTransitions(_scene);
            throw;
        }
        finally
        {
            try { sceneOwner?.EndSceneUpdate(); }
            finally { _preparingUpdate = false; }
        }
    }

    public UiHostScrollUpdate ScrollCollection(UiSymbolId collection, float delta)
    {
        EnsureNotPreparing();
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
        Accessibility = BuildAccessibility(_scene, Layout, Interactions.Snapshot);
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
        EnsureNotPreparing();
        foreach (UiCollectionLayoutWindow window in Layout.CollectionWindows)
        {
            if (!Layout.TryGetEntry(window.Collection, out UiLayoutEntry? entry) || entry == null ||
                !entry.ContentBounds.Contains(point) || !entry.Clip.Contains(point))
                continue;
            return ScrollCollection(window.Collection, delta);
        }
        return new UiHostScrollUpdate(false, false, false, 0);
    }

    public UiInteractionUpdate MoveFocus(UiNavigationDirection direction)
    {
        EnsureNotPreparing();
        UiInteractionUpdate update = Interactions.MoveFocus(direction);
        if (!update.Consumed ||
            !Interactions.TryGetFocusedCollectionItem(out UiCollectionSceneNode collection, out UiSymbolId item, out int index) ||
            !Layout.TryGetCollection(collection.Id, out UiCollectionLayoutWindow? window) || window == null ||
            !Layout.TryGetEntry(collection.Id, out UiLayoutEntry? entry) || entry == null)
            return update;

        foreach (UiVirtualizedItemLayout target in window.Items)
        {
            if (target.Item.Id != item) continue;
            float top = Math.Max(entry.ContentBounds.Y, entry.Clip.Y);
            float bottom = Math.Min(entry.ContentBounds.Bottom, entry.Clip.Bottom);
            if (target.Bounds.Y >= top && target.Bounds.Bottom <= bottom) return update;
            // An oversized row is revealed once at its top; repeated navigation cannot oscillate.
            float delta = target.Bounds.Height > bottom - top || target.Bounds.Y < top
                ? target.Bounds.Y - top : target.Bounds.Bottom - bottom;
            _collections.SetOffset(collection.Id, Math.Max(0, window.ScrollOffset + delta));
            return ReconcileRevealedFocus(update);
        }
        _collections.Reveal(collection.Id, item, index);
        return ReconcileRevealedFocus(update);
    }

    private UiInteractionUpdate ReconcileRevealedFocus(UiInteractionUpdate update)
    {
        // Called only by explicit navigation. Passive wheel/scene updates retain focus without
        // bringing it back into view. The portal's ordinary interaction refresh renders this layout.
        Layout = BuildLayout(_scene, _placement);
        _collections.Synchronize(Layout);
        Interactions.Reconcile(_scene, Layout);
        return update with { StateChanged = true };
    }

    public UiHostUpdate? RefreshInteractionVisuals(bool textChanged = false)
    {
        EnsureNotPreparing();
        var compose = _composeInteraction ?? _scene.RecomposePublication;
        if (compose == null)
        {
            if (textChanged)
                return RefreshLiveText();
            _frame = BuildFrame(_scene);
            Accessibility = BuildAccessibility(_scene, Layout, Interactions.Snapshot);
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
        UiScene next = compose(Interactions.Snapshot)
            ?? throw new InvalidOperationException("The interaction scene composer returned null.");
        return Update(next, _placement);
    }

    private UiHostUpdate RefreshLiveText()
    {
        Layout = BuildLayout(_scene, _placement);
        _collections.Synchronize(Layout);
        Interactions.Reconcile(_scene, Layout);
        _frame = BuildFrame(_scene);
        Accessibility = BuildAccessibility(_scene, Layout, Interactions.Snapshot);
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
        EnsureNotPreparing();
        _frame = BuildFrame(_scene);
        Accessibility = BuildAccessibility(_scene, Layout, Interactions.Snapshot);
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

    public void Render()
    {
        if (_active) _compositor.Render(_frame, _platform);
    }

    private UiLayoutSnapshot BuildLayout(UiScene scene, UiHostPlacementContext placement)
    {
        EnsureActive();
        UiLayoutSnapshot layout = _layoutEngine.Build(scene, placement, _collections.Snapshot());
        EnsureActive();
        _layoutBuilds++;
        return layout;
    }

    private UiRenderFrame BuildFrame(UiScene scene)
        => BuildFrame(scene, Layout, Interactions.Snapshot);

    private UiRenderFrame BuildFrame(UiScene scene, UiLayoutSnapshot layout, UiInteractionSnapshot interaction,
        IUiActionResolver? actions = null)
    {
        EnsureActive();
        UiRenderFrame frame = _renderPlanner.Build(scene, layout, interaction, _platform, actions ?? _actions.Current);
        EnsureActive();
        _frameBuilds++;
        return frame;
    }

    private UiAccessibilitySnapshot BuildAccessibility(
        UiScene scene, UiLayoutSnapshot layout, UiInteractionSnapshot interaction,
        IUiActionResolver? actions = null)
    {
        EnsureActive();
        UiAccessibilitySnapshot snapshot = _accessibilityBuilder.Build(scene, layout, interaction, actions ?? _actions.Current);
        EnsureActive();
        return snapshot;
    }

    private long CaptureInputOwnerVersion()
    {
        EnsureNotPreparing();
        return AcceptedVersion;
    }

    private void EnsureNotPreparing()
    {
        _actions.RequireOwner();
        EnsureActive();
        if (_preparingUpdate)
            throw new InvalidOperationException("The host cannot be mutated while a scene update is being prepared.");
    }

    private void EnsureActive()
    {
        if (!_active) throw new ObjectDisposedException(nameof(UiHostRuntimeSession));
    }
}
