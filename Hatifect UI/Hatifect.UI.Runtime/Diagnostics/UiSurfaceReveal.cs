using System;
using System.Collections.Generic;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Scene;

namespace Hatifect.UI.Runtime.Diagnostics;

internal sealed record UiSurfaceRevealFailure(UiSymbolId Semantic, string Reason, int Inputs,
    long SceneVersion, long FrameVersion, UiRect? TargetBounds, UiRect? TargetClip,
    UiRootScrollLayout? RootScroll, UiPoint? LastPoint, float? LastDelta, bool? LastConsumed);

internal static class UiSurfaceReveal
{
    private const int MaxNodes = 1024;
    private const int MaxInputs = 64;

    internal static bool Reveal(UiPortalHostSession host, UiSymbolId semantic,
        Action requireOwner, Func<UiPoint, float, UiPortalScrollDispatch> scroll,
        Action<UiSurfaceRevealFailure>? reportFailure = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(requireOwner);
        ArgumentNullException.ThrowIfNull(scroll);
        if (!semantic.IsValid) throw new ArgumentException("A valid semantic ID is required.", nameof(semantic));
        RequireOwner(host, requireOwner);
        UiScene scene = host.Root.Scene;
        UiSceneNode? target = FindUnique(scene.Root, semantic);
        UiPoint? lastPoint = null;
        float? lastDelta = null;
        bool? lastConsumed = null;
        if (target is null) return Failure("target-missing", 0);
        for (int step = 0; step <= MaxInputs; step++)
        {
            RequireOwner(host, requireOwner);
            if (!ReferenceEquals(scene, host.Root.Scene))
                throw new InvalidOperationException("The reveal scene changed during input.");
            UiLayoutSnapshot layout = host.Root.Layout;
            if (!layout.TryGetEntry(target.Id, out UiLayoutEntry? entry) || entry is null)
                return Failure("layout-entry-missing", step);
            if (Contains(entry.Clip, entry.Bounds)) return true;
            if (step == MaxInputs) return Failure("input-budget-exhausted", step);
            if (layout.RootScroll is not { } root) return Failure("root-scroll-unavailable", step);
            UiRect bounds = entry.Bounds;
            UiRect viewport = root.Viewport;
            if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Height > viewport.Height
                || bounds.X < viewport.X || bounds.Right > viewport.Right)
                return Failure("target-outside-reachable-geometry", step);
            float delta = bounds.Y < viewport.Y ? MathF.Floor(bounds.Y - viewport.Y)
                : bounds.Bottom > viewport.Bottom ? MathF.Ceiling(bounds.Bottom - viewport.Bottom) : 0;
            // A nested clip cannot be repaired by changing the root offset.
            if (delta == 0) return Failure("nested-clip", step);
            if (!TryRootPoint(layout, viewport, out UiPoint point)) return Failure("root-input-point-unavailable", step);
            lastPoint = point;
            lastDelta = Math.Clamp(delta, -120, 120);
            UiPortalScrollDispatch dispatch = scroll(point, lastDelta.Value);
            lastConsumed = dispatch.Consumed;
            RequireOwner(host, requireOwner);
            if (!ReferenceEquals(scene, host.Root.Scene))
                throw new InvalidOperationException("The reveal scene changed during input.");
            if (dispatch.Portal is not null)
                throw new InvalidOperationException("Reveal input was redirected to a portal.");
            if (!dispatch.Consumed) return Failure("scroll-not-consumed", step + 1);
            if (host.Root.Layout.RootScroll?.Offset == root.Offset) return Failure("root-offset-did-not-change", step + 1);
        }
        return Failure("input-budget-exhausted", MaxInputs);

