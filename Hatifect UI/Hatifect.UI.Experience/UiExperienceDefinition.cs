using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Experience;

public sealed record UiSemanticElementDefinition(
    UiSymbolId Id,
    string Name,
    IUiSemanticSource Source,
    IReadOnlyList<UiCapability> Capabilities);

public sealed record UiVisualRoleDefinition(UiSymbolId Id, string Name);

public sealed class UiExperienceDefinition
{
    internal UiExperienceDefinition(
        UiSymbolId id,
        string displayName,
        UiSemanticElementDefinition[] elements,
        UiActionDefinition[] actions,
        UiVisualRoleDefinition[] visualRoles)
    {
        Id = id;
        DisplayName = displayName;
        Elements = Array.AsReadOnly(elements);
        Actions = Array.AsReadOnly(actions);
        VisualRoles = Array.AsReadOnly(visualRoles);
    }

    public UiSymbolId Id { get; }
    public string DisplayName { get; }
    public IReadOnlyList<UiSemanticElementDefinition> Elements { get; }
    public IReadOnlyList<UiActionDefinition> Actions { get; }
    public IReadOnlyList<UiVisualRoleDefinition> VisualRoles { get; }

    public UiBindingContext CreateBindingContext()
    {
        var context = new UiBindingContext(Id);
        foreach (UiSemanticElementDefinition element in Elements)
            context.DeclareElement(element.Name, element.Capabilities.Select(capability => capability.Id).ToArray());
        foreach (UiVisualRoleDefinition role in VisualRoles)
            context.DeclareRole(role.Name);
        return context;
    }
}
