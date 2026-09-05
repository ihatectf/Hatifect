using System;
using System.Collections.Generic;
using System.Text;
using Hatifect.UI.Language;
using Hatifect.UI.Language.Diagnostics;
using Hatifect.UI.Language.Syntax;

namespace Hatifect.UI.Tooling.Formatting;

public sealed class UiFormatResult
{
    internal UiFormatResult(string text, UiDiagnostic[] diagnostics, bool canApply, bool changed)
    {
        Text = text;
        Diagnostics = Array.AsReadOnly((UiDiagnostic[])diagnostics.Clone());
        CanApply = canApply;
        Changed = changed;
    }

    public string Text { get; }
    public IReadOnlyList<UiDiagnostic> Diagnostics { get; }
    public bool CanApply { get; }
    public bool Changed { get; }
}

/// <summary>Canonical formatter over the shared recovery syntax tree.</summary>
public sealed class UiSourceFormatter
{
    private const int IndentWidth = 4;

    public UiFormatResult Format(string source, string sourceName = "<memory>")
    {
        ArgumentNullException.ThrowIfNull(source);
        UiDocumentSyntax syntax = UiSyntaxTree.Parse(source, sourceName);
        UiDiagnostic[] diagnostics = syntax.Diagnostics.ToArray();
        if (diagnostics.Any(diagnostic => diagnostic.Severity == UiDiagnosticSeverity.Error))
            return new UiFormatResult(source, diagnostics, canApply: false, changed: false);

        var text = new StringBuilder();
        text.Append(syntax.KindToken.Text)
            .Append(' ')
            .Append(syntax.NameToken.Text)
            .Append('\n');
        if (syntax.Statements.Count > 0) text.Append('\n');
        WriteStatements(text, syntax.Statements, depth: 0);
        string formatted = text.ToString();
        return new UiFormatResult(formatted, diagnostics, canApply: true, changed: formatted != source);
    }

    private static void WriteStatements(
        StringBuilder output,
        IReadOnlyList<UiStatementSyntax> statements,
        int depth)
    {
        foreach (UiStatementSyntax statement in statements)
        {
            output.Append(' ', checked(depth * IndentWidth));
            switch (statement)
            {
                case UiAssignmentSyntax assignment:
                    output.Append(assignment.Property)
                        .Append(" = ")
                        .Append(FormatValue(assignment.Value))
                        .Append('\n');
                    break;
                case UiPlacementSyntax placement:
                    output.Append(placement.Subject)
                        .Append(" -> ")
                        .Append(placement.Region)
                        .Append('\n');
                    break;
                case UiBlockSyntax block:
                    if (block.Target != null) output.Append(block.Target);
                    if (block.Specialization != null)
                        output.Append('@').Append(block.Specialization.Text);
                    output.Append('\n');
                    WriteStatements(output, block.Statements, depth + 1);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported syntax statement '{statement.GetType().Name}'.");
            }
        }
    }

    private static string FormatValue(UiValueSyntax value)
    {
        var output = new StringBuilder();
        UiSyntaxKind previous = UiSyntaxKind.BadToken;
        foreach (UiSyntaxToken token in value.Tokens)
        {
            bool compact = token.Kind == UiSyntaxKind.DotToken || previous is UiSyntaxKind.DotToken or UiSyntaxKind.AtToken;
            if (output.Length > 0 && !compact && token.Kind != UiSyntaxKind.AtToken) output.Append(' ');
            output.Append(token.Text);
            previous = token.Kind;
        }
        return output.ToString();
    }
}
