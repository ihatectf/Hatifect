using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Projection;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Resolution;

namespace Hatifect.UI.Runtime.Layout;

internal enum UiTextOverflow
{
    Wrap,
    Clip,
    Ellipsis,
    Scroll
}

internal interface IUiTextMetrics
{
    UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow);
}

internal sealed record UiTooltipLayout(
    UiTooltipPresentation Presentation,
    UiSize DesiredSize,
    UiThickness Inset,
    UiRect Clip);

internal sealed record UiLayoutEntry(
    UiRect Bounds,
    UiRect ContentBounds,
    UiRect Clip,
    UiSize DesiredSize,
    UiTextOverflow Overflow,
    string? Heading = null,
    UiRect? HeadingBounds = null,
    float InputPromptWidth = 0,
    float ActionStatusLineHeight = 0,
    UiTooltipLayout? Tooltip = null);

// Root content remains in logical coordinates; only its viewport is visible/hit-testable.
internal sealed record UiRootScrollLayout(UiRect Viewport, float Extent, float Offset)
{
    public float MaximumOffset => MaximumFor(Viewport, Extent);

    internal static float MaximumFor(UiRect viewport, float extent)
    {
        // The maximum must reach the represented viewport edge; rounding a positive
        // extent deficit down can strand the final line below it by one float step.
        double required = Math.Max(0, (double)viewport.Y + extent - viewport.Bottom);
        float maximum = (float)required;
        if (maximum < required) maximum = MathF.BitIncrement(maximum);
        // Check the operations actually used during arrangement as well: the rounded
        // subtraction/addition can still lie above the edge after an outward cast.
        for (int correction = 0; correction < 4 && (viewport.Y - maximum) + extent > viewport.Bottom; correction++)
            maximum = MathF.BitIncrement(maximum);
        if ((viewport.Y - maximum) + extent > viewport.Bottom)
            throw new UiLayoutException("Root scroll geometry cannot represent a reachable final edge.");
        return maximum;
    }
}

internal sealed class UiLayoutSnapshot
{
    private readonly IReadOnlyDictionary<UiSymbolId, UiLayoutEntry> _entries;
    private readonly IReadOnlyDictionary<UiSymbolId, UiCollectionLayoutWindow> _collections;
    private readonly IReadOnlyList<UiCollectionLayoutWindow> _collectionWindows;

    public UiLayoutSnapshot(
        IDictionary<UiSymbolId, UiLayoutEntry> entries,
        UiHostPlacementResult hostPlacement,
        IDictionary<UiSymbolId, UiCollectionLayoutWindow>? collections = null,
        UiRootScrollLayout? rootScroll = null)
    {
        _entries = new ReadOnlyDictionary<UiSymbolId, UiLayoutEntry>(
            new Dictionary<UiSymbolId, UiLayoutEntry>(entries ?? throw new ArgumentNullException(nameof(entries))));
        HostPlacement = hostPlacement ?? throw new ArgumentNullException(nameof(hostPlacement));
        RootScroll = rootScroll;
        _collections = new ReadOnlyDictionary<UiSymbolId, UiCollectionLayoutWindow>(
            new Dictionary<UiSymbolId, UiCollectionLayoutWindow>(
                collections ?? new Dictionary<UiSymbolId, UiCollectionLayoutWindow>()));
        var collectionWindows = new UiCollectionLayoutWindow[_collections.Count];
        int index = 0;
        foreach (UiCollectionLayoutWindow window in _collections.Values)
            collectionWindows[index++] = window;
        _collectionWindows = Array.AsReadOnly(collectionWindows);
    }

    public UiHostPlacementResult HostPlacement { get; }
    public UiRootScrollLayout? RootScroll { get; }
    public IReadOnlyList<UiCollectionLayoutWindow> CollectionWindows => _collectionWindows;

    public bool TryGet(UiSymbolId node, out UiRect bounds)
    {
        if (_entries.TryGetValue(node, out UiLayoutEntry? entry))
        {
            bounds = entry.Bounds;
            return true;
        }
        bounds = default;
        return false;
    }

    public bool TryGetEntry(UiSymbolId node, out UiLayoutEntry? entry)
        => _entries.TryGetValue(node, out entry);

    public bool TryGetCollection(UiSymbolId node, out UiCollectionLayoutWindow? window)
        => _collections.TryGetValue(node, out window);
}

internal sealed class UiLayoutException : InvalidOperationException
{
    public UiLayoutException(string message) : base(message) { }
}

/// <summary>
/// Deterministic host-free measure/arrange pass. It is invoked only after a scene or a
/// measure/arrange-affecting property invalidates the previous snapshot.
/// </summary>
internal sealed class UiSceneLayoutEngine
{
    private readonly IUiTextMetrics _textMetrics;
    private readonly UiHostPlacementEngine _placement = new();
    private readonly UiCollectionVirtualizer _collections;

