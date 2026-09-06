using System;
using System.Collections.Generic;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Resolution;

namespace Hatifect.UI.Runtime.Rendering;

internal abstract record UiRenderPrimitive(UiSymbolId Node, UiRect Bounds, UiRect Clip);

internal sealed record UiSurfacePrimitive(
    UiSymbolId Node,
    UiRect Bounds,
    UiRect Clip,
    UiSurface Surface,
    UiCornerRadius Radius,
    UiBorder? Border,
    UiElevation? Elevation,
    UiOpacity Opacity,
    UiTransform Transform) : UiRenderPrimitive(Node, Bounds, Clip);

internal sealed record UiTextPrimitive(
    UiSymbolId Node,
    UiRect Bounds,
    UiRect Clip,
    string Text,
    UiColor Foreground,
    UiTypography Typography,
    UiTextOverflow Overflow,
    UiOpacity Opacity,
    UiTransform Transform) : UiRenderPrimitive(Node, Bounds, Clip);

internal sealed class UiRenderFrame
{
    public UiRenderFrame(UiRenderPrimitive[] primitives)
        => Primitives = Array.AsReadOnly(primitives ?? throw new ArgumentNullException(nameof(primitives)));

    public IReadOnlyList<UiRenderPrimitive> Primitives { get; }
}

internal interface IUiRenderBackend
{
    void DrawSurface(UiSurfacePrimitive surface);
    void DrawText(UiTextPrimitive text);
}

/// <summary>Single backend boundary used by every host and component.</summary>
internal sealed class UiCompositor
{
    public void Render(UiRenderFrame frame, IUiRenderBackend backend)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(backend);
        for (int index = 0; index < frame.Primitives.Count; index++)
        {
            UiRenderPrimitive primitive = frame.Primitives[index];
            switch (primitive)
            {
                case UiSurfacePrimitive surface:
                    backend.DrawSurface(surface);
                    break;
                case UiTextPrimitive text:
                    backend.DrawText(text);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported render primitive '{primitive.GetType().Name}'.");
            }
        }
    }
}

internal sealed class UiSceneRenderPlanner
{
    private static readonly UiSurface TransparentSurface = UiSurface.Solid(new UiColor(0, 0, 0, 0));

    public UiRenderFrame Build(UiScene scene, UiLayoutSnapshot layout)
        => BuildCore(scene, layout, interaction: null, UnusedTextMetrics.Instance);

