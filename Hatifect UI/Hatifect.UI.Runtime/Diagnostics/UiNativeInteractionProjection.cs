using System;
using System.Collections.Generic;
using Hatifect.UI.Runtime.Accessibility;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Scene;

namespace Hatifect.UI.Runtime.Diagnostics;

/// <summary>Detached native-input targets from the accepted Scene, Layout and Accessibility model.</summary>
internal sealed record UiNativeInteractionNode(
    string NodeId, string? SemanticId, string? ActionId, string Role,
    string? Name, string? Value, bool Enabled, bool Focused, UiRect Bounds, UiRect Clip,
    string? ParentNodeId, string? CollectionId, string? ItemId, bool Selected,
    int? PositionInSet, int? SetSize);

internal sealed record UiNativeCollectionGeometry(
    string NodeId, string? SemanticId, UiRect Bounds, UiRect Viewport, UiRect Clip,
    float Offset, float MaximumOffset, int TotalCount, bool Selectable);

internal sealed record UiNativeInteractionSnapshot(
    IReadOnlyList<UiNativeInteractionNode> Elements,
    IReadOnlyList<UiNativeCollectionGeometry> Collections,
    UiRootScrollLayout? RootScroll);

/// <summary>
/// Observation only. Does not read live sources, dispatch actions, reveal controls or mutate focus.
/// Virtual rows retain both their collection semantic owner and the existing stable item identity.
/// </summary>
internal static class UiNativeInteractionProjection
{
    internal const int MaximumNodes = 1024;
    internal const int MaximumCollections = 128;

    internal static UiNativeInteractionSnapshot Capture(
        UiScene scene, UiLayoutSnapshot layout, UiAccessibilitySnapshot accessibility)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(accessibility);
        if (scene.Experience != accessibility.Experience)
            throw new InvalidOperationException("Native observation has inconsistent experience identity.");
        Dictionary<UiSymbolId, UiSceneNode> origins = BuildSceneOrigins(scene);
        List<UiNativeInteractionNode> elements = BuildInteractionElements(accessibility, origins);
        List<UiNativeCollectionGeometry> collections = BuildCollectionGeometry(layout, origins);
        return new(elements.AsReadOnly(), collections.AsReadOnly(), layout.RootScroll);
    }

    private static Dictionary<UiSymbolId, UiSceneNode> BuildSceneOrigins(UiScene scene)
    {
        var origins = new Dictionary<UiSymbolId, UiSceneNode>();
        var pending = new Stack<UiSceneNode>();
        pending.Push(scene.Root);
        while (pending.TryPop(out var node))
        {
            if (origins.Count == MaximumNodes || !origins.TryAdd(node.Id, node))
                throw new InvalidOperationException("Native scene exceeds its unique-node budget.");
            if (node.Children.Count > MaximumNodes - origins.Count - pending.Count)
                throw new InvalidOperationException("Native scene exceeds its pending-node budget.");
            foreach (var child in node.Children) pending.Push(child);
        }
        return origins;
    }

    private static List<UiNativeInteractionNode> BuildInteractionElements(
        UiAccessibilitySnapshot accessibility,
        IReadOnlyDictionary<UiSymbolId, UiSceneNode> origins)
    {
        var elements = new List<UiNativeInteractionNode>();
        var ids = new HashSet<UiSymbolId>();
        var tree = new Stack<(UiAccessibilityNodeSnapshot Node, string? Parent, UiSymbolId? Collection, bool Selectable)>();
        tree.Push((accessibility.Root, null, null, false));
        while (tree.TryPop(out var item))
        {
            var node = item.Node;
            if (elements.Count == MaximumNodes || !ids.Add(node.Id))
                throw new InvalidOperationException("Native accessibility exceeds its unique-node budget.");
            origins.TryGetValue(node.Id, out var origin);
            bool row = node.Role == UiAccessibilityRole.ListItem;
            if (row && item.Collection is null)
                throw new InvalidOperationException("A virtualized accessibility row has no semantic collection owner.");
            if (node.Name?.Length > 4096 || node.Value?.Length > 4096)
                throw new InvalidOperationException("Native element text exceeds the observation budget.");
            UiSymbolId? semantic = row ? item.Collection : origin?.SemanticId;
            elements.Add(new(node.Id.ToString(), semantic?.ToString(),
                origin is UiButtonSceneNode button ? button.Action.Id.ToString() : null,
                node.Role.ToString(), node.Name, node.Value, node.Enabled && (!row || item.Selectable),
                node.Focused, node.Bounds, node.Clip, item.Parent,
                item.Collection?.ToString(), row ? node.Id.ToString() : null,
                node.Selected, node.PositionInSet, node.SetSize));
            if (node.Children.Count > MaximumNodes - elements.Count - tree.Count)
                throw new InvalidOperationException("Native accessibility exceeds its pending-node budget.");
            UiSymbolId? collection = node.Role == UiAccessibilityRole.List ? origin?.SemanticId : item.Collection;
            bool selectable = origin is UiCollectionSceneNode source ? source.IsSelectable : item.Selectable;
            for (int i = node.Children.Count - 1; i >= 0; i--)
                tree.Push((node.Children[i], node.Id.ToString(), collection, selectable));
        }
        return elements;
    }

    private static List<UiNativeCollectionGeometry> BuildCollectionGeometry(
        UiLayoutSnapshot layout,
        IReadOnlyDictionary<UiSymbolId, UiSceneNode> origins)
    {
        var collections = new List<UiNativeCollectionGeometry>();
        foreach (var window in layout.CollectionWindows)
        {
            if (collections.Count == MaximumCollections)
                throw new InvalidOperationException("Native collection observation exceeds its budget.");
            if (!origins.TryGetValue(window.Collection, out var origin) || origin is not UiCollectionSceneNode source
                || !layout.TryGetEntry(window.Collection, out var entry) || entry is null)
                throw new InvalidOperationException("Native collection layout has no current scene owner.");
            collections.Add(new(window.Collection.ToString(), source.SemanticId?.ToString(),
                entry.Bounds, entry.ContentBounds, entry.Clip, window.ScrollOffset,
                Math.Max(0, window.TotalExtent - entry.ContentBounds.Height), window.TotalCount, source.IsSelectable));
        }
        return collections;
    }
}