    public UiSceneLayoutEngine(IUiTextMetrics textMetrics)
    {
        _textMetrics = textMetrics ?? throw new ArgumentNullException(nameof(textMetrics));
        _collections = new UiCollectionVirtualizer(textMetrics);
    }

    public UiLayoutSnapshot Build(UiScene scene, UiRect viewport)
        => Build(scene, new UiHostPlacementContext(viewport));

    internal void RestoreCollectionTransitions(UiScene accepted)
        => _collections.RestoreTransitions(accepted.Root);

    public UiLayoutSnapshot Build(UiScene scene, UiHostPlacementContext context)
        => Build(scene, context, UiCollectionViewportSnapshot.Empty);

    public UiLayoutSnapshot Build(
        UiScene scene,
        UiHostPlacementContext context,
        UiCollectionViewportSnapshot collections,
        float rootOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(collections);
        if (!float.IsFinite(rootOffset)) throw new ArgumentOutOfRangeException(nameof(rootOffset));

        var measured = new Dictionary<UiSymbolId, MeasuredNode>();
        Measure(
            scene.Root,
            context.Viewport.Width,
            context.Viewport.Width,
            scene.MeasurementContext,
            measured,
            scene.DisplayName);
        MeasuredNode root = measured[scene.Root.Id];
        UiHostPlacementResult placement = _placement.Place(
            scene.Root.Policy, context, root.Desired, root.Minimum);

        UiRootScrollLayout? rootScroll = null;
        if ((scene.Root.Policy.CustomPolicy == UiProvisionalHostPolicies.OverlayCenteredId ||
             scene.Root.Policy.CustomPolicy == UiProvisionalHostPolicies.SemanticWindowId) &&
            placement.WasClamped && root.Minimum.Height > placement.Bounds.Height + .01f)
        {
            UiRect content = placement.Bounds.Inset(root.Inset);
            float height = content.Height - root.HeadingHeight;
            // Scrolling cannot repair an unusable viewport or insufficient horizontal space.
            if (height <= 0 || placement.Bounds.Width + .01f < root.Minimum.Width)
                throw new UiLayoutException($"Node '{scene.Root.Id}' has space smaller than required minimum viewport.");
            var viewport = new UiRect(content.X, content.Y + root.HeadingHeight, content.Width, height);
            float extent = root.Desired.Height - root.Inset.Top - root.Inset.Bottom - root.HeadingHeight;
            rootScroll = new(viewport, extent, Math.Clamp(rootOffset, 0, UiRootScrollLayout.MaximumFor(viewport, extent)));
        }
        var entries = new Dictionary<UiSymbolId, UiLayoutEntry>();
        var collectionWindows = new Dictionary<UiSymbolId, UiCollectionLayoutWindow>();
        Arrange(
            scene.Root, placement.Bounds, context.Viewport, measured, entries,
            collections, collectionWindows, scene.MeasurementContext, context.Viewport, rootScroll);
        _collections.Synchronize(collectionWindows.Keys);
        return new UiLayoutSnapshot(entries, placement, collectionWindows, rootScroll);
    }

