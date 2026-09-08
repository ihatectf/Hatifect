using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Hatifect.UI.Tooling.Client;
using Hatifect.UI.Tooling.Protocol;
using Xunit;

namespace Hatifect.UI.Tooling.Tests;

public sealed class ToolingClientTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("legacy")]
    [InlineData("version")]
    [InlineData("capability")]
    public async Task UnsupportedServerCannotReceiveInitializedOrCompile(string scenario)
    {
        object advertised = scenario switch
        {
            "missing" => new { capabilities = new { } },
            "legacy" => new { capabilities = new { experimental = new { hatifectUi = new { bindingMetadataVersions = new[] { 1 } } } } },
            "version" => Handshake(1),
            _ => Handshake(0, new[] { "diagnostics" })
        };
        using var input = await Frames(new { jsonrpc = "2.0", id = 1, result = advertised });
        using var output = new MemoryStream();
        using var client = new UiToolingClient(input, output);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.InitializeAsync(JsonSerializer.SerializeToElement(new { }), new[] { "compilation" }));
        Assert.False(client.Supports("compilation"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompileAsync("file:///Storage.hatifect"));
        output.Position = 0;
        using var reader = new UiLspMessageStream(output, Stream.Null);
        using var request = JsonDocument.Parse((await reader.ReadFrameAsync())!);
        Assert.Equal("initialize", request.RootElement.GetProperty("method").GetString());
        Assert.Null(await reader.ReadFrameAsync());
    }

    [Theory]
    [InlineData("status")]
    [InlineData("diagnostics")]
    [InlineData("uri")]
    [InlineData("version")]
    [InlineData("bindingRevision")]
    [InlineData("resultId")]
    [InlineData("contradiction")]
    [InlineData("requestId")]
    [InlineData("both")]
    [InlineData("error")]
    [InlineData("range")]
    public async Task PartialOrMismatchedCompilationNeverBecomesSuccess(string mutation)
    {
        JsonObject result = JsonSerializer.SerializeToNode(new { uri = "file:///Storage.hatifect", version = 1,
            bindingRevision = 0, resultId = "opaque-current", status = "valid", diagnostics = Array.Empty<object>() })!.AsObject();
        JsonObject envelope = JsonSerializer.SerializeToNode(new { jsonrpc = "2.0", id = 3, result })!.AsObject();
        JsonObject body = envelope["result"]!.AsObject();
        switch (mutation)
        {
            case "status": body.Remove("status"); break;
            case "diagnostics": body.Remove("diagnostics"); break;
            case "uri": body["uri"] = "file:///Other.hatifect"; break;
            case "version": body["version"] = 2; break;
            case "bindingRevision": body["bindingRevision"] = 1; break;
            case "resultId": body["resultId"] = "old"; break;
            case "contradiction": body["status"] = "invalid"; break;
            case "requestId": envelope["id"] = 4; break;
            case "both": envelope["error"] = JsonSerializer.SerializeToNode(new { code = -32603, message = "Too large" }); break;
            case "error": envelope.Remove("result"); envelope["error"] = JsonSerializer.SerializeToNode(new { code = -32603, message = "Too large" }); break;
            case "range": body["status"] = "invalid"; body["diagnostics"] = JsonSerializer.SerializeToNode(new[] { new
                { severity = 1, code = "LUI", source = "hatifect-ui", message = "Invalid", range = new
                    { start = new { line = 1, character = 0 }, end = new { line = 0, character = 0 } } } }); break;
        }
        using var input = await Frames(new { jsonrpc = "2.0", id = 1, result = Handshake() },
            new { jsonrpc = "2.0", id = 2, result = new { kind = "full", resultId = "opaque-current", items = Array.Empty<object>() } }, envelope);
        using var output = new MemoryStream();
        using var client = new UiToolingClient(input, output);
        await client.InitializeAsync(JsonSerializer.SerializeToElement(new { }));
        await client.SynchronizeAsync("file:///Storage.hatifect", 1, "presentation Storage");
        Exception? failure = await Record.ExceptionAsync(() => client.CompileAsync("file:///Storage.hatifect"));
        Assert.NotNull(failure);
        Assert.True(failure is InvalidDataException or UiToolingRequestException, failure.ToString());
        Assert.False(client.Supports("compilation"));
        long written = output.Length;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompileAsync("file:///Storage.hatifect"));
        Assert.Equal(written, output.Length);
    }

    [Fact]
    public async Task ClosedTransportCannotBeRetriedAndBorrowedStreamsRemainOpen()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        var client = new UiToolingClient(input, output);
        await Assert.ThrowsAsync<EndOfStreamException>(() => client.InitializeAsync(JsonSerializer.SerializeToElement(new { })));
        long written = output.Length;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.InitializeAsync(JsonSerializer.SerializeToElement(new { })));
        Assert.Equal(written, output.Length);
        client.Dispose();
        Assert.True(input.CanRead);
        Assert.True(output.CanWrite);
    }

    private static object Handshake(int version = 0, string[]? names = null)
        => new { capabilities = new { experimental = new { hatifectUi = new
        { protocolVersion = version, capabilities = names ?? new[] { "diagnostics", "compilation" } } } } };

    private static async Task<MemoryStream> Frames(params object[] messages)
    {
        var stream = new MemoryStream();
        using var frames = new UiLspMessageStream(Stream.Null, stream);
        foreach (object message in messages) await frames.WriteFrameAsync(JsonSerializer.SerializeToUtf8Bytes(message));
        stream.Position = 0;
        return stream;
    }
}
