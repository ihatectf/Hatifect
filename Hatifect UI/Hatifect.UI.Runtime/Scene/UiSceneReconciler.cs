using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Runtime.Identity;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Visual.Resolution;
using Hatifect.UI.Semantics;
using Hatifect.UI.Runtime.Actions;
using Hatifect.UI.Runtime.Input;

namespace Hatifect.UI.Runtime.Scene;

internal sealed record UiSceneDiff(
    UiPropertyEffects Effects,
    IReadOnlyList<UiSymbolId> ChangedNodes)
{
    public bool RequiresRecompose => Effects.HasFlag(UiPropertyEffects.Recompose);
    public bool RequiresLayout => RequiresRecompose ||
                                  Effects.HasFlag(UiPropertyEffects.Measure) ||
                                  Effects.HasFlag(UiPropertyEffects.Arrange);
    public bool RequiresRender => RequiresLayout || Effects.HasFlag(UiPropertyEffects.Render);
}

/// <summary>Compares immutable scene snapshots by stable semantic node identity.</summary>
internal sealed class UiSceneReconciler
{
    public UiSceneDiff Compare(UiScene previous, UiScene next,
        UiInteractionSnapshot? interaction = null, IUiActionResolver? previousActions = null,
        IUiActionResolver? nextActions = null)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        if (previous.Experience != next.Experience)
            return Structural(next);

        bool measurementContextChanged = previous.MeasurementContext != next.MeasurementContext;

        Dictionary<UiSymbolId, StructuralNode> oldNodes = Flatten(previous);
        Dictionary<UiSymbolId, StructuralNode> newNodes = Flatten(next);
        if (oldNodes.Count != newNodes.Count || oldNodes.Keys.Any(id => !newNodes.ContainsKey(id)))
            return Structural(next);

        foreach ((UiSymbolId id, StructuralNode oldNode) in oldNodes)
        {
            StructuralNode newNode = newNodes[id];
            if (oldNode.Node.Kind != newNode.Node.Kind ||
                oldNode.Parent != newNode.Parent ||
                oldNode.Index != newNode.Index)
                return Structural(next);
        }

        UiPropertyEffects effects = UiPropertyEffects.None;
        var changed = new List<UiSymbolId>();
        foreach ((UiSymbolId id, StructuralNode oldNode) in oldNodes.OrderBy(
                     item => item.Key,
                     UiSymbolIdOrdinalComparer.Instance))
        {
            UiSceneNode newNode = newNodes[id].Node;
            UiPropertyEffects nodeEffects = newNode.Visual.InvalidationFrom(oldNode.Node.Visual);
            if (oldNode.Node is UiButtonSceneNode oldButton && newNode is UiButtonSceneNode newButton &&
                previousActions is not null && nextActions is not null)
                nodeEffects |= newButton.VisualFor(nextActions.CanInvoke(newButton.Action), interaction)
                    .InvalidationFrom(oldButton.VisualFor(previousActions.CanInvoke(oldButton.Action), interaction));
            if (!string.Equals(UiSceneLayoutEngine.Text(oldNode.Node), UiSceneLayoutEngine.Text(newNode), StringComparison.Ordinal))
                nodeEffects |= UiPropertyEffects.Measure | UiPropertyEffects.Arrange | UiPropertyEffects.Render;
            if (oldNode.Node is UiRouteButtonSceneNode oldRoute && newNode is UiRouteButtonSceneNode newRoute
                && oldRoute.Icon != newRoute.Icon)
                nodeEffects |= UiPropertyEffects.Measure | UiPropertyEffects.Arrange | UiPropertyEffects.Render;
            if (oldNode.Node is UiCollectionSceneNode oldCollection &&
                newNode is UiCollectionSceneNode newCollection)
            {
                if (measurementContextChanged)
                    nodeEffects |= UiPropertyEffects.Measure | UiPropertyEffects.Arrange | UiPropertyEffects.Render;
                if (!CollectionLayoutEquals(oldCollection, newCollection))
                    nodeEffects |= UiPropertyEffects.Measure | UiPropertyEffects.Arrange | UiPropertyEffects.Render;
                else
                {
                    nodeEffects |= newCollection.SelectedItemVisual.InvalidationFrom(oldCollection.SelectedItemVisual);
                    if (oldCollection.SelectedItemId != newCollection.SelectedItemId ||
                        !ItemVisualsEqual(oldCollection, newCollection))
                        nodeEffects |= UiPropertyEffects.Render;
                }
            }
            if (nodeEffects == UiPropertyEffects.None) continue;
            effects |= nodeEffects;
            changed.Add(id);
        }
        return new UiSceneDiff(effects, changed.AsReadOnly());
    }

    private static UiSceneDiff Structural(UiScene scene)
        => new(
            UiPropertyEffects.Recompose | UiPropertyEffects.Measure |
            UiPropertyEffects.Arrange | UiPropertyEffects.Render,
            Flatten(scene).Keys.OrderBy(id => id, UiSymbolIdOrdinalComparer.Instance).ToArray());

    private static Dictionary<UiSymbolId, StructuralNode> Flatten(UiScene scene)
    {
        var result = new Dictionary<UiSymbolId, StructuralNode>();
        Visit(scene.Root, parent: null, index: 0, result);
        return result;
    }

    private static bool CollectionLayoutEquals(UiCollectionSceneNode previous, UiCollectionSceneNode next)
    {
        if (previous.Recipe != next.Recipe) return false;
        return ReferenceEquals(previous.SourceIdentity, next.SourceIdentity) &&
               previous.SourceRevision == next.SourceRevision &&
               previous.Count == next.Count;
    }

    private static bool ItemVisualsEqual(UiCollectionSceneNode previous, UiCollectionSceneNode next)
    {
        if (previous.ActiveItemVisuals.Count != next.ActiveItemVisuals.Count) return false;
        foreach ((UiSymbolId node, UiVisualResolution visual) in previous.ActiveItemVisuals)
        {
            if (!next.ActiveItemVisuals.TryGetValue(node, out UiVisualResolution? nextVisual) ||
                nextVisual.InvalidationFrom(visual) != UiPropertyEffects.None)
                return false;
        }
        return true;
    }

    private static void Visit(
        UiSceneNode node,
        UiSymbolId? parent,
        int index,
        IDictionary<UiSymbolId, StructuralNode> result)
    {
        if (!result.TryAdd(node.Id, new StructuralNode(node, parent, index)))
            throw new InvalidOperationException($"Scene contains duplicate node ID '{node.Id}'.");
        for (int childIndex = 0; childIndex < node.Children.Count; childIndex++)
            Visit(node.Children[childIndex], node.Id, childIndex, result);
    }

    private sealed record StructuralNode(UiSceneNode Node, UiSymbolId? Parent, int Index);
}
