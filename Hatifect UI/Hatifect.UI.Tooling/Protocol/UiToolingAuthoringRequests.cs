using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Hatifect.UI.Tooling.Editor;

namespace Hatifect.UI.Tooling.Protocol;

internal sealed partial class UiToolingProtocolSession
{
    private UiJsonRpcDispatchResult SemanticTokens(UiJsonRpcRequest request)
        => Success(new { data = RequestDocument(request).Analysis.SemanticTokens });

    private UiJsonRpcDispatchResult DocumentSymbols(UiJsonRpcRequest request)
    {
        UiEditorDocumentSnapshot document = RequestDocument(request);
        IReadOnlyList<UiEditorOutlineNode> outline = document.Analysis.Outline;
        // Flat symbols also support older clients and keep recovery output within JSON depth limits.
        if (!_hierarchicalSymbols || outline.Any(n => n.Depth >= 24))
            return Success(outline.Select(n => new
            {
                name = n.Name, kind = n.Kind,
                location = new { uri = document.SourceName, range = Range(document.Text, n.Selection) },
                containerName = n.Parent >= 0 ? outline[n.Parent].Name : string.Empty
            }).ToArray());
        return Success(BuildHierarchicalSymbols(document));
    }

    private static object[] BuildHierarchicalSymbols(UiEditorDocumentSnapshot document)
    {
        IReadOnlyList<UiEditorOutlineNode> outline = document.Analysis.Outline;
        var children = new Dictionary<int, List<object>>();
        for (int i = outline.Count - 1; i >= 0; i--)
        {
            UiEditorOutlineNode node = outline[i];
            if (!children.TryGetValue(node.Parent, out List<object>? siblings))
                children.Add(node.Parent, siblings = new List<object>());
            object[] nested = children.TryGetValue(i, out List<object>? members)
                ? members.AsEnumerable().Reverse().ToArray() : System.Array.Empty<object>();
            siblings.Add(new { name = node.Name, kind = node.Kind, range = Range(document.Text, node.Span),
                selectionRange = Range(document.Text, node.Selection), children = nested });
        }
        return children.TryGetValue(-1, out List<object>? roots)
            ? roots.AsEnumerable().Reverse().ToArray() : System.Array.Empty<object>();
    }

    private UiJsonRpcDispatchResult FoldingRanges(UiJsonRpcRequest request)
    {
        UiEditorDocumentSnapshot document = RequestDocument(request);
        return Success(document.Analysis.Outline.Where(n => n.IsBlock)
            .Select(n => new { startLine = document.Text.PositionAt(n.Span.Start).Line,
                endLine = document.Text.PositionAt(n.Span.End).Line, kind = "region" })
            .Where(range => range.endLine > range.startLine).ToArray());
    }

    private UiEditorDocumentSnapshot RequestDocument(UiJsonRpcRequest request)
    {
        RequireRequest(request, request.Method);
        return OpenDocument(RequiredString(TextDocument(request.Parameters), "uri"));
    }

    private UiJsonRpcDispatchResult Hover(UiJsonRpcRequest request)
    {
        (UiEditorDocumentSnapshot document, int offset) = DocumentPosition(request);
        UiEditorSymbol? symbol = document.Analysis.SymbolAt(offset);
        return symbol == null || symbol.Description.Length == 0
            ? UiJsonRpcDispatchResult.Success()
            : Success(new { contents = new { kind = "plaintext", value = symbol.Description },
                range = Range(document.Text, symbol.Span) });
    }

    private UiJsonRpcDispatchResult Completion(UiJsonRpcRequest request)
    {
        (UiEditorDocumentSnapshot document, int offset) = DocumentPosition(request);
        return Success(new
        {
            isIncomplete = false,
            items = document.Analysis.Complete(offset).Select(item => new
            {
                label = item.Label,
                kind = item.Kind,
                detail = item.Detail,
                textEdit = new { range = Range(document.Text, item.Span), newText = item.Label }
            }).ToArray()
        });
    }

    private (UiEditorDocumentSnapshot Document, int Offset) DocumentPosition(UiJsonRpcRequest request)
    {
        RequireRequest(request, request.Method);
        JsonElement parameters = RequiredObject(request.Parameters, "position params");
        UiEditorDocumentSnapshot document = OpenDocument(RequiredString(TextDocument(parameters), "uri"));
        return (document, Offset(document.Text, Property(parameters, "position")));
    }
}
