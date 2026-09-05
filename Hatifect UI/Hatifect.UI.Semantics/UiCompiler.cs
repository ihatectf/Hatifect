using System;
using Hatifect.UI.Language;
using Hatifect.UI.Language.Syntax;

namespace Hatifect.UI.Semantics;

/// <summary>The single semantic entry point used by runtime, build tooling, tests, and the future LSP.</summary>
public sealed class UiCompiler
{
    private readonly UiSemanticCatalog _catalog;

    public UiCompiler(UiSemanticCatalog? catalog = null)
    {
        _catalog = catalog ?? UiSemanticCatalog.CreateFoundation();
    }

    public UiCompilationResult Compile(string source, UiBindingContext context, string sourceName = "<memory>")
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);
        UiDocumentSyntax syntax = UiSyntaxTree.Parse(source, sourceName);
        return new UiBinder(syntax, context, _catalog).Bind();
    }
}
