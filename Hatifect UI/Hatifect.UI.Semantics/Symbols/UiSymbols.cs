using System;
using System.Collections.Generic;

namespace Hatifect.UI.Semantics;

public enum UiDefinitionKind
{
    Presentation,
    Visual
}

public enum UiSymbolKind
{
    Experience,
    SemanticElement,
    Region,
    Presentation,
    PresentationPattern,
    PresentationProfile,
    VisualRole,
    VisualState,
    Property,
    Token,
    EnumValue,
    Capability
}

public abstract record UiSymbol(UiSymbolId Id, string Name, UiSymbolKind Kind);

public sealed record UiCapabilitySymbol(UiSymbolId Id, string Name)
    : UiSymbol(Id, Name, UiSymbolKind.Capability);

public sealed record UiElementSymbol(
    UiSymbolId Id,
    string Name,
    IReadOnlySet<UiSymbolId> Capabilities)
    : UiSymbol(Id, Name, UiSymbolKind.SemanticElement);

public sealed record UiPresentationSymbol(
    UiSymbolId Id,
    string Name,
    IReadOnlySet<UiSymbolId> SupportedCapabilities)
    : UiSymbol(Id, Name, UiSymbolKind.Presentation);

public sealed record UiTokenSymbol(UiSymbolId Id, string Name, UiSemanticType Type)
    : UiSymbol(Id, Name, UiSymbolKind.Token);

/// <summary>
/// Catalog-owned symbolic value. The owning property supplies its closed value domain without
/// exporting a CLR enum or reducing the value to an untyped string.
/// </summary>
public sealed record UiEnumValueSymbol(UiSymbolId Id, string Name, UiSymbolId Property)
    : UiSymbol(Id, Name, UiSymbolKind.EnumValue);

public sealed record UiPropertySymbol(
    UiSymbolId Id,
    string Name,
    UiDefinitionKind DefinitionKind,
    UiSemanticType Type,
    UiPropertyEffects Effects,
    bool Animatable)
    : UiSymbol(Id, Name, UiSymbolKind.Property);