    private MeasuredNode Measure(
        UiSceneNode node,
        float availableWidth,
        float overlayWidth,
        UiSceneMeasurementContext measurementContext,
        IDictionary<UiSymbolId, MeasuredNode> measured,
        string? hostTitle = null)
    {
        UiThickness inset = Insets(node.Visual);
        float contentWidth = Math.Max(0, availableWidth - inset.Left - inset.Right);
        var children = new MeasuredNode[node.Children.Count];
        for (int index = 0; index < children.Length; index++)
        {
            children[index] = Measure(
                node.Children[index],
                contentWidth,
                overlayWidth,
                measurementContext,
                measured);
        }
        UiTextOverflow overflow = Overflow(node.Kind);
        UiSize desiredContent;
        UiSize minimumContent;
        float itemExtent = 0;
        float itemLineHeight = 0;
        float preferredItemWidth = 0;
        UiTypography? collectionTypography = null;
        float inputPromptWidth = 0;
        float actionStatusLineHeight = 0;
        UiSize? tooltipSize = null;
        UiThickness tooltipInset = default;

        if (node.Tooltip is { } tooltip)
        {
            tooltipInset = Insets(tooltip.Visual);
            UiTypography typography = Required<UiTypography>(tooltip.Visual, "typography", tooltip.Id);
            float maximumWidth = Math.Min(overlayWidth, typography.Size * UiTooltipPlacement.MaximumLineCharacters);
            float textWidth = Math.Max(1, maximumWidth - tooltipInset.Left - tooltipInset.Right);
            tooltipSize = AddInsets(
                _textMetrics.Measure(tooltip.Text, typography, textWidth, UiTextOverflow.Wrap),
                tooltipInset);
        }

        string? text = RuntimeText(node);
        if (node is UiCollectionSceneNode collection)
        {
            UiTypography typography = Required<UiTypography>(node.Visual, "typography", node.Id);
            UiSize line = _textMetrics.Measure("M", typography, contentWidth, UiTextOverflow.Clip);
            itemLineHeight = Math.Max(1, line.Height);
            collectionTypography = typography;
            float desiredWidth = line.Width;
            int samples = Math.Min(collection.Count, 8);
            bool hasSupportingText = collection.MayHaveSupportingText;
            int maximumSupportingLength = 0;
            for (int index = 0; index < samples; index++)
            {
                UiSemanticCollectionItem item = collection.ItemAt(index);
                UiSize sample = _textMetrics.Measure(
                    item.Label,
                    typography,
                    contentWidth,
                    UiTextOverflow.Ellipsis);
                desiredWidth = Math.Max(desiredWidth, sample.Width);
                if (!collection.Recipe.IsNavigation && !string.IsNullOrWhiteSpace(item.SupportingText))
                {
                    maximumSupportingLength = Math.Max(maximumSupportingLength, item.SupportingText!.Length);
                }
            }
            float density = UiDensityPolicy.Factor(collection.Recipe.Density, measurementContext.Profile);
            float localeFactor = measurementContext.Locale.StartsWith("ru", StringComparison.OrdinalIgnoreCase)
                ? 1.05f
                : 1;
            int supportingLines = hasSupportingText
                ? collection.Recipe.IsAdaptive
                    ? Math.Clamp(
                        (int)MathF.Ceiling(
                            maximumSupportingLength * typography.Size * 0.55f * localeFactor /
                            Math.Max(1, contentWidth)),
                        1,
                        3)
                    : 1
                : 0;
            itemExtent = itemLineHeight +
                         supportingLines * itemLineHeight +
                         (supportingLines > 0 ? itemLineHeight * 0.2f * density : 0) +
                         itemLineHeight * 0.7f * density * localeFactor;
            preferredItemWidth = Math.Max(
                1,
                collection.Recipe.Layout == UiCollectionLayoutKind.AdaptiveGrid
                    ? Math.Max(desiredWidth, line.Height * 6)
                    : desiredWidth);
            int desiredColumns = collection.Recipe.Layout == UiCollectionLayoutKind.AdaptiveGrid
                ? Math.Min(collection.Count, collection.Recipe.PreferredColumns)
                : Math.Min(collection.Count, 1);
            int totalRows = desiredColumns == 0
                ? 0
                : (collection.Count + desiredColumns - 1) / desiredColumns;
            int desiredRows = Math.Min(totalRows, collection.Recipe.PreviewRows);
            if (collection.Count == 0 && collection.IsSelectable)
            {
                desiredColumns = 1;
                desiredRows = 1;
            }
            desiredContent = new UiSize(preferredItemWidth * desiredColumns, desiredRows * itemExtent);
            minimumContent = collection.Count == 0 && !collection.IsSelectable
                ? default
                : new UiSize(
                    Math.Max(
                        1,
                        collection.Recipe.Layout == UiCollectionLayoutKind.AdaptiveGrid
                            ? Math.Min(preferredItemWidth, contentWidth)
                            : line.Width),
                    itemExtent);
        }
        else if (text != null && !(node is UiSourceSceneNode && text.Length == 0))
        {
            UiTypography typography = Required<UiTypography>(node.Visual, "typography", node.Id);
            float iconSpace = node is UiRouteButtonSceneNode route ? route.IconSpace : 0;
            UiInputPrompt? prompt = InputPrompt(node);
            float promptSpace = 0;
            float promptHeight = 0;
            if (prompt != null)
            {
                UiTypography promptTypography = Required<UiTypography>(node.Visual, "prompt.typography", node.Id);
                UiSize promptSize = _textMetrics.Measure(
                    prompt.Label,
                    promptTypography,
                    Math.Max(1, contentWidth - iconSpace),
                    UiTextOverflow.Clip);
                float promptSpacing = Required<UiSpacing>(node.Visual, "prompt.spacing", node.Id).Value;
                inputPromptWidth = Math.Min(contentWidth, promptSize.Width);
                promptSpace = Math.Min(contentWidth, inputPromptWidth + promptSpacing);
                promptHeight = promptSize.Height;
            }
            float reservedSpace = Math.Min(contentWidth, iconSpace + promptSpace);
            UiSize desiredText = _textMetrics.Measure(
                text,
                typography,
                Math.Max(1, contentWidth - reservedSpace),
                overflow);
            UiSize minimumLine = _textMetrics.Measure("M", typography, contentWidth, UiTextOverflow.Clip);
            desiredContent = new UiSize(
                Math.Min(contentWidth, desiredText.Width + reservedSpace),
                Math.Max(Math.Max(desiredText.Height, promptHeight),
                    iconSpace > 0 ? UiRouteButtonSceneNode.IconExtent : 0));
            minimumContent = new UiSize(
                IsInteractive(node) ? Math.Min(contentWidth, Math.Max(1, minimumLine.Width + reservedSpace)) : 0,
                Math.Max(Math.Max(iconSpace > 0 ? UiRouteButtonSceneNode.IconExtent : 1, minimumLine.Height), promptHeight));
            if (node is UiButtonSceneNode { Action.Binding: not null })
            {
                // Reserve a second line before invocation so status changes retain geometry.
                actionStatusLineHeight = minimumLine.Height;
                desiredContent = new UiSize(Math.Max(desiredContent.Width, Math.Min(contentWidth, minimumLine.Width * 24)),
                    desiredContent.Height + minimumLine.Height);
                minimumContent = new UiSize(minimumContent.Width, minimumContent.Height + minimumLine.Height);
            }
        }
        else if (node is UiHostSceneNode { Policy.Kind: UiHostKind.Terminal })
        {
            (desiredContent, minimumContent) = MeasureTerminalShell(node.Children, children);
        }
        else if (children.Length > 0)
        {
            bool horizontal = node.Kind == UiSceneNodeKind.ActionBar;
            desiredContent = Aggregate(children, horizontal, minimum: false);
            minimumContent = Aggregate(children, horizontal, minimum: true);
        }
        else
        {
            desiredContent = default;
            minimumContent = default;
        }

        // Headings reserve one measured line. Ellipsis keeps that height valid when
        // host placement narrows the final content box; values retain their wrap policy.
        string? heading = node is UiHostSceneNode ? hostTitle : Heading(node);
        float headingHeight = 0;
        if (!string.IsNullOrEmpty(heading))
        {
            UiTypography typography = Required<UiTypography>(node.Visual, "typography", node.Id);
            UiSize headingSize = _textMetrics.Measure(heading, typography, contentWidth, UiTextOverflow.Ellipsis);
            headingHeight = headingSize.Height;
            desiredContent = new UiSize(Math.Max(desiredContent.Width, headingSize.Width), desiredContent.Height + headingHeight);
            minimumContent = new UiSize(minimumContent.Width, minimumContent.Height + headingHeight);
        }

        UiSize desired = AddInsets(desiredContent, inset);
        UiSize minimum = AddInsets(minimumContent, inset);
        if (IsInteractive(node))
        {
            desired = new UiSize(Math.Max(1, desired.Width), Math.Max(1, desired.Height));
            minimum = new UiSize(Math.Max(1, minimum.Width), Math.Max(1, minimum.Height));
        }
        desired = new UiSize(
            Math.Max(desired.Width, minimum.Width),
            Math.Max(desired.Height, minimum.Height));

        var result = new MeasuredNode(
            desired,
            minimum,
            inset,
            overflow,
            IsFlexible(node),
            itemExtent,
            itemLineHeight,
            preferredItemWidth,
            collectionTypography,
            heading,
            headingHeight,
            inputPromptWidth,
            actionStatusLineHeight,
            tooltipSize,
            tooltipInset);
        measured.Add(node.Id, result);
        return result;
    }

