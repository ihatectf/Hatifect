using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Hatifect.UI.Language.Diagnostics;
using Hatifect.UI.Tooling.Editor;

namespace Hatifect.UI.Tooling.Protocol;

internal sealed partial class UiToolingProtocolSession
{
    private UiJsonRpcDispatchResult Compilation(UiJsonRpcRequest request)
    {
        RequireRequest(request, request.Method);
        JsonElement parameters = RequiredObject(request.Parameters, "compilation params");
        UiEditorDocumentSnapshot snapshot = CurrentCompilationSnapshot(parameters);
        return Success(new
        {
            uri = snapshot.SourceName,
            version = snapshot.Version,
            bindingRevision = _bindingRevision,
            resultId = snapshot.ResultId,
            status = snapshot.IsValid ? "valid" : "invalid",
            diagnostics = DiagnosticItems(snapshot)
        });
    }

    private UiEditorDocumentSnapshot CurrentCompilationSnapshot(JsonElement parameters)
    {
        JsonElement document = TextDocument(parameters);
        UiEditorDocumentSnapshot snapshot = OpenDocument(RequiredString(document, "uri"));
        int version = RequiredNonNegativeInt32(document, "version");
        int bindingRevision = RequiredNonNegativeInt32(parameters, "bindingRevision");
        string resultId = RequiredString(parameters, "resultId");
        if (version != snapshot.Version || bindingRevision != _bindingRevision || resultId != snapshot.ResultId)
        {
            throw new InvalidDataException("The requested compilation snapshot is no longer current. Pull diagnostics again.");
        }
        return snapshot;
    }

    private UiJsonRpcDispatchResult WorkspaceDiagnostic(UiJsonRpcRequest request)
    {
        RequireRequest(request, request.Method);
        JsonElement parameters = RequiredObject(request.Parameters, "workspace diagnostic params");
        Dictionary<string, string> ids = ReadPreviousDiagnosticIds(parameters);
        var reports = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (UiEditorDocumentSnapshot document in _workspace.Snapshots())
        {
            bool unchanged = ids.Remove(document.SourceName, out string? id) && id == document.ResultId;
            reports.Add(document.SourceName, unchanged
                ? new { uri = document.SourceName, version = document.Version, kind = "unchanged", resultId = document.ResultId }
                : new { uri = document.SourceName, version = document.Version, kind = "full", resultId = document.ResultId,
                    items = DiagnosticItems(document) });
        }
        foreach (string uri in ids.Keys)
            reports.Add(uri, new { uri, version = (long?)null, kind = "full", items = Array.Empty<object>() });
        return Success(new { items = reports.Values.ToArray() });
    }

    private Dictionary<string, string> ReadPreviousDiagnosticIds(JsonElement parameters)
    {
        JsonElement previous = Property(parameters, "previousResultIds");
        if (previous.ValueKind != JsonValueKind.Array || previous.GetArrayLength() > _workspace.Capacity * 2)
            throw new InvalidDataException("previousResultIds exceeds the workspace diagnostic budget.");
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonElement value in previous.EnumerateArray())
        {
            JsonElement item = RequiredObject(value, "previous result ID");
            if (!ids.TryAdd(RequiredString(item, "uri"), RequiredString(item, "value", allowEmpty: true)))
                throw new InvalidDataException("A previous diagnostic URI is duplicated.");
        }
        return ids;
    }

    private static object[] DiagnosticItems(UiEditorDocumentSnapshot snapshot)
        => snapshot.Diagnostics.Select(diagnostic => (object)new
        {
            range = Range(snapshot.Text, diagnostic.Span),
            severity = diagnostic.Severity switch
            {
                UiDiagnosticSeverity.Error => 1,
                UiDiagnosticSeverity.Warning => 2,
                _ => 3
            },
            code = diagnostic.Id, source = "hatifect-ui", message = diagnostic.Message
        }).ToArray();
}
