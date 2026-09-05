using System;
using System.Collections.Generic;

namespace Hatifect.UI.Tooling.Editor;

internal sealed partial class UiEditorAnalysis
{
    internal static readonly IReadOnlyList<string> TokenTypes = Array.AsReadOnly(new[]
    {
        "keyword", "type", "variable", "property", "enumMember", "string", "number", "operator"
    });

    internal IReadOnlyList<int> SemanticTokens { get; }

    private IReadOnlyList<int> BuildSemanticTokens()
    {
        var data = new List<int>(Symbols.Count * 5);
        int previousLine = 0;
        int previousCharacter = 0;
        foreach (UiEditorSymbol symbol in Symbols)
        {
            UiEditorPosition start = Text.PositionAt(symbol.Span.Start);
            UiEditorPosition end = Text.PositionAt(symbol.Span.End);
            for (int line = start.Line; line <= end.Line; line++)
            {
                int character = line == start.Line ? start.Character : 0;
                int endCharacter = line == end.Line ? end.Character : Text.LineEnd(line) - Text.LineStart(line);
                if (endCharacter <= character) continue;
                data.Add(line - previousLine);
                data.Add(line == previousLine ? character - previousCharacter : character);
                data.Add(endCharacter - character);
                data.Add((int)symbol.Kind);
                data.Add(symbol.IsDeclaration ? 1 : 0);
                previousLine = line;
                previousCharacter = character;
            }
        }
        return Array.AsReadOnly(data.ToArray());
    }
}
