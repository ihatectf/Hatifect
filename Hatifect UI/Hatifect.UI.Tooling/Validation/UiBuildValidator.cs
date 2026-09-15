using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Language.Diagnostics;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Tooling.Validation;

public sealed record UiBuildAsset
{
    public UiBuildAsset(
        string sourceName,
        string source,
        UiBindingContext bindingContext,
        UiDefinitionKind? expectedKind = null)
    {
        if (string.IsNullOrWhiteSpace(sourceName)) throw new ArgumentException("A source name is required.", nameof(sourceName));
        SourceName = sourceName;
        Source = source ?? throw new ArgumentNullException(nameof(source));
        BindingContext = bindingContext ?? throw new ArgumentNullException(nameof(bindingContext));
        ExpectedKind = expectedKind;
    }

    public string SourceName { get; }
    public string Source { get; }
    public UiBindingContext BindingContext { get; }
    public UiDefinitionKind? ExpectedKind { get; }
}

public sealed record UiValidatedDefinition(string SourceName, UiBoundDefinition Definition);

public sealed class UiBuildValidationResult
{
    internal UiBuildValidationResult(UiValidatedDefinition[] definitions, UiDiagnostic[] diagnostics)
    {
        Definitions = Array.AsReadOnly((UiValidatedDefinition[])definitions.Clone());
        Diagnostics = Array.AsReadOnly((UiDiagnostic[])diagnostics.Clone());
    }

    public IReadOnlyList<UiValidatedDefinition> Definitions { get; }
    public IReadOnlyList<UiDiagnostic> Diagnostics { get; }
    public bool IsValid => Diagnostics.All(diagnostic => diagnostic.Severity != UiDiagnosticSeverity.Error);
}

/// <summary>Deterministic multi-asset build gate backed by the runtime compiler.</summary>
public sealed class UiBuildValidator
{
    private readonly UiCompiler _compiler;

    public UiBuildValidator(UiSemanticCatalog? catalog = null)
        => _compiler = new UiCompiler(catalog);

    public UiBuildValidationResult Validate(IEnumerable<UiBuildAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        UiBuildAsset[] ordered = assets
            .Select(asset => asset ?? throw new ArgumentException("Build assets cannot contain null.", nameof(assets)))
            .OrderBy(asset => asset.SourceName, StringComparer.Ordinal)
            .ToArray();
        var definitions = new List<UiValidatedDefinition>(ordered.Length);
        var diagnostics = new List<UiDiagnostic>();
        var owners = new Dictionary<UiSymbolId, string>();

        foreach (UiBuildAsset asset in ordered)
            ValidateAsset(asset, owners, definitions, diagnostics);

        UiDiagnostic[] stableDiagnostics = diagnostics
            .OrderBy(diagnostic => diagnostic.SourceName, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Span.Start)
            .ThenBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .ToArray();
        return new UiBuildValidationResult(definitions.ToArray(), stableDiagnostics);
    }

    private void ValidateAsset(
        UiBuildAsset asset,
        Dictionary<UiSymbolId, string> owners,
        List<UiValidatedDefinition> definitions,
        List<UiDiagnostic> diagnostics)
    {
        UiCompilationResult compilation = _compiler.Compile(asset.Source, asset.BindingContext, asset.SourceName);
        diagnostics.AddRange(compilation.Diagnostics);
        if (!compilation.IsValid || compilation.Definition == null) return;

        UiBoundDefinition definition = compilation.Definition;
        if (asset.ExpectedKind is { } expected && definition.Kind != expected)
        {
            diagnostics.Add(new UiDiagnostic(
                "LUI3001",
                UiDiagnosticSeverity.Error,
                $"Expected a {expected} definition but source declares {definition.Kind}.",
                compilation.Syntax.KindToken.Span,
                asset.SourceName));
            return;
        }

        if (owners.TryGetValue(definition.Id, out string? previous))
        {
            diagnostics.Add(new UiDiagnostic(
                "LUI3002",
                UiDiagnosticSeverity.Error,
                $"Definition '{definition.Id}' is already provided by '{previous}'.",
                compilation.Syntax.NameToken.Span,
                asset.SourceName));
            return;
        }

        owners.Add(definition.Id, asset.SourceName);
        definitions.Add(new UiValidatedDefinition(asset.SourceName, definition));
    }
}
