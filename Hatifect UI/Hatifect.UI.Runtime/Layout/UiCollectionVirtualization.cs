using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Caching;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Resolution;

namespace Hatifect.UI.Runtime.Layout;

internal readonly record struct UiVirtualizedItemLayout(
    UiSymbolId Node,
    int Index,
    UiSemanticCollectionItem Item,
    UiRect Bounds,
    UiRect LabelBounds,
    UiRect? SupportingBounds,
    UiTextOverflow SupportingOverflow,
    UiRect Clip,
    UiVisualResolution Visual,
    bool Selected,
    UiRect? IconBounds = null);

internal sealed record UiCollectionScrollAnchor(UiSymbolId Item, float LocalOffset);

internal sealed class UiCollectionLayoutWindow
{
    public UiCollectionLayoutWindow(
        UiSymbolId collection,
        int totalCount,
        float itemExtent,
        int columns,
        float totalExtent,
        float scrollOffset,
        UiCollectionScrollAnchor? anchor,
        int anchorIndex,
        UiVirtualizedItemLayout[] items)
    {
        Collection = collection;
        TotalCount = totalCount;
        ItemExtent = itemExtent;
        Columns = columns;
        TotalExtent = totalExtent;
        ScrollOffset = scrollOffset;
        Anchor = anchor;
        AnchorIndex = anchorIndex;
        Items = Array.AsReadOnly(items ?? throw new ArgumentNullException(nameof(items)));
    }

    public UiSymbolId Collection { get; }
    public int TotalCount { get; }
    public float ItemExtent { get; }
    public int Columns { get; }
    public float TotalExtent { get; }
    public float ScrollOffset { get; }
    public UiCollectionScrollAnchor? Anchor { get; }
    public int AnchorIndex { get; }
    public IReadOnlyList<UiVirtualizedItemLayout> Items { get; }
}

internal sealed record UiCollectionViewportRequest(
    float RequestedOffset,
    UiCollectionScrollAnchor? Anchor,
    int AnchorIndexHint);

internal sealed class UiCollectionViewportSnapshot
{
    private readonly IReadOnlyDictionary<UiSymbolId, UiCollectionViewportRequest> _requests;

    public UiCollectionViewportSnapshot(
        IDictionary<UiSymbolId, UiCollectionViewportRequest>? requests = null)
        => _requests = new ReadOnlyDictionary<UiSymbolId, UiCollectionViewportRequest>(
            new Dictionary<UiSymbolId, UiCollectionViewportRequest>(
                requests ?? new Dictionary<UiSymbolId, UiCollectionViewportRequest>()));

    public UiCollectionViewportRequest RequestFor(UiSymbolId collection)
        => _requests.TryGetValue(collection, out UiCollectionViewportRequest? request)
            ? request
            : new UiCollectionViewportRequest(0, null, 0);

    public static UiCollectionViewportSnapshot Empty { get; } = new();
}

/// <summary>Bounded per-host scroll state retained only for active collection nodes.</summary>
internal sealed class UiCollectionViewportState
{
    private readonly Dictionary<UiSymbolId, UiCollectionViewportRequest> _requests = new();

    public UiCollectionViewportSnapshot Snapshot() => new(_requests);

