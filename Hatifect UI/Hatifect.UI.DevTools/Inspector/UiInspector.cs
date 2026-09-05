using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Language.Diagnostics;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Diagnostics;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Visual.Resolution;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Inspection;

namespace Hatifect.UI.DevTools;

internal sealed record UiInspectorSnapshot(
    UiSymbolId Experience,
    string DisplayName,
    UiSymbolId Pattern,
    UiHostKind Host,
    UiSymbolId Profile,
    UiRect HostBounds,
    UiHostPlacementKind Placement,
    bool PlacementUsedFallback,
    bool PlacementWasClamped,
    IReadOnlyList<UiInspectorPlanDecisionSnapshot> Decisions,
    IReadOnlyList<UiInspectorProjectionSnapshot> Projection,
    IReadOnlyList<UiInspectorNodeSnapshot> Nodes,
    UiInspectorInputSnapshot Input,
    UiInspectorInvalidationSnapshot Invalidation,
    UiInspectorLifecycleSnapshot Lifecycle,
    IReadOnlyList<UiDiagnostic> Diagnostics)
{
    public UiInspectorNodeSnapshot? Find(UiSymbolId id)
        => Nodes.FirstOrDefault(node => node.Id == id);
}

internal sealed record UiInspectorPlanDecisionSnapshot(
    UiPlanDecisionCode Code,
    UiSymbolId? Element,
    string Reason,
    UiSourceProvenance? Source,
    UiSourceInspectionEntry? SourceReveal);

internal sealed record UiInspectorProjectionSnapshot(
    UiSymbolId Element,
    UiSymbolId SourceRegion,
    UiSymbolId HostSlot,
    string Reason);

internal sealed record UiInspectorResolvedPropertySnapshot(
    UiPropertySymbol Property,
    string Value,
    UiVisualResolutionLayer Layer,
    UiSymbolId? State,
    UiSymbolId? Token,
    UiSourceProvenance? Source,
    UiSourceInspectionEntry? SourceReveal);

internal sealed record UiInspectorCollectionItemSnapshot(
    UiSymbolId Id,
    int Index,
    string Label,
    string? SupportingText,
    UiRect Bounds,
    UiRect Clip,
    bool Selected,
    bool Focused,
    bool Hovered,
    bool Pressed,
    bool Focusable,
    bool HitTestTarget,
    IReadOnlyList<UiSymbolId> ActiveStates,
    IReadOnlyList<UiInspectorResolvedPropertySnapshot> VisualProperties);

internal sealed record UiInspectorCollectionSnapshot(
    int TotalCount,
    float ScrollOffset,
    float TotalExtent,
    int Columns,
    UiSymbolId? Anchor,
    float? AnchorLocalOffset,
    IReadOnlyList<UiInspectorCollectionItemSnapshot> MaterializedItems);

internal sealed record UiInspectorSemanticStateSnapshot(
    string? Name,
    string? Value,
    bool? Enabled,
    int? CollectionCount,
    long? CollectionRevision,
    UiSymbolId? SelectedItem,
    UiSymbolId? Route,
    bool? IsCurrent);

internal sealed record UiInspectorNodeSnapshot(
    int Depth,
    UiSymbolId Id,
    UiSymbolId? Parent,
    string Kind,
    UiInspectorSemanticStateSnapshot Semantic,
    UiSymbolId Role,
    UiRect Bounds,
    UiRect ContentBounds,
    UiRect Clip,
    UiSize DesiredSize,
    bool Focused,
    bool Hovered,
    bool Pressed,
    bool Focusable,
    bool HitTestTarget,
    IReadOnlyList<UiSymbolId> ActiveStates,
    IReadOnlyList<UiInspectorResolvedPropertySnapshot> VisualProperties,
    UiInspectorCollectionSnapshot? Collection)
{
    public string? SemanticName => Semantic.Name;
}

internal sealed record UiInspectorTextEditingSnapshot(
    UiSymbolId Input,
    int Anchor,
    int Caret,
    int SelectionStart,
    int SelectionLength);

