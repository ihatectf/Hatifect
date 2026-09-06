using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Runtime.Accessibility;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Scene;

namespace Hatifect.UI.Runtime.Platform;

internal sealed record UiPortalOwner
{
    public UiPortalOwner(UiSymbolId node, UiSymbolId? portal = null)
    {
        if (!node.IsValid) throw new ArgumentException("A portal owner node ID is required.", nameof(node));
        if (portal is { } owner && !owner.IsValid)
            throw new ArgumentException("A portal owner ID must be valid.", nameof(portal));
        Node = node;
        Portal = portal;
    }

    public UiSymbolId Node { get; }
    public UiSymbolId? Portal { get; }
}

internal sealed record UiPortalRequest
{
    public UiPortalRequest(
        UiSymbolId id,
        UiPortalOwner owner,
        UiScene scene,
        UiHostPlacementContext placement,
        Func<UiInteractionSnapshot, UiScene>? composeInteraction = null)
    {
        if (!id.IsValid) throw new ArgumentException("A stable portal ID is required.", nameof(id));
        Id = id;
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Scene = scene ?? throw new ArgumentNullException(nameof(scene));
        Placement = placement ?? throw new ArgumentNullException(nameof(placement));
        ComposeInteraction = composeInteraction;
        if (scene.Root.Policy.Kind is not (UiHostKind.Popup or UiHostKind.Context or
            UiHostKind.Sheet or UiHostKind.Overlay))
            throw new ArgumentException(
                $"Host kind '{scene.Root.Policy.Kind}' cannot be presented as a portal.", nameof(scene));
    }

    public UiSymbolId Id { get; }
    public UiPortalOwner Owner { get; }
    public UiScene Scene { get; }
    public UiHostPlacementContext Placement { get; }
    public Func<UiInteractionSnapshot, UiScene>? ComposeInteraction { get; }
}

internal sealed record UiPortalDispatch(
    bool Consumed,
    UiSymbolId? Portal,
    UiInteractionUpdate? Interaction,
    bool PortalClosed = false)
{
    public bool RequestsRootDismissal => !Consumed || (Portal == null && Interaction?.DismissRequested == true);
}

internal sealed record UiPortalScrollDispatch(
    bool Consumed,
    UiSymbolId? Portal,
    UiHostScrollUpdate? Scroll);

/// <summary>Platform-normalized input target; Runtime implementations retain route and portal behavior.</summary>
internal interface IUiPlatformInputSession
{
    UiPortalDispatch PressPointer(UiPoint point);
    UiPortalDispatch MovePointer(UiPoint point);
    UiPortalDispatch ReleasePointer(UiPoint point);
    UiPortalDispatch UnhandledInput();
    UiPortalDispatch Cancel();
    UiPortalDispatch MoveFocus(UiNavigationDirection direction);
    UiPortalDispatch Submit();
    UiPortalDispatch ReplaceText(string? text);
    UiPortalDispatch InsertText(string? text);
    UiPortalDispatch EditText(UiTextEditAction action, bool extendSelection = false);
    UiPortalScrollDispatch ScrollAt(UiPoint point, float delta);
    UiTextEditingSnapshot? FocusedTextEditing { get; }
}

internal sealed class UiPortalHandle : IDisposable
{
    private Action? _close;

    internal UiPortalHandle(Action close)
        => _close = close ?? throw new ArgumentNullException(nameof(close));

    public void Dispose()
    {
        Action? close = _close;
        close?.Invoke();
        _close = null;
    }
}

/// <summary>
/// Owns one root runtime and its ordered portal stack. The platform normalizes input and supplies
/// geometry; this session owns ancestry, modality, focus isolation, dismissal, and paint order.
/// </summary>
internal sealed class UiPortalHostSession : IUiPlatformInputSession, IUiHostSceneOwner
{
    private readonly IUiPlatformBridge _platform;
    private readonly List<PortalEntry> _portals = new();
    private PortalEntry[] _portalSnapshot = Array.Empty<PortalEntry>();
    private long _nextGeneration;
    private bool _active = true;
    private bool _pumping;
    private bool _updating;
    private PortalEntry[] _updateRetirements = Array.Empty<PortalEntry>();