    public bool SetOffset(UiSymbolId collection, float offset)
    {
        if (!float.IsFinite(offset) || offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (_requests.TryGetValue(collection, out UiCollectionViewportRequest? current) &&
            Math.Abs(current.RequestedOffset - offset) <= 0.01f && current.Anchor == null)
            return false;
        _requests[collection] = new UiCollectionViewportRequest(offset, null, 0);
        return true;
    }

    public void Reveal(UiSymbolId collection, UiSymbolId item, int index)
        => _requests[collection] = new UiCollectionViewportRequest(
            0, new UiCollectionScrollAnchor(item, 0), index);

    public void Synchronize(UiLayoutSnapshot layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var active = new HashSet<UiSymbolId>();
        foreach (UiCollectionLayoutWindow window in layout.CollectionWindows)
        {
            active.Add(window.Collection);
            if (window.TotalCount == 0 || window.Anchor == null)
                _requests.Remove(window.Collection);
            else
                _requests[window.Collection] = new UiCollectionViewportRequest(
                    window.ScrollOffset,
                    window.Anchor,
                    window.AnchorIndex);
        }
        foreach (UiSymbolId stale in _requests.Keys.Where(id => !active.Contains(id)).ToArray())
            _requests.Remove(stale);
    }
}

/// <summary>
/// Host-lifetime collection virtualizer. Adaptive exact measurements and row-height overrides are
/// both bounded; prefix lookup uses a sparse Fenwick delta index and never walks the collection.
/// </summary>
internal sealed class UiCollectionVirtualizer
{
    private const int UniformOverscanRows = 2;
    private const float AdaptiveOverscanPixels = 128;
    private const int MeasurementCacheCapacity = 1024;
    private const int ExactRowCapacity = 512;
    private readonly IUiTextMetrics _textMetrics;
    private readonly Dictionary<UiSymbolId, CollectionState> _states = new();

    public UiCollectionVirtualizer(IUiTextMetrics textMetrics)
        => _textMetrics = textMetrics ?? throw new ArgumentNullException(nameof(textMetrics));

    public UiCollectionLayoutWindow Materialize(
        UiCollectionSceneNode collection,
        UiRect viewport,
        UiRect clip,
        float preferredItemWidth,
        float itemExtent,
        float lineHeight,
        UiTypography typography,
        UiSceneMeasurementContext measurementContext,
        UiCollectionViewportRequest request)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (!float.IsFinite(itemExtent) || itemExtent <= 0)
            throw new UiLayoutException($"Collection '{collection.Id}' resolved an invalid item extent.");
        if (!float.IsFinite(preferredItemWidth) || preferredItemWidth <= 0)
            throw new UiLayoutException($"Collection '{collection.Id}' resolved an invalid preferred item width.");
        if (!float.IsFinite(lineHeight) || lineHeight <= 0)
            throw new UiLayoutException($"Collection '{collection.Id}' resolved an invalid line height.");

        int count = collection.Count;
        int columns = Columns(collection.Recipe, viewport.Width, preferredItemWidth, count);
        CollectionState state = StateFor(collection);
        request = state.ResolveTransition(collection, request);
        UiCollectionLayoutWindow window = collection.Recipe.IsAdaptive
            ? MaterializeAdaptive(
                collection,
                viewport,
                clip,
                itemExtent,
                lineHeight,
                columns,
                typography,
                measurementContext,
                request)
            : MaterializeUniform(
                collection,
                viewport,
                clip,
                itemExtent,
                lineHeight,
                columns,
                request,
                measurementContext);
        state.Remember(collection);
        return window;
    }

    public void Synchronize(IEnumerable<UiSymbolId> activeCollections)
    {
        var active = new HashSet<UiSymbolId>(activeCollections);
        foreach (UiSymbolId stale in _states.Keys.Where(id => !active.Contains(id)).ToArray())
            _states.Remove(stale);
    }

    internal void RestoreTransitions(UiSceneNode acceptedRoot)
    {
        // A rejected candidate must not become the predecessor order of the next transition.
        // Keyed measurement caches may be cold after failure; never copy their size-N row index
        // on a successful update just to retain these small logical ownership references.
        var active = new HashSet<UiSymbolId>();
        Restore(acceptedRoot);
        foreach (UiSymbolId stale in _states.Keys.Where(id => !active.Contains(id)).ToArray())
            _states.Remove(stale);

        void Restore(UiSceneNode node)
        {
            if (node is UiCollectionSceneNode collection)
            {
                active.Add(collection.Id);
                StateFor(collection).RestoreTransition(collection);
            }
            foreach (UiSceneNode child in node.Children) Restore(child);
        }
    }

