using System.Text.Json;
using System.Threading.Tasks;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Metadata;
using Hatifect.UI.Tooling.Protocol;
using Xunit;

namespace Hatifect.UI.Tooling.Tests;

internal static class AuthoringTestSession
{
    internal const string DocumentUri = "file:///Storage.hatifect";

    internal static async Task<UiToolingProtocolSession> Open(
        string source,
        UiBindingContext? context = null,
        int maximumSourceLength = 1024 * 1024)
    {
        var session = new UiToolingProtocolSession(maximumSourceLength: maximumSourceLength);
        await Initialize(session, context);
        Assert.False((await Send(session, "textDocument/didOpen", new
        {
            textDocument = new { uri = DocumentUri, version = 1, text = source }
        }, notification: true)).IsError);
        return session;
    }

    internal static async Task Initialize(UiToolingProtocolSession session, UiBindingContext? context = null,
        object[]? declarations = null)
    {
        context ??= new UiBindingContext(new UiSymbolId("Author.Mod", "storage"))
            .DeclareElement("Items", UiSemanticCatalog.CreateFoundation().Capability("Browse"),
                UiSemanticCatalog.CreateFoundation().Capability("Select"))
            .DeclareRole("Item");
        using JsonDocument metadata = JsonDocument.Parse(UiBindingContextMetadataWire.Serialize(
            UiBindingContextMetadataExporter.Export(context)));
        Assert.False((await Send(session, "initialize", new
        {
            capabilities = new
            {
                workspace = new { workspaceEdit = new { documentChanges = true } },
                textDocument = new
                {
                    documentSymbol = new { hierarchicalDocumentSymbolSupport = true },
                    codeAction = new { codeActionLiteralSupport = new { codeActionKind = new { valueSet = new[] { "quickfix" } } } }
                }
            },
            initializationOptions = new { bindingMetadata = metadata.RootElement.Clone(), declarations = declarations ?? System.Array.Empty<object>() }
        })).IsError);
        Assert.False((await Send(session, "initialized", new { }, notification: true)).IsError);
    }

    internal static ValueTask<UiJsonRpcDispatchResult> Send(
        UiToolingProtocolSession session, string method, object parameters, bool notification = false)
        => session.HandleAsync(new UiJsonRpcRequest(method, notification,
            notification ? null : JsonSerializer.SerializeToElement(1), JsonSerializer.SerializeToElement(parameters)));

    internal static object At(int line, int character, string uri = DocumentUri)
        => new { textDocument = new { uri }, position = new { line, character } };

    internal static object Document(string uri = DocumentUri) => new { textDocument = new { uri } };

    internal static object Edit(int line, int start, int end, string text)
        => new { range = new { start = new { line, character = start }, end = new { line, character = end } }, text };
}
