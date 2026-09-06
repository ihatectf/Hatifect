using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Hatifect.UI.Tooling.Editor;

namespace Hatifect.UI.Tooling.Protocol;

internal sealed partial class UiToolingProtocolSession
{
    private UiJsonRpcDispatchResult CodeActions(UiJsonRpcRequest request)
    {
        UiEditorDocumentSnapshot document = RequestDocument(request);
        JsonElement parameters = RequiredObject(request.Parameters, "code action params");
        JsonElement range = RequiredObject(Property(parameters, "range"), "code action range");
        int start = Offset(document.Text, Property(range, "start"));
        int end = Offset(document.Text, Property(range, "end"));
        if (end < start) throw new InvalidDataException("A code action range must be ordered.");
        JsonElement context = RequiredObject(Property(parameters, "context"), "code action context");
        if (Property(context, "diagnostics").ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Code action diagnostics must be an array.");
        if (OptionalProperty(context, "only") is { } only)
        {
            if (only.ValueKind != JsonValueKind.Array || only.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
                throw new InvalidDataException("Code action kinds must be strings.");
            if (!only.EnumerateArray().Any(v => v.GetString() is "" or "quickfix")) return Success(Array.Empty<object>());
        }
        if (!_versionedCodeActions) return Success(Array.Empty<object>());
        return Success(UiEditorQuickFixes.Find(document, _authoringCompiler, start, end, _workspace.MaximumSourceLength)
            .Select(fix => new
            {
                title = $"Replace with '{fix.NewText}'", kind = "quickfix",
                diagnostics = new[] { new { range = Range(document.Text, fix.Diagnostic.Span), severity = 1,
                    code = fix.Diagnostic.Id, source = "hatifect-ui", message = fix.Diagnostic.Message } },
                edit = new { documentChanges = new[] { new
                {
                    textDocument = new { uri = document.SourceName, version = document.Version },
                    edits = new[] { new { range = Range(document.Text, fix.Span), newText = fix.NewText } }
                } } }
            }).ToArray());
    }
}
