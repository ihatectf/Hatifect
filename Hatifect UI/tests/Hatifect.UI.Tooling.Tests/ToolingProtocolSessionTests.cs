using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Hatifect.UI;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Editor;
using Hatifect.UI.Tooling.Metadata;
using Hatifect.UI.Tooling.Protocol;
using Xunit;

namespace Hatifect.UI.Tooling.Tests;

public sealed class ToolingProtocolSessionTests
{
    [Fact]
    public void ProtocolV0RemainsInternalAndCanBackTheExistingDispatcherHandler()
    {
        var session = new UiToolingProtocolSession();
        UiJsonRpcRequestHandler handler = session.HandleAsync;

        Assert.False(typeof(UiToolingProtocolSession).IsPublic);
        Assert.NotNull(handler);
    }

    [Fact]
    public async Task LifecycleDocumentSyncDiagnosticsAndFormattingUseSharedWorkspace()
    {
        var session = new UiToolingProtocolSession();

        UiJsonRpcDispatchResult initialize = await session.HandleAsync(Request(
            "initialize",
            InitializeParams()));
        UiJsonRpcDispatchResult initialized = await session.HandleAsync(Notification(
            "initialized",
            Json("{}")));
        UiJsonRpcDispatchResult opened = await session.HandleAsync(Notification(
            "textDocument/didOpen",
            Json("{\"textDocument\":{\"uri\":\"file:///Storage.hatifect\",\"version\":1," +
                "\"text\":\"visual Storage\\r\\n\\r\\nItem\\r\\n\\tsurface=Surface.Raised\\r\\n\"}}")));
        UiJsonRpcDispatchResult formatting = await session.HandleAsync(Request(
            "textDocument/formatting",
            TextDocumentParams()));
        UiJsonRpcDispatchResult changed = await session.HandleAsync(Notification(
            "textDocument/didChange",
            Json("{\"textDocument\":{\"uri\":\"file:///Storage.hatifect\",\"version\":2}," +
                "\"contentChanges\":[{\"text\":\"visual Storage\\n\\nItem\\n" +
                "    mystery = Surface.Raised\\n\"}]}")));
        UiJsonRpcDispatchResult diagnostics = await session.HandleAsync(Request(
            "textDocument/diagnostic",
            TextDocumentParams()));

        Assert.False(initialize.IsError);
        Assert.Equal(UiToolingProtocolState.Active, session.State);
        Assert.False(initialized.IsError);
        Assert.False(opened.IsError);
        Assert.False(changed.IsError);
        JsonElement edit = Assert.Single(formatting.Result!.Value.EnumerateArray());
        Assert.Equal(
            "visual Storage\n\nItem\n    surface = Surface.Raised\n",
            edit.GetProperty("newText").GetString());
        JsonElement diagnostic = Assert.Single(
            diagnostics.Result!.Value.GetProperty("items").EnumerateArray());
        Assert.Equal("LUI2007", diagnostic.GetProperty("code").GetString());
        Assert.True(session.TryGetDocument("file:///Storage.hatifect", out UiEditorDocumentSnapshot? snapshot));
        Assert.Equal(2, snapshot!.Version);

        Assert.False((await session.HandleAsync(Notification(
            "textDocument/didClose",
            TextDocumentParams()))).IsError);
        Assert.False((await session.HandleAsync(Request("shutdown", Json("{}")))).IsError);
        Assert.False((await session.HandleAsync(Notification("exit", Json("{}")))).IsError);
        Assert.True(session.ExitRequested);
        Assert.Equal(0, session.DocumentCount);
    }

