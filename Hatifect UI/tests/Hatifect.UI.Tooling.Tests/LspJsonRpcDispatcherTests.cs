using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hatifect.UI.Tooling.Protocol;
using Xunit;

namespace Hatifect.UI.Tooling.Tests;

public sealed class LspJsonRpcDispatcherTests
{
    [Fact]
    public async Task RequestPreservesIdAndDispatchesStructuredParameters()
    {
        using DispatcherHarness harness = await DispatcherHarness.CreateAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":\"request-7\",\"method\":\"hatifect/inspect\",\"params\":{\"source\":\"Storage\"}}",
            (request, _) =>
            {
                Assert.False(request.IsNotification);
                Assert.Equal("request-7", request.Id?.GetString());
                Assert.Equal("hatifect/inspect", request.Method);
                Assert.Equal("Storage", request.Parameters?.GetProperty("source").GetString());
                return ValueTask.FromResult(UiJsonRpcDispatchResult.Success(Json("{\"found\":true}")));
            });

        Assert.True(await harness.Dispatcher.DispatchNextAsync());
        Assert.False(await harness.Dispatcher.DispatchNextAsync());

        using JsonDocument response = await harness.ReadResponseAsync();
        Assert.Equal("2.0", response.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal("request-7", response.RootElement.GetProperty("id").GetString());
        Assert.True(response.RootElement.GetProperty("result").GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task ValidNotificationDispatchesWithoutResponse()
    {
        int calls = 0;
        using DispatcherHarness harness = await DispatcherHarness.CreateAsync(
            "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{}}",
            (request, _) =>
            {
                calls++;
                Assert.True(request.IsNotification);
                Assert.Null(request.Id);
                return ValueTask.FromResult(UiJsonRpcDispatchResult.Success());
            });

        Assert.True(await harness.Dispatcher.DispatchNextAsync());

        Assert.Equal(1, calls);
        Assert.Equal(0, harness.Output.Length);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"jsonrpc\":\"1.0\",\"id\":1,\"method\":\"test\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"test\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1.5,\"method\":\"test\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":2147483648,\"method\":\"test\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":4}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"test\",\"params\":true}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"id\":2,\"method\":\"test\"}")]
    public async Task InvalidLspRequestReturnsStandardErrorWithoutDispatch(string payload)
    {
        using DispatcherHarness harness = await DispatcherHarness.CreateAsync(
            payload,
            (_, _) => throw new InvalidOperationException("Invalid requests must not dispatch."));

        Assert.True(await harness.Dispatcher.DispatchNextAsync());

        using JsonDocument response = await harness.ReadResponseAsync();
        Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("id").ValueKind);
        Assert.Equal(
            UiJsonRpcErrorCodes.InvalidRequest,
            response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData("{\"jsonrpc\":")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"test\"} trailing")]
    public async Task MalformedJsonReturnsParseError(string payload)
    {
        using DispatcherHarness harness = await DispatcherHarness.CreateAsync(
            payload,
            (_, _) => throw new InvalidOperationException("Malformed JSON must not dispatch."));

        Assert.True(await harness.Dispatcher.DispatchNextAsync());

        using JsonDocument response = await harness.ReadResponseAsync();
        Assert.Equal(
            UiJsonRpcErrorCodes.ParseError,
            response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task HandlerErrorsAndExceptionsReturnProtocolErrorsForRequests()
    {
        using DispatcherHarness expected = await DispatcherHarness.CreateAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"missing\"}",
            (_, _) => ValueTask.FromResult(UiJsonRpcDispatchResult.Error(
                UiJsonRpcErrorCodes.MethodNotFound,
                "Method not found",
                Json("{\"method\":\"missing\"}"))));
        Assert.True(await expected.Dispatcher.DispatchNextAsync());
        using JsonDocument expectedResponse = await expected.ReadResponseAsync();
        JsonElement expectedError = expectedResponse.RootElement.GetProperty("error");
        Assert.Equal(UiJsonRpcErrorCodes.MethodNotFound, expectedError.GetProperty("code").GetInt32());
        Assert.Equal("missing", expectedError.GetProperty("data").GetProperty("method").GetString());
        Assert.Equal(9, expectedResponse.RootElement.GetProperty("id").GetInt32());

        using DispatcherHarness unexpected = await DispatcherHarness.CreateAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"broken\"}",
            (_, _) => ValueTask.FromException<UiJsonRpcDispatchResult>(
                new InvalidOperationException("private implementation detail")));
        Assert.True(await unexpected.Dispatcher.DispatchNextAsync());
        using JsonDocument unexpectedResponse = await unexpected.ReadResponseAsync();
        JsonElement unexpectedError = unexpectedResponse.RootElement.GetProperty("error");
        Assert.Equal(UiJsonRpcErrorCodes.InternalError, unexpectedError.GetProperty("code").GetInt32());
        Assert.Equal("Internal error", unexpectedError.GetProperty("message").GetString());
        Assert.DoesNotContain("private", unexpectedResponse.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotificationHandlerFailurePropagatesBecauseNoErrorResponseIsLegal()
    {
        using DispatcherHarness harness = await DispatcherHarness.CreateAsync(
            "{\"jsonrpc\":\"2.0\",\"method\":\"broken-notification\"}",
            (_, _) => ValueTask.FromException<UiJsonRpcDispatchResult>(new IOException("handler failed")));

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => harness.Dispatcher.DispatchNextAsync().AsTask());

        Assert.Equal("handler failed", error.Message);
        Assert.Equal(0, harness.Output.Length);
    }

    [Fact]
    public async Task OversizedResponseFailsBeforeWritingAnyFrame()
    {
        using DispatcherHarness harness = await DispatcherHarness.CreateAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"large\"}",
            (_, _) => ValueTask.FromResult(UiJsonRpcDispatchResult.Success(
                Json($"\"{new string('x', 512)}\""))),
            maximumPayloadBytes: 256);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => harness.Dispatcher.DispatchNextAsync().AsTask());

        Assert.Equal(0, harness.Output.Length);
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToInternalError()
    {
        using var cancellation = new CancellationTokenSource();
        using DispatcherHarness harness = await DispatcherHarness.CreateAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"cancel\"}",
            (_, token) =>
            {
                cancellation.Cancel();
                return ValueTask.FromCanceled<UiJsonRpcDispatchResult>(token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Dispatcher.DispatchNextAsync(cancellation.Token).AsTask());

        Assert.Equal(0, harness.Output.Length);
    }

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class DispatcherHarness : IDisposable
    {
        private readonly UiLspMessageStream _messages;

        private DispatcherHarness(
            MemoryStream input,
            MemoryStream output,
            UiLspMessageStream messages,
            UiJsonRpcRequestHandler handler)
        {
            Input = input;
            Output = output;
            _messages = messages;
            Dispatcher = new UiLspJsonRpcDispatcher(messages, handler);
        }

        public MemoryStream Input { get; }
        public MemoryStream Output { get; }
        public UiLspJsonRpcDispatcher Dispatcher { get; }

        public static async Task<DispatcherHarness> CreateAsync(
            string payload,
            UiJsonRpcRequestHandler handler,
            int maximumPayloadBytes = UiLspMessageStream.DefaultMaximumPayloadBytes)
        {
            var input = new MemoryStream();
            using (var writer = new UiLspMessageStream(
                Stream.Null,
                input,
                maximumPayloadBytes: Math.Max(maximumPayloadBytes, Encoding.UTF8.GetByteCount(payload))))
            {
                await writer.WriteFrameAsync(Encoding.UTF8.GetBytes(payload));
            }
            input.Position = 0;
            var output = new MemoryStream();
            var messages = new UiLspMessageStream(
                input,
                output,
                maximumPayloadBytes: maximumPayloadBytes);
            return new DispatcherHarness(input, output, messages, handler);
        }

        public async Task<JsonDocument> ReadResponseAsync()
        {
            Output.Position = 0;
            using var reader = new UiLspMessageStream(Output, Stream.Null);
            byte[] response = Assert.IsType<byte[]>(await reader.ReadFrameAsync());
            Assert.Null(await reader.ReadFrameAsync());
            return JsonDocument.Parse(response);
        }

        public void Dispose()
        {
            _messages.Dispose();
            Input.Dispose();
            Output.Dispose();
        }
    }
}
