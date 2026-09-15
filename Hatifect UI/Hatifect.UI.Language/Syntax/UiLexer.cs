using System;
using System.Collections.Generic;
using System.Globalization;
using Hatifect.UI.Language.Diagnostics;
using Hatifect.UI.Language.Text;

namespace Hatifect.UI.Language.Syntax;

public sealed class UiLexResult
{
    internal UiLexResult(UiSyntaxToken[] tokens, UiDiagnostic[] diagnostics)
    {
        Tokens = tokens;
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<UiSyntaxToken> Tokens { get; }
    public IReadOnlyList<UiDiagnostic> Diagnostics { get; }
}

/// <summary>Indentation-aware lexer shared by Presentation and Visual documents.</summary>
public sealed class UiLexer
{
    private readonly string _source;
    private readonly string _sourceName;
    private readonly List<UiSyntaxToken> _tokens = new();
    private readonly List<UiDiagnostic> _diagnostics = new();
    private readonly List<int> _indents = new() { 0 };
    private int _position;
    private int _line;
    private int _column;

    public UiLexer(string source, string sourceName = "<memory>")
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _sourceName = string.IsNullOrWhiteSpace(sourceName) ? "<memory>" : sourceName;
    }

    public UiLexResult Lex()
    {
        while (_position < _source.Length)
            LexLine();

        while (_indents.Count > 1)
        {
            _indents.RemoveAt(_indents.Count - 1);
            _tokens.Add(Token(UiSyntaxKind.DedentToken, string.Empty, null, _position, 0, _line, _column));
        }
        _tokens.Add(Token(UiSyntaxKind.EndOfFileToken, string.Empty, null, _position, 0, _line, _column));
        return new UiLexResult(_tokens.ToArray(), _diagnostics.ToArray());
    }

    private void LexLine()
    {
        int lineStart = _position;
        int indentation = 0;
        while (_position < _source.Length && _source[_position] is ' ' or '\t')
        {
            if (_source[_position] == '\t')
            {
                _diagnostics.Add(new UiDiagnostic(
                    "LUI0001", UiDiagnosticSeverity.Warning,
                    "Tabs are accepted as four spaces but spaces are required by the formatter.",
                    new UiTextSpan(_position, 1, _line, indentation), _sourceName));
                indentation += 4;
            }
            else indentation++;
            _position++;
        }

        _column = indentation;
        bool blank = _position >= _source.Length || _source[_position] is '\r' or '\n';
        if (!blank)
            EmitIndentation(indentation, lineStart);

        while (_position < _source.Length && _source[_position] is not '\r' and not '\n')
        {
            char current = _source[_position];
            if (current == ' ' || current == '\t')
            {
                _position++;
                _column++;
                continue;
            }

            int start = _position;
            int column = _column;
            if (char.IsLetter(current) || current == '_')
            {
                LexIdentifier(start, column);
                continue;
            }

            if (char.IsDigit(current) || (current == '-' && Peek(1) is char next && char.IsDigit(next)))
            {
                LexNumber(start, column);
                continue;
            }

            switch (current)
            {
                case '=':
                    AdvanceAndAdd(UiSyntaxKind.EqualsToken, "=", start, column);
                    break;
                case '.' :
                    AdvanceAndAdd(UiSyntaxKind.DotToken, ".", start, column);
                    break;
                case '@':
                    AdvanceAndAdd(UiSyntaxKind.AtToken, "@", start, column);
                    break;
                case '-' when Peek(1) == '>':
                    _position += 2;
                    _column += 2;
                    _tokens.Add(Token(UiSyntaxKind.ArrowToken, "->", null, start, 2, _line, column));
                    break;
                case '"':
                    LexString(start, column);
                    break;
                default:
                    _position++;
                    _column++;
                    _tokens.Add(Token(UiSyntaxKind.BadToken, current.ToString(), null, start, 1, _line, column));
                    _diagnostics.Add(new UiDiagnostic(
                        "LUI0002", UiDiagnosticSeverity.Error, $"Unexpected character '{current}'.",
                        new UiTextSpan(start, 1, _line, column), _sourceName));
                    break;
            }
        }

        if (_position < _source.Length && _source[_position] == '\r') _position++;
        if (_position < _source.Length && _source[_position] == '\n') _position++;
        _tokens.Add(Token(UiSyntaxKind.NewLineToken, "\n", null, _position, 0, _line, _column));
        _line++;
        _column = 0;
    }

