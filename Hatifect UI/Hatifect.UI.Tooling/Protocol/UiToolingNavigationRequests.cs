using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Editor;
using Hatifect.UI.Tooling.Metadata;

namespace Hatifect.UI.Tooling.Protocol;

internal sealed record UiEditorLocation(string Uri, UiEditorPosition Start, UiEditorPosition End);

internal sealed partial class UiToolingProtocolSession
{
    private Dictionary<UiSymbolId, UiEditorLocation> _declarations = new();

    private UiJsonRpcDispatchResult Definition(UiJsonRpcRequest request)
    {
        (UiEditorDocumentSnapshot document, int offset) = DocumentPosition(request);
        if (document.Analysis.SymbolAt(offset)?.Id is not { } id) return Success(Array.Empty<object>());
        if (_declarations.TryGetValue(id, out UiEditorLocation? declaration))
            return Success(new[] { Location(declaration) });
        return Success(UiEditorReferences.Find(_workspace.Snapshots(), id, true)
            .Where(reference => reference.Symbol.IsDeclaration)
            .Select(reference => new { uri = reference.Document.SourceName,
                range = Range(reference.Document.Text, reference.Symbol.Span) }).ToArray());
    }

    private UiJsonRpcDispatchResult References(UiJsonRpcRequest request)
    {
        (UiEditorDocumentSnapshot document, int offset) = DocumentPosition(request);
        JsonElement parameters = RequiredObject(request.Parameters, "reference params");
        JsonElement context = RequiredObject(Property(parameters, "context"), "reference context");
        bool includeDeclaration = Property(context, "includeDeclaration").ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException("includeDeclaration must be a boolean.")
        };
        if (document.Analysis.SymbolAt(offset)?.Id is not { } id) return Success(Array.Empty<object>());
        var locations = UiEditorReferences.Find(_workspace.Snapshots(), id, includeDeclaration)
            .Select(reference => (object)new { uri = reference.Document.SourceName,
                range = Range(reference.Document.Text, reference.Symbol.Span) }).ToList();
        if (includeDeclaration && _declarations.TryGetValue(id, out UiEditorLocation? declaration))
            locations.Add(Location(declaration));
        return Success(locations);
    }

    private UiJsonRpcDispatchResult DocumentHighlights(UiJsonRpcRequest request)
    {
        (UiEditorDocumentSnapshot document, int offset) = DocumentPosition(request);
        if (document.Analysis.SymbolAt(offset)?.Id is not { } id) return Success(Array.Empty<object>());
        return Success(document.Analysis.Occurrences(id).Select(symbol => new
        {
            range = Range(document.Text, symbol.Span),
            kind = symbol.IsDeclaration || symbol.Kind == UiEditorSymbolKind.Property ? 3 : 2
        }).ToArray());
    }

    private static object Location(UiEditorLocation location)
        => new { uri = location.Uri, range = new { start = Position(location.Start), end = Position(location.End) } };

    private static Dictionary<UiSymbolId, UiEditorLocation> ReadDeclarations(JsonElement options,
        IEnumerable<UiBindingContextMetadata> contexts)
    {
        var declarations = new Dictionary<UiSymbolId, UiEditorLocation>();
        if (OptionalProperty(options, "declarations") is not { } values) return declarations;
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 4096)
            throw new InvalidDataException("declarations must be an array of at most 4096 entries.");
        var known = contexts.SelectMany(context => context.Elements.Select(e => e.Id)
            .Concat(context.Roles.Select(r => r.Id))).ToHashSet();
        foreach (JsonElement value in values.EnumerateArray())
        {
            JsonElement entry = RequiredObject(value, "declaration");
            string name = RequiredString(entry, "symbolId");
            if (!UiSymbolId.TryParse(name, out UiSymbolId id) || !known.Contains(id))
                throw new InvalidDataException("A declaration must identify a declared binding element or role.");
            string uri = RequiredString(entry, "uri");
            if (uri.Length > 8192 || !Uri.TryCreate(uri, UriKind.Absolute, out _))
                throw new InvalidDataException("A declaration URI must be absolute and at most 8192 characters.");
            JsonElement range = RequiredObject(Property(entry, "range"), "declaration range");
            UiEditorPosition start = ReadPosition(Property(range, "start"));
            UiEditorPosition end = ReadPosition(Property(range, "end"));
            if (end.Line < start.Line || end.Line == start.Line && end.Character < start.Character)
                throw new InvalidDataException("A declaration range must be ordered.");
            if (!declarations.TryAdd(id, new UiEditorLocation(uri, start, end)))
                throw new InvalidDataException("A declaration symbol is duplicated.");
        }
        return declarations;
    }

    private static UiEditorPosition ReadPosition(JsonElement value)
    {
        JsonElement position = RequiredObject(value, "position");
        return new UiEditorPosition(RequiredNonNegativeInt32(position, "line"),
            RequiredNonNegativeInt32(position, "character"));
    }
}
