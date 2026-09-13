namespace Hatifect.UI.Language.Syntax;

public enum UiSyntaxKind
{
    BadToken,
    EndOfFileToken,
    NewLineToken,
    IndentToken,
    DedentToken,
    IdentifierToken,
    NumberToken,
    StringToken,
    EqualsToken,
    ArrowToken,
    DotToken,
    AtToken,

    Document,
    BlockStatement,
    AssignmentStatement,
    PlacementStatement,
    Value
}

public enum UiDocumentKind
{
    Unknown = 0,
    Presentation = 1,
    Visual = 2
}
