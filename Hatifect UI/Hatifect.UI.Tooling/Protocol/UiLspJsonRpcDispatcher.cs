using System;
using System.Buffers;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hatifect.UI.Tooling.Protocol;

internal static class UiJsonRpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}

internal sealed class UiJsonRpcRequest
{
    internal UiJsonRpcRequest(
        string method,
        bool isNotification,
        JsonElement? id,
        JsonElement? parameters)
    {
        Method = method;
        IsNotification = isNotification;
        Id = id;
        Parameters = parameters;
    }

    public string Method { get; }
    public bool IsNotification { get; }
    public JsonElement? Id { get; }
    public JsonElement? Parameters { get; }
}

internal readonly struct UiJsonRpcDispatchResult
{
    private UiJsonRpcDispatchResult(
        bool isError,
        JsonElement? result,
        int errorCode,
        string? errorMessage,
        JsonElement? errorData)
    {
        IsError = isError;
        Result = result?.Clone();
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        ErrorData = errorData?.Clone();
    }

    public bool IsError { get; }
    public JsonElement? Result { get; }
    public int ErrorCode { get; }
    public string? ErrorMessage { get; }
    public JsonElement? ErrorData { get; }

    public static UiJsonRpcDispatchResult Success(JsonElement? result = null)
        => new(false, result, 0, null, null);

    public static UiJsonRpcDispatchResult Error(int code, string message, JsonElement? data = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("A JSON-RPC error message is required.", nameof(message));
        return new UiJsonRpcDispatchResult(true, null, code, message, data);
    }
}

internal delegate ValueTask<UiJsonRpcDispatchResult> UiJsonRpcRequestHandler(
    UiJsonRpcRequest request,
    CancellationToken cancellationToken);

/// <summary>
/// Processes one LSP JSON-RPC request or notification at a time. Product methods, scheduling, and
/// server-process lifecycle are deliberately supplied by the caller rather than frozen here.
/// </summary>
internal sealed class UiLspJsonRpcDispatcher
{
    private const int MaximumJsonDepth = 64;
    private const int MinimumResponseBufferBytes = 256;
    private readonly UiLspMessageStream _messages;
    private readonly UiJsonRpcRequestHandler _handler;

    public UiLspJsonRpcDispatcher(
        UiLspMessageStream messages,
        UiJsonRpcRequestHandler handler)
    {
        _messages = messages ?? throw new ArgumentNullException(nameof(messages));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        if (messages.MaximumPayloadBytes < MinimumResponseBufferBytes)
            throw new ArgumentException(
                $"JSON-RPC dispatch requires an LSP payload limit of at least {MinimumResponseBufferBytes} bytes.",
                nameof(messages));
    }

