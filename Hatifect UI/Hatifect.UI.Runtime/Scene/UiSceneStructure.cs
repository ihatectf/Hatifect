using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Hatifect.UI.Runtime.Identity;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Runtime.Scene;

/// <summary>Structural lookup owned by one immutable scene; no history or global cache.</summary>
internal sealed class UiSceneStructure
{
    internal UiSceneStructure(UiSceneNode root)
    {
        var nodes = new Dictionary<UiSymbolId, UiSceneStructuralNode>();
        Visit(root, parent: null, index: 0, nodes);
        Nodes = new ReadOnlyDictionary<UiSymbolId, UiSceneStructuralNode>(nodes);
        OrderedIds = Array.AsReadOnly(nodes.Keys
            .OrderBy(id => id, UiSymbolIdOrdinalComparer.Instance).ToArray());
    }

    internal IReadOnlyDictionary<UiSymbolId, UiSceneStructuralNode> Nodes { get; }
    internal IReadOnlyList<UiSymbolId> OrderedIds { get; }

    private static void Visit(UiSceneNode node, UiSymbolId? parent, int index,
        IDictionary<UiSymbolId, UiSceneStructuralNode> nodes)
    {
        if (!nodes.TryAdd(node.Id, new UiSceneStructuralNode(node, parent, index)))
            throw new InvalidOperationException($"Scene contains duplicate node ID '{node.Id}'.");
        for (int childIndex = 0; childIndex < node.Children.Count; childIndex++)
            Visit(node.Children[childIndex], node.Id, childIndex, nodes);
    }
}

internal sealed record UiSceneStructuralNode(UiSceneNode Node, UiSymbolId? Parent, int Index);
