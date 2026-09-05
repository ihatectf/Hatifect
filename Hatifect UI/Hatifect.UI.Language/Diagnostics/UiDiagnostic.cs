using Hatifect.UI.Language.Text;

namespace Hatifect.UI.Language.Diagnostics;

public enum UiDiagnosticSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2
}

public sealed record UiDiagnostic(
    string Id,
    UiDiagnosticSeverity Severity,
    string Message,
    UiTextSpan Span,
    string SourceName);
