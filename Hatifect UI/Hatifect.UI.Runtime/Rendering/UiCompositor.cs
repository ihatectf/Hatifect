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
        => BuildCore(scene, layout, interaction: null, UnusedTextMetrics.Instance, null);

    public UiRenderFrame Build(
        UiScene scene,
        UiLayoutSnapshot layout,
        UiInteractionSnapshot interaction,
        IUiTextMetrics textMetrics, Hatifect.UI.Runtime.Actions.IUiActionResolver? actions = null)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        return BuildCore(scene, layout, interaction, textMetrics, actions);
    }

    private static UiRenderFrame BuildCore(
        UiScene scene,
        UiLayoutSnapshot layout,
        UiInteractionSnapshot? interaction,
        IUiTextMetrics textMetrics, Hatifect.UI.Runtime.Actions.IUiActionResolver? actions)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(textMetrics);
        var primitives = new List<UiRenderPrimitive>();
        Visit(scene.Root, layout, interaction, textMetrics, primitives, actions);
        AddTooltip(scene, layout, interaction, primitives);
        return new UiRenderFrame(primitives.ToArray());
    }

    private static void AddTooltip(
        UiScene scene,
        UiLayoutSnapshot layout,
        UiInteractionSnapshot? interaction,
        ICollection<UiRenderPrimitive> primitives)
    {
        if (interaction is null ||
            !TryTooltipTarget(scene, layout, interaction.Hovered, out UiRect anchor, out UiTooltipLayout? tooltip) &&
            !TryTooltipTarget(scene, layout, interaction.Focused, out anchor, out tooltip))
            return;
        UiVisualResolution visual = tooltip!.Presentation.Visual;
        if (!TrySurface(visual, out UiSurface surface) ||
            !TryValue(visual, "foreground", out UiColor foreground) ||
            !TryValue(visual, "typography", out UiTypography typography))
            throw new InvalidOperationException(
                $"Tooltip '{tooltip.Presentation.Id}' requires surface, foreground and typography values.");
        float gap = Math.Max(1, tooltip.Inset.Top * 2);
        UiRect bounds = UiTooltipPlacement.Place(anchor, tooltip.DesiredSize, tooltip.Clip, gap);
        UiRect content = bounds.Inset(tooltip.Inset);
        UiRect clip = UiRect.Intersect(tooltip.Clip, bounds);
        UiOpacity opacity = Value(visual, "opacity", new UiOpacity(1));
        UiTransform transform = Value(visual, "transform", new UiTransform(0, 0));
        primitives.Add(new UiSurfacePrimitive(
            tooltip.Presentation.Id,
            bounds,
            clip,
            surface,
            Value(visual, "radius", new UiCornerRadius(0)),
            Optional<UiBorder>(visual, "border"),
            OptionalReference<UiElevation>(visual, "elevation"),
            opacity,
            transform));
        primitives.Add(new UiTextPrimitive(
            tooltip.Presentation.Id,
            content,
            UiRect.Intersect(clip, content),
            tooltip.Presentation.Text,
            foreground,
            typography,
            UiTextOverflow.Wrap,
            opacity,
            transform));
    }

    private static bool TryTooltipTarget(
        UiScene scene,
        UiLayoutSnapshot layout,
        UiSymbolId? target,
        out UiRect anchor,
        out UiTooltipLayout? tooltip)
    {
        if (target is { } id &&
            scene.Structure.Nodes.TryGetValue(id, out UiSceneStructuralNode? node) &&
            node.Node.Tooltip is not null &&
            layout.TryGetEntry(id, out UiLayoutEntry? entry) &&
            entry?.Tooltip is { } nodeTooltip)
        {
            anchor = entry.Bounds;
            tooltip = nodeTooltip;
            return true;
        }
        if (target is { } item &&
            layout.TryGetCollectionItem(item, out UiVirtualizedItemLayout itemLayout) &&
            itemLayout.Tooltip is { } itemTooltip)
        {
            anchor = itemLayout.Bounds;
            tooltip = itemTooltip;
            return true;
        }
        anchor = default;
        tooltip = null;
        return false;
    }

    private static void Visit(
        UiSceneNode node,
        UiLayoutSnapshot layout,
        UiInteractionSnapshot? interaction,
        IUiTextMetrics textMetrics,
        ICollection<UiRenderPrimitive> primitives, Hatifect.UI.Runtime.Actions.IUiActionResolver? actions)
    {
        if (layout.TryGetEntry(node.Id, out UiLayoutEntry? entry) && entry != null)
        {
            UiVisualResolution visual = node switch
            {
                UiCollectionSceneNode currentCollection => currentCollection.ContainerVisualFor(interaction),
                UiButtonSceneNode button when actions is not null => button.VisualFor(actions.CanInvoke(button.Action), interaction),
                _ => node.Visual
            };
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

            if (entry.Heading is { } heading && entry.HeadingBounds is { } headingBounds &&
                TryValue(visual, "foreground", out UiColor headingForeground) &&
                TryValue(visual, "typography", out UiTypography headingTypography))
                primitives.Add(new UiTextPrimitive(node.Id, headingBounds,
                    UiRect.Intersect(entry.Clip, headingBounds), heading, headingForeground, headingTypography,
                    UiTextOverflow.Ellipsis, opacity, transform));

            string? text = UiSceneLayoutEngine.RuntimeText(node);
            if (text != null && !(node is UiSourceSceneNode && text.Length == 0) &&
                TryValue(visual, "foreground", out UiColor foreground) &&
                TryValue(visual, "typography", out UiTypography typography))
            {
                UiRect textBounds = entry.ContentBounds;
                UiRect textClip = entry.HeadingBounds is null ? entry.Clip : UiRect.Intersect(entry.Clip, textBounds);
                UiTextOverflow overflow = entry.Overflow;
                if (node is UiButtonSceneNode { Action.Binding: not null } actionButton)
                {
                    float messageLineHeight = Math.Min(
                        textBounds.Height,
                        entry.ActionStatusLineHeight);
                    float labelLineHeight = Math.Max(0, textBounds.Height - messageLineHeight);
                    var messageBounds = new UiRect(
                        textBounds.X,
                        textBounds.Y + labelLineHeight,
                        textBounds.Width,
                        messageLineHeight);
                    textBounds = new UiRect(textBounds.X, textBounds.Y, textBounds.Width, labelLineHeight);
                    textClip = UiRect.Intersect(entry.Clip, textBounds);
                    if (actions?.Status(actionButton.Action)?.Message is { Length: > 0 } message)
                        primitives.Add(new UiTextPrimitive(node.Id, messageBounds,
                            UiRect.Intersect(entry.Clip, messageBounds), message, foreground, typography,
                            UiTextOverflow.Ellipsis, opacity, transform));
                }
                if (UiSceneLayoutEngine.InputPrompt(node) is { } prompt)
                {
                    UiTypography promptTypography = Value(
                        visual,
                        "prompt.typography",
                        typography);
                    UiColor promptForeground = Value(
                        visual,
                        "prompt.foreground",
                        foreground);
                    float promptSpacing = Value(
                        visual,
                        "prompt.spacing",
                        new UiSpacing(0)).Value;
                    float promptWidth = Math.Min(textBounds.Width, entry.InputPromptWidth);
                    var promptBounds = new UiRect(
                        textBounds.X,
                        textBounds.Y,
                        promptWidth,
                        textBounds.Height);
                    primitives.Add(new UiTextPrimitive(
                        node.Id,
                        promptBounds,
                        UiRect.Intersect(textClip, promptBounds),
                        prompt.Label,
                        promptForeground,
                        promptTypography,
                        UiTextOverflow.Clip,
                        opacity,
                        transform));
                    float advance = Math.Min(textBounds.Width, promptWidth + promptSpacing);
                    textBounds = new UiRect(
                        textBounds.X + advance,
                        textBounds.Y,
                        Math.Max(0, textBounds.Width - advance),
                        textBounds.Height);
                }
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
                // The empty-field hint is presentation only. Caret, selection and scrolling
                // continue to use the actual editable text, including its zero length.
                string displayedText = node is UiTextInputSceneNode input && text.Length == 0
                    ? input.SemanticName : text;
                primitives.Add(new UiTextPrimitive(
                    node.Id, textBounds, textClip, displayedText, foreground, typography,
                    displayedText == text ? overflow : UiTextOverflow.Ellipsis, opacity, transform));
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
                    if (collection.InputPrompt is { } prompt && item.InputPromptBounds is { } promptBounds)
                    {
                        primitives.Add(new UiTextPrimitive(
                            item.Node,
                            promptBounds,
                            item.Clip,
                            prompt.Label,
                            Value(itemVisual, "prompt.foreground", itemForeground),
                            Value(itemVisual, "prompt.typography", itemTypography),
                            UiTextOverflow.Clip,
                            itemOpacity,
                            itemTransform));
                    }
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
            Visit(child, layout, interaction, textMetrics, primitives, actions);
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
