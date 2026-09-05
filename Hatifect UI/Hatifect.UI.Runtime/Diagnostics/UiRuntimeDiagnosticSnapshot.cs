using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Hatifect.UI.Language.Diagnostics;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual.Resolution;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Runtime.Diagnostics;

internal enum UiRuntimeLifecyclePhase
{
    Initialized,
    Updated
}

internal sealed record UiRuntimeLifecycleDiagnosticSnapshot(
    UiRuntimeLifecyclePhase Phase,
    long LayoutBuilds,
    long FrameBuilds);

internal sealed record UiRuntimeTextEditingDiagnosticSnapshot(
    UiSymbolId Input,
    int Anchor,
    int Caret,
    int SelectionStart,
    int SelectionLength);

internal sealed record UiRuntimeInputDiagnosticSnapshot(
    UiSymbolId? Hovered,
    UiSymbolId? Pressed,
    UiSymbolId? Focused,
    UiRuntimeTextEditingDiagnosticSnapshot? TextEditing,
    IReadOnlyList<UiSymbolId> FocusOrder,
    IReadOnlyList<UiSymbolId> HitTestTargets);

internal sealed record UiRuntimeSemanticDiagnosticSnapshot(
    string? Name,
    string? Value,
    bool? Enabled,
    int? CollectionCount,
    long? CollectionRevision,
    UiSymbolId? SelectedItem,
    UiSymbolId? Route,
    bool? IsCurrent);

internal sealed record UiRuntimeResolvedVisualDiagnosticSnapshot(
    UiPropertySymbol Property,
    string Value,
    UiVisualResolutionLayer Layer,
    UiSymbolId? State,
    UiSymbolId? Token,
    UiSourceProvenance? Provenance);

internal sealed record UiRuntimeCollectionItemDiagnosticSnapshot(
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
    IReadOnlyList<UiRuntimeResolvedVisualDiagnosticSnapshot> VisualProperties);

internal sealed record UiRuntimeCollectionDiagnosticSnapshot(
    int TotalCount,
    float ScrollOffset,
    float TotalExtent,
    int Columns,
    UiSymbolId? Anchor,
    float? AnchorLocalOffset,
    IReadOnlyList<UiRuntimeCollectionItemDiagnosticSnapshot> MaterializedItems);

internal sealed record UiRuntimeNodeDiagnosticSnapshot(
    int Depth,
    UiSymbolId Id,
    UiSymbolId? Parent,
    string Kind,
    UiRuntimeSemanticDiagnosticSnapshot Semantic,
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
    IReadOnlyList<UiRuntimeResolvedVisualDiagnosticSnapshot> VisualProperties,
    UiRuntimeCollectionDiagnosticSnapshot? Collection);

internal sealed record UiRuntimeInvalidationDiagnosticSnapshot(
    UiPropertyEffects Effects,
    bool LayoutChanged,
    bool FrameChanged,
    IReadOnlyList<UiSymbolId> ChangedNodes);

/// <summary>
/// Immutable, bounded copy of host-owned Runtime state. DevTools may consume this friend-only
/// snapshot, but never receives mutable Scene, layout, input, renderer, or platform objects.
/// </summary>
internal sealed record UiRuntimeDiagnosticSnapshot(
    UiSymbolId Experience,
    string DisplayName,
    UiHostKind Host,
    UiSymbolId Profile,
    UiRect HostBounds,
    UiHostPlacementKind Placement,
    bool PlacementUsedFallback,
    bool PlacementWasClamped,
    IReadOnlyList<UiRuntimeNodeDiagnosticSnapshot> Nodes,
    UiRuntimeInputDiagnosticSnapshot Input,
    UiRuntimeInvalidationDiagnosticSnapshot Invalidation,
    UiRuntimeLifecycleDiagnosticSnapshot Lifecycle);