    private void Arrange(
        UiSceneNode node,
        UiRect bounds,
        UiRect parentClip,
        IReadOnlyDictionary<UiSymbolId, MeasuredNode> measured,
        IDictionary<UiSymbolId, UiLayoutEntry> entries,
        UiCollectionViewportSnapshot collectionViewport,
        IDictionary<UiSymbolId, UiCollectionLayoutWindow> collectionWindows,
        UiSceneMeasurementContext measurementContext,
        UiRect overlayClip,
        UiRootScrollLayout? rootScroll = null)
    {
        MeasuredNode own = measured[node.Id];
        if (bounds.Width + 0.01f < own.Minimum.Width ||
            (rootScroll == null && bounds.Height + 0.01f < own.Minimum.Height))
            throw new UiLayoutException(
                $"Node '{node.Id}' has space smaller than required minimum " +
                $"{own.Minimum.Width:0.##}x{own.Minimum.Height:0.##}.");
        UiRect clip = UiRect.Intersect(parentClip, bounds);
        UiRect content = bounds.Inset(own.Inset);
        if (IsInteractive(node) && (bounds.Width <= 0 || bounds.Height <= 0))
            throw new UiLayoutException($"Interactive node '{node.Id}' resolved to non-positive geometry.");

        UiRect? headingBounds = null;
        if (own.HeadingHeight > 0)
        {
            headingBounds = new UiRect(content.X, content.Y, content.Width, own.HeadingHeight);
            content = new UiRect(content.X, content.Y + own.HeadingHeight,
                content.Width, Math.Max(0, content.Height - own.HeadingHeight));
        }
        UiTooltipLayout? tooltip = node.Tooltip is { } presentation && own.TooltipSize is { } tooltipSize
            ? new UiTooltipLayout(presentation, tooltipSize, own.TooltipInset, overlayClip)
            : null;
        entries.Add(node.Id, new UiLayoutEntry(bounds, content, clip, own.Desired, own.Overflow,
            own.Heading, headingBounds, own.InputPromptWidth, own.ActionStatusLineHeight, tooltip));
        if (node is UiCollectionSceneNode collection)
        {
            collectionWindows.Add(
                node.Id,
                _collections.Materialize(
                    collection,
                    content,
                    clip,
                    own.PreferredItemWidth,
                    own.ItemExtent,
                    own.ItemLineHeight,
                    own.CollectionTypography ?? throw new UiLayoutException(
                        $"Collection '{node.Id}' has no resolved typography."),
                    measurementContext,
                    collectionViewport.RequestFor(node.Id)));
            return;
        }
        if (node.Children.Count == 0) return;
        if (rootScroll != null)
        {
            // Keep the root surface/header stationary. Children retain their full measured
            // extent, clipped below the header, even when their bounds are outside the window.
            clip = UiRect.Intersect(clip, rootScroll.Viewport);
            content = new UiRect(content.X, content.Y - rootScroll.Offset, content.Width, rootScroll.Extent);
        }

        if (node is UiHostSceneNode { Policy.Kind: UiHostKind.Terminal })
        {
            ArrangeTerminalShell(
                node.Children,
                content,
                clip,
                measured,
                entries,
                collectionViewport,
                collectionWindows,
                measurementContext,
                overlayClip);
            return;
        }

        bool horizontal = node.Kind == UiSceneNodeKind.ActionBar;
        var childMeasurements = new MeasuredNode[node.Children.Count];
        for (int index = 0; index < childMeasurements.Length; index++)
            childMeasurements[index] = measured[node.Children[index].Id];
        float available = horizontal ? content.Width : content.Height;
        float[] allocations = Allocate(childMeasurements, available, horizontal);
        float origin = horizontal ? content.X : content.Y;
        float localOffset = 0;

        for (int index = 0; index < node.Children.Count; index++)
        {
            UiSceneNode child = node.Children[index];
            // Match the local accumulation used by measurement. Adding the translated
            // origin on every step compounds rounding and can overrun the parent clip.
            float cursor = origin + localOffset;
            UiRect childBounds = horizontal
                ? new UiRect(cursor, content.Y, allocations[index], content.Height)
                : new UiRect(content.X, cursor, content.Width, allocations[index]);
            Arrange(
                child, childBounds, clip, measured, entries,
                collectionViewport, collectionWindows, measurementContext, overlayClip);
            localOffset += allocations[index];
        }
    }