    private UiCollectionLayoutWindow MaterializeUniform(
        UiCollectionSceneNode collection,
        UiRect viewport,
        UiRect clip,
        float itemExtent,
        float lineHeight,
        int columns,
        UiCollectionViewportRequest request,
        UiSceneMeasurementContext measurementContext)
    {
        int count = collection.Count;
        int totalRows = Rows(count, columns);
        float totalExtent = totalRows * itemExtent;
        float requestedOffset = request.RequestedOffset;
        int retainedIndex = -1;
        if (request.Anchor is { } stable &&
            collection.TryGetIndex(stable.Item, request.AnchorIndexHint, out int stableIndex))
        {
            requestedOffset = stableIndex / columns * itemExtent + stable.LocalOffset;
            retainedIndex = stableIndex;
        }
        float offset = ClampOffset(requestedOffset, totalExtent, viewport.Height);
        if (count == 0 || viewport.Height <= 0)
            return Empty(collection.Id, count, itemExtent, columns, totalExtent, offset);

        int firstVisibleRow = Math.Min(totalRows - 1, (int)MathF.Floor(offset / itemExtent));
        int visibleRows = Math.Max(1, (int)MathF.Ceiling(viewport.Height / itemExtent) + 1);
        int firstRow = Math.Max(0, firstVisibleRow - UniformOverscanRows);
        int endRow = Math.Min(totalRows, firstVisibleRow + visibleRows + UniformOverscanRows);
        CollectionState state = StateFor(collection);
        state.PrepareSource(collection);
        UiVirtualizedItemLayout[] items = ArrangeItems(
            collection,
            viewport,
            clip,
            columns,
            firstRow,
            endRow,
            row => row * itemExtent,
            _ => itemExtent,
            offset,
            lineHeight,
            state,
            measureExactly: false,
            measurementContext,
            typography: null);
        int anchorIndex = retainedIndex >= 0 ? retainedIndex : Math.Min(count - 1, firstVisibleRow * columns);
        UiSemanticCollectionItem anchorItem = collection.ItemAt(anchorIndex);
        return new UiCollectionLayoutWindow(
            collection.Id,
            count,
            itemExtent,
            columns,
            totalExtent,
            offset,
            retainedIndex >= 0 ? request.Anchor : new UiCollectionScrollAnchor(anchorItem.Id, offset - firstVisibleRow * itemExtent),
            anchorIndex,
            items);
    }

    private UiCollectionLayoutWindow MaterializeAdaptive(
        UiCollectionSceneNode collection,
        UiRect viewport,
        UiRect clip,
        float estimate,
        float lineHeight,
        int columns,
        UiTypography typography,
        UiSceneMeasurementContext measurementContext,
        UiCollectionViewportRequest request)
    {
        int count = collection.Count;
        int totalRows = Rows(count, columns);
        if (count == 0 || viewport.Height <= 0)
            return Empty(collection.Id, count, estimate, columns, 0, 0);

        CollectionState state = StateFor(collection);
        var themeMetrics = new ThemeMetricIdentity(typography, lineHeight);
        var scope = new HeightScope(
            totalRows,
            columns,
            estimate,
            viewport.Width / columns,
            measurementContext.Profile,
            measurementContext.Locale,
            measurementContext.Theme,
            themeMetrics,
            collection.Recipe.ItemSizing,
            collection.Recipe.Density);
        state.Prepare(collection, scope);
        AdaptiveRowHeightIndex heights = state.Heights;

        AnchorResolution anchor = ResolveAnchor(collection, heights, columns, viewport.Height, request);
        float offset = ClampOffset(
            heights.Prefix(anchor.Row) + anchor.Anchor.LocalOffset,
            heights.TotalExtent,
            viewport.Height);

        bool converged = false;
        for (int pass = 0; pass < 8; pass++)
        {
            (int firstRow, int endRow) = PixelWindow(heights, offset, viewport.Height);
            MeasureRows(
                state,
                collection,
                firstRow,
                endRow,
                columns,
                scope.ItemWidth,
                lineHeight,
                typography,
                measurementContext);
            float refinedOffset = ClampOffset(
                heights.Prefix(anchor.Row) + anchor.Anchor.LocalOffset,
                heights.TotalExtent,
                viewport.Height);
            (int refinedFirst, int refinedEnd) = PixelWindow(heights, refinedOffset, viewport.Height);
            offset = refinedOffset;
            if (firstRow == refinedFirst && endRow == refinedEnd)
            {
                converged = true;
                break;
            }
        }
        if (!converged)
            throw new UiLayoutException(
                $"Adaptive collection '{collection.Id}' did not converge after bounded height refinement.");

        (int arrangedFirst, int arrangedEnd) = PixelWindow(heights, offset, viewport.Height);
        UiVirtualizedItemLayout[] items = ArrangeItems(
            collection,
            viewport,
            clip,
            columns,
            arrangedFirst,
            arrangedEnd,
            heights.Prefix,
            heights.Height,
            offset,
            lineHeight,
            state,
            measureExactly: true,
            measurementContext,
            typography);
        return new UiCollectionLayoutWindow(
            collection.Id,
            count,
            estimate,
            columns,
            heights.TotalExtent,
            offset,
            anchor.Anchor,
            anchor.Index,
            items);
    }