internal sealed record UiInspectorInputSnapshot(
    UiSymbolId? Hovered,
    UiSymbolId? Pressed,
    UiSymbolId? Focused,
    UiInspectorTextEditingSnapshot? TextEditing,
    IReadOnlyList<UiSymbolId> FocusOrder,
    IReadOnlyList<UiSymbolId> HitTestTargets);

internal sealed record UiInspectorInvalidationSnapshot(
    UiPropertyEffects Effects,
    bool LayoutChanged,
    bool FrameChanged,
    IReadOnlyList<UiSymbolId> ChangedNodes);

internal sealed record UiInspectorLifecycleSnapshot(
    string Phase,
    long LayoutBuilds,
    long FrameBuilds);

/// <summary>
/// Explicit, host-free clean-slate inspection boundary. Capture is opt-in, immutable, and copies only
/// retained scene nodes plus the already bounded materialized collection windows.
/// </summary>
internal static class UiInspector
{
    public static UiInspectorSnapshot Capture(
        UiInvocationResult invocation,
        UiRuntimeDiagnosticSnapshot runtime,
        UiSourceInspectionIndex? sources = null,
        IEnumerable<UiDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(runtime);
        if (invocation.Experience.Id != runtime.Experience || invocation.Plan.Experience != runtime.Experience)
            throw new InvalidOperationException(
                $"Inspector invocation '{invocation.Experience.Id}' does not own runtime snapshot '{runtime.Experience}'.");
        if (invocation.Plan.Host.HostKind != runtime.Host)
            throw new InvalidOperationException(
                $"Inspector plan host '{invocation.Plan.Host.HostKind}' does not match runtime host '{runtime.Host}'.");

        var sourceLookup = new SourceLookup(sources);
        return new UiInspectorSnapshot(
            runtime.Experience,
            runtime.DisplayName,
            invocation.Plan.Pattern,
            runtime.Host,
            runtime.Profile,
            runtime.HostBounds,
            runtime.Placement,
            runtime.PlacementUsedFallback,
            runtime.PlacementWasClamped,
            ReadOnly(CaptureDecisions(invocation.Plan, sourceLookup)),
            ReadOnly(invocation.Projection.Elements
                .Select(element => new UiInspectorProjectionSnapshot(
                    element.Element,
                    element.SourceRegion,
                    element.HostSlot,
                    element.Reason))
                .ToArray()),
            ReadOnly(runtime.Nodes.Select(node => CaptureNode(node, sourceLookup)).ToArray()),
            CaptureInput(runtime.Input),
            new UiInspectorInvalidationSnapshot(
                runtime.Invalidation.Effects,
                runtime.Invalidation.LayoutChanged,
                runtime.Invalidation.FrameChanged,
                ReadOnly(runtime.Invalidation.ChangedNodes.ToArray())),
            new UiInspectorLifecycleSnapshot(
                runtime.Lifecycle.Phase.ToString(),
                runtime.Lifecycle.LayoutBuilds,
                runtime.Lifecycle.FrameBuilds),
            ReadOnly(CaptureDiagnostics(diagnostics)));
    }

    private static UiInspectorPlanDecisionSnapshot[] CaptureDecisions(
        UiPresentationPlan plan,
        SourceLookup sources)
        => plan.Decisions
            .Concat(plan.Elements.SelectMany(element => element.Decisions))
            .Select(decision => new UiInspectorPlanDecisionSnapshot(
                decision.Code,
                decision.Element,
                decision.Message,
                decision.Source,
                sources.Reveal(decision.Source, property: null)))
            .ToArray();

