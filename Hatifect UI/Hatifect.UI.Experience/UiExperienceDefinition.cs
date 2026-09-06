using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Experience;

public sealed record UiSemanticElementDefinition(
    UiSymbolId Id,
    string Name,
    IUiSemanticSource Source,
    IReadOnlyList<UiCapability> Capabilities)
{
    public string Alias { get; init; } = Name;
    public string Label => Name;
    public UiDataType? DataType { get; init; }
    public IReadOnlyList<UiProjectionInput> Inputs { get; init; } = Array.Empty<UiProjectionInput>();
}

public sealed record UiVisualRoleDefinition(UiSymbolId Id, string Name);

public sealed class UiExperienceDefinition
{
    internal UiExperienceDefinition(
        UiSymbolId id,
        string displayName,
        UiSemanticElementDefinition[] elements,
        UiActionDefinition[] actions,
        UiVisualRoleDefinition[] visualRoles,
        UiSemanticElementDefinition[] sources,
        UiSemanticGraph graph)
    {
        Id = id;
        DisplayName = displayName;
        Elements = Array.AsReadOnly(elements);
        Actions = Array.AsReadOnly(actions);
        VisualRoles = Array.AsReadOnly(visualRoles);
        Sources = Array.AsReadOnly(sources);
        Graph = graph;
    }

    public UiSymbolId Id { get; }
    public string DisplayName { get; }
    public IReadOnlyList<UiSemanticElementDefinition> Elements { get; }
    public IReadOnlyList<UiActionDefinition> Actions { get; }
    public IReadOnlyList<UiVisualRoleDefinition> VisualRoles { get; }
    public IReadOnlyList<UiSemanticElementDefinition> Sources { get; }
    public UiSemanticGraph Graph { get; }

    public UiBindingContext CreateBindingContext()
    {
        // V1 can express this exact independent legacy graph without losing information.
        bool legacy = Graph.Relations.Count == 0 && Graph.Nodes.Count == Elements.Count
            && Elements.All(element => element.DataType is null && element.Inputs.Count == 0
                && element.Alias == element.Label && element.Id == Id.Child("element/" + element.Alias))
            && VisualRoles.All(role => role.Id == Id.Child("role/" + role.Name));
        if (!legacy) return new UiBindingContext(Graph);
        var context = new UiBindingContext(Id);
        foreach (UiSemanticElementDefinition element in Elements)
            context.DeclareElement(element.Alias, element.Capabilities.Select(capability => capability.Id).ToArray());
        foreach (UiVisualRoleDefinition role in VisualRoles)
            context.DeclareRole(role.Name);
        return context;
    }
}
