using System;
using System.Collections.Generic;
using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Accessibility;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Rendering;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Runtime.Diagnostics;

internal readonly record struct UiSurfaceRenderStamp(long Sequence, long SceneVersion, long FrameVersion);

/// <summary>One exact-harness owner. No scene/source/frame references or history are retained.</summary>
internal sealed class UiSurfaceObservationState
{
    internal const int MaxRows = 256;
    internal const int MaxNodes = 1024;
    internal const int MaxPrimitives = 4096;
    internal const int MaxTextLength = 4096;
    private readonly Guid _instance = Guid.NewGuid();
    private readonly UiSymbolId _surface;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private UiSurfaceRenderStamp _rendered;
    private long _passes;
    private UiSemanticSurfaceSnapshot? _retired;

    internal UiSurfaceObservationState(UiSymbolId surface) => _surface = surface;

    // Called after the complete platform-owned pass and before any public Rendered observer.
    internal void CompleteRender(UiSurfaceRenderStamp rendered)
    {
        RequireOwner();
        if (_retired is not null || rendered.Sequence <= _rendered.Sequence) return;
        _rendered = rendered;
        _passes++;
    }

    internal UiSemanticSurfaceSnapshot Retire()
    {
        RequireOwner();
        return _retired ??= new(_instance, _surface, null, false, true, 0, null, null, _passes, null,
            Array.Empty<UiSemanticSurfaceElement>(), Array.Empty<UiSemanticSurfaceText>(), false, false);
    }

    internal UiSemanticSurfaceSnapshot Capture(UiPortalHostSession host, UiEnvironment environment, bool visible)
    {
        RequireOwner();
        if (_retired is not null) return _retired;
        ArgumentNullException.ThrowIfNull(host);
        UiHostRuntimeSession runtime = host.Root;
        runtime.RequireOwner();
        if (!runtime.IsActive) return Retire();
        int unobservedPortalCount = host.ActivePortalCount;
        bool truncated = false;
        bool unmapped = false;
        var origins = new Dictionary<UiSymbolId, (UiSymbolId? Semantic, UiSymbolId? Action)>();
        var sceneNodes = new Stack<UiSceneNode>();
        sceneNodes.Push(runtime.Scene.Root);
        int visited = 0;
        while (sceneNodes.TryPop(out UiSceneNode? node))
        {
            visited++;
            origins.Add(node.Id, (node.SemanticId, node is UiButtonSceneNode button ? button.Action.Id : null));
            int take = Math.Min(node.Children.Count, MaxNodes - visited - sceneNodes.Count);
            truncated |= take < node.Children.Count;
            for (int i = take - 1; i >= 0; i--) sceneNodes.Push(node.Children[i]);
        }

        var elements = new List<UiSemanticSurfaceElement>();
        var nodes = new Stack<(UiAccessibilityNodeSnapshot Node, UiSymbolId? Collection)>();
        nodes.Push((runtime.Accessibility.Root, null));
        visited = 0;
        while (nodes.TryPop(out var item))
        {
            visited++;
            var node = item.Node;
            origins.TryGetValue(node.Id, out var origin);
            if (node.Role == UiAccessibilityRole.ListItem && item.Collection is { } collection)
            {
                origin = (collection, null);
                origins.TryAdd(node.Id, origin);
            }
            if (node.Name is not null || node.Value is not null || origin.Action is not null)
            {
                if (elements.Count < MaxRows)
                {
                    elements.Add(new(node.Id, origin.Semantic, node.Role.ToString(),
                        Limit(node.Name, ref truncated), Limit(node.Value, ref truncated), node.Enabled, origin.Action));
                    unmapped |= origin.Semantic is null;
                }
                else truncated = true;
            }
            int take = Math.Min(node.Children.Count, MaxNodes - visited - nodes.Count);
            truncated |= take < node.Children.Count;
            UiSymbolId? itemOwner = node.Role == UiAccessibilityRole.List ? origin.Semantic : null;
            for (int i = take - 1; i >= 0; i--) nodes.Push((node.Children[i], itemOwner));
        }

        var texts = new List<UiSemanticSurfaceText>();
        var primitives = runtime.Frame.Primitives;
        int primitiveCount = Math.Min(primitives.Count, MaxPrimitives);
        truncated |= primitiveCount < primitives.Count;
        for (int i = 0; i < primitiveCount; i++)
        {
            if (primitives[i] is not UiTextPrimitive text) continue;
            if (texts.Count == MaxRows) { truncated = true; break; }
            origins.TryGetValue(text.Node, out var origin);
            texts.Add(new(text.Node, origin.Semantic, Limit(text.Text, ref truncated)!));
            unmapped |= origin.Semantic is null;
        }

        return new(_instance, _surface, runtime.Scene.Experience, visible, false, unobservedPortalCount, environment,
            new(runtime.AcceptedVersion, runtime.FrameVersion), _passes,
            _passes == 0 ? null : new(_rendered.SceneVersion, _rendered.FrameVersion),
            elements.ToArray(), texts.ToArray(), truncated, unmapped);
    }

    private static string? Limit(string? text, ref bool truncated)
    {
        if (text is null || text.Length <= MaxTextLength) return text;
        truncated = true;
        int count = char.IsHighSurrogate(text[MaxTextLength - 1]) ? MaxTextLength - 1 : MaxTextLength;
        return text[..count];
    }

    private void RequireOwner()
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Surface observation belongs to its creating UI thread.");
    }
}
