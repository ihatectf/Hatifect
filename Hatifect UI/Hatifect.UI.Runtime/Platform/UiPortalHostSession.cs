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
    bool PortalClosed = false);

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
        _close = null;
        close?.Invoke();
    }
}

/// <summary>
/// Owns one root runtime and its ordered portal stack. The platform normalizes input and supplies
/// geometry; this session owns ancestry, modality, focus isolation, dismissal, and paint order.
/// </summary>
internal sealed class UiPortalHostSession : IUiPlatformInputSession
{
    private readonly IUiPlatformBridge _platform;
    private readonly List<PortalEntry> _portals = new();
    private long _nextGeneration;

    public UiPortalHostSession(
        UiScene root,
        UiHostPlacementContext placement,
        IUiPlatformBridge platform,
        UiInteractionSnapshot? interaction = null,
        Func<UiInteractionSnapshot, UiScene>? composeInteraction = null)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        Root = new UiHostRuntimeSession(root, placement, platform, interaction, composeInteraction);
    }

    public UiHostRuntimeSession Root { get; }
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
        ArgumentNullException.ThrowIfNull(request);
        if (_portals.Any(item => item.Request.Id == request.Id))
            throw new InvalidOperationException($"Portal '{request.Id}' is already active.");
        if (!OwnerExists(request.Owner))
            throw new InvalidOperationException(
                $"Portal '{request.Id}' references missing owner node '{request.Owner.Node}'.");

        long generation = ++_nextGeneration;
        var runtime = new UiHostRuntimeSession(
            request.Scene,
            request.Placement,
            _platform,
            composeInteraction: request.ComposeInteraction);
        if (request.Scene.Root.Policy.Focus is UiFocusScopePolicy.Contained or UiFocusScopePolicy.Trapped)
        {
            UiInteractionUpdate focus = runtime.Interactions.MoveFocus(UiNavigationDirection.Next);
            Refresh(runtime, focus);
        }
        _portals.Add(new PortalEntry(request, runtime, generation));
        return new UiPortalHandle(() => Close(request.Id, generation));
    }

    public UiHostUpdate UpdateRoot(UiScene scene, UiHostPlacementContext placement)
    {
        UiHostUpdate update = Root.Update(scene, placement);
        RetireOrphans();
        return update;
    }

    public UiHostUpdate UpdatePortal(UiSymbolId id, UiScene scene, UiHostPlacementContext placement)
    {
        PortalEntry entry = Find(id);
        if (scene.Root.Policy.Kind is not (UiHostKind.Popup or UiHostKind.Context or
            UiHostKind.Sheet or UiHostKind.Overlay))
            throw new ArgumentException($"Host kind '{scene.Root.Policy.Kind}' cannot be used by a portal.", nameof(scene));
        UiHostUpdate update = entry.Runtime.Update(scene, placement);
        entry.Request = new UiPortalRequest(
            id, entry.Request.Owner, scene, placement, entry.Request.ComposeInteraction);
        RetireOrphans();
        return update;
    }

    public bool Close(UiSymbolId id)
    {
        PortalEntry? entry = _portals.FirstOrDefault(item => item.Request.Id == id);
        return entry != null && Close(id, entry.Generation);
    }

    public UiPortalDispatch PressPointer(UiPoint point)
    {
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
        => new(HasModalInputBarrier(), _portals.Count == 0 ? null : _portals[^1].Request.Id, null);

    public UiPortalDispatch Cancel()
    {
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
        => DispatchToKeyboardOwner(runtime => runtime.Interactions.MoveFocus(direction));

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
        Root.Render();
        foreach (PortalEntry portal in _portals) portal.Runtime.Render();
    }

    private bool Close(UiSymbolId id, long generation)
    {
        int root = _portals.FindIndex(item => item.Request.Id == id && item.Generation == generation);
        if (root < 0) return false;
        var retiring = new HashSet<UiSymbolId> { id };
        bool changed;
        do
        {
            changed = false;
            foreach (PortalEntry entry in _portals)
            {
                if (entry.Request.Owner.Portal is not { } owner || !retiring.Contains(owner)) continue;
                changed |= retiring.Add(entry.Request.Id);
            }
        } while (changed);
        _portals.RemoveAll(entry => retiring.Contains(entry.Request.Id));
        return true;
    }

    private UiPortalDispatch DispatchToKeyboardOwner(
        Func<UiHostRuntimeSession, UiInteractionUpdate> dispatch)
    {
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

    private static void Refresh(UiHostRuntimeSession runtime, UiInteractionUpdate update)
    {
        if (update.StateChanged || update.TextChanged || update.ActionInvoked)
            runtime.RefreshInteractionVisuals(update.TextChanged);
        if (update.TextEditingChanged && !update.TextChanged)
            runtime.RefreshTextEditingVisuals();
    }

    private void RetireOrphans()
    {
        bool changed;
        do
        {
            changed = false;
            for (int index = _portals.Count - 1; index >= 0; index--)
            {
                PortalEntry entry = _portals[index];
                if (OwnerExists(entry.Request.Owner)) continue;
                Close(entry.Request.Id, entry.Generation);
                changed = true;
            }
        } while (changed);
    }

    private bool OwnerExists(UiPortalOwner owner)
    {
        if (owner.Portal == null) return Root.ContainsNode(owner.Node);
        PortalEntry? portal = _portals.FirstOrDefault(item => item.Request.Id == owner.Portal.Value);
        return portal != null && portal.Runtime.ContainsNode(owner.Node);
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
