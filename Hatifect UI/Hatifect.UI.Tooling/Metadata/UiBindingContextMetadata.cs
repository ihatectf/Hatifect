using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Tooling.Metadata;

internal sealed record UiBindingElementMetadata(
    UiSymbolId Id,
    string Name,
    IReadOnlyList<UiSymbolId> Capabilities);

internal sealed record UiBindingRoleMetadata(UiSymbolId Id, string Name);

/// <summary>
/// Immutable transport-neutral compiler context metadata. Discovery and wire serialization remain
/// product concerns; Tooling never needs a dependency on an Experience implementation assembly.
/// </summary>
internal sealed class UiBindingContextMetadata
{
    internal UiBindingContextMetadata(
        UiSymbolId ownerId,
        bool requireDeclaredElements,
        bool requireDeclaredRoles,
        UiBindingElementMetadata[] elements,
        UiBindingRoleMetadata[] roles,
        UiSemanticGraph? graph = null)
    {
        OwnerId = ownerId;
        RequireDeclaredElements = requireDeclaredElements;
        RequireDeclaredRoles = requireDeclaredRoles;
        Elements = Array.AsReadOnly((UiBindingElementMetadata[])elements.Clone());
        Roles = Array.AsReadOnly((UiBindingRoleMetadata[])roles.Clone());
        Graph = graph;
    }

    public UiSymbolId OwnerId { get; }
    public bool RequireDeclaredElements { get; }
    public bool RequireDeclaredRoles { get; }
    public IReadOnlyList<UiBindingElementMetadata> Elements { get; }
    public IReadOnlyList<UiBindingRoleMetadata> Roles { get; }
    public UiSemanticGraph? Graph { get; }
}

internal static class UiBindingContextMetadataExporter
{
    public static UiBindingContextMetadata Export(UiBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        UiElementSymbol[] symbols = context.SnapshotDeclaredElements();
        UiBindingElementMetadata[] elements = symbols
            .OrderBy(element => element.Name, StringComparer.Ordinal)
            .Select(element => new UiBindingElementMetadata(
                element.Id,
                element.Name,
                Array.AsReadOnly(element.Capabilities
                    .OrderBy(capability => capability.ToString(), StringComparer.Ordinal)
                    .ToArray())))
            .ToArray();
        UiBindingRoleMetadata[] roles = context.SnapshotDeclaredRoles()
            .OrderBy(role => role.Key, StringComparer.Ordinal)
            .Select(role => new UiBindingRoleMetadata(role.Value, role.Key))
            .ToArray();
        UiSemanticGraph? graph = GraphForExport(context, symbols, roles);
        return new UiBindingContextMetadata(
            context.OwnerId,
            context.RequireDeclaredElements,
            context.RequireDeclaredRoles,
            elements,
            roles,
            graph);
    }

    private static UiSemanticGraph? GraphForExport(
        UiBindingContext context,
        UiElementSymbol[] symbols,
        UiBindingRoleMetadata[] roles)
    {
        if (context.Graph is { } graph) return graph;

        // Legacy metadata derives IDs from names and cannot retain separate labels.
        bool needsGraph = symbols.Any(element =>
            element.Id != context.OwnerId.Child("element/" + element.Name) || element.Label != element.Name)
            || roles.Any(role => role.Id != context.OwnerId.Child("role/" + role.Name));
        if (!needsGraph) return null;

        return new UiSemanticGraph(context.OwnerId,
            symbols.Select(element => new UiSemanticNode(element.Id, element.Name, element.Label, null, element.Capabilities)),
            roles: roles.Select(role => new UiGraphRole(role.Id, role.Name)));
    }
}