    [Fact]
    public async Task ProductMethodsBeforeInitializedFailWithoutMutatingWorkspace()
    {
        var session = new UiToolingProtocolSession();

        UiJsonRpcDispatchResult result = await session.HandleAsync(Notification(
            "textDocument/didOpen",
            Json("{\"textDocument\":{\"uri\":\"file:///Storage.hatifect\",\"version\":1," +
                "\"text\":\"visual Storage\\n\"}}")));

        Assert.True(result.IsError);
        Assert.Equal(UiJsonRpcErrorCodes.InvalidRequest, result.ErrorCode);
        Assert.Equal(UiToolingProtocolState.Created, session.State);
        Assert.Equal(0, session.DocumentCount);
    }

    [Fact]
    public async Task InvalidMetadataLeavesSessionUninitialized()
    {
        var session = new UiToolingProtocolSession();
        JsonElement parameters = Json(
            "{\"initializationOptions\":{\"bindingMetadata\":{\"schemaVersion\":99}}}");

        UiJsonRpcDispatchResult result = await session.HandleAsync(Request("initialize", parameters));

        Assert.True(result.IsError);
        Assert.Equal(UiJsonRpcErrorCodes.InvalidParams, result.ErrorCode);
        Assert.Equal(UiToolingProtocolState.Created, session.State);
    }

    [Fact]
    public async Task StaleFullDocumentChangeFailsAtomically()
    {
        var session = await ActiveSession();
        await session.HandleAsync(Notification(
            "textDocument/didOpen",
            Json("{\"textDocument\":{\"uri\":\"file:///Storage.hatifect\",\"version\":4," +
                "\"text\":\"visual Storage\\n\\nItem\\n    surface = Surface.Raised\\n\"}}")));

        UiJsonRpcDispatchResult stale = await session.HandleAsync(Notification(
            "textDocument/didChange",
            Json("{\"textDocument\":{\"uri\":\"file:///Storage.hatifect\",\"version\":4}," +
                "\"contentChanges\":[{\"text\":\"visual Changed\\n\"}]}")));

        Assert.True(stale.IsError);
        Assert.Equal(UiJsonRpcErrorCodes.InvalidParams, stale.ErrorCode);
        Assert.True(session.TryGetDocument("file:///Storage.hatifect", out UiEditorDocumentSnapshot? snapshot));
        Assert.Equal(4, snapshot!.Version);
        Assert.Contains("surface = Surface.Raised", snapshot.Source);
    }

    [Fact]
    public async Task UnknownMethodUsesStandardJsonRpcError()
    {
        var session = await ActiveSession();

        UiJsonRpcDispatchResult result = await session.HandleAsync(Request("textDocument/hover", Json("{}")));

        Assert.True(result.IsError);
        Assert.Equal(UiJsonRpcErrorCodes.MethodNotFound, result.ErrorCode);
    }

    private static async Task<UiToolingProtocolSession> ActiveSession()
    {
        var session = new UiToolingProtocolSession();
        Assert.False((await session.HandleAsync(Request("initialize", InitializeParams()))).IsError);
        Assert.False((await session.HandleAsync(Notification("initialized", Json("{}")))).IsError);
        return session;
    }

    private static JsonElement InitializeParams()
    {
        var context = new UiBindingContext(new UiSymbolId("Author.Mod", "storage"))
            .DeclareElement("Items")
            .DeclareRole("Item");
        byte[] metadata = UiBindingContextMetadataWire.Serialize(
            UiBindingContextMetadataExporter.Export(context));
        using JsonDocument metadataDocument = JsonDocument.Parse(metadata);
        return JsonSerializer.SerializeToElement(new
        {
            initializationOptions = new
            {
                bindingMetadata = metadataDocument.RootElement.Clone()
            }
        });
    }

    private static JsonElement TextDocumentParams()
        => Json("{\"textDocument\":{\"uri\":\"file:///Storage.hatifect\"}}");

    private static UiJsonRpcRequest Request(string method, JsonElement parameters)
        => new(method, isNotification: false, Json("1"), parameters);

    private static UiJsonRpcRequest Notification(string method, JsonElement parameters)
        => new(method, isNotification: true, id: null, parameters);

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
