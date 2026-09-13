using System;
using System.Collections.Generic;
using Hatifect.UI.Language.Diagnostics;
using Hatifect.UI.Language.Text;

namespace Hatifect.UI.Language.Syntax;

/// <summary>Error-recovering parser. User syntax errors are diagnostics, never exceptions.</summary>
public sealed class UiParser
{
    private readonly IReadOnlyList<UiSyntaxToken> _tokens;
    private readonly string _sourceName;
    private readonly List<UiDiagnostic> _diagnostics;
    private int _position;

    public UiParser(UiLexResult lexResult, string sourceName = "<memory>")
    {
        ArgumentNullException.ThrowIfNull(lexResult);
        _tokens = lexResult.Tokens;
        _sourceName = string.IsNullOrWhiteSpace(sourceName) ? "<memory>" : sourceName;
        _diagnostics = new List<UiDiagnostic>(lexResult.Diagnostics);
    }

    public UiDocumentSyntax ParseDocument()
    {
        SkipNewLines();
        UiSyntaxToken kindToken = Match(UiSyntaxKind.IdentifierToken, "LUI1001", "Expected 'presentation' or 'visual'.");
        UiDocumentKind kind = kindToken.Text switch
        {
            "presentation" => UiDocumentKind.Presentation,
            "visual" => UiDocumentKind.Visual,
            _ => UiDocumentKind.Unknown
        };
        if (!kindToken.IsMissing && kind == UiDocumentKind.Unknown)
            Report("LUI1001", "Expected 'presentation' or 'visual'.", kindToken.Span);

        UiSyntaxToken name = Match(UiSyntaxKind.IdentifierToken, "LUI1002", "Expected a document name.");
        ConsumeLineEnd();

        UiStatementSyntax[] statements = ParseStatementList(stopOnDedent: false);
        UiSyntaxToken eof = Match(UiSyntaxKind.EndOfFileToken, "LUI1003", "Expected end of file.");
        UiTextSpan span = UiTextSpan.Between(kindToken.Span, eof.Span);
        return new UiDocumentSyntax(kind, kindToken, name, statements, eof, _diagnostics.ToArray(), _sourceName, span);
    }

    private UiStatementSyntax[] ParseStatementList(bool stopOnDedent)
    {
        var statements = new List<UiStatementSyntax>();
        while (Current.Kind != UiSyntaxKind.EndOfFileToken && (!stopOnDedent || Current.Kind != UiSyntaxKind.DedentToken))
        {
            if (Current.Kind == UiSyntaxKind.NewLineToken)
            {
                NextToken();
                continue;
            }
            if (Current.Kind == UiSyntaxKind.IndentToken)
            {
                Report("LUI1004", "Unexpected indentation.", Current.Span);
                NextToken();
                continue;
            }
            if (Current.Kind == UiSyntaxKind.DedentToken)
            {
                Report("LUI1005", "Unexpected block end.", Current.Span);
                NextToken();
                continue;
            }

            int start = _position;
            UiStatementSyntax? statement = ParseStatement();
            if (statement != null) statements.Add(statement);
            if (_position == start)
            {
                Report("LUI1006", "Unable to parse statement.", Current.Span);
                SynchronizeLine();
            }
        }
        return statements.ToArray();
    }

    private UiStatementSyntax? ParseStatement()
    {
        if (Current.Kind == UiSyntaxKind.AtToken)
        {
            UiSyntaxToken at = NextToken();
            UiSyntaxToken profile = Match(UiSyntaxKind.IdentifierToken, "LUI1007", "Expected a profile after '@'.");
            UiStatementSyntax[] members = ParseRequiredBlock();
            UiTextSpan span = members.Length == 0 ? UiTextSpan.Between(at.Span, profile.Span) : UiTextSpan.Between(at.Span, members[^1].Span);
            return new UiBlockSyntax(null, profile, members, span);
        }

        if (Current.Kind != UiSyntaxKind.IdentifierToken)
        {
            Report("LUI1008", "Expected a subject or property name.", Current.Span);
            SynchronizeLine();
            return null;
        }

        UiNameSyntax name = ParseName();
        UiSyntaxToken? specialization = null;
        if (Current.Kind == UiSyntaxKind.AtToken)
        {
            NextToken();
            specialization = Match(UiSyntaxKind.IdentifierToken, "LUI1009", "Expected a state after '@'.");
        }

        if (Current.Kind == UiSyntaxKind.ArrowToken)
        {
            NextToken();
            UiNameSyntax region = ParseName();
            ConsumeLineEnd();
            return new UiPlacementSyntax(name, region, UiTextSpan.Between(name.Span, region.Span));
        }

        if (Current.Kind == UiSyntaxKind.EqualsToken)
        {
            NextToken();
            UiValueSyntax value = ParseValue();
            ConsumeLineEnd();
            return new UiAssignmentSyntax(name, value, UiTextSpan.Between(name.Span, value.Span));
        }

        if (Current.Kind == UiSyntaxKind.NewLineToken || Current.Kind == UiSyntaxKind.IndentToken)
        {
            UiStatementSyntax[] members = ParseRequiredBlock();
            UiTextSpan span = members.Length == 0 ? name.Span : UiTextSpan.Between(name.Span, members[^1].Span);
            return new UiBlockSyntax(name, specialization, members, span);
        }

        Report("LUI1010", "Expected '=', '->', '@State', or an indented block.", Current.Span);
        SynchronizeLine();
        return new UiBlockSyntax(name, specialization, Array.Empty<UiStatementSyntax>(), name.Span);
    }

