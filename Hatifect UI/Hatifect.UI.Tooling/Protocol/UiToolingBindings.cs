using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Metadata;

namespace Hatifect.UI.Tooling.Protocol;

internal sealed partial class UiToolingProtocolSession
{
    private Dictionary<string, UiBindingContext> _documentBindings = new(StringComparer.Ordinal);
    private int _bindingRevision;

    private UiBindingContext ContextFor(string uri)
        => _documentBindings.TryGetValue(uri, out UiBindingContext? context) ? context : _bindingContext!;

    private UiJsonRpcDispatchResult UpdateBindings(UiJsonRpcRequest request)
    {
        RequireRequest(request, request.Method);
        JsonElement parameters = RequiredObject(request.Parameters, "binding update params");
        int revision = RequiredNonNegativeInt32(parameters, "bindingRevision");
        if (revision <= _bindingRevision) throw new InvalidDataException("The binding revision must increase.");
        BindingConfiguration candidate = ReadBindingConfiguration(parameters);
        UiJsonRpcDispatchResult response = Success(new { bindingRevision = revision, documentsReanalyzed = _workspace.Count });
        if (response.IsError) return response;
        _workspace.Rebind(uri => candidate.Documents.TryGetValue(uri, out UiBindingContext? context)
            ? context : candidate.Default);
        _bindingContext = candidate.Default;
        _documentBindings = candidate.Documents;
        _declarations = candidate.Declarations;
        _bindingRevision = revision;
        return response;
    }

    private BindingConfiguration ReadBindingConfiguration(JsonElement options)
    {
        UiBindingContextMetadata defaults = ReadMetadata(Property(options, "bindingMetadata"));
        var metadata = new List<UiBindingContextMetadata> { defaults };
        var documents = new Dictionary<string, UiBindingContext>(StringComparer.Ordinal);
        int symbols = SymbolCount(defaults);
        if (OptionalProperty(options, "documentBindings") is { } entries)
        {
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > _workspace.Capacity)
                throw new InvalidDataException($"documentBindings must have at most {_workspace.Capacity} entries.");
            foreach (JsonElement value in entries.EnumerateArray())
            {
                JsonElement entry = RequiredObject(value, "document binding");
                string uri = RequiredString(entry, "uri");
                if (uri.Length > 8192 || !Uri.TryCreate(uri, UriKind.Absolute, out _))
                    throw new InvalidDataException("A document binding URI must be absolute and at most 8192 characters.");
                UiBindingContextMetadata binding = ReadMetadata(Property(entry, "bindingMetadata"));
                symbols += SymbolCount(binding);
                if (symbols > 16384) throw new InvalidDataException("The session binding budget is 16384 symbols.");
                if (!documents.TryAdd(uri, UiBindingContextMetadataWire.CreateBindingContext(binding)))
                    throw new InvalidDataException("A document binding URI is duplicated.");
                metadata.Add(binding);
            }
        }
        return new BindingConfiguration(UiBindingContextMetadataWire.CreateBindingContext(defaults), documents,
            ReadDeclarations(options, metadata));
    }

    private static UiBindingContextMetadata ReadMetadata(JsonElement value)
    {
        UiBindingContextMetadata metadata = UiBindingContextMetadataWire.Deserialize(Encoding.UTF8.GetBytes(value.GetRawText()));
        if (SymbolCount(metadata) > 4096 || metadata.Elements.Any(e => e.Capabilities.Count > 64)
            || metadata.Graph is { } graph && graph.Nodes.Any(node => node.Capabilities.Count > 64))
            throw new InvalidDataException("A binding context supports at most 4096 symbols and 64 capabilities per element.");
        if (metadata.Elements.Any(e => e.Name.Length > 512) || metadata.Roles.Any(r => r.Name.Length > 512)
            || metadata.Graph is { } model && model.Nodes.Any(node => node.Alias.Length > 512
                || node.Inputs.Any(input => input.Name.Length > 512)))
            throw new InvalidDataException("A binding symbol name must be at most 512 characters.");
        return metadata;
    }

    private static int SymbolCount(UiBindingContextMetadata metadata)
        => metadata.Graph is { } graph
            ? checked(graph.Nodes.Count + graph.Relations.Count + graph.Roles.Count + graph.Nodes.Sum(node => node.Inputs.Count))
            : metadata.Elements.Count + metadata.Roles.Count;

    private sealed record BindingConfiguration(UiBindingContext Default,
        Dictionary<string, UiBindingContext> Documents, Dictionary<UiSymbolId, UiEditorLocation> Declarations);
}
