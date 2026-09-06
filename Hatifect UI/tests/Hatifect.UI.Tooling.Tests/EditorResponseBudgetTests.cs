using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Metadata;
using Hatifect.UI.Tooling.Protocol;
using Xunit;
using static Hatifect.UI.Tooling.Tests.AuthoringTestSession;

namespace Hatifect.UI.Tooling.Tests;

public sealed class EditorResponseBudgetTests
{
    [Fact]
    public async Task OversizedProductResponseReturnsBoundedErrorThenServesHoverAndShutdown()
    {
        const int budget = 4096;
        const string largeId = "large \" ответ \n";
        using var metadata = JsonDocument.Parse(UiBindingContextJson.Export(
            new UiBindingContext(new UiSymbolId("Author.Mod", "storage")).DeclareRole("Item")));
        using var input = new MemoryStream();
        using (var frames = new UiLspMessageStream(Stream.Null, input, maximumPayloadBytes: budget))
        {
            await Write(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new
            {
                initializationOptions = new { bindingMetadata = metadata.RootElement.Clone() }
            } });
            await Write(new { jsonrpc = "2.0", method = "initialized", @params = new { } });
            await Write(new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new
            {
                textDocument = new { uri = DocumentUri, version = 1,
                    text = "visual Storage\n" + string.Concat(Enumerable.Repeat("Item\n    opacity = 1\n", 120)) }
            } });
            await Write(new { jsonrpc = "2.0", id = new string('я', 1000), method = "shutdown", @params = new { } });
            await Write(new { jsonrpc = "2.0", id = 2, method = "textDocument/hover", @params = At(0, 0, "file:///" + new string('я', 1000)) });
            await Write(new { jsonrpc = "2.0", id = largeId, method = "textDocument/documentSymbol", @params = Document() });
            await Write(new { jsonrpc = "2.0", id = 3, method = "textDocument/hover", @params = At(2, 5) });
            await Write(new { jsonrpc = "2.0", id = 4, method = "shutdown", @params = new { } });
            await Write(new { jsonrpc = "2.0", method = "exit", @params = new { } });

            ValueTask Write(object message) => frames.WriteFrameAsync(JsonSerializer.SerializeToUtf8Bytes(message,
                new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        input.Position = 0;
        using var output = new MemoryStream();
        var session = new UiToolingProtocolSession();
        var loop = await UiToolingProtocolTransportLoop.RunAsync(input, output, session, maximumPayloadBytes: budget);
        Assert.Equal(UiToolingProtocolTermination.ExitRequested, loop.Termination);
        output.Position = 0;
        var replies = new List<JsonElement>();
        using (var frames = new UiLspMessageStream(output, Stream.Null, maximumPayloadBytes: budget))
            while (await frames.ReadFrameAsync() is { } payload)
            {
                Assert.InRange(payload.Length, 1, budget);
                using var json = JsonDocument.Parse(payload);
                replies.Add(json.RootElement.Clone());
            }
        Assert.Equal(6, replies.Count);
        Assert.Equal(JsonValueKind.Null, replies[1].GetProperty("id").ValueKind);
        Assert.Equal(UiJsonRpcErrorCodes.InvalidRequest, replies[1].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(UiJsonRpcErrorCodes.InvalidParams, replies[2].GetProperty("error").GetProperty("code").GetInt32());
        Assert.EndsWith("…", replies[2].GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(largeId, replies[3].GetProperty("id").GetString());
        Assert.Equal(UiJsonRpcErrorCodes.InternalError, replies[3].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("Opacity", replies[4].GetProperty("result").GetProperty("contents").GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, replies[5].GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task InitializationResponseBudgetFailureLeavesSessionRetryable()
    {
        var session = new UiToolingProtocolSession();
        session.ConfigurePayloadLimit(256);
        using var metadata = JsonDocument.Parse(UiBindingContextJson.Export(new UiBindingContext(new UiSymbolId("Author.Mod", "storage"))));
        var result = await Send(session, "initialize", new
        {
            initializationOptions = new { bindingMetadata = metadata.RootElement.Clone() }
        });
        Assert.True(result.IsError);
        Assert.Equal(UiToolingProtocolState.Created, session.State);
        session.ConfigurePayloadLimit(4096);
        await Initialize(session);
        Assert.Equal(UiToolingProtocolState.Active, session.State);
    }
}
