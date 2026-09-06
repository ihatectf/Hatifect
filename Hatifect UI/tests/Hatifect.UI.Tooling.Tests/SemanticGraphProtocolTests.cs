using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Metadata;
using Hatifect.UI.Tooling.Protocol;
using Xunit;
using static Hatifect.UI.Tooling.Tests.AuthoringTestSession;

namespace Hatifect.UI.Tooling.Tests;

public sealed class SemanticGraphProtocolTests
{
    [Fact]
    public async Task V2IsAdvertisedBeforeActivationAndMalformedGraphCannotInitialize()
    {
        var session = new UiToolingProtocolSession();
        JsonNode invalid = JsonNode.Parse(UiBindingContextJson.Export(SemanticGraphMetadataTests.Context()))!;
        invalid["graph"]!["relations"] = new JsonArray();
        var rejected = await Send(session, "initialize", new { initializationOptions = new { bindingMetadata = invalid } });
        Assert.True(rejected.IsError);
        Assert.Equal(UiToolingProtocolState.Created, session.State);
        var accepted = await Send(session, "initialize", new { initializationOptions = new { bindingMetadata = Metadata(SemanticGraphMetadataTests.Context()) } });
        Assert.False(accepted.IsError);
        Assert.Equal(new[] { 1, 2 }, accepted.Result!.Value.GetProperty("capabilities").GetProperty("experimental")
            .GetProperty("hatifectUi").GetProperty("bindingMetadataVersions").EnumerateArray().Select(value => value.GetInt32()));
        Assert.NotEqual(UiToolingProtocolState.Active, session.State);
        await Send(session, "initialized", new { }, notification: true);
        Assert.Equal(UiToolingProtocolState.Active, session.State);
    }

    [Fact]
    public async Task MixedV1V2BindingsAndInvalidRefreshPreserveSnapshotsAndAllowSameRevisionRetry()
    {
        var session = new UiToolingProtocolSession();
        var legacy = new UiBindingContext(new UiSymbolId("Legacy", "view")).DeclareRole("Item");
        var graph = SemanticGraphMetadataTests.Context();
        const string graphUri = "file:///Graph.hatifect";
        object Options(object graphMetadata) => new
        {
            bindingRevision = 1,
            bindingMetadata = Metadata(legacy),
            documentBindings = new[] { new { uri = graphUri, bindingMetadata = graphMetadata } }
        };
        Assert.False((await Send(session, "initialize", new { initializationOptions = new
        {
            bindingMetadata = Metadata(legacy), documentBindings = new[] { new { uri = graphUri, bindingMetadata = Metadata(graph) } }
        } })).IsError);
        await Send(session, "initialized", new { }, notification: true);
        await Send(session, "textDocument/didOpen", new { textDocument = new { uri = DocumentUri, version = 1, text = "visual Legacy\nItem\n    opacity = 1" } }, notification: true);
        await Send(session, "textDocument/didOpen", new { textDocument = new { uri = graphUri, version = 1, text = "presentation Navigator\nStorages -> Primary\n" } }, notification: true);
        Assert.True(session.TryGetDocument(DocumentUri, out var beforeLegacy));
        Assert.True(session.TryGetDocument(graphUri, out var beforeGraph));
        Assert.True(beforeLegacy!.IsValid);
        Assert.True(beforeGraph!.IsValid);
        Assert.Equal(graph.OwnerId, beforeGraph.Analysis.Bindings.OwnerId);
        JsonNode invalid = JsonNode.Parse(UiBindingContextJson.Export(graph))!;
        invalid["graph"]!["relations"] = new JsonArray();
        Assert.True((await Send(session, "hatifect/updateBindings", Options(invalid))).IsError);
        Assert.True(session.TryGetDocument(DocumentUri, out var retainedLegacy));
        Assert.True(session.TryGetDocument(graphUri, out var retainedGraph));
        Assert.Same(beforeLegacy, retainedLegacy);
        Assert.Same(beforeGraph, retainedGraph);
        Assert.False((await Send(session, "hatifect/updateBindings", Options(Metadata(graph)))).IsError);
        Assert.True(session.TryGetDocument(graphUri, out var rebound));
        Assert.NotSame(beforeGraph, rebound);
        Assert.True(rebound!.IsValid);
        Assert.Equal(1, rebound.Version);
    }

    [Theory]
    [InlineData("nodes")]
    [InlineData("inputs")]
    [InlineData("relations")]
    public async Task AuxiliaryGraphCannotBypassTheContextSymbolBudget(string dimension)
    {
        var owner = new UiSymbolId("Budget", "graph");
        UiSymbolId Id(string value) => owner.Child(value);
        UiSymbolId Cap(string value) => new("Hatifect.UI", "capability/" + value);
        UiSemanticGraph graph;
        if (dimension == "nodes")
            graph = new(owner, Enumerable.Range(0, 4097).Select(index => new UiSemanticNode(Id("n" + index), "N" + index, "Value", UiDataTypes.String, Array.Empty<UiSymbolId>())), presentedNodes: Array.Empty<UiSymbolId>());
        else if (dimension == "inputs")
            graph = new(owner, new[] { new UiSemanticNode(Id("node"), "Node", "Node", UiDataTypes.Collection(UiDataTypes.String), new[] { Cap("Browse") },
                Enumerable.Range(0, 4096).Select(index => new UiProjectionInput(Id("node/input/" + index), "Input" + index, UiDataTypes.String, false))) });
        else
        {
            var selection = new UiSemanticNode(Id("selected"), "Selected", "Selected", UiDataTypes.Selection(UiDataTypes.String), new[] { Cap("Select") });
            graph = new(owner, new[] { selection }.Concat(Enumerable.Range(0, 2300).Select(index => new UiSemanticNode(Id("details/" + index), "Details" + index, "Details", UiDataTypes.String with { Nullable = true }, new[] { Cap("Inspect") }))),
                Enumerable.Range(0, 2300).Select(index => new UiSemanticRelation(Id("relation/" + index), UiRelationKind.Details, selection.Id, Id("details/" + index))),
                Array.Empty<UiSymbolId>());
        }
        var session = new UiToolingProtocolSession();
        var result = await Send(session, "initialize", new { initializationOptions = new { bindingMetadata = Metadata(new UiBindingContext(graph)) } });
        Assert.True(result.IsError);
        Assert.Equal(UiJsonRpcErrorCodes.InvalidParams, result.ErrorCode);
        Assert.Equal(UiToolingProtocolState.Created, session.State);
    }

    private static JsonElement Metadata(UiBindingContext context)
    {
        using var json = JsonDocument.Parse(UiBindingContextJson.Export(context));
        return json.RootElement.Clone();
    }
}
