using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Language.Diagnostics;
using Hatifect.UI.Language.Text;

namespace Hatifect.UI.Language.Syntax;

public abstract class UiSyntaxNode
{
    protected UiSyntaxNode(UiSyntaxKind kind, UiTextSpan span)
    {
        Kind = kind;
        Span = span;
    }

    public UiSyntaxKind Kind { get; }
    public UiTextSpan Span { get; }
}

public sealed class UiNameSyntax : UiSyntaxNode
{
    public UiNameSyntax(UiSyntaxToken[] segments, UiTextSpan span)
        : base(UiSyntaxKind.Value, span)
    {
        Segments = segments ?? throw new ArgumentNullException(nameof(segments));
    }

    public IReadOnlyList<UiSyntaxToken> Segments { get; }
    public bool IsMissing => Segments.Count == 0 || Segments.Any(segment => segment.IsMissing);
    public override string ToString() => string.Join(".", Segments.Select(segment => segment.Text));
}

public abstract class UiStatementSyntax : UiSyntaxNode
{
    protected UiStatementSyntax(UiSyntaxKind kind, UiTextSpan span) : base(kind, span) { }
}

public sealed class UiBlockSyntax : UiStatementSyntax
{
    public UiBlockSyntax(
        UiNameSyntax? target,
        UiSyntaxToken? specialization,
        UiStatementSyntax[] statements,
        UiTextSpan span)
        : base(UiSyntaxKind.BlockStatement, span)
    {
        Target = target;
        Specialization = specialization;
        Statements = statements ?? throw new ArgumentNullException(nameof(statements));
    }

    public UiNameSyntax? Target { get; }
    public UiSyntaxToken? Specialization { get; }
    public IReadOnlyList<UiStatementSyntax> Statements { get; }
}

public sealed class UiAssignmentSyntax : UiStatementSyntax
{
    public UiAssignmentSyntax(UiNameSyntax property, UiValueSyntax value, UiTextSpan span)
        : base(UiSyntaxKind.AssignmentStatement, span)
    {
        Property = property;
        Value = value;
    }

    public UiNameSyntax Property { get; }
    public UiValueSyntax Value { get; }
}

public sealed class UiPlacementSyntax : UiStatementSyntax
{
    public UiPlacementSyntax(UiNameSyntax subject, UiNameSyntax region, UiTextSpan span)
        : base(UiSyntaxKind.PlacementStatement, span)
    {
        Subject = subject;
        Region = region;
    }

    public UiNameSyntax Subject { get; }
    public UiNameSyntax Region { get; }
}

public sealed class UiValueSyntax : UiSyntaxNode
{
    public UiValueSyntax(UiSyntaxToken[] tokens, UiTextSpan span)
        : base(UiSyntaxKind.Value, span)
    {
        Tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    }

    public IReadOnlyList<UiSyntaxToken> Tokens { get; }
    public bool IsMissing => Tokens.Count == 0 || Tokens.Any(token => token.IsMissing);
    public string Text => string.Join(" ", Tokens.Select(token => token.Text));
}

public sealed class UiDocumentSyntax : UiSyntaxNode
{
    public UiDocumentSyntax(
        UiDocumentKind documentKind,
        UiSyntaxToken kindToken,
        UiSyntaxToken nameToken,
        UiStatementSyntax[] statements,
        UiSyntaxToken endOfFileToken,
        UiDiagnostic[] diagnostics,
        string sourceName,
        UiTextSpan span)
        : base(UiSyntaxKind.Document, span)
    {
        DocumentKind = documentKind;
        KindToken = kindToken;
        NameToken = nameToken;
        Statements = statements ?? throw new ArgumentNullException(nameof(statements));
        EndOfFileToken = endOfFileToken;
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        SourceName = sourceName;
    }

    public UiDocumentKind DocumentKind { get; }
    public UiSyntaxToken KindToken { get; }
    public UiSyntaxToken NameToken { get; }
    public IReadOnlyList<UiStatementSyntax> Statements { get; }
    public UiSyntaxToken EndOfFileToken { get; }
    public IReadOnlyList<UiDiagnostic> Diagnostics { get; }
    public string SourceName { get; }
}
