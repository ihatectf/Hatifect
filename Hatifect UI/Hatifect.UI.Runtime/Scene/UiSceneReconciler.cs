using System;
using System.Collections.Generic;
using System.Linq;
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

        UiSceneStructure oldStructure = previous.Structure;
        UiSceneStructure newStructure = next.Structure;
        IReadOnlyDictionary<UiSymbolId, UiSceneStructuralNode> oldNodes = oldStructure.Nodes;
        IReadOnlyDictionary<UiSymbolId, UiSceneStructuralNode> newNodes = newStructure.Nodes;
        if (oldNodes.Count != newNodes.Count || oldNodes.Keys.Any(id => !newNodes.ContainsKey(id)))
            return Structural(next);

        foreach ((UiSymbolId id, UiSceneStructuralNode oldNode) in oldNodes)
        {
            UiSceneStructuralNode newNode = newNodes[id];
            if (oldNode.Node.Kind != newNode.Node.Kind ||
                oldNode.Parent != newNode.Parent ||
                oldNode.Index != newNode.Index)
                return Structural(next);
        }

        UiPropertyEffects effects = UiPropertyEffects.None;
        var changed = new List<UiSymbolId>();
        foreach (UiSymbolId id in oldStructure.OrderedIds)
        {
            UiSceneStructuralNode oldNode = oldNodes[id];
            UiSceneNode newNode = newNodes[id].Node;
            UiPropertyEffects nodeEffects = newNode.Visual.InvalidationFrom(oldNode.Node.Visual);
            nodeEffects |= TooltipInvalidation(oldNode.Node.Tooltip, newNode.Tooltip);
            if (oldNode.Node is UiButtonSceneNode oldButton && newNode is UiButtonSceneNode newButton &&
                previousActions is not null && nextActions is not null)
                nodeEffects |= newButton.VisualFor(nextActions.CanInvoke(newButton.Action), interaction)
                    .InvalidationFrom(oldButton.VisualFor(previousActions.CanInvoke(oldButton.Action), interaction));
            if (!string.Equals(UiSceneLayoutEngine.Text(oldNode.Node), UiSceneLayoutEngine.Text(newNode), StringComparison.Ordinal))
                nodeEffects |= UiPropertyEffects.Measure | UiPropertyEffects.Arrange | UiPropertyEffects.Render;
            if (!string.Equals(UiSceneLayoutEngine.Heading(oldNode.Node), UiSceneLayoutEngine.Heading(newNode), StringComparison.Ordinal) ||
                (id == next.Root.Id && !string.Equals(previous.DisplayName, next.DisplayName, StringComparison.Ordinal)))
                nodeEffects |= UiPropertyEffects.Measure | UiPropertyEffects.Arrange | UiPropertyEffects.Render;
            if (UiSceneLayoutEngine.InputPrompt(oldNode.Node) != UiSceneLayoutEngine.InputPrompt(newNode))
                nodeEffects |= UiPropertyEffects.Measure | UiPropertyEffects.Arrange | UiPropertyEffects.Render;
            if (oldNode.Node is UiRouteButtonSceneNode oldRoute && newNode is UiRouteButtonSceneNode newRoute
                && oldRoute.Icon != newRoute.Icon)
                nodeEffects |= UiPropertyEffects.Measure | UiPropertyEffects.Arrange | UiPropertyEffects.Render;
            if (oldNode.Node is UiCollectionSceneNode oldCollection &&
                newNode is UiCollectionSceneNode newCollection)
            {
                nodeEffects |= CollectionInvalidation(oldCollection, newCollection, measurementContextChanged);
            }
            if (nodeEffects == UiPropertyEffects.None) continue;
            effects |= nodeEffects;
            changed.Add(id);
        }
        return new UiSceneDiff(effects, changed.AsReadOnly());
    }

    private static UiPropertyEffects CollectionInvalidation(
        UiCollectionSceneNode previous,
        UiCollectionSceneNode next,
        bool measurementContextChanged)
    {
        UiPropertyEffects effects = UiPropertyEffects.None;
        if (measurementContextChanged)
            effects |= UiPropertyEffects.Measure | UiPropertyEffects.Arrange | UiPropertyEffects.Render;
        if (!CollectionLayoutEquals(previous, next))
            return effects | UiPropertyEffects.Measure | UiPropertyEffects.Arrange | UiPropertyEffects.Render;

        effects |= next.SelectedItemVisual.InvalidationFrom(previous.SelectedItemVisual);
        effects |= TooltipVisualInvalidation(previous.ItemTooltipVisual, next.ItemTooltipVisual);
        if (previous.SelectedItemId != next.SelectedItemId || !ItemVisualsEqual(previous, next))
            effects |= UiPropertyEffects.Render;
        return effects;
    }

    private static UiPropertyEffects TooltipInvalidation(
        UiTooltipPresentation? previous,
        UiTooltipPresentation? next)
    {
        if (previous is null || next is null)
            return previous == next
                ? UiPropertyEffects.None
                : UiPropertyEffects.Measure | UiPropertyEffects.Arrange | UiPropertyEffects.Render;
        UiPropertyEffects effects = next.Visual.InvalidationFrom(previous.Visual);
        if (previous.Id != next.Id || !string.Equals(previous.Text, next.Text, StringComparison.Ordinal))
            effects |= UiPropertyEffects.Measure | UiPropertyEffects.Arrange | UiPropertyEffects.Render;
        return effects;
    }

    private static UiPropertyEffects TooltipVisualInvalidation(
        UiVisualResolution? previous,
        UiVisualResolution? next)
    {
        if (previous is null || next is null)
            return previous == next
                ? UiPropertyEffects.None
                : UiPropertyEffects.Measure | UiPropertyEffects.Arrange | UiPropertyEffects.Render;
        return next.InvalidationFrom(previous);
    }

    private static UiSceneDiff Structural(UiScene scene)
        => new(
            UiPropertyEffects.Recompose | UiPropertyEffects.Measure |
            UiPropertyEffects.Arrange | UiPropertyEffects.Render,
            scene.Structure.OrderedIds);

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

}