    private void MeasureRows(
        CollectionState state,
        UiCollectionSceneNode collection,
        int firstRow,
        int endRow,
        int columns,
        float itemWidth,
        float lineHeight,
        UiTypography typography,
        UiSceneMeasurementContext measurementContext)
    {
        for (int row = firstRow; row < endRow; row++)
        {
            if (state.Heights.TouchExact(row)) continue;

            float exact = 0;
            int first = row * columns;
            int end = Math.Min(collection.Count, first + columns);
            for (int index = first; index < end; index++)
            {
                UiSemanticCollectionItem item = collection.ItemAt(index);
                MeasuredItem measured = MeasureItem(
                    state,
                    collection,
                    item,
                    itemWidth,
                    lineHeight,
                    typography,
                    measurementContext);
                exact = Math.Max(exact, measured.Height);
            }
            state.Heights.SetExact(row, Math.Max(1, exact));
        }
    }

    private MeasuredItem MeasureItem(
        CollectionState state,
        UiCollectionSceneNode collection,
        UiSemanticCollectionItem item,
        float itemWidth,
        float lineHeight,
        UiTypography typography,
        UiSceneMeasurementContext context)
    {
        var themeMetrics = new ThemeMetricIdentity(typography, lineHeight);
        var key = new MeasurementKey(
            item.Id,
            item.Icon != null,
            itemWidth,
            context.Profile,
            context.Locale,
            context.Theme,
            themeMetrics,
            collection.Recipe.ItemSizing,
            collection.Recipe.Density);
        if (state.Measurements.TryGetValue(key, out CachedMeasurement cached) &&
            cached.ContentVersion == item.ContentVersion &&
            string.Equals(cached.Label, item.Label, StringComparison.Ordinal) &&
            string.Equals(cached.SupportingText, item.SupportingText, StringComparison.Ordinal))
            return cached.Measurement;

        RecipeMetrics recipe = Metrics(collection.Recipe, lineHeight, context.Profile, context.Locale);
        float contentWidth = Math.Max(1, itemWidth - recipe.HorizontalPadding * 2 - (item.Icon != null ? lineHeight + 4 : 0));
        UiSize label = _textMetrics.Measure(item.Label, typography, contentWidth, UiTextOverflow.Ellipsis);
        float labelHeight = Math.Max(lineHeight, label.Height);
        float supportingHeight = 0;
        if (!collection.Recipe.IsNavigation && !string.IsNullOrWhiteSpace(item.SupportingText))
        {
            UiSize supporting = _textMetrics.Measure(
                item.SupportingText!,
                typography,
                contentWidth,
                UiTextOverflow.Wrap);
            supportingHeight = Math.Max(lineHeight, supporting.Height);
        }
        float gap = supportingHeight > 0 ? recipe.SupportingGap : 0;
        var measured = new MeasuredItem(
            recipe.VerticalPadding * 2 + labelHeight + gap + supportingHeight,
            labelHeight,
            supportingHeight,
            recipe.HorizontalPadding,
            recipe.VerticalPadding,
            gap);
        state.Measurements.Set(key, new CachedMeasurement(
            item.ContentVersion, item.Label, item.SupportingText, measured));
        return measured;
    }