    private static float[] Allocate(MeasuredNode[] children, float available, bool horizontal)
    {
        var desired = new float[children.Length];
        float minimumTotal = 0;
        float desiredTotal = 0;
        for (int index = 0; index < children.Length; index++)
        {
            MeasuredNode child = children[index];
            float desiredValue = horizontal ? child.Desired.Width : child.Desired.Height;
            float minimumValue = horizontal ? child.Minimum.Width : child.Minimum.Height;
            desired[index] = desiredValue;
            desiredTotal += desiredValue;
            minimumTotal += minimumValue;
        }
        if (minimumTotal > available + 0.01f)
            throw new UiLayoutException(
                $"Available layout space {available:0.##} is smaller than required minimum {minimumTotal:0.##}.");

        // Use the same tolerance as minimum admission: subtracting headings and
        // insets can leave a subpixel deficit even when every child is at minimum.
        if (desiredTotal > available + 0.01f)
        {
            float excess = desiredTotal - available;
            float slack = 0;
            for (int index = 0; index < children.Length; index++)
            {
                MeasuredNode child = children[index];
                float minimumValue = horizontal ? child.Minimum.Width : child.Minimum.Height;
                slack += desired[index] - minimumValue;
            }
            if (slack <= 0) throw new UiLayoutException("Layout constraints cannot be satisfied.");
            for (int index = 0; index < children.Length; index++)
            {
                MeasuredNode child = children[index];
                float minimumValue = horizontal ? child.Minimum.Width : child.Minimum.Height;
                float itemSlack = desired[index] - minimumValue;
                desired[index] -= excess * (itemSlack / slack);
            }
        }
        else if (desiredTotal < available)
        {
            int flexibleCount = 0;
            for (int index = 0; index < children.Length; index++)
            {
                if (children[index].Flexible) flexibleCount++;
            }
            if (flexibleCount > 0)
            {
                float extra = (available - desiredTotal) / flexibleCount;
                for (int index = 0; index < children.Length; index++)
                {
                    if (children[index].Flexible) desired[index] += extra;
                }
            }
        }
        return desired;
    }

