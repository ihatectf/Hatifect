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
    void FenceAcceptedScene(UiHostRuntimeSession runtime, bool renewedGeneration);
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
    private UiActionPresentationSnapshot _actionPresentation = null!;
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
            _actionPresentation = actions.Map.CapturePresentation(scene.MeasurementContext.Locale, actions.Resolver);
            if (actions.Map.HasLegacyBindings)
                Interactions.CommitReconcile(Interactions.PrepareReconcile(scene, Layout, _actionPresentation));
            Accessibility = BuildAccessibility(scene, Layout, Interactions.Snapshot, _actionPresentation);
            _frame = BuildFrame(scene, Layout, Interactions.Snapshot, _actionPresentation);
            _actionPresentation.RequireCurrent(actions.Map, scene.MeasurementContext.Locale);
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
    internal long FrameVersion { get; private set; }
    internal UiSurfaceRenderStamp LastCompletedRender { get; private set; }
    internal void RequireOwner() => _actions.RequireOwner();
    internal void RequireSceneMutation() => EnsureNotPreparing();
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
    internal UiActionDispatcherOwnershipSnapshot ActionOwnership => _actions.CaptureOwnership();

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
                var presentation = _actions.CapturePresentation(_scene.MeasurementContext.Locale);
                UiAccessibilitySnapshot accessibility = BuildAccessibility(_scene, Layout, interaction.Snapshot, presentation);
                UiRenderFrame frame = BuildFrame(_scene, Layout, interaction.Snapshot, presentation);
                EnsureActive();
                _actions.ValidatePresentation(presentation, _scene.MeasurementContext.Locale);
                Interactions.CommitReconcile(interaction);
                AcceptFrame(frame);
                Accessibility = accessibility;
                _actionPresentation = presentation;
                _actionPresentationDirty = false;
                return true;
            }
            finally { _preparingUpdate = false; }
        }
        finally { _pumpingActions = false; }
    }
    internal UiHostRuntimePerformanceSnapshot Performance
        => new(_layoutBuilds, _frameBuilds);

    internal bool AdvanceInteractions(TimeSpan elapsed)
    {
        EnsureNotPreparing();
        if (!Interactions.AdvanceTooltip(elapsed)) return false;
        AcceptFrame(BuildFrame(_scene));
        UiSymbolId[] changed = Interactions.Snapshot.Hovered is { } hovered
            ? new[] { hovered }
            : Array.Empty<UiSymbolId>();
        LastUpdate = new UiHostUpdate(
            LayoutChanged: false,
            FrameChanged: true,
            new UiSceneDiff(UiPropertyEffects.Render, changed));
        return true;
    }

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
        => Update(next, placement, renewActionGeneration: false, acceptOwnerState: null);

    // acceptOwnerState is framework-owned, callback-free metadata publication. All validation
    // belongs before this call; it runs after scene acceptance and before retirement callbacks.
    internal UiHostUpdate Update(UiScene next, UiHostPlacementContext placement,
        bool renewActionGeneration, Action? acceptOwnerState, Action? validatePreparedOwner = null)
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
            using var actions = _actions.Prepare(next, renewActionGeneration);
            UiSceneDiff diff = _reconciler.Compare(_scene, next, Interactions.Snapshot, _actions.Current, actions.Map);
            bool layoutChanged = placement != _placement || diff.RequiresLayout;
            attemptedLayout = layoutChanged;
            UiLayoutSnapshot layout = layoutChanged ? BuildLayout(next, placement) : Layout;
            UiInteractionSession interaction = Interactions.PrepareReconcile(next, layout, actions.Resolver);
            EnsureActive();
            var presentation = actions.Map.CapturePresentation(next.MeasurementContext.Locale, actions.Resolver);
            // Legacy callbacks may have retired a sibling since the first focus preparation.
            // Reconcile the candidate against the same callback-free state used by its frame.
            if (actions.Map.HasLegacyBindings)
                interaction = interaction.PrepareReconcile(next, layout, presentation);
            UiAccessibilitySnapshot accessibility = BuildAccessibility(next, layout, interaction.Snapshot, presentation);
            bool frameChanged = !presentation.HasSamePresentation(_actionPresentation) || _actionPresentationDirty || layoutChanged || diff.RequiresRender || actions.RequiresRender ||
                                interaction.Snapshot != Interactions.Snapshot;
            UiRenderFrame frame = frameChanged ? BuildFrame(next, layout, interaction.Snapshot, presentation) : _frame;
            UiCollectionViewportState collections = _collections;
            if (layoutChanged)
            {
                collections = new UiCollectionViewportState();
                collections.Synchronize(layout);
            }
            var update = new UiHostUpdate(layoutChanged, frameChanged, diff);
            validatePreparedOwner?.Invoke();
            EnsureActive();
            presentation.RequireCurrent(actions.Map, next.MeasurementContext.Locale);

            // All callback-bearing work succeeded. Publish the prepared state without calling
            // consumers between assignments, retaining the input session's existing identity.
            actions.Commit();
            interaction.SetActionResolver(actions.Map);
            Layout = layout;
            Interactions.CommitReconcile(interaction);
            AcceptFrame(frame);
            Accessibility = accessibility;
            _actionPresentation = presentation;
            _collections = collections;
            _scene = next;
            _placement = placement;
            LastUpdate = update;
            AcceptedVersion++;
            _actionPresentationDirty = false;
            sceneOwner?.FenceAcceptedScene(this, renewActionGeneration);
            acceptOwnerState?.Invoke();
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
        AcceptFrame(BuildFrame(_scene));
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
        return Layout.RootScroll is { } root && root.Viewport.Contains(point)
            ? ScrollRoot(delta)
            : new UiHostScrollUpdate(false, false, false, 0);
    }

    private UiHostScrollUpdate ScrollRoot(float delta)
    {
        if (!float.IsFinite(delta)) throw new ArgumentOutOfRangeException(nameof(delta));
        UiRootScrollLayout root = Layout.RootScroll!;
        float offset = Math.Clamp(root.Offset + delta, 0, root.MaximumOffset);
        if (offset == root.Offset) return new(true, false, false, root.Offset);
        _preparingUpdate = true;
        try
        {
            UiLayoutSnapshot layout = BuildLayout(_scene, _placement, offset);
            UiInteractionSession interaction = Interactions.PrepareReconcile(_scene, layout, _actions.Current);
            UiAccessibilitySnapshot accessibility = BuildAccessibility(_scene, layout, interaction.Snapshot);
            UiRenderFrame frame = BuildFrame(_scene, layout, interaction.Snapshot);
            var collections = new UiCollectionViewportState();
            collections.Synchronize(layout);
            EnsureActive();
            Layout = layout;
            Interactions.CommitReconcile(interaction);
            Accessibility = accessibility;
            AcceptFrame(frame);
            _collections = collections;
            LastUpdate = new(true, true, new UiSceneDiff(
                UiPropertyEffects.Arrange | UiPropertyEffects.Render, new[] { _scene.Root.Id }));
            return new(true, true, true, layout.RootScroll?.Offset ?? 0);
        }
        catch
        {
            _layoutEngine.RestoreCollectionTransitions(_scene);
            throw;
        }
        finally { _preparingUpdate = false; }
    }

    private static float RootFocusOffset(UiInteractionSession interaction, UiLayoutSnapshot layout)
    {
        UiRootScrollLayout root = layout.RootScroll!;
        if (interaction.Snapshot.Focused is not { } focused) return root.Offset;
        // Reveal the collection viewport before navigating its own virtualized items.
        UiSymbolId target = interaction.TryGetFocusedCollectionItem(out UiCollectionSceneNode owner, out _, out _)
            ? owner.Id : focused;
        if (!layout.TryGetEntry(target, out UiLayoutEntry? entry) || entry == null) return root.Offset;
        UiRect bounds = entry.Bounds;
        if (bounds.Y >= root.Viewport.Y && bounds.Bottom <= root.Viewport.Bottom) return root.Offset;
        float delta = bounds.Height > root.Viewport.Height || bounds.Y < root.Viewport.Y
            ? bounds.Y - root.Viewport.Y : bounds.Bottom - root.Viewport.Bottom;
        return Math.Clamp(root.Offset + delta, 0, root.MaximumOffset);
    }

    private UiInteractionUpdate MoveFocusInScrollableRoot(UiNavigationDirection direction)
    {
        _preparingUpdate = true;
        try
        {
            UiInteractionSession interaction = Interactions.PrepareMoveFocus(direction, out UiInteractionUpdate update);
            if (!update.Consumed) return update;
            UiLayoutSnapshot layout = Layout;
            float offset = RootFocusOffset(interaction, layout);
            if (offset != layout.RootScroll!.Offset)
            {
                layout = BuildLayout(_scene, _placement, offset);
                interaction = interaction.PrepareReconcile(_scene, layout);
            }
            UiCollectionViewportState? collections = null;
            if (interaction.TryGetFocusedCollectionItem(out UiCollectionSceneNode collection, out UiSymbolId item, out int index) &&
                layout.TryGetCollection(collection.Id, out UiCollectionLayoutWindow? window) && window != null &&
                layout.TryGetEntry(collection.Id, out UiLayoutEntry? entry) && entry != null)
            {
                UiVirtualizedItemLayout? target = null;
                foreach (UiVirtualizedItemLayout row in window.Items)
                    if (row.Item.Id == item) { target = row; break; }
                float top = Math.Max(entry.ContentBounds.Y, entry.Clip.Y);
                float bottom = Math.Min(entry.ContentBounds.Bottom, entry.Clip.Bottom);
                if (target is not { } visible || visible.Bounds.Y < top || visible.Bounds.Bottom > bottom)
                {
                    collections = new UiCollectionViewportState();
                    collections.Synchronize(layout);
                    if (target is { } row)
                    {
                        float delta = row.Bounds.Height > bottom - top || row.Bounds.Y < top
                            ? row.Bounds.Y - top : row.Bounds.Bottom - bottom;
                        collections.SetOffset(collection.Id, Math.Max(0, window.ScrollOffset + delta));
                    }
                    else collections.Reveal(collection.Id, item, index);
                    layout = BuildLayout(_scene, _placement, offset, collections);
                    interaction = interaction.PrepareReconcile(_scene, layout);
                }
            }
            bool layoutChanged = !ReferenceEquals(layout, Layout);
            if (layoutChanged)
            {
                collections ??= new UiCollectionViewportState();
                collections.Synchronize(layout);
                UiAccessibilitySnapshot accessibility = BuildAccessibility(_scene, layout, interaction.Snapshot);
                UiRenderFrame frame = BuildFrame(_scene, layout, interaction.Snapshot);
                EnsureActive();
                // Root and nested collection reveal are one acceptance transaction.
                Layout = layout;
                Accessibility = accessibility;
                AcceptFrame(frame);
                _collections = collections;
                LastUpdate = new(true, true, new UiSceneDiff(
                    UiPropertyEffects.Arrange | UiPropertyEffects.Render, new[] { _scene.Root.Id }));
            }
            EnsureActive();
            Interactions.CommitReconcile(interaction);
            return update with { StateChanged = update.StateChanged || layoutChanged };
        }
        catch
        {
            _layoutEngine.RestoreCollectionTransitions(_scene);
            throw;
        }
        finally { _preparingUpdate = false; }
    }

    public UiInteractionUpdate MoveFocus(UiNavigationDirection direction)
    {
        EnsureNotPreparing();
        if (Layout.RootScroll != null) return MoveFocusInScrollableRoot(direction);
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
            AcceptFrame(BuildFrame(_scene));
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
        long version = AcceptedVersion;
        UiScene next = compose(Interactions.Snapshot)
            ?? throw new InvalidOperationException("The interaction scene composer returned null.");
        EnsureNotPreparing();
        // Composition may accept a nested update. Keep that accepted frame and reject
        // this obsolete result, including when the nested update reused the same model.
        if (AcceptedVersion != version)
            throw new InvalidOperationException("The accepted host changed during interaction composition.");
        return Update(next, _placement);
    }

    private UiHostUpdate RefreshLiveText()
    {
        Layout = BuildLayout(_scene, _placement);
        _collections.Synchronize(Layout);
        Interactions.Reconcile(_scene, Layout);
        AcceptFrame(BuildFrame(_scene));
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
        AcceptFrame(BuildFrame(_scene));
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
        if (!_active) return;
        UiRenderFrame frame = _frame;
        long sceneVersion = AcceptedVersion;
        long frameVersion = FrameVersion;
        _compositor.Render(frame, _platform);
        LastCompletedRender = new(LastCompletedRender.Sequence + 1, sceneVersion, frameVersion);
    }

    private void AcceptFrame(UiRenderFrame frame)
    {
        if (ReferenceEquals(_frame, frame)) return;
        _frame = frame;
        FrameVersion++;
    }

    private UiLayoutSnapshot BuildLayout(UiScene scene, UiHostPlacementContext placement,
        float? rootOffset = null, UiCollectionViewportState? collections = null)
    {
        EnsureActive();
        float offset = rootOffset ?? (scene.Root.Id == _scene.Root.Id ? Layout?.RootScroll?.Offset ?? 0 : 0);
        UiLayoutSnapshot layout = _layoutEngine.Build(scene, placement, (collections ?? _collections).Snapshot(), offset);
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
        UiRenderFrame frame = _renderPlanner.Build(scene, layout, interaction, _platform, actions ?? _actionPresentation);
        EnsureActive();
        _frameBuilds++;
        return frame;
    }

    private UiAccessibilitySnapshot BuildAccessibility(
        UiScene scene, UiLayoutSnapshot layout, UiInteractionSnapshot interaction,
        IUiActionResolver? actions = null)
    {
        EnsureActive();
        UiAccessibilitySnapshot snapshot = _accessibilityBuilder.Build(scene, layout, interaction, actions ?? _actionPresentation);
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