    private UiVirtualizedItemLayout[] ArrangeItems(
        UiCollectionSceneNode collection,
        UiRect viewport,
        UiRect clip,
        int columns,
        int firstRow,
        int endRow,
        Func<int, float> rowTop,
        Func<int, float> rowHeight,
        float offset,
        float lineHeight,
        CollectionState state,
        bool measureExactly,
        UiSceneMeasurementContext measurementContext,
        UiTypography? typography)
    {
        int first = firstRow * columns;
        int end = Math.Min(collection.Count, endRow * columns);
        float itemWidth = viewport.Width / columns;
        var items = new UiVirtualizedItemLayout[end - first];
        HashSet<UiSymbolId> itemIds = state.MaterializedIds;
        itemIds.Clear();
        for (int index = first; index < end; index++)
        {
            UiSemanticCollectionItem item = collection.ItemAt(index);
            if (!itemIds.Add(item.Id))
                throw new UiLayoutException(
                    $"Collection '{collection.Id}' materialized duplicate item ID '{item.Id}'.");
            int row = index / columns;
            int column = index % columns;
            float height = rowHeight(row);
            var bounds = new UiRect(
                viewport.X + column * itemWidth,
                viewport.Y + rowTop(row) - offset,
                itemWidth,
                height);
            MeasuredItem content = !measureExactly
                ? EstimateUniformItem(collection, item, lineHeight, measurementContext)
                : MeasureItem(
                    state,
                    collection,
                    item,
                    itemWidth,
                    lineHeight,
                    typography!,
                    measurementContext);
            float iconSpace = item.Icon != null ? lineHeight + 4 : 0;
            UiRect? iconBounds = item.Icon != null ? new UiRect(bounds.X + content.HorizontalPadding,
                bounds.Y + content.VerticalPadding, lineHeight, lineHeight) : null;
            var labelBounds = new UiRect(
                bounds.X + content.HorizontalPadding + iconSpace,
                bounds.Y + content.VerticalPadding,
                Math.Max(0, bounds.Width - content.HorizontalPadding * 2 - iconSpace),
                Math.Min(content.LabelHeight, Math.Max(0, bounds.Height - content.VerticalPadding * 2)));
            UiRect? supportingBounds = content.SupportingHeight > 0
                ? new UiRect(
                    labelBounds.X,
                    labelBounds.Bottom + content.Gap,
                    labelBounds.Width,
                    Math.Min(
                        content.SupportingHeight,
                        Math.Max(0, bounds.Bottom - content.VerticalPadding - labelBounds.Bottom - content.Gap)))
                : null;
            UiSymbolId node = state.NodeFor(collection, item.Id);
            items[index - first] = new UiVirtualizedItemLayout(
                node,
                index,
                item,
                bounds,
                labelBounds,
                supportingBounds,
                collection.Recipe.IsAdaptive ? UiTextOverflow.Wrap : UiTextOverflow.Ellipsis,
                UiRect.Intersect(clip, bounds),
                collection.VisualFor(node, item.Id),
                collection.IsSelected(item.Id), iconBounds);
        }
        return items;
    }

    private static MeasuredItem EstimateUniformItem(
        UiCollectionSceneNode collection,
        UiSemanticCollectionItem item,
        float lineHeight,
        UiSceneMeasurementContext context)
    {
        RecipeMetrics recipe = Metrics(
            collection.Recipe,
            lineHeight,
            context.Profile,
            context.Locale);
        bool hasSupporting = !collection.Recipe.IsNavigation && !string.IsNullOrWhiteSpace(item.SupportingText);
        float support = hasSupporting ? lineHeight : 0;
        return new MeasuredItem(
            recipe.VerticalPadding * 2 + lineHeight + (hasSupporting ? recipe.SupportingGap : 0) + support,
            lineHeight,
            support,
            recipe.HorizontalPadding,
            recipe.VerticalPadding,
            hasSupporting ? recipe.SupportingGap : 0);
    }

