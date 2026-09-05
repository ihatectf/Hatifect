using Hatifect.UI.Language.Text;

namespace Hatifect.UI.Language.Syntax;

public sealed record UiSyntaxToken(
    UiSyntaxKind Kind,
    string Text,
    object? Value,
    UiTextSpan Span,
    bool IsMissing = false)
{
    public static UiSyntaxToken Missing(UiSyntaxKind kind, UiTextSpan at)
        => new(kind, string.Empty, null, new UiTextSpan(at.Start, 0, at.Line, at.Column), IsMissing: true);
}
