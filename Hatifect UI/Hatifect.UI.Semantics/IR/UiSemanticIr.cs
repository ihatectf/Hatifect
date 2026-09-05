using System;
using System.Collections.Generic;
using Hatifect.UI.Language.Diagnostics;
using Hatifect.UI.Language.Syntax;
using Hatifect.UI.Language.Text;

namespace Hatifect.UI.Semantics;

public sealed record UiSourceProvenance(string SourceName, UiTextSpan Span);

public abstract record UiBoundValue(UiSemanticType Type);
public sealed record UiBooleanValue(bool Value) : UiBoundValue(UiSemanticType.Bool);
public sealed record UiIntegerValue(int Value) : UiBoundValue(UiSemanticType.Int);
public sealed record UiFloatValue(double Value) : UiBoundValue(UiSemanticType.Float);
public sealed record UiStringValue(string Value) : UiBoundValue(UiSemanticType.String);
public sealed record UiLengthValue(int Value) : UiBoundValue(UiSemanticType.Length);
public sealed record UiOpacityValue(double Value) : UiBoundValue(UiSemanticType.Opacity);
public sealed record UiSymbolValue(UiSymbolId Symbol, string Name, UiSemanticType SymbolType) : UiBoundValue(SymbolType);
public sealed record UiBorderValue(UiSymbolValue Color, int Width) : UiBoundValue(UiSemanticType.Border);

public sealed record UiTransitionValue : UiBoundValue
{
    public UiTransitionValue(UiBoundValue from, UiBoundValue to)
        : base(UiSemanticType.TransitionOf(from.Type))
    {
        if (from.Type != to.Type) throw new ArgumentException("Transition endpoints must have the same semantic type.");
        From = from;
        To = to;
    }

    public UiBoundValue From { get; }
    public UiBoundValue To { get; }
}

public sealed record UiPlacementIr(
    UiSymbolId Element,
    UiSymbolId Region,
    UiSourceProvenance Provenance);

public sealed record UiPropertyAssignmentIr(
    UiSymbolId? Target,
    UiPropertySymbol Property,
    UiSymbolId? Profile,
    UiSymbolId? State,
    UiBoundValue Value,
    UiSourceProvenance Provenance);

public abstract class UiBoundDefinition
{
    protected UiBoundDefinition(UiSymbolId id, UiDefinitionKind kind)
    {
        Id = id;
        Kind = kind;
    }

    public UiSymbolId Id { get; }
    public UiDefinitionKind Kind { get; }
}

public sealed class UiPresentationDefinition : UiBoundDefinition
{
    public UiPresentationDefinition(UiSymbolId id, UiPlacementIr[] placements, UiPropertyAssignmentIr[] assignments)
        : base(id, UiDefinitionKind.Presentation)
    {
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(assignments);
        Placements = Array.AsReadOnly((UiPlacementIr[])placements.Clone());
        Assignments = Array.AsReadOnly((UiPropertyAssignmentIr[])assignments.Clone());
    }

    public IReadOnlyList<UiPlacementIr> Placements { get; }
    public IReadOnlyList<UiPropertyAssignmentIr> Assignments { get; }
}

public sealed class UiVisualDefinition : UiBoundDefinition
{
    public UiVisualDefinition(UiSymbolId id, UiPropertyAssignmentIr[] recipes)
        : base(id, UiDefinitionKind.Visual)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        Recipes = Array.AsReadOnly((UiPropertyAssignmentIr[])recipes.Clone());
    }

    public IReadOnlyList<UiPropertyAssignmentIr> Recipes { get; }
}

public sealed class UiCompilationResult
{
    internal UiCompilationResult(UiDocumentSyntax syntax, UiBoundDefinition? definition, UiDiagnostic[] diagnostics)
    {
        Syntax = syntax;
        Definition = definition;
        Diagnostics = Array.AsReadOnly(diagnostics);
    }

    public UiDocumentSyntax Syntax { get; }
    public UiBoundDefinition? Definition { get; }
    public IReadOnlyList<UiDiagnostic> Diagnostics { get; }
    public bool IsValid
    {
        get
        {
            foreach (UiDiagnostic diagnostic in Diagnostics)
                if (diagnostic.Severity == UiDiagnosticSeverity.Error) return false;
            return Definition != null;
        }
    }
}