    private AnchorResolution ResolveAnchor(
        UiCollectionSceneNode collection,
        AdaptiveRowHeightIndex heights,
        int columns,
        float viewportHeight,
        UiCollectionViewportRequest request)
    {
        if (request.Anchor is { } stable &&
            collection.TryGetIndex(stable.Item, request.AnchorIndexHint, out int stableIndex))
        {
            int stableRow = stableIndex / columns;
            return new AnchorResolution(stable, stableIndex, stableRow);
        }

        float offset = ClampOffset(request.RequestedOffset, heights.TotalExtent, viewportHeight);
        int row = heights.FindRow(offset);
        int index = Math.Min(collection.Count - 1, row * columns);
        UiSemanticCollectionItem item = collection.ItemAt(index);
        return new AnchorResolution(
            new UiCollectionScrollAnchor(item.Id, offset - heights.Prefix(row)),
            index,
            row);
    }

    private static (int First, int End) PixelWindow(
        AdaptiveRowHeightIndex heights,
        float offset,
        float viewportHeight)
    {
        float start = Math.Max(0, offset - AdaptiveOverscanPixels);
        float end = Math.Min(heights.TotalExtent, offset + viewportHeight + AdaptiveOverscanPixels);
        int firstRow = heights.FindRow(start);
        int endRow = Math.Min(heights.Count, heights.FindRow(Math.Max(start, end - 0.01f)) + 1);
        return (firstRow, Math.Max(firstRow + 1, endRow));
    }

    private CollectionState StateFor(UiCollectionSceneNode collection)
    {
        if (_states.TryGetValue(collection.Id, out CollectionState? state)) return state;
        state = new CollectionState(MeasurementCacheCapacity, ExactRowCapacity);
        _states.Add(collection.Id, state);
        return state;
    }

    private static RecipeMetrics Metrics(
        UiCollectionPresentationRecipe recipe,
        float lineHeight,
        UiSymbolId profile,
        string locale)
    {
        float density = string.Equals(recipe.Density, "Compact", StringComparison.OrdinalIgnoreCase)
            ? 0.75f
            : string.Equals(recipe.Density, "Comfortable", StringComparison.OrdinalIgnoreCase)
                ? 1.25f
                : 1;
        if (profile.LocalId.EndsWith("/Compact", StringComparison.Ordinal) ||
            profile.LocalId.EndsWith("/Controller", StringComparison.Ordinal))
            density *= 0.9f;
        float localeFactor = locale.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? 1.05f : 1;
        return new RecipeMetrics(
            lineHeight * 0.35f * density * localeFactor,
            lineHeight * 0.2f * density,
            lineHeight * 0.5f * density);
    }

    private static int Columns(
        UiCollectionPresentationRecipe recipe,
        float viewportWidth,
        float preferredItemWidth,
        int count)
    {
        if (recipe.Layout == UiCollectionLayoutKind.List || count <= 1 || viewportWidth <= 0)
            return 1;
        int resolved = Math.Max(1, (int)MathF.Floor(viewportWidth / preferredItemWidth));
        return Math.Min(count, resolved);
    }

    private static int Rows(int count, int columns)
        => count == 0 ? 0 : (count + columns - 1) / columns;

    private static float ClampOffset(float requested, float totalExtent, float viewportHeight)
        => Math.Min(Math.Max(0, totalExtent - viewportHeight), Math.Max(0, requested));

    private static UiCollectionLayoutWindow Empty(
        UiSymbolId collection,
        int count,
        float itemExtent,
        int columns,
        float totalExtent,
        float offset)
        => new(
            collection,
            count,
            itemExtent,
            columns,
            totalExtent,
            offset,
            null,
            0,
            Array.Empty<UiVirtualizedItemLayout>());

    private readonly record struct RecipeMetrics(
        float VerticalPadding,
        float SupportingGap,
        float HorizontalPadding);

    private readonly record struct ThemeMetricIdentity(UiTypography Typography, float LineHeight);

    private readonly record struct MeasurementKey(
        UiSymbolId Item,
        bool HasIcon,
        float Width,
        UiSymbolId Profile,
        string Locale,
        UiSymbolId Theme,
        ThemeMetricIdentity ThemeMetrics,
        UiSymbolId ItemSizing,
        string Density);