    /// <summary>Returns false only for clean EOF before another framed message.</summary>
    public async ValueTask<bool> DispatchNextAsync(CancellationToken cancellationToken = default)
    {
        byte[]? payload = await _messages.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        if (payload == null) return false;

        UiJsonRpcRequest request;
        try
        {
            if (!TryParseRequest(payload, _messages.MaximumPayloadBytes, out request))
            {
                await WriteErrorAsync(
                    id: null,
                    UiJsonRpcErrorCodes.InvalidRequest,
                    "Invalid Request",
                    cancellationToken).ConfigureAwait(false);
                return true;
            }
        }
        catch (JsonException)
        {
            await WriteErrorAsync(
                id: null,
                UiJsonRpcErrorCodes.ParseError,
                "Parse error",
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        UiJsonRpcDispatchResult result;
        try
        {
            result = await _handler(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (!request.IsNotification)
        {
            result = UiJsonRpcDispatchResult.Error(
                UiJsonRpcErrorCodes.InternalError,
                "Internal error");
        }

        if (request.IsNotification) return true;
        await WriteResponseAsync(request.Id, result, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static bool TryParseRequest(byte[] payload, int maximumPayloadBytes, out UiJsonRpcRequest request)
    {
        using JsonDocument document = JsonDocument.Parse(
            payload,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumJsonDepth
            });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            request = null!;
            return false;
        }

        JsonElement version = default;
        JsonElement method = default;
        JsonElement id = default;
        JsonElement parameters = default;
        bool hasVersion = false;
        bool hasMethod = false;
        bool hasId = false;
        bool hasParameters = false;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "jsonrpc":
                    if (hasVersion) return Invalid(out request);
                    hasVersion = true;
                    version = property.Value;
                    break;
                case "method":
                    if (hasMethod) return Invalid(out request);
                    hasMethod = true;
                    method = property.Value;
                    break;
                case "id":
                    if (hasId) return Invalid(out request);
                    hasId = true;
                    id = property.Value;
                    break;
                case "params":
                    if (hasParameters) return Invalid(out request);
                    hasParameters = true;
                    parameters = property.Value;
                    break;
            }
        }

        if (!hasVersion || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
            return Invalid(out request);
        if (!hasMethod || method.ValueKind != JsonValueKind.String)
            return Invalid(out request);
        if (hasId && !IsValidLspRequestId(id, maximumPayloadBytes))
            return Invalid(out request);
        if (hasParameters &&
            parameters.ValueKind != JsonValueKind.Object &&
            parameters.ValueKind != JsonValueKind.Array)
            return Invalid(out request);

        request = new UiJsonRpcRequest(
            method.GetString()!,
            isNotification: !hasId,
            hasId ? id.Clone() : null,
            hasParameters ? parameters.Clone() : null);
        return true;
    }

    internal static bool IsValidLspRequestId(JsonElement id, int maximumPayloadBytes)
    {
        if (id.ValueKind == JsonValueKind.Number) return id.TryGetInt32(out _);
        if (id.ValueKind != JsonValueKind.String) return false;
        // Reject before dispatch: even a null result or an error must be able to echo the ID.
        int maximumIdBytes = Math.Min(4096, maximumPayloadBytes / 4);
        return id.GetString()!.Length <= maximumIdBytes &&
            JsonSerializer.SerializeToUtf8Bytes(id).Length <= maximumIdBytes;
    }

    private static bool Invalid(out UiJsonRpcRequest request)
    {
        request = null!;
        return false;
    }

    private ValueTask WriteErrorAsync(
        JsonElement? id,
        int code,
        string message,
        CancellationToken cancellationToken)
        => WriteResponseAsync(
            id,
            UiJsonRpcDispatchResult.Error(code, message),
            cancellationToken);

    private async ValueTask WriteResponseAsync(
        JsonElement? id,
        UiJsonRpcDispatchResult result,
        CancellationToken cancellationToken)
    {
        var buffer = new UiBoundedBufferWriter(_messages.MaximumPayloadBytes);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            if (id is { } requestId) requestId.WriteTo(writer);
            else writer.WriteNullValue();

            if (result.IsError)
            {
                writer.WritePropertyName("error");
                writer.WriteStartObject();
                writer.WriteNumber("code", result.ErrorCode);
                writer.WriteString("message", result.ErrorMessage);
                if (result.ErrorData is { } data)
                {
                    writer.WritePropertyName("data");
                    data.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            else
            {
                writer.WritePropertyName("result");
                if (result.Result is { } value) value.WriteTo(writer);
                else writer.WriteNullValue();
            }

            writer.WriteEndObject();
            writer.Flush();
        }
        await _messages.WriteFrameAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    internal sealed class UiBoundedBufferWriter : IBufferWriter<byte>
    {
        private readonly int _maximumLength;
        private byte[] _buffer = Array.Empty<byte>();
        private int _written;

        public UiBoundedBufferWriter(int maximumLength)
            => _maximumLength = maximumLength;

        public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

        public void Advance(int count)
        {
            if (count < 0 || count > _buffer.Length - _written)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (count > _maximumLength - _written)
                throw new InvalidDataException(
                    $"The JSON-RPC response exceeds the {_maximumLength} byte LSP payload limit.");
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (sizeHint < 0) throw new ArgumentOutOfRangeException(nameof(sizeHint));
            if (sizeHint == 0) sizeHint = 1;
            // Utf8JsonWriter reserves up to three UTF-8 bytes per escaped UTF-16 unit and a
            // 4 KiB growth segment. Advance still enforces the actual serialized byte limit.
            int maximumCapacity = (int)Math.Min(int.MaxValue, (long)_maximumLength * 3 + 4096);
            if (sizeHint > maximumCapacity - _written)
                throw new InvalidDataException(
                    $"The JSON-RPC response exceeds the {_maximumLength} byte LSP payload limit.");

            int required = _written + sizeHint;
            if (required <= _buffer.Length) return;
            int doubled = _buffer.Length == 0
                ? MinimumResponseBufferBytes
                : _buffer.Length <= maximumCapacity / 2 ? _buffer.Length * 2 : maximumCapacity;
            int capacity = Math.Min(maximumCapacity, Math.Max(required, doubled));
            Array.Resize(ref _buffer, capacity);
        }
    }
}
