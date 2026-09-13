using Hatifect.UI.Language.Syntax;

namespace Hatifect.UI.Language;

public static class UiSyntaxTree
{
    public static UiDocumentSyntax Parse(string source, string sourceName = "<memory>")
    {
        UiLexResult lexed = new UiLexer(source, sourceName).Lex();
        return new UiParser(lexed, sourceName).ParseDocument();
    }
}