    private readonly record struct HeightScope(
        int Rows,
        int Columns,
        float Estimate,
        float ItemWidth,
        UiSymbolId Profile,
        string Locale,
        UiSymbolId Theme,
        ThemeMetricIdentity ThemeMetrics,
        UiSymbolId ItemSizing,
        string Density);

    private readonly record struct CachedMeasurement(
        long ContentVersion,
        string Label,
        string? SupportingText,
        MeasuredItem Measurement);

    private readonly record struct MeasuredItem(
        float Height,
        float LabelHeight,
        float SupportingHeight,
        float HorizontalPadding,
        float VerticalPadding,
        float Gap);

    private readonly record struct AnchorResolution(
        UiCollectionScrollAnchor Anchor,
        int Index,
        int Row);

    private sealed class CollectionState
    {
        private readonly int _exactRowCapacity;
        private object? _source;
        private long _sourceRevision;
        private HeightScope? _scope;
        private UiCollectionSceneNode? _previous;
        private UiCollectionViewportRequest? _removedAnchorRequest;
        private UiCollectionViewportRequest? _fallbackRequest;

        public CollectionState(int measurementCapacity, int exactRowCapacity)
        {
            Measurements = new UiBoundedCache<MeasurementKey, CachedMeasurement>(measurementCapacity);
            _exactRowCapacity = exactRowCapacity;
            Heights = new AdaptiveRowHeightIndex(0, 1, exactRowCapacity);
        }

        public UiBoundedCache<MeasurementKey, CachedMeasurement> Measurements { get; }
        public HashSet<UiSymbolId> MaterializedIds { get; } = new();
        public AdaptiveRowHeightIndex Heights { get; private set; }

        public UiCollectionViewportRequest ResolveTransition(
            UiCollectionSceneNode collection, UiCollectionViewportRequest request)
        {
            bool changed = _previous != null &&
                (!ReferenceEquals(_previous.SourceIdentity, collection.SourceIdentity) ||
                 _previous.SourceRevision != collection.SourceRevision || _previous.Count != collection.Count);
            if (!changed)
                return request == _removedAnchorRequest ? _fallbackRequest! : request;

            _removedAnchorRequest = null;
            _fallbackRequest = null;
            if (collection.Count == 0 || request.Anchor is not { } anchor ||
                collection.TryGetIndex(anchor.Item, request.AnchorIndexHint, out _))
                return request;

            // Only an immutable old capture can prove predecessor order after its owner changes.
            // Walk at most one old snapshot on a revision transition, never during steady layout.
            if (_previous is { HasCapturedItems: true } previous &&
                previous.TryGetIndex(anchor.Item, request.AnchorIndexHint, out int oldIndex))
            {
                for (int index = oldIndex - 1; index >= 0; index--)
                {
                    UiSymbolId candidate = previous.ItemAt(index).Id;
                    if (collection.TryGetIndex(candidate, -1, out int nextIndex))
                        return RememberFallback(request, candidate, nextIndex, anchor.LocalOffset);
                }
            }
            return RememberFallback(request, collection.ItemAt(0).Id, 0, 0);
        }

        public void Remember(UiCollectionSceneNode collection) => _previous = collection;

        public void RestoreTransition(UiCollectionSceneNode collection)
        {
            _previous = collection;
            _removedAnchorRequest = null;
            _fallbackRequest = null;
        }

        private UiCollectionViewportRequest RememberFallback(
            UiCollectionViewportRequest request, UiSymbolId item, int index, float localOffset)
        {
            _removedAnchorRequest = request;
            _fallbackRequest = new UiCollectionViewportRequest(
                request.RequestedOffset, new UiCollectionScrollAnchor(item, localOffset), index);
            return _fallbackRequest;
        }

        public void Prepare(UiCollectionSceneNode collection, HeightScope scope)
        {
            bool sourceChanged = PrepareSource(collection);
            if (sourceChanged || _scope != scope)
            {
                Heights = new AdaptiveRowHeightIndex(scope.Rows, scope.Estimate, _exactRowCapacity);
                _scope = scope;
            }
        }