        bool Failure(string reason, int inputs)
        {
            if (reportFailure is null) return false;
            UiLayoutEntry? entry = null;
            if (target is not null) host.Root.Layout.TryGetEntry(target.Id, out entry);
            reportFailure(new(semantic, reason, inputs, host.Root.AcceptedVersion, host.Root.FrameVersion,
                entry?.Bounds, entry?.Clip, host.Root.Layout.RootScroll, lastPoint, lastDelta, lastConsumed));
            return false;
        }
    }

    private static void RequireOwner(UiPortalHostSession host, Action requireOwner)
    {
        host.Root.RequireOwner();
        requireOwner();
        if (!host.Root.IsActive) throw new ObjectDisposedException(nameof(UiSurfaceReveal));
        if (host.ActivePortalCount != 0)
            throw new InvalidOperationException("Reveal input requires a root surface without portals.");
    }

    private static UiSceneNode? FindUnique(UiSceneNode root, UiSymbolId semantic)
    {
        var pending = new Stack<UiSceneNode>();
        pending.Push(root);
        UiSceneNode? result = null;
        int visited = 0;
        while (pending.TryPop(out UiSceneNode? node))
        {
            if (++visited > MaxNodes || node.Children.Count > MaxNodes - visited - pending.Count)
                throw new InvalidOperationException("The reveal scene exceeds the bounded node limit.");
            if (node.SemanticId == semantic)
            {
                if (result is not null)
                    throw new InvalidOperationException("The semantic element has multiple reveal targets.");
                result = node;
            }
            foreach (UiSceneNode child in node.Children) pending.Push(child);
        }
        return result;
    }

    private static bool Contains(UiRect outer, UiRect inner)
        => inner.Width > 0 && inner.Height > 0 && outer.Width > 0 && outer.Height > 0
            && inner.X >= outer.X && inner.Y >= outer.Y
            && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    private static bool TryRootPoint(UiLayoutSnapshot layout, UiRect viewport, out UiPoint point)
    {
        // Probe a bounded grid of actual integer input coordinates. Never scroll an unrelated
        // collection to reach the root; an unsupported route returns false without that input.
        for (int y = 0; y < 3; y++)
        for (int x = 0; x < 3; x++)
        {
            float px = Coordinate(viewport.X, viewport.Right, x);
            float py = Coordinate(viewport.Y, viewport.Bottom, y);
            point = new(px, py);
            if (!viewport.Contains(point)) continue;
            bool blocked = false;
            foreach (UiCollectionLayoutWindow window in layout.CollectionWindows)
            {
                if (layout.TryGetEntry(window.Collection, out UiLayoutEntry? entry) && entry is not null
                    && entry.ContentBounds.Contains(point) && entry.Clip.Contains(point))
                {
                    blocked = true;
                    break;
                }
            }
            if (!blocked) return true;
        }
        // Separate collections can cover every grid probe while leaving root-input gaps.
        // Sweep their occupied vertical intervals at the same integer X coordinates.
        // The scene admission above bounds this diagnostic-only fallback to MaxNodes.
        var intervals = new List<(float Start, float End)>();
        for (int x = 0; x < 3; x++)
        {
            float px = Coordinate(viewport.X, viewport.Right, x);
            if (px < viewport.X || px >= viewport.Right) continue;
            intervals.Clear();
            foreach (UiCollectionLayoutWindow window in layout.CollectionWindows)
            {
                if (!layout.TryGetEntry(window.Collection, out UiLayoutEntry? entry) || entry is null) continue;
                UiRect occupied = UiRect.Intersect(UiRect.Intersect(entry.ContentBounds, entry.Clip), viewport);
                if (occupied.Height > 0 && px >= occupied.X && px < occupied.Right)
                    intervals.Add((occupied.Y, occupied.Bottom));
            }
            intervals.Sort(static (left, right) => left.Start.CompareTo(right.Start));
            float py = MathF.Ceiling(viewport.Y);
            foreach (var interval in intervals)
            {
                if (py < interval.Start) break;
                if (py < interval.End) py = MathF.Ceiling(interval.End);
            }
            point = new(px, py);
            if (viewport.Contains(point)) return true;
        }
        point = default;
        return false;
    }

    private static float Coordinate(float start, float end, int slot)
        => slot switch { 0 => MathF.Ceiling(start), 1 => MathF.Floor((start + end) / 2), _ => MathF.Ceiling(end) - 1 };
}
