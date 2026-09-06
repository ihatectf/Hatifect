using Hatifect.UI.Language.Text;

namespace Hatifect.UI.Semantics;

public enum UiRelationKind { Selection, Details, Query, Filter, Validation, Submission, ActionTarget }

public sealed record UiGraphProvenance(string SourceName, UiTextSpan Span, UiSymbolId? DeclarationId = null);
public sealed record UiProjectionInput(UiSymbolId Id, string Name, UiDataType AcceptedType, bool Required);

/// <summary>A null DataType is an opaque legacy declaration, never an inferred required type.</summary>
public sealed class UiSemanticNode
{
    public UiSemanticNode(UiSymbolId id, string alias, string label, UiDataType? dataType,
        IEnumerable<UiSymbolId> capabilities, IEnumerable<UiProjectionInput>? inputs = null)
    {
        Id = id;
        Alias = alias ?? throw new ArgumentNullException(nameof(alias));
        Label = label ?? throw new ArgumentNullException(nameof(label));
        DataType = dataType;
        Capabilities = Array.AsReadOnly((capabilities ?? throw new ArgumentNullException(nameof(capabilities))).ToArray());
        Inputs = Array.AsReadOnly((inputs ?? Array.Empty<UiProjectionInput>()).ToArray());
    }

    public UiSymbolId Id { get; }
    public string Alias { get; }
    public string Label { get; }
    public UiDataType? DataType { get; }
    public IReadOnlyList<UiSymbolId> Capabilities { get; }
    public IReadOnlyList<UiProjectionInput> Inputs { get; }
}

/// <summary>Mapping declares a consumer-owned form adapter's output; the graph never executes it.</summary>
public sealed record UiSemanticRelation(UiSymbolId Id, UiRelationKind Kind, UiSymbolId Source,
    UiSymbolId Target, UiSymbolId? TargetInput = null, UiDataType? Mapping = null, UiGraphProvenance? Provenance = null);
public sealed record UiGraphRole(UiSymbolId Id, string Alias);

/// <summary>One immutable graph. PresentedNodes is the explicit subset that the existing planner renders.</summary>
public sealed class UiSemanticGraph
{
    public UiSemanticGraph(UiSymbolId ownerId, IEnumerable<UiSemanticNode> nodes,
        IEnumerable<UiSemanticRelation>? relations = null, IEnumerable<UiSymbolId>? presentedNodes = null,
        IEnumerable<UiGraphRole>? roles = null)
    {
        OwnerId = ownerId;
        Nodes = Array.AsReadOnly((nodes ?? throw new ArgumentNullException(nameof(nodes))).ToArray());
        Relations = Array.AsReadOnly((relations ?? Array.Empty<UiSemanticRelation>()).ToArray());
        PresentedNodes = Array.AsReadOnly((presentedNodes ?? Nodes.Select(node => node.Id)).ToArray());
        Roles = Array.AsReadOnly((roles ?? Array.Empty<UiGraphRole>()).ToArray());
    }

    public UiSymbolId OwnerId { get; }
    public IReadOnlyList<UiSemanticNode> Nodes { get; }
    public IReadOnlyList<UiSemanticRelation> Relations { get; }
    public IReadOnlyList<UiSymbolId> PresentedNodes { get; }
    public IReadOnlyList<UiGraphRole> Roles { get; }
}

public sealed record UiGraphDiagnostic(string Code, string Message, UiSymbolId Subject,
    UiSymbolId? Source = null, UiSymbolId? Target = null, UiSymbolId? Input = null,
    UiGraphProvenance? Provenance = null);

public sealed class UiGraphValidationException : InvalidOperationException
{
    public UiGraphValidationException(IEnumerable<UiGraphDiagnostic> diagnostics)
        : this(diagnostics.ToArray()) { }
    private UiGraphValidationException(UiGraphDiagnostic[] diagnostics)
        : base(string.Join(Environment.NewLine, diagnostics.Select(item => $"{item.Code} {item.Subject}: {item.Message}")))
        => Diagnostics = Array.AsReadOnly(diagnostics);
    public IReadOnlyList<UiGraphDiagnostic> Diagnostics { get; }
}