    private UiStatementSyntax[] ParseRequiredBlock()
    {
        if (Current.Kind == UiSyntaxKind.NewLineToken) NextToken();
        else ConsumeLineEnd();

        if (Current.Kind != UiSyntaxKind.IndentToken)
        {
            Report("LUI1011", "Expected an indented block.", Current.Span);
            return Array.Empty<UiStatementSyntax>();
        }
        NextToken();
        UiStatementSyntax[] statements = ParseStatementList(stopOnDedent: true);
        if (Current.Kind == UiSyntaxKind.DedentToken) NextToken();
        return statements;
    }

    private UiNameSyntax ParseName()
    {
        var segments = new List<UiSyntaxToken>();
        UiSyntaxToken first = Match(UiSyntaxKind.IdentifierToken, "LUI1012", "Expected an identifier.");
        segments.Add(first);
        UiSyntaxToken last = first;
        while (Current.Kind == UiSyntaxKind.DotToken)
        {
            NextToken();
            last = Match(UiSyntaxKind.IdentifierToken, "LUI1013", "Expected an identifier after '.'.");
            segments.Add(last);
        }
        return new UiNameSyntax(segments.ToArray(), UiTextSpan.Between(first.Span, last.Span));
    }

    private UiValueSyntax ParseValue()
    {
        var tokens = new List<UiSyntaxToken>();
        UiSyntaxToken first = Current;
        UiSyntaxToken last = first;
        while (Current.Kind is not UiSyntaxKind.NewLineToken and not UiSyntaxKind.DedentToken and not UiSyntaxKind.EndOfFileToken)
        {
            if (Current.Kind is UiSyntaxKind.IdentifierToken or UiSyntaxKind.NumberToken or UiSyntaxKind.StringToken or
                UiSyntaxKind.DotToken or UiSyntaxKind.ArrowToken or UiSyntaxKind.AtToken)
            {
                last = NextToken();
                tokens.Add(last);
            }
            else
            {
                Report("LUI1014", $"Unexpected token '{Current.Text}' in value.", Current.Span);
                NextToken();
            }
        }

        if (tokens.Count == 0)
        {
            UiSyntaxToken missing = UiSyntaxToken.Missing(UiSyntaxKind.IdentifierToken, Current.Span);
            tokens.Add(missing);
            first = last = missing;
            Report("LUI1015", "Expected a value.", Current.Span);
        }
        else first = tokens[0];

        return new UiValueSyntax(tokens.ToArray(), UiTextSpan.Between(first.Span, last.Span));
    }

    private void ConsumeLineEnd()
    {
        if (Current.Kind == UiSyntaxKind.NewLineToken)
        {
            NextToken();
            return;
        }
        if (Current.Kind is UiSyntaxKind.EndOfFileToken or UiSyntaxKind.DedentToken) return;
        Report("LUI1016", "Expected end of line.", Current.Span);
        SynchronizeLine();
    }

    private void SynchronizeLine()
    {
        while (Current.Kind is not UiSyntaxKind.NewLineToken and not UiSyntaxKind.DedentToken and not UiSyntaxKind.EndOfFileToken)
            NextToken();
        if (Current.Kind == UiSyntaxKind.NewLineToken) NextToken();
    }

    private void SkipNewLines()
    {
        while (Current.Kind == UiSyntaxKind.NewLineToken) NextToken();
    }

    private UiSyntaxToken Match(UiSyntaxKind kind, string diagnosticId, string message)
    {
        if (Current.Kind == kind) return NextToken();
        Report(diagnosticId, message, Current.Span);
        return UiSyntaxToken.Missing(kind, Current.Span);
    }

    private UiSyntaxToken Current => Peek(0);

    private UiSyntaxToken Peek(int offset)
    {
        int index = _position + offset;
        if (index >= _tokens.Count) return _tokens[^1];
        return _tokens[index];
    }

    private UiSyntaxToken NextToken()
    {
        UiSyntaxToken current = Current;
        if (_position < _tokens.Count) _position++;
        return current;
    }

    private void Report(string id, string message, UiTextSpan span)
        => _diagnostics.Add(new UiDiagnostic(id, UiDiagnosticSeverity.Error, message, span, _sourceName));
}