    private void EmitIndentation(int indentation, int lineStart)
    {
        int current = _indents[^1];
        if (indentation > current)
        {
            _indents.Add(indentation);
            _tokens.Add(Token(UiSyntaxKind.IndentToken, string.Empty, indentation, lineStart, indentation, _line, 0));
            return;
        }

        while (indentation < _indents[^1])
        {
            _indents.RemoveAt(_indents.Count - 1);
            _tokens.Add(Token(UiSyntaxKind.DedentToken, string.Empty, indentation, lineStart, 0, _line, 0));
        }
        if (indentation != _indents[^1])
        {
            _diagnostics.Add(new UiDiagnostic(
                "LUI0003", UiDiagnosticSeverity.Error,
                "Indentation does not match an enclosing block.",
                new UiTextSpan(lineStart, indentation, _line, 0), _sourceName));
        }
    }

    private void LexIdentifier(int start, int column)
    {
        _position++;
        _column++;
        while (_position < _source.Length)
        {
            char c = _source[_position];
            if (!char.IsLetterOrDigit(c) && c is not '_' and not '-') break;
            _position++;
            _column++;
        }
        string text = _source[start.._position];
        _tokens.Add(Token(UiSyntaxKind.IdentifierToken, text, text, start, text.Length, _line, column));
    }

    private void LexNumber(int start, int column)
    {
        _position++;
        _column++;
        bool seenDot = false;
        while (_position < _source.Length)
        {
            char c = _source[_position];
            if (c == '.' && !seenDot && char.IsDigit(Peek(1) ?? '\0'))
            {
                seenDot = true;
                _position++;
                _column++;
                continue;
            }
            if (!char.IsDigit(c)) break;
            _position++;
            _column++;
        }
        string text = _source[start.._position];
        object value;
        bool parsed;
        if (seenDot)
        {
            parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double floating)
                && double.IsFinite(floating);
            value = parsed ? floating : 0d;
        }
        else
        {
            parsed = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer);
            value = parsed ? integer : 0;
        }
        if (!parsed)
        {
            value = seenDot ? 0d : 0;
            _diagnostics.Add(new UiDiagnostic(
                "LUI0005", UiDiagnosticSeverity.Error, $"Numeric literal '{text}' is outside the supported range.",
                new UiTextSpan(start, text.Length, _line, column), _sourceName));
        }
        _tokens.Add(Token(UiSyntaxKind.NumberToken, text, value, start, text.Length, _line, column));
    }

    private void LexString(int start, int column)
    {
        _position++;
        _column++;
        var value = new System.Text.StringBuilder();
        bool terminated = false;
        while (_position < _source.Length && _source[_position] is not '\r' and not '\n')
        {
            char c = _source[_position++];
            _column++;
            if (c == '"')
            {
                terminated = true;
                break;
            }
            if (c == '\\' && _position < _source.Length)
            {
                char escaped = _source[_position++];
                _column++;
                value.Append(escaped switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => escaped });
            }
            else value.Append(c);
        }

        string text = _source[start.._position];
        _tokens.Add(Token(UiSyntaxKind.StringToken, text, value.ToString(), start, text.Length, _line, column));
        if (!terminated)
        {
            _diagnostics.Add(new UiDiagnostic(
                "LUI0004", UiDiagnosticSeverity.Error, "Unterminated string literal.",
                new UiTextSpan(start, text.Length, _line, column), _sourceName));
        }
    }

    private void AdvanceAndAdd(UiSyntaxKind kind, string text, int start, int column)
    {
        _position++;
        _column++;
        _tokens.Add(Token(kind, text, null, start, text.Length, _line, column));
    }

    private char? Peek(int offset)
    {
        int index = _position + offset;
        return index >= 0 && index < _source.Length ? _source[index] : null;
    }

    private static UiSyntaxToken Token(UiSyntaxKind kind, string text, object? value, int start, int length, int line, int column)
        => new(kind, text, value, new UiTextSpan(start, length, line, column));

}