internal static class UiRuntimeDiagnosticCapture
{
    public static UiRuntimeDiagnosticSnapshot Capture(
        UiScene scene,
        UiLayoutSnapshot layout,
        UiInteractionDiagnosticSnapshot interaction,
        UiHostUpdate? update,
        UiHostRuntimePerformanceSnapshot performance)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(interaction);
        var focusable = interaction.FocusOrder.ToHashSet();
        var hitTestTargets = interaction.HitTestTargets.ToHashSet();
        var nodes = new List<UiRuntimeNodeDiagnosticSnapshot>();
        Visit(scene.Root, parent: null, depth: 0, layout, interaction, focusable, hitTestTargets, nodes);
        UiHostPlacementResult placement = layout.HostPlacement;
        return new UiRuntimeDiagnosticSnapshot(
            scene.Experience,
            scene.DisplayName,
            scene.Root.Policy.Kind,
            scene.MeasurementContext.Profile,
            placement.Bounds,
            placement.Kind,
            placement.UsedFallback,
            placement.WasClamped,
            ReadOnly(nodes.ToArray()),
            new UiRuntimeInputDiagnosticSnapshot(
                interaction.Hovered,
                interaction.Pressed,
                interaction.Focused,
                interaction.TextEditing is { } editing
                    ? new UiRuntimeTextEditingDiagnosticSnapshot(
                        editing.Input,
                        editing.Anchor,
                        editing.Caret,
                        editing.SelectionStart,
                        editing.SelectionLength)
                    : null,
                ReadOnly(interaction.FocusOrder.ToArray()),
                ReadOnly(interaction.HitTestTargets.ToArray())),
            Invalidation(update),
            new UiRuntimeLifecycleDiagnosticSnapshot(
                update == null ? UiRuntimeLifecyclePhase.Initialized : UiRuntimeLifecyclePhase.Updated,
                performance.LayoutBuilds,
                performance.FrameBuilds));
    }

    private static void Visit(
        UiSceneNode node,
        UiSymbolId? parent,
        int depth,
        UiLayoutSnapshot layout,
        UiInteractionDiagnosticSnapshot interaction,
        IReadOnlySet<UiSymbolId> focusable,
        IReadOnlySet<UiSymbolId> hitTestTargets,
        ICollection<UiRuntimeNodeDiagnosticSnapshot> output)
    {
        if (!layout.TryGetEntry(node.Id, out UiLayoutEntry? entry) || entry == null)
            throw new InvalidOperationException($"Runtime diagnostic node '{node.Id}' has no layout entry.");

        UiRuntimeCollectionDiagnosticSnapshot? collection = null;
        if (node is UiCollectionSceneNode &&
            layout.TryGetCollection(node.Id, out UiCollectionLayoutWindow? window) &&
            window != null)
        {
            collection = new UiRuntimeCollectionDiagnosticSnapshot(
                window.TotalCount,
                window.ScrollOffset,
                window.TotalExtent,
                window.Columns,
                window.Anchor?.Item,
                window.Anchor?.LocalOffset,
                ReadOnly(window.Items.Select(item => new UiRuntimeCollectionItemDiagnosticSnapshot(
                    item.Node,
                    item.Index,
                    item.Item.Label,
                    item.Item.SupportingText,
                    item.Bounds,
                    item.Clip,
                    item.Selected,
                    interaction.Focused == item.Node,
                    interaction.Hovered == item.Node,
                    interaction.Pressed == item.Node,
                    focusable.Contains(item.Node),
                    hitTestTargets.Contains(item.Node),
                    States(item.Visual, interaction, item.Node, enabled: true, selected: item.Selected),
                    Visual(item.Visual))).ToArray()));
        }

        bool enabled = node is not UiButtonSceneNode button || button.Action.CanExecute;
        output.Add(new UiRuntimeNodeDiagnosticSnapshot(
            depth,
            node.Id,
            parent,
            node.Kind.ToString(),
            Semantic(node, enabled),
            node.Role,
            entry.Bounds,
            entry.ContentBounds,
            entry.Clip,
            entry.DesiredSize,
            interaction.Focused == node.Id,
            interaction.Hovered == node.Id,
            interaction.Pressed == node.Id,
            focusable.Contains(node.Id),
            hitTestTargets.Contains(node.Id),
            States(node.Visual, interaction, node.Id, enabled, node is UiRouteButtonSceneNode { IsCurrent: true }),
            Visual(node.Visual),
            collection));

        foreach (UiSceneNode child in node.Children)
            Visit(child, node.Id, depth + 1, layout, interaction, focusable, hitTestTargets, output);
    }

    private static UiRuntimeSemanticDiagnosticSnapshot Semantic(UiSceneNode node, bool enabled)
        => node switch
        {
            UiContainerSceneNode container => new(container.SemanticName, null, null, null, null, null, null, null),
            UiSourceSceneNode source => new(source.SemanticName, source.DisplayText, null, null, null, null, null, null),
            UiCollectionSceneNode collection => new(
                collection.SemanticName,
                null,
                collection.IsSelectable,
                collection.Count,
                collection.SourceRevision,
                collection.SelectedItemId,
                null,
                null),
            UiTextSceneNode text => new(null, text.Text, null, null, null, null, null, null),
            UiButtonSceneNode button => new(null, button.Label, enabled, null, null, null, null, null),
            UiRouteButtonSceneNode route => new(null, route.Label, true, null, null, null, route.Route, route.IsCurrent),
            UiTextInputSceneNode input => new(input.SemanticName, input.CurrentText, true, null, null, null, null, null),
            _ => new(null, null, null, null, null, null, null, null)
        };

    private static IReadOnlyList<UiSymbolId> States(
        UiVisualResolution visual,
        UiInteractionDiagnosticSnapshot interaction,
        UiSymbolId node,
        bool enabled,
        bool selected)
    {
        var states = visual.Properties
            .Where(property => property.State != null)
            .Select(property => property.State!.Value)
            .Concat(interaction.Hovered == node ? new[] { UiVisualStates.Hover.Id } : Array.Empty<UiSymbolId>())
            .Concat(interaction.Focused == node ? new[] { UiVisualStates.Focused.Id } : Array.Empty<UiSymbolId>())
            .Concat(interaction.Pressed == node ? new[] { UiVisualStates.Pressed.Id } : Array.Empty<UiSymbolId>())
            .Concat(!enabled ? new[] { UiVisualStates.Disabled.Id } : Array.Empty<UiSymbolId>())
            .Concat(selected ? new[] { UiVisualStates.Selected.Id } : Array.Empty<UiSymbolId>())
            .Distinct()
            .OrderBy(state => state.ToString(), StringComparer.Ordinal)
            .ToArray();
        return ReadOnly(states);
    }

    private static IReadOnlyList<UiRuntimeResolvedVisualDiagnosticSnapshot> Visual(UiVisualResolution visual)
        => ReadOnly(visual.Properties.Select(property => new UiRuntimeResolvedVisualDiagnosticSnapshot(
            property.Property,
            Format(property.Value),
            property.Layer,
            property.State,
            property.Token,
            property.Provenance)).ToArray());

    private static string Format(object value)
        => value switch
        {
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

    private static UiRuntimeInvalidationDiagnosticSnapshot Invalidation(UiHostUpdate? update)
        => update == null
            ? new UiRuntimeInvalidationDiagnosticSnapshot(
                UiPropertyEffects.None,
                LayoutChanged: false,
                FrameChanged: false,
                Array.Empty<UiSymbolId>())
            : new UiRuntimeInvalidationDiagnosticSnapshot(
                update.Diff.Effects,
                update.LayoutChanged,
                update.FrameChanged,
                ReadOnly(update.Diff.ChangedNodes.ToArray()));

    private static IReadOnlyList<T> ReadOnly<T>(T[] values) => Array.AsReadOnly(values);
}
