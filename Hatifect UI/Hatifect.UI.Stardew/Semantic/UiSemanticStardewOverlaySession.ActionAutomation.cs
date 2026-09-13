using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Input;
using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Scene;

namespace Hatifect.UI.Stardew.Semantic;

internal sealed partial class UiSemanticStardewOverlaySession
{
    internal bool ActivateForAutomatedAcceptance(UiSymbolId action)
    {
        ThrowIfDisposed();
        RequireCurrentScreen();
        if (!UiAutomatedAcceptanceScenarioRegistry.IsRegistrationEnabled)
            throw new InvalidOperationException("Action input requires the exact automated TestHarness environment.");
        var session = Host.Session;
        var root = session.Root;
        root.RequireOwner();
        if (!CanRouteInput() || session.ActivePortals.Count != 0)
            throw new InvalidOperationException("Action input requires a current root surface without portals.");
        UiButtonSceneNode? target = FindAction(root.Scene.Root, action);
        if (target is null) return false;
        UiActionDefinition definition = target.Action;
        UiSymbolId node = target.Id;
        var sample = BeginAcceptanceSample();
        try
        {
            for (int step = 0; step <= 256; step++)
            {
                if (!root.IsActive || !CanRouteInput() || session.ActivePortals.Count != 0)
                    throw new InvalidOperationException("The action input owner changed during navigation.");
                UiButtonSceneNode? current = FindAction(root.Scene.Root, action);
                if (current is null || current.Id != node || !ReferenceEquals(current.Action, definition))
                    throw new InvalidOperationException("The action identity changed during navigation.");
                if (root.Interactions.Snapshot.Focused == node)
                {
                    var dispatch = _input.KeyDown(Keys.Enter, shift: false, control: false);
                    NotifyPortalClosed(dispatch);
                    return dispatch.Portal is null && dispatch.Interaction?.ActionInvoked == true;
                }
                if (step == 256) return false;
                var navigation = _input.KeyDown(Keys.Tab, shift: false, control: false);
                NotifyPortalClosed(navigation);
            }
            return false;
        }
        finally
        {
            SyncTextInputOwnership();
            sample.Complete();
        }
    }

    private static UiButtonSceneNode? FindAction(UiSceneNode root, UiSymbolId action)
    {
        var pending = new Stack<UiSceneNode>();
        pending.Push(root);
        UiButtonSceneNode? result = null;
        int visited = 0;
        while (pending.Count > 0)
        {
            if (++visited > 4096)
                throw new InvalidOperationException("The action acceptance scene exceeds the bounded node limit.");
            UiSceneNode node = pending.Pop();
            if (node is UiButtonSceneNode button && button.Action.Id == action)
            {
                if (result is not null)
                    throw new InvalidOperationException("The action has multiple root input targets.");
                result = button;
            }
            if (pending.Count + node.Children.Count > 4096)
                throw new InvalidOperationException("The action acceptance scene exceeds the bounded node limit.");
            foreach (UiSceneNode child in node.Children) pending.Push(child);
        }
        return result;
    }
}