    // An action can retire its host while dispatch is still unwinding. Stop post-action
    // composition before its owner and platform resources are released.
    internal void Deactivate()
    {
        Root.RequireOwner();
        if (!_active) return;
        _active = false;
        PortalEntry[] retiring = _portalSnapshot;
        Root.FenceRetirement();
        foreach (PortalEntry portal in retiring) portal.Runtime.FenceRetirement();
        _portals.Clear();
        _portalSnapshot = Array.Empty<PortalEntry>();
        Root.Deactivate();
        foreach (PortalEntry portal in retiring) portal.Runtime.Deactivate();
    }

    public UiPortalHostSession(
        UiScene root,
        UiHostPlacementContext placement,
        IUiPlatformBridge platform,
        UiInteractionSnapshot? interaction = null,
        Func<UiInteractionSnapshot, UiScene>? composeInteraction = null)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        Root = new UiHostRuntimeSession(root, placement, platform, interaction, composeInteraction, sceneOwner: this);
    }

    public UiHostRuntimeSession Root { get; }
    internal bool PumpActions()
    {
        Root.RequireOwner();
        if (!_active || _pumping || _updating) return false;
        _pumping = true;
        // Membership changes replace this snapshot. Children presented by a completion are
        // first eligible on the next tick; removed children are already fenced before cleanup.
        PortalEntry[] portals = _portalSnapshot;
        try
        {
            bool changed = Root.PumpActions();
            foreach (PortalEntry portal in portals)
            {
                if (!_active) break;
                if (portal.Runtime.IsActive) changed |= portal.Runtime.PumpActions();
            }
            return changed;
        }
        finally { _pumping = false; }
    }
    internal UiHostRuntimePerformanceSnapshot Performance
    {
        get
        {
            UiHostRuntimePerformanceSnapshot root = Root.Performance;
            long layoutBuilds = root.LayoutBuilds;
            long frameBuilds = root.FrameBuilds;
            foreach (PortalEntry portal in _portals)
            {
                UiHostRuntimePerformanceSnapshot current = portal.Runtime.Performance;
                layoutBuilds += current.LayoutBuilds;
                frameBuilds += current.FrameBuilds;
            }
            return new UiHostRuntimePerformanceSnapshot(layoutBuilds, frameBuilds);
        }
    }
    public IReadOnlyList<UiSymbolId> ActivePortals
        => _portals.Select(item => item.Request.Id).ToArray();
    public UiAccessibilityHostSnapshot Accessibility
        => new(
            Root.Accessibility,
            _portals.Select(item => new UiAccessibilityPortalSnapshot(
                    item.Request.Id,
                    item.Request.Owner.Node,
                    item.Request.Owner.Portal,
                    item.Runtime.Policy.Modal == UiModalPolicy.Modal,
                    item.Runtime.Accessibility))
                .ToArray());

    public UiPortalHandle Present(UiPortalRequest request)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(request);
        if (_portals.Any(item => item.Request.Id == request.Id))
            throw new InvalidOperationException($"Portal '{request.Id}' is already active.");
        if (!OwnerExists(request.Owner))
            throw new InvalidOperationException(
                $"Portal '{request.Id}' references missing owner node '{request.Owner.Node}'.");
        UiHostRuntimeSession owner = OwnerRuntime(request.Owner)!;
        long ownerVersion = owner.AcceptedVersion;

        long generation = ++_nextGeneration;
        var runtime = new UiHostRuntimeSession(
            request.Scene,
            request.Placement,
            _platform,
            composeInteraction: request.ComposeInteraction, sceneOwner: this);
        try
        {
            EnsureCurrentOwner();
            if (request.Scene.Root.Policy.Focus is UiFocusScopePolicy.Contained or UiFocusScopePolicy.Trapped)
            {
                UiInteractionUpdate focus = runtime.MoveFocus(UiNavigationDirection.Next);
                Refresh(runtime, focus);
            }
            EnsureCurrentOwner();
            _portals.Add(new PortalEntry(request, runtime, generation));
            _portalSnapshot = _portals.ToArray();
            return new UiPortalHandle(() => Close(request.Id, generation));
        }
        catch
        {
            runtime.Deactivate();
            throw;
        }

        void EnsureCurrentOwner()
        {
            EnsureActive();
            if (!ReferenceEquals(owner, OwnerRuntime(request.Owner)) || !owner.IsActive ||
                owner.AcceptedVersion != ownerVersion || !OwnerExists(request.Owner) ||
                _portals.Any(item => item.Request.Id == request.Id))
                throw new InvalidOperationException("The portal owner or registration changed during preparation.");
        }
    }

    public UiHostUpdate UpdateRoot(UiScene scene, UiHostPlacementContext placement)
        => UpdateRoot(scene, placement, renewActionGeneration: false, acceptOwnerState: null);

    internal UiHostUpdate UpdateRoot(UiScene scene, UiHostPlacementContext placement,
        bool renewActionGeneration, Action? acceptOwnerState)
    {
        EnsureActive();
        return Root.Update(scene, placement, renewActionGeneration, acceptOwnerState);
    }

    public UiHostUpdate UpdatePortal(UiSymbolId id, UiScene scene, UiHostPlacementContext placement)
    {
        EnsureActive();
        PortalEntry entry = Find(id);
        if (scene.Root.Policy.Kind is not (UiHostKind.Popup or UiHostKind.Context or
            UiHostKind.Sheet or UiHostKind.Overlay))
            throw new ArgumentException($"Host kind '{scene.Root.Policy.Kind}' cannot be used by a portal.", nameof(scene));
        UiHostUpdate update = entry.Runtime.Update(scene, placement);
        entry.Request = new UiPortalRequest(
            id, entry.Request.Owner, scene, placement, entry.Request.ComposeInteraction);
        return update;
    }

    void IUiHostSceneOwner.BeginSceneUpdate()
    {
        EnsureActive();
        _updating = true;
    }

    void IUiHostSceneOwner.FenceAcceptedScene(UiHostRuntimeSession runtime, bool renewedGeneration)
        => _updateRetirements = DetachOrphans(renewedGeneration ? runtime : null);

    void IUiHostSceneOwner.EndSceneUpdate()
    {
        // Detached entries are absent from Deactivate/Close. Retain this local set even
        // when a removed owner's cancellation retires the host or closes another tree.
        PortalEntry[] retiring = _updateRetirements;
        _updateRetirements = Array.Empty<PortalEntry>();
        try { foreach (PortalEntry entry in retiring) entry.Runtime.Deactivate(); }
        finally { _updating = false; }
    }

    public bool Close(UiSymbolId id)
    {
        Root.RequireOwner();
        PortalEntry? entry = _portals.FirstOrDefault(item => item.Request.Id == id);
        return entry != null && Close(id, entry.Generation);
    }

    public UiPortalDispatch PressPointer(UiPoint point)
    {
        EnsureActive();
        for (int index = _portals.Count - 1; index >= 0; index--)
        {
            PortalEntry entry = _portals[index];
            UiInteractionUpdate update = entry.Runtime.Interactions.PressPointer(point);
            if (update.DismissRequested)
            {
                Close(entry.Request.Id, entry.Generation);
                return new UiPortalDispatch(true, entry.Request.Id, update, PortalClosed: true);
            }
            Refresh(entry.Runtime, update);
            if (update.Consumed)
                return new UiPortalDispatch(true, entry.Request.Id, update);
            if (entry.Runtime.Policy.Modal == UiModalPolicy.Modal)
                return new UiPortalDispatch(true, entry.Request.Id, update);
        }

        UiInteractionUpdate root = Root.Interactions.PressPointer(point);
        Refresh(Root, root);
        return new UiPortalDispatch(root.Consumed || HasModalInputBarrier(), null, root);
    }

    public UiPortalDispatch MovePointer(UiPoint point)
    {
        EnsureActive();
        for (int index = _portals.Count - 1; index >= 0; index--)
        {
            PortalEntry entry = _portals[index];
            UiInteractionUpdate update = entry.Runtime.Interactions.MovePointer(point);
            Refresh(entry.Runtime, update);
            if (update.Consumed)
                return new UiPortalDispatch(true, entry.Request.Id, update);
            if (entry.Runtime.Policy.Modal == UiModalPolicy.Modal)
                return new UiPortalDispatch(true, entry.Request.Id, update);
        }
        UiInteractionUpdate root = Root.Interactions.MovePointer(point);
        Refresh(Root, root);
        return new UiPortalDispatch(root.Consumed || HasModalInputBarrier(), null, root);
    }

    public UiPortalDispatch ReleasePointer(UiPoint point)
    {
        EnsureActive();
        for (int index = _portals.Count - 1; index >= 0; index--)
        {
            PortalEntry entry = _portals[index];
            if (!entry.Runtime.Contains(point) && entry.Runtime.Interactions.Snapshot.Pressed == null)
            {
                if (entry.Runtime.Policy.Modal == UiModalPolicy.Modal)
                    return new UiPortalDispatch(true, entry.Request.Id, null);
                continue;
            }
            UiInteractionUpdate update = entry.Runtime.Interactions.ReleasePointer(point);
            Refresh(entry.Runtime, update);
            return new UiPortalDispatch(update.Consumed || HasModalInputBarrier(), entry.Request.Id, update);
        }

        UiInteractionUpdate root = Root.Interactions.ReleasePointer(point);
        Refresh(Root, root);
        return new UiPortalDispatch(root.Consumed || HasModalInputBarrier(), null, root);
    }

    // Unmapped keys and secondary pointer buttons cross the same modal boundary, but must not
    // be translated into a primary action, focus change, or dismissal.
    public UiPortalDispatch UnhandledInput()
    {
        EnsureActive();
        return new(HasModalInputBarrier(), _portals.Count == 0 ? null : _portals[^1].Request.Id, null);
    }

    public UiPortalDispatch Cancel()
    {
        EnsureActive();
        if (_portals.Count == 0)
        {
            UiInteractionUpdate root = Root.Interactions.Cancel();
            return new UiPortalDispatch(root.Consumed || HasModalInputBarrier(), null, root);
        }

        PortalEntry entry = _portals[^1];
        UiInteractionUpdate update = entry.Runtime.Interactions.Cancel();
        if (update.DismissRequested)
        {
            Close(entry.Request.Id, entry.Generation);
            return new UiPortalDispatch(true, entry.Request.Id, update, PortalClosed: true);
        }
        return new UiPortalDispatch(update.Consumed || HasModalInputBarrier(), entry.Request.Id, update);
    }

    public UiPortalDispatch MoveFocus(UiNavigationDirection direction)
        => DispatchToKeyboardOwner(runtime => runtime.MoveFocus(direction));

    public UiPortalDispatch Submit()
        => DispatchToKeyboardOwner(runtime => runtime.Interactions.Submit());

    public UiPortalDispatch ReplaceText(string? text)
        => DispatchToKeyboardOwner(runtime => runtime.Interactions.ReplaceText(text));

    public UiPortalDispatch InsertText(string? text)
        => DispatchToKeyboardOwner(runtime => runtime.Interactions.InsertText(text));

    public UiPortalDispatch EditText(UiTextEditAction action, bool extendSelection = false)
        => DispatchToKeyboardOwner(runtime => runtime.Interactions.EditText(action, extendSelection));

    public UiTextEditingSnapshot? FocusedTextEditing
        => KeyboardOwner().Interactions.Snapshot.TextEditing;

    public UiPortalScrollDispatch ScrollAt(UiPoint point, float delta)
    {
        EnsureActive();
        for (int index = _portals.Count - 1; index >= 0; index--)
        {
            PortalEntry entry = _portals[index];
            UiHostScrollUpdate update = entry.Runtime.ScrollAt(point, delta);
            if (update.Consumed)
                return new UiPortalScrollDispatch(true, entry.Request.Id, update);
            if (entry.Runtime.Policy.Modal == UiModalPolicy.Modal)
                return new UiPortalScrollDispatch(true, entry.Request.Id, update);
        }
        UiHostScrollUpdate root = Root.ScrollAt(point, delta);
        return new UiPortalScrollDispatch(root.Consumed || HasModalInputBarrier(), null, root);
    }

    public void Render()
    {
        if (!_active) return;
        Root.Render();
        foreach (PortalEntry portal in _portals) portal.Runtime.Render();
    }

    private bool Close(UiSymbolId id, long generation)
    {
        Root.RequireOwner();
        int root = _portals.FindIndex(item => item.Request.Id == id && item.Generation == generation);
        if (root < 0) return false;
        PortalEntry[] retiring = DetachPortals(new HashSet<UiSymbolId> { id });
        foreach (PortalEntry entry in retiring) entry.Runtime.Deactivate();
        return true;
    }

    private PortalEntry[] DetachPortals(HashSet<UiSymbolId> retiring)
    {
        // Present requires an existing owner, so every ancestor precedes its descendants.
        foreach (PortalEntry entry in _portalSnapshot)
            if (entry.Request.Owner.Portal is { } owner && retiring.Contains(owner))
                retiring.Add(entry.Request.Id);
        PortalEntry[] detached = _portalSnapshot.Where(entry => retiring.Contains(entry.Request.Id)).ToArray();
        foreach (PortalEntry entry in detached) entry.Runtime.FenceRetirement();
        _portals.RemoveAll(entry => retiring.Contains(entry.Request.Id));
        _portalSnapshot = _portals.ToArray();
        return detached;
    }

    private UiPortalDispatch DispatchToKeyboardOwner(
        Func<UiHostRuntimeSession, UiInteractionUpdate> dispatch)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(dispatch);
        PortalEntry? entry = _portals.Count == 0 ? null : _portals[^1];
        UiHostRuntimeSession runtime = entry?.Runtime ?? Root;
        UiInteractionUpdate update = dispatch(runtime);
        Refresh(runtime, update);
        return new UiPortalDispatch(update.Consumed || HasModalInputBarrier(), entry?.Request.Id, update);
    }

    private bool HasModalInputBarrier()
    {
        if (Root.Policy.Modal == UiModalPolicy.Modal) return true;
        for (int index = _portals.Count - 1; index >= 0; index--)
            if (_portals[index].Runtime.Policy.Modal == UiModalPolicy.Modal) return true;
        return false;
    }

    private UiHostRuntimeSession KeyboardOwner()
        => _portals.Count == 0 ? Root : _portals[^1].Runtime;

    private void Refresh(UiHostRuntimeSession runtime, UiInteractionUpdate update)
    {
        if (!_active || !runtime.IsActive) return;
        if (update.StateChanged || update.TextChanged || update.ActionInvoked)
            runtime.RefreshInteractionVisuals(update.TextChanged);
        if (_active && runtime.IsActive && update.TextEditingChanged && !update.TextChanged)
            runtime.RefreshTextEditingVisuals();
    }

    private PortalEntry[] DetachOrphans(UiHostRuntimeSession? renewedOwner = null)
    {
        bool rootRenewed = ReferenceEquals(renewedOwner, Root);
        UiSymbolId? renewedPortal = renewedOwner == null || rootRenewed ? null
            : _portals.FirstOrDefault(entry => ReferenceEquals(entry.Runtime, renewedOwner))?.Request.Id;
        HashSet<UiSymbolId>? retiring = null;
        foreach (PortalEntry entry in _portalSnapshot)
        {
            UiSymbolId? owner = entry.Request.Owner.Portal;
            bool ownerRenewed = rootRenewed && owner == null || renewedPortal != null && owner == renewedPortal;
            if (!ownerRenewed && OwnerExists(entry.Request.Owner)) continue;
            (retiring ??= new HashSet<UiSymbolId>()).Add(entry.Request.Id);
        }
        return retiring == null ? Array.Empty<PortalEntry>() : DetachPortals(retiring);
    }

    private bool OwnerExists(UiPortalOwner owner)
        => OwnerRuntime(owner)?.ContainsNode(owner.Node) == true;

    private UiHostRuntimeSession? OwnerRuntime(UiPortalOwner owner)
    {
        if (owner.Portal == null) return Root;
        PortalEntry? portal = _portals.FirstOrDefault(item => item.Request.Id == owner.Portal.Value);
        return portal?.Runtime;
    }

    private void EnsureActive()
    {
        Root.RequireOwner();
        if (!_active) throw new ObjectDisposedException(nameof(UiPortalHostSession));
        if (_updating) throw new InvalidOperationException("The portal graph cannot be mutated during a scene update.");
    }

    private PortalEntry Find(UiSymbolId id)
        => _portals.FirstOrDefault(item => item.Request.Id == id)
           ?? throw new KeyNotFoundException($"Portal '{id}' is not active.");

    private sealed class PortalEntry
    {
        public PortalEntry(UiPortalRequest request, UiHostRuntimeSession runtime, long generation)
        {
            Request = request;
            Runtime = runtime;
            Generation = generation;
        }

        public UiPortalRequest Request { get; set; }
        public UiHostRuntimeSession Runtime { get; }
        public long Generation { get; }
    }
}