    private void ArrangeTerminalShell(
        IReadOnlyList<UiSceneNode> children,
        UiRect content,
        UiRect clip,
        IReadOnlyDictionary<UiSymbolId, MeasuredNode> measured,
        IDictionary<UiSymbolId, UiLayoutEntry> entries,
        UiCollectionViewportSnapshot collectionViewport,
        IDictionary<UiSymbolId, UiCollectionLayoutWindow> collectionWindows,
        UiSceneMeasurementContext measurementContext,
        UiRect overlayClip)
    {
        IReadOnlyDictionary<UiSymbolId, UiSlotSceneNode> slots = TerminalSlots(children);
        UiSlotSceneNode navigation = slots[UiHostSlots.Navigation];
        UiSlotSceneNode utility = slots[UiHostSlots.Utility];
        UiSlotSceneNode primary = slots[UiHostSlots.Content];
        UiSlotSceneNode context = slots[UiHostSlots.Context];
        UiSlotSceneNode actions = slots[UiHostSlots.Actions];
        UiSlotSceneNode status = slots[UiHostSlots.Status];
        UiSlotSceneNode overlay = slots[UiHostSlots.Overlay];

        UiSize centerDesired = VerticalSize(
            measured[utility.Id].Desired,
            measured[primary.Id].Desired,
            measured[actions.Id].Desired);
        UiSize centerMinimum = VerticalSize(
            measured[utility.Id].Minimum,
            measured[primary.Id].Minimum,
            measured[actions.Id].Minimum);
        UiSize upperDesired = new(
            measured[navigation.Id].Desired.Width + centerDesired.Width + measured[context.Id].Desired.Width,
            Math.Max(centerDesired.Height, Math.Max(
                measured[navigation.Id].Desired.Height,
                measured[context.Id].Desired.Height)));
        UiSize upperMinimum = new(
            measured[navigation.Id].Minimum.Width + centerMinimum.Width + measured[context.Id].Minimum.Width,
            Math.Max(centerMinimum.Height, Math.Max(
                measured[navigation.Id].Minimum.Height,
                measured[context.Id].Minimum.Height)));
        float[] rows = Allocate(
            new[]
            {
                LayoutNode(upperDesired, upperMinimum, flexible: true),
                LayoutNode(measured[status.Id].Desired, measured[status.Id].Minimum, flexible: false)
            },
            content.Height,
            horizontal: false);

        float[] columns = Allocate(
            new[]
            {
                LayoutNode(measured[navigation.Id].Desired, measured[navigation.Id].Minimum, flexible: false),
                LayoutNode(centerDesired, centerMinimum, flexible: true),
                LayoutNode(measured[context.Id].Desired, measured[context.Id].Minimum, flexible: false)
            },
            content.Width,
            horizontal: true);

        var navigationBounds = new UiRect(content.X, content.Y, columns[0], rows[0]);
        var centerBounds = new UiRect(content.X + columns[0], content.Y, columns[1], rows[0]);
        var contextBounds = new UiRect(centerBounds.Right, content.Y, columns[2], rows[0]);
        var statusBounds = new UiRect(content.X, content.Y + rows[0], content.Width, rows[1]);
        Arrange(
            navigation, navigationBounds, clip, measured, entries,
            collectionViewport, collectionWindows, measurementContext, overlayClip);
        Arrange(
            context, contextBounds, clip, measured, entries,
            collectionViewport, collectionWindows, measurementContext, overlayClip);
        Arrange(
            status, statusBounds, clip, measured, entries,
            collectionViewport, collectionWindows, measurementContext, overlayClip);

        float[] centerRows = Allocate(
            new[]
            {
                LayoutNode(measured[utility.Id].Desired, measured[utility.Id].Minimum, flexible: false),
                LayoutNode(measured[primary.Id].Desired, measured[primary.Id].Minimum, flexible: true),
                LayoutNode(measured[actions.Id].Desired, measured[actions.Id].Minimum, flexible: false)
            },
            centerBounds.Height,
            horizontal: false);
        float centerY = centerBounds.Y;
        UiSlotSceneNode[] centerSlots = { utility, primary, actions };
        for (int index = 0; index < centerSlots.Length; index++)
        {
            var slotBounds = new UiRect(centerBounds.X, centerY, centerBounds.Width, centerRows[index]);
            Arrange(
                centerSlots[index], slotBounds, clip, measured, entries,
                collectionViewport, collectionWindows, measurementContext, overlayClip);
            centerY += centerRows[index];
        }

        Arrange(
            overlay, content, clip, measured, entries,
            collectionViewport, collectionWindows, measurementContext, overlayClip);
    }