    private static UiInspectorNodeSnapshot CaptureNode(
        UiRuntimeNodeDiagnosticSnapshot node,
        SourceLookup sources)
        => new(
            node.Depth,
            node.Id,
            node.Parent,
            node.Kind,
            new UiInspectorSemanticStateSnapshot(
                node.Semantic.Name,
                node.Semantic.Value,
                node.Semantic.Enabled,
                node.Semantic.CollectionCount,
                node.Semantic.CollectionRevision,
                node.Semantic.SelectedItem,
                node.Semantic.Route,
                node.Semantic.IsCurrent),
            node.Role,
            node.Bounds,
            node.ContentBounds,
            node.Clip,
            node.DesiredSize,
            node.Focused,
            node.Hovered,
            node.Pressed,
            node.Focusable,
            node.HitTestTarget,
            ReadOnly(node.ActiveStates.ToArray()),
            ReadOnly(CaptureVisual(node.VisualProperties, sources)),
            node.Collection is { } collection
                ? new UiInspectorCollectionSnapshot(
                    collection.TotalCount,
                    collection.ScrollOffset,
                    collection.TotalExtent,
                    collection.Columns,
                    collection.Anchor,
                    collection.AnchorLocalOffset,
                    ReadOnly(collection.MaterializedItems.Select(item => new UiInspectorCollectionItemSnapshot(
                        item.Id,
                        item.Index,
                        item.Label,
                        item.SupportingText,
                        item.Bounds,
                        item.Clip,
                        item.Selected,
                        item.Focused,
                        item.Hovered,
                        item.Pressed,
                        item.Focusable,
                        item.HitTestTarget,
                        ReadOnly(item.ActiveStates.ToArray()),
                        ReadOnly(CaptureVisual(item.VisualProperties, sources)))).ToArray()))
                : null);

    private static UiInspectorResolvedPropertySnapshot[] CaptureVisual(
        IReadOnlyList<UiRuntimeResolvedVisualDiagnosticSnapshot> properties,
        SourceLookup sources)
        => properties.Select(property => new UiInspectorResolvedPropertySnapshot(
                property.Property,
                property.Value,
                property.Layer,
                property.State,
                property.Token,
                property.Provenance,
                sources.Reveal(property.Provenance, property.Property)))
            .ToArray();

    private static UiInspectorInputSnapshot CaptureInput(UiRuntimeInputDiagnosticSnapshot input)
        => new(
            input.Hovered,
            input.Pressed,
            input.Focused,
            input.TextEditing is { } editing
                ? new UiInspectorTextEditingSnapshot(
                    editing.Input,
                    editing.Anchor,
                    editing.Caret,
                    editing.SelectionStart,
                    editing.SelectionLength)
                : null,
            ReadOnly(input.FocusOrder.ToArray()),
            ReadOnly(input.HitTestTargets.ToArray()));

    private static IReadOnlyList<T> ReadOnly<T>(T[] values)
        => Array.AsReadOnly(values);

    private static UiDiagnostic[] CaptureDiagnostics(IEnumerable<UiDiagnostic>? diagnostics)
        => (diagnostics ?? Array.Empty<UiDiagnostic>())
            .Select(diagnostic => diagnostic ??
                throw new ArgumentException("Inspector diagnostics cannot contain null.", nameof(diagnostics)))
            .OrderBy(diagnostic => diagnostic.SourceName, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Span.Start)
            .ThenBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .ToArray();

    private sealed class SourceLookup
    {
        private readonly IReadOnlyDictionary<SourceKey, UiSourceInspectionEntry[]> _entries;

        public SourceLookup(UiSourceInspectionIndex? index)
            => _entries = (index?.Entries ?? Array.Empty<UiSourceInspectionEntry>())
                .GroupBy(entry => new SourceKey(
                    entry.Source.SourceName,
                    entry.Source.Span.Start,
                    entry.Source.Span.Length))
                .ToDictionary(group => group.Key, group => group.ToArray());

        public UiSourceInspectionEntry? Reveal(
            UiSourceProvenance? source,
            UiPropertySymbol? property)
        {
            if (source == null ||
                !_entries.TryGetValue(
                    new SourceKey(source.SourceName, source.Span.Start, source.Span.Length),
                    out UiSourceInspectionEntry[]? candidates))
                return null;
            return candidates.FirstOrDefault(candidate =>
                property == null || candidate.Property?.Id == property.Id);
        }
    }

    private readonly record struct SourceKey(string SourceName, int Start, int Length);
}