        public bool PrepareSource(UiCollectionSceneNode collection)
        {
            bool ownerChanged = !ReferenceEquals(_source, collection.SourceIdentity);
            bool sourceChanged = ownerChanged || _sourceRevision != collection.SourceRevision;
            // Cached values validate exact text without hashing it, including external
            // sources without content versions. Each item/geometry slot replaces old text.
            // A revision still resets row heights, but unaffected measurements survive.
            if (ownerChanged) Measurements.Clear();
            _source = collection.SourceIdentity;
            _sourceRevision = collection.SourceRevision;
            return sourceChanged;
        }

        public UiSymbolId NodeFor(UiCollectionSceneNode collection, UiSymbolId item)
            => collection.ItemNodeId(item);
    }

    /// <summary>Estimated prefix sums plus bounded exact row deltas.</summary>
    private sealed class AdaptiveRowHeightIndex
    {
        private readonly float[] _tree;
        private readonly float _estimate;
        private readonly int _capacity;
        private readonly Dictionary<int, LinkedListNode<RowEntry>> _exact = new();
        private readonly LinkedList<RowEntry> _recency = new();

        public AdaptiveRowHeightIndex(int count, float estimate, int capacity)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (!float.IsFinite(estimate) || estimate <= 0) throw new ArgumentOutOfRangeException(nameof(estimate));
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            Count = count;
            _estimate = estimate;
            _capacity = capacity;
            _tree = new float[count + 1];
        }

        public int Count { get; }
        public float TotalExtent => Prefix(Count);

        public float Height(int row)
        {
            if ((uint)row >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(row));
            return _exact.TryGetValue(row, out LinkedListNode<RowEntry>? entry)
                ? entry.Value.Height
                : _estimate;
        }

        public bool TouchExact(int row)
        {
            if ((uint)row >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(row));
            if (!_exact.TryGetValue(row, out LinkedListNode<RowEntry>? entry)) return false;
            _recency.Remove(entry);
            _recency.AddLast(entry);
            return true;
        }

        public float Prefix(int endExclusive)
        {
            if ((uint)endExclusive > (uint)Count) throw new ArgumentOutOfRangeException(nameof(endExclusive));
            float delta = 0;
            for (int cursor = endExclusive; cursor > 0; cursor -= cursor & -cursor)
                delta += _tree[cursor];
            return endExclusive * _estimate + delta;
        }

        public int FindRow(float offset)
        {
            if (Count == 0) return 0;
            float clamped = Math.Min(Math.Max(0, offset), Math.Max(0, TotalExtent - 0.01f));
            int low = 0;
            int high = Count - 1;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (Prefix(middle + 1) > clamped) high = middle;
                else low = middle + 1;
            }
            return low;
        }

        public void SetExact(int row, float height)
        {
            if ((uint)row >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(row));
            if (!float.IsFinite(height) || height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            if (_exact.TryGetValue(row, out LinkedListNode<RowEntry>? current))
            {
                Update(row, height - current.Value.Height);
                current.Value = new RowEntry(row, height);
                _recency.Remove(current);
                _recency.AddLast(current);
                return;
            }
            if (_exact.Count == _capacity)
            {
                LinkedListNode<RowEntry> oldest = _recency.First
                    ?? throw new InvalidOperationException("A full exact-height index has no eviction candidate.");
                _recency.RemoveFirst();
                _exact.Remove(oldest.Value.Row);
                Update(oldest.Value.Row, _estimate - oldest.Value.Height);
            }
            var node = new LinkedListNode<RowEntry>(new RowEntry(row, height));
            _recency.AddLast(node);
            _exact.Add(row, node);
            Update(row, height - _estimate);
        }

        private void Update(int row, float delta)
        {
            for (int cursor = row + 1; cursor < _tree.Length; cursor += cursor & -cursor)
                _tree[cursor] += delta;
        }

        private readonly record struct RowEntry(int Row, float Height);
    }
}