    private static (UiSize Desired, UiSize Minimum) MeasureTerminalShell(
        IReadOnlyList<UiSceneNode> children,
        IReadOnlyList<MeasuredNode> measurements)
    {
        if (children.Count != measurements.Count)
            throw new UiLayoutException("Terminal shell child measurements are inconsistent.");
        TerminalSlots(children);
        var measured = new Dictionary<UiSymbolId, MeasuredNode>();
        for (int index = 0; index < children.Count; index++)
            measured.Add(((UiSlotSceneNode)children[index]).Slot, measurements[index]);

        UiSize desiredCenter = VerticalSize(
            measured[UiHostSlots.Utility].Desired,
            measured[UiHostSlots.Content].Desired,
            measured[UiHostSlots.Actions].Desired);
        UiSize minimumCenter = VerticalSize(
            measured[UiHostSlots.Utility].Minimum,
            measured[UiHostSlots.Content].Minimum,
            measured[UiHostSlots.Actions].Minimum);
        UiSize desiredUpper = new(
            measured[UiHostSlots.Navigation].Desired.Width + desiredCenter.Width +
            measured[UiHostSlots.Context].Desired.Width,
            Math.Max(desiredCenter.Height, Math.Max(
                measured[UiHostSlots.Navigation].Desired.Height,
                measured[UiHostSlots.Context].Desired.Height)));
        UiSize minimumUpper = new(
            measured[UiHostSlots.Navigation].Minimum.Width + minimumCenter.Width +
            measured[UiHostSlots.Context].Minimum.Width,
            Math.Max(minimumCenter.Height, Math.Max(
                measured[UiHostSlots.Navigation].Minimum.Height,
                measured[UiHostSlots.Context].Minimum.Height)));
        UiSize desired = VerticalSize(desiredUpper, measured[UiHostSlots.Status].Desired);
        UiSize minimum = VerticalSize(minimumUpper, measured[UiHostSlots.Status].Minimum);

        return (desired, minimum);
    }

    private static IReadOnlyDictionary<UiSymbolId, UiSlotSceneNode> TerminalSlots(
        IReadOnlyList<UiSceneNode> children)
    {
        var result = new Dictionary<UiSymbolId, UiSlotSceneNode>();
        foreach (UiSceneNode child in children)
        {
            if (child is not UiSlotSceneNode slot)
                throw new UiLayoutException("Terminal host children must be explicit host slots.");
            if (!result.TryAdd(slot.Slot, slot))
                throw new UiLayoutException($"Terminal host contains duplicate slot '{slot.Slot}'.");
        }

        foreach (UiSymbolId slot in UiHostSlots.TerminalOrder)
        {
            if (!result.ContainsKey(slot))
                throw new UiLayoutException($"Terminal host is missing required slot '{slot}'.");
        }
        if (result.Count != UiHostSlots.TerminalOrder.Count)
            throw new UiLayoutException("Terminal host contains an unknown shell slot.");
        return result;
    }

    private static UiSize VerticalSize(UiSize first, UiSize second)
        => new(
            Math.Max(first.Width, second.Width),
            first.Height + second.Height);

    private static UiSize VerticalSize(UiSize first, UiSize second, UiSize third)
        => new(
            Math.Max(first.Width, Math.Max(second.Width, third.Width)),
            first.Height + second.Height + third.Height);

    private static MeasuredNode LayoutNode(UiSize desired, UiSize minimum, bool flexible)
        => new(
            desired,
            minimum,
            new UiThickness(0),
            UiTextOverflow.Clip,
            flexible,
            0,
            0,
            0,
            null);

    private static UiSize Aggregate(MeasuredNode[] children, bool horizontal, bool minimum)
    {
        float totalWidth = 0;
        float totalHeight = 0;
        float maximumWidth = 0;
        float maximumHeight = 0;
        for (int index = 0; index < children.Length; index++)
        {
            UiSize size = minimum ? children[index].Minimum : children[index].Desired;
            totalWidth += size.Width;
            totalHeight += size.Height;
            maximumWidth = Math.Max(maximumWidth, size.Width);
            maximumHeight = Math.Max(maximumHeight, size.Height);
        }

        return horizontal
            ? new UiSize(totalWidth, maximumHeight)
            : new UiSize(maximumWidth, totalHeight);
    }