    public UiRenderFrame Build(
        UiScene scene,
        UiLayoutSnapshot layout,
        UiInteractionSnapshot interaction,
        IUiTextMetrics textMetrics)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        return BuildCore(scene, layout, interaction, textMetrics);
    }

    private static UiRenderFrame BuildCore(
        UiScene scene,
        UiLayoutSnapshot layout,
        UiInteractionSnapshot? interaction,
        IUiTextMetrics textMetrics)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(textMetrics);
        var primitives = new List<UiRenderPrimitive>();
        Visit(scene.Root, layout, interaction, textMetrics, primitives);
        return new UiRenderFrame(primitives.ToArray());
    }

    private static void Visit(
        UiSceneNode node,
        UiLayoutSnapshot layout,
        UiInteractionSnapshot? interaction,
        IUiTextMetrics textMetrics,
        ICollection<UiRenderPrimitive> primitives)
    {
        if (layout.TryGetEntry(node.Id, out UiLayoutEntry? entry) && entry != null)
        {
            UiVisualResolution visual = node is UiCollectionSceneNode currentCollection
                ? currentCollection.ContainerVisualFor(interaction) : node.Visual;
            UiRect bounds = entry.Bounds;
            UiOpacity opacity = Value(visual, "opacity", new UiOpacity(1));
            UiTransform transform = Value(visual, "transform", new UiTransform(0, 0));
            if (TrySurface(visual, out UiSurface surface))
            {
                primitives.Add(new UiSurfacePrimitive(
                    node.Id,
                    bounds,
                    entry.Clip,
                    surface,
                    Value(visual, "radius", new UiCornerRadius(0)),
                    Optional<UiBorder>(visual, "border"),
                    OptionalReference<UiElevation>(visual, "elevation"),
                    opacity,
                    transform));
            }

            string? text = UiSceneLayoutEngine.RuntimeText(node);
            if (text != null &&
                TryValue(visual, "foreground", out UiColor foreground) &&
                TryValue(visual, "typography", out UiTypography typography))
            {
                UiRect textBounds = entry.ContentBounds;
                UiRect textClip = entry.Clip;
                UiTextOverflow overflow = entry.Overflow;
                if (node is UiRouteButtonSceneNode { Icon: { } icon } route)
                {
                    float size = Math.Min(UiRouteButtonSceneNode.IconExtent, textBounds.Height);
                    var iconBounds = new UiRect(textBounds.X, textBounds.Y + (textBounds.Height - size) / 2, size, size);
                    primitives.Add(new UiSurfacePrimitive(node.Id, iconBounds, textClip,
                        UiSurface.Texture(icon, new UiColor(255, 255, 255), pixelSnap: true),
                        new UiCornerRadius(0), null, null, opacity, transform));
                    textBounds = new UiRect(textBounds.X + route.IconSpace, textBounds.Y,
                        Math.Max(0, textBounds.Width - route.IconSpace), textBounds.Height);
                }
                UiTextEditingSnapshot? editing = node is UiTextInputSceneNode &&
                    interaction?.TextEditing is { } candidate && candidate.Input == node.Id
                        ? candidate
                        : null;
                float scroll = 0;
                if (editing != null)
                {
                    textClip = UiRect.Intersect(entry.Clip, entry.ContentBounds);
                    float totalWidth = UiTextEditingGeometry.PrefixWidth(
                        text, text.Length, typography, textMetrics);
                    scroll = UiTextEditingGeometry.HorizontalScroll(
                        text,
                        editing.Caret,
                        entry.ContentBounds.Width,
                        typography,
                        textMetrics);
                    textBounds = new UiRect(
                        entry.ContentBounds.X - scroll,
                        entry.ContentBounds.Y,
                        Math.Max(entry.ContentBounds.Width, totalWidth),
                        entry.ContentBounds.Height);
                    overflow = UiTextOverflow.Scroll;
                    AddSelection(
                        node.Id,
                        entry.ContentBounds,
                        textClip,
                        text,
                        editing,
                        typography,
                        foreground,
                        scroll,
                        opacity,
                        transform,
                        textMetrics,
                        primitives);
                }
                primitives.Add(new UiTextPrimitive(
                    node.Id, textBounds, textClip, text, foreground, typography,
                    overflow, opacity, transform));
                if (editing != null)
                    AddCaret(
                        node.Id,
                        entry.ContentBounds,
                        textClip,
                        text,
                        editing,
                        typography,
                        foreground,
                        scroll,
                        opacity,
                        transform,
                        textMetrics,
                        primitives);
            }

            if (node is UiCollectionSceneNode collection &&
                layout.TryGetCollection(node.Id, out UiCollectionLayoutWindow? window) &&
                window != null)
            {
                foreach (UiVirtualizedItemLayout item in window.Items)
                {
                    if (item.Clip.Width <= 0 || item.Clip.Height <= 0) continue;
                    UiVisualResolution itemVisual = collection.VisualFor(item.Node, item.Item.Id, interaction);
                    UiOpacity itemOpacity = Value(itemVisual, "opacity", opacity);
                    UiTransform itemTransform = Value(itemVisual, "transform", transform);
                    if (TrySurface(itemVisual, out UiSurface itemSurface))
                    {
                        primitives.Add(new UiSurfacePrimitive(
                            item.Node,
                            item.Bounds,
                            item.Clip,
                            itemSurface,
                            Value(itemVisual, "radius", new UiCornerRadius(0)),
                            Optional<UiBorder>(itemVisual, "border"),
                            OptionalReference<UiElevation>(itemVisual, "elevation"),
                            itemOpacity,
                            itemTransform));
                    }
                    if (item.Item.Icon is { } icon && item.IconBounds is { } iconBounds)
                        primitives.Add(new UiSurfacePrimitive(item.Node, iconBounds, item.Clip,
                            UiSurface.Texture(icon, new UiColor(255, 255, 255), pixelSnap: true),
                            new UiCornerRadius(0), null, null, itemOpacity, itemTransform));
                    if (!TryValue(itemVisual, "foreground", out UiColor itemForeground) ||
                        !TryValue(itemVisual, "typography", out UiTypography itemTypography))
                        continue;
                    primitives.Add(new UiTextPrimitive(
                        item.Node,
                        item.LabelBounds,
                        item.Clip,
                        item.Item.Label,
                        itemForeground,
                        itemTypography,
                        UiTextOverflow.Ellipsis,
                        itemOpacity,
                        itemTransform));
                    if (item.SupportingBounds is { } supportingBounds &&
                        !string.IsNullOrWhiteSpace(item.Item.SupportingText))
                    {
                        primitives.Add(new UiTextPrimitive(
                            item.Node,
                            supportingBounds,
                            item.Clip,
                            item.Item.SupportingText!,
                            itemForeground,
                            itemTypography,
                            item.SupportingOverflow,
                            itemOpacity,
                            itemTransform));
                    }
                }
            }
        }

        foreach (UiSceneNode child in node.Children)
            Visit(child, layout, interaction, textMetrics, primitives);
    }

    private static void AddSelection(
        UiSymbolId node,
        UiRect content,
        UiRect clip,
        string text,
        UiTextEditingSnapshot editing,
        UiTypography typography,
        UiColor foreground,
        float scroll,
        UiOpacity opacity,
        UiTransform transform,
        IUiTextMetrics textMetrics,
        ICollection<UiRenderPrimitive> primitives)
    {
        if (editing.SelectionLength <= 0) return;
        float start = UiTextEditingGeometry.PrefixWidth(
            text, editing.SelectionStart, typography, textMetrics);
        float end = UiTextEditingGeometry.PrefixWidth(
            text,
            editing.SelectionStart + editing.SelectionLength,
            typography,
            textMetrics);
        primitives.Add(new UiSurfacePrimitive(
            node,
            new UiRect(content.X + start - scroll, content.Y, Math.Max(1, end - start), content.Height),
            clip,
            UiSurface.Solid(new UiColor(foreground.R, foreground.G, foreground.B, 70)),
            new UiCornerRadius(0),
            null,
            null,
            opacity,
            transform));
    }

    private static void AddCaret(
        UiSymbolId node,
        UiRect content,
        UiRect clip,
        string text,
        UiTextEditingSnapshot editing,
        UiTypography typography,
        UiColor foreground,
        float scroll,
        UiOpacity opacity,
        UiTransform transform,
        IUiTextMetrics textMetrics,
        ICollection<UiRenderPrimitive> primitives)
    {
        float x = UiTextEditingGeometry.PrefixWidth(
            text, editing.Caret, typography, textMetrics) - scroll;
        float height = Math.Min(content.Height, typography.Size * typography.LineHeight);
        float y = content.Y + Math.Max(0, (content.Height - height) / 2);
        primitives.Add(new UiSurfacePrimitive(
            node,
            new UiRect(content.X + x, y, 1, Math.Max(1, height)),
            clip,
            UiSurface.Solid(foreground),
            new UiCornerRadius(0),
            null,
            null,
            opacity,
            transform));
    }

    private static bool TrySurface(UiVisualResolution visual, out UiSurface surface)
    {
        if (TryValue(visual, "surface", out surface)) return true;
        if (TryValue<UiBorder>(visual, "border", out _))
        {
            surface = TransparentSurface;
            return true;
        }
        return false;
    }

    private static T Value<T>(UiVisualResolution visual, string property, T fallback) where T : notnull
        => TryValue(visual, property, out T value) ? value : fallback;

    private static T? Optional<T>(UiVisualResolution visual, string property) where T : struct
        => TryValue(visual, property, out T value) ? value : null;

    private static T? OptionalReference<T>(UiVisualResolution visual, string property) where T : class
        => TryValue(visual, property, out T value) ? value : null;

    private static bool TryValue<T>(UiVisualResolution visual, string property, out T value)
    {
        foreach (UiResolvedVisualProperty item in visual.Properties)
        {
            if (!string.Equals(item.Property.Name, property, StringComparison.Ordinal)) continue;
            if (item.Value is not T typed)
                throw new InvalidOperationException(
                    $"Resolved property '{property}' contains {item.Value.GetType().Name}, expected {typeof(T).Name}.");
            value = typed;
            return true;
        }
        value = default!;
        return false;
    }

    private sealed class UnusedTextMetrics : IUiTextMetrics
    {
        public static UnusedTextMetrics Instance { get; } = new();

        public UiSize Measure(
            string text,
            UiTypography typography,
            float availableWidth,
            UiTextOverflow overflow)
            => throw new InvalidOperationException(
                "Text metrics are required when rendering an active text editor.");
    }
}
