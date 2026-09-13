using System.Text;
using System.Text.Json;
using Hatifect.UI;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Metadata;
using Hatifect.UI.Tooling.Protocol;
using Xunit;

namespace Hatifect.UI.Tooling.Tests;

public sealed class ToolingProtocolTransportLoopTests
{
    [Fact]
    public async Task LifecycleRunsSequentiallyUntilExitAndLeavesTrailingInputUnread()
    {
        using var input = await FramesAsync(
            Request(1, "initialize", InitializeParams()),
            Notification("initialized", "{}"),
            Request(2, "shutdown", "{}"),
            Notification("exit", "{}"),
            Request(3, "initialize", InitializeParams()));
        using var output = new MemoryStream();
        var session = new UiToolingProtocolSession();

        UiToolingProtocolLoopResult result = await UiToolingProtocolTransportLoop.RunAsync(
            input,
            output,
            session);

        Assert.Equal(UiToolingProtocolTermination.ExitRequested, result.Termination);
        Assert.Equal(4, result.ProcessedMessages);
        Assert.Equal(UiToolingProtocolState.Exited, session.State);
        Assert.True(input.Position < input.Length);
        JsonElement[] responses = await ResponsesAsync(output);
        Assert.Equal(new[] { 1, 2 }, responses.Select(response => response.GetProperty("id").GetInt32()));
        Assert.All(responses, response => Assert.True(response.TryGetProperty("result", out _)));
    }

    [Fact]
    public async Task CleanEofReturnsWithoutInventingAnExitTransition()
    {
        using var input = await FramesAsync(
            Request(1, "initialize", InitializeParams()),
            Notification("initialized", "{}"));
        using var output = new MemoryStream();
        var session = new UiToolingProtocolSession();

        UiToolingProtocolLoopResult result = await UiToolingProtocolTransportLoop.RunAsync(
            input,
            output,
            session);

        Assert.Equal(UiToolingProtocolTermination.EndOfStream, result.Termination);
        Assert.Equal(2, result.ProcessedMessages);
        Assert.Equal(UiToolingProtocolState.Active, session.State);
        Assert.False(session.ExitRequested);
    }

    [Fact]
    public async Task AlreadyExitedSessionDoesNotReadOrWriteBorrowedStreams()
    {
        var session = new UiToolingProtocolSession();
        await session.HandleAsync(new UiJsonRpcRequest(
            "exit",
            isNotification: true,
            id: null,
            Json("{}")));
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("not a frame"));
        using var output = new MemoryStream();

        UiToolingProtocolLoopResult result = await UiToolingProtocolTransportLoop.RunAsync(
            input,
            output,
            session);

        Assert.Equal(UiToolingProtocolTermination.ExitRequested, result.Termination);
        Assert.Equal(0, result.ProcessedMessages);
        Assert.Equal(0, input.Position);
        Assert.Equal(0, output.Length);
        Assert.True(input.CanRead);
        Assert.True(output.CanWrite);
    }

    [Fact]
    public async Task CallerCancellationAndFramingFailuresPropagateFailClosed()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UiToolingProtocolTransportLoop.RunAsync(
                new MemoryStream(),
                new MemoryStream(),
                new UiToolingProtocolSession(),
                cancellationToken: cancellation.Token).AsTask());

        using var truncated = new MemoryStream(
            Encoding.ASCII.GetBytes("Content-Length: 10\r\n\r\n{}"));
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            UiToolingProtocolTransportLoop.RunAsync(
                truncated,
                output,
                new UiToolingProtocolSession()).AsTask());
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task TransportBoundsAreAppliedByTheLoopWithoutTakingStreamOwnership()
    {
        using var input = await FramesAsync(Request(1, "initialize", InitializeParams()));
        using var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UiToolingProtocolTransportLoop.RunAsync(
                input,
                output,
                new UiToolingProtocolSession(),
                maximumPayloadBytes: 256).AsTask());

        Assert.True(input.CanRead);
        Assert.True(output.CanWrite);
        Assert.Equal(0, output.Length);
    }

    private static async Task<MemoryStream> FramesAsync(params string[] payloads)
    {
        var stream = new MemoryStream();
        using (var messages = new UiLspMessageStream(Stream.Null, stream))
        {
            foreach (string payload in payloads)
                await messages.WriteFrameAsync(Encoding.UTF8.GetBytes(payload));
        }
        stream.Position = 0;
        return stream;
    }

    private static async Task<JsonElement[]> ResponsesAsync(MemoryStream output)
    {
        output.Position = 0;
        using var messages = new UiLspMessageStream(output, Stream.Null);
        var responses = new List<JsonElement>();
        byte[]? payload;
        while ((payload = await messages.ReadFrameAsync()) != null)
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            responses.Add(document.RootElement.Clone());
        }
        return responses.ToArray();
    }

    private static string InitializeParams()
    {
        var context = new UiBindingContext(new UiSymbolId("Author.Mod", "storage"))
            .DeclareElement("Items")
            .DeclareRole("Item");
        using JsonDocument metadata = JsonDocument.Parse(
            UiBindingContextMetadataWire.Serialize(
                UiBindingContextMetadataExporter.Export(context)));
        return JsonSerializer.Serialize(new
        {
            initializationOptions = new
            {
                bindingMetadata = metadata.RootElement.Clone()
            }
        });
    }

    private static string Request(int id, string method, string parameters)
        => FormattableString.Invariant(
            $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":{JsonSerializer.Serialize(method)},\"params\":{parameters}}}");

    private static string Notification(string method, string parameters)
        => FormattableString.Invariant(
            $"{{\"jsonrpc\":\"2.0\",\"method\":{JsonSerializer.Serialize(method)},\"params\":{parameters}}}");

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