    private static UiSize AddInsets(UiSize size, UiThickness inset)
        => new(size.Width + inset.Left + inset.Right, size.Height + inset.Top + inset.Bottom);

    private static UiThickness Insets(UiVisualResolution visual)
    {
        UiSpacing padding = Optional(visual, "padding", new UiSpacing(0));
        UiBorder border = Optional(visual, "border", new UiBorder(default, 0));
        float value = padding.Value + border.Width;
        return new UiThickness(value);
    }

    private static UiTextOverflow Overflow(UiSceneNodeKind kind)
        => kind switch
        {
            UiSceneNodeKind.Button or UiSceneNodeKind.RouteButton or UiSceneNodeKind.TextInput => UiTextOverflow.Ellipsis,
            UiSceneNodeKind.Collection => UiTextOverflow.Scroll,
            UiSceneNodeKind.Text or UiSceneNodeKind.Inspector or UiSceneNodeKind.Form or UiSceneNodeKind.Status => UiTextOverflow.Wrap,
            _ => UiTextOverflow.Clip
        };

    private static bool IsInteractive(UiSceneNode node)
        => node.Kind is UiSceneNodeKind.Button or UiSceneNodeKind.RouteButton or UiSceneNodeKind.TextInput ||
           node is UiCollectionSceneNode { IsSelectable: true, Count: 0 };

    private static bool IsFlexible(UiSceneNode node)
    {
        if (node.Kind is UiSceneNodeKind.Host or UiSceneNodeKind.Slot or UiSceneNodeKind.Collection)
            return true;

        for (int index = 0; index < node.Children.Count; index++)
        {
            if (IsFlexible(node.Children[index])) return true;
        }
        return false;
    }

    internal static string? Text(UiSceneNode node)
        => node switch
        {
            UiTextSceneNode text => text.Text,
            UiButtonSceneNode button => button.Label,
            UiRouteButtonSceneNode route => route.Label,
            UiTextInputSceneNode input => input.Text,
            UiSourceSceneNode source when source.Kind is UiSceneNodeKind.Text or UiSceneNodeKind.Inspector or UiSceneNodeKind.Form or UiSceneNodeKind.Status
                => source.DisplayText,
            _ => null
        };

    internal static string? Heading(UiSceneNode node)
        => node is UiSourceSceneNode source &&
           source.Kind is UiSceneNodeKind.Text or UiSceneNodeKind.Inspector or UiSceneNodeKind.Form or UiSceneNodeKind.Status
            ? source.SemanticName : null;

    internal static string? RuntimeText(UiSceneNode node)
        => node is UiTextInputSceneNode input ? input.CurrentText : Text(node);

    internal static UiInputPrompt? InputPrompt(UiSceneNode node)
        => node switch
        {
            UiButtonSceneNode button => button.InputPrompt,
            UiRouteButtonSceneNode route => route.InputPrompt,
            _ => null
        };

    private static T Required<T>(UiVisualResolution visual, string property, UiSymbolId node) where T : notnull
        => TryValue(visual, property, out T value)
            ? value
            : throw new UiLayoutException($"Node '{node}' renders text but has no resolved '{property}' value.");

    private static T Optional<T>(UiVisualResolution visual, string property, T fallback) where T : notnull
        => TryValue(visual, property, out T value) ? value : fallback;

    private static bool TryValue<T>(UiVisualResolution visual, string property, out T value)
    {
        foreach (UiResolvedVisualProperty item in visual.Properties)
        {
            if (!string.Equals(item.Property.Name, property, StringComparison.Ordinal)) continue;
            if (item.Value is not T typed)
                throw new UiLayoutException(
                    $"Resolved property '{property}' contains {item.Value.GetType().Name}, expected {typeof(T).Name}.");
            value = typed;
            return true;
        }
        value = default!;
        return false;
    }

    private sealed record MeasuredNode(
        UiSize Desired,
        UiSize Minimum,
        UiThickness Inset,
        UiTextOverflow Overflow,
        bool Flexible,
        float ItemExtent,
        float ItemLineHeight,
        float PreferredItemWidth,
        UiTypography? CollectionTypography,
        string? Heading = null,
        float HeadingHeight = 0,
        float InputPromptWidth = 0,
        float ActionStatusLineHeight = 0,
        UiSize? TooltipSize = null,
        UiThickness TooltipInset = default);
}
