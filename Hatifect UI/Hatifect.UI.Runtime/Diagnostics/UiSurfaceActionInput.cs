using System;
using System.Collections.Generic;
using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Scene;

namespace Hatifect.UI.Runtime.Diagnostics;

/// <summary>Bounded semantic action lookup followed exclusively by ordinary navigation and submit.</summary>
internal static class UiSurfaceActionInput
{
    private const int MaxNodes = 4096;
    private const int MaxNavigationInputs = 256;

    internal static bool Activate(UiPortalHostSession host, UiSymbolId action, Action requireOwner,
        Func<UiNavigationDirection, UiPortalDispatch> navigate, Func<UiPortalDispatch> submit)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(requireOwner);
        ArgumentNullException.ThrowIfNull(navigate);
        ArgumentNullException.ThrowIfNull(submit);
        if (!action.IsValid) throw new ArgumentException("A valid action ID is required.", nameof(action));
        RequireOwner(host, requireOwner);
        UiButtonSceneNode? target = FindAction(host.Root.Scene.Root, action);
        if (target is null) return false;
        UiActionDefinition definition = target.Action;
        UiSymbolId node = target.Id;
        UiNavigationDirection direction = UiNavigationDirection.Next;
        for (int step = 0; step <= MaxNavigationInputs; step++)
        {
            RequireOwner(host, requireOwner);
            // Normal focus refresh can recompose the scene. Preserve semantic action identity,
            // not a scene object which ordinary input is allowed to replace.
            UiButtonSceneNode? current = FindAction(host.Root.Scene.Root, action);
            if (current is null || current.Id != node || !ReferenceEquals(current.Action, definition))
                throw new InvalidOperationException("The action identity changed during navigation.");
            UiSymbolId? focused = host.Root.Interactions.Snapshot.Focused;
            if (focused == node)
            {
                UiPortalDispatch dispatch = submit();
                // A successfully invoked action may legitimately close its own Window.
                return dispatch.Portal is null && dispatch.Interaction?.ActionInvoked == true;
            }
            if (step == MaxNavigationInputs) return false;
            UiPortalDispatch navigation = navigate(direction);
            RequireOwner(host, requireOwner);
            if (navigation.Portal is not null)
                throw new InvalidOperationException("Action navigation was redirected to a portal.");
            if (host.Root.Interactions.Snapshot.Focused != focused) continue;
            if (direction == UiNavigationDirection.Previous) return false;
            // Contained Window focus does not wrap: search the earlier controls at its boundary.
            direction = UiNavigationDirection.Previous;
        }
        return false;
    }

    private static void RequireOwner(UiPortalHostSession host, Action requireOwner)
    {
        host.Root.RequireOwner();
        requireOwner();
        if (!host.Root.IsActive) throw new ObjectDisposedException(nameof(UiSurfaceActionInput));
        if (host.ActivePortalCount != 0)
            throw new InvalidOperationException("Action input requires a root surface without portals.");
    }

    private static UiButtonSceneNode? FindAction(UiSceneNode root, UiSymbolId action)
    {
        var pending = new Stack<UiSceneNode>();
        pending.Push(root);
        UiButtonSceneNode? result = null;
        int visited = 0;
        while (pending.TryPop(out UiSceneNode? node))
        {
            if (++visited > MaxNodes || node.Children.Count > MaxNodes - visited - pending.Count)
                throw new InvalidOperationException("The action acceptance scene exceeds the bounded node limit.");
            if (node is UiButtonSceneNode button && button.Action.Id == action)
            {
                if (result is not null)
                    throw new InvalidOperationException("The action has multiple root input targets.");
                result = button;
            }
            foreach (UiSceneNode child in node.Children) pending.Push(child);
        }
        return result;
    }
}
