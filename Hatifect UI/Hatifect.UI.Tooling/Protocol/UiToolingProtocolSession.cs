using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hatifect.UI.Language.Diagnostics;
using Hatifect.UI.Language.Text;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Editor;
using Hatifect.UI.Tooling.Metadata;

namespace Hatifect.UI.Tooling.Protocol;

internal enum UiToolingProtocolState
{
    Created,
    AwaitingInitialized,
    Active,
    Shutdown,
    Exited
}

/// <summary>
/// Internal/provisional product protocol v0. The embedding owner supplies scheduling and the process
/// loop; this session serializes lifecycle/workspace mutations and owns only bounded editor state.
/// </summary>
internal sealed class UiToolingProtocolSession
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly UiEditorWorkspace _workspace;
    private UiBindingContext? _bindingContext;
    private UiToolingProtocolState _state;

    public UiToolingProtocolSession(
        UiSemanticCatalog? catalog = null,
        int documentCapacity = UiEditorWorkspace.DefaultCapacity,
        int maximumSourceLength = UiEditorWorkspace.DefaultMaximumSourceLength)
        => _workspace = new UiEditorWorkspace(catalog, documentCapacity, maximumSourceLength);

    public UiToolingProtocolState State => _state;
    public bool ExitRequested => _state == UiToolingProtocolState.Exited;
    public int DocumentCount => _workspace.Count;

    public bool TryGetDocument(string uri, out UiEditorDocumentSnapshot? snapshot)
        => _workspace.TryGet(uri, out snapshot);

    public async ValueTask<UiJsonRpcDispatchResult> HandleAsync(
        UiJsonRpcRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Handle(request);
        }
        finally
        {
            _gate.Release();
        }
    }

    private UiJsonRpcDispatchResult Handle(UiJsonRpcRequest request)
    {
        if (_state == UiToolingProtocolState.Exited)
            return Error(UiJsonRpcErrorCodes.InvalidRequest, "The tooling session has exited.");

        return request.Method switch
        {
            "initialize" => Initialize(request),
            "initialized" => Initialized(request),
            "shutdown" => Shutdown(request),
            "exit" => Exit(request),
            "textDocument/didOpen" => WithActive(request, DidOpen),
            "textDocument/didChange" => WithActive(request, DidChange),
            "textDocument/didClose" => WithActive(request, DidClose),
            "textDocument/diagnostic" => WithActive(request, Diagnostic),
            "textDocument/formatting" => WithActive(request, Formatting),
            _ => Error(UiJsonRpcErrorCodes.MethodNotFound, "Method not found")
        };
    }

    private UiJsonRpcDispatchResult Initialize(UiJsonRpcRequest request)
    {
        if (request.IsNotification)
            return Error(UiJsonRpcErrorCodes.InvalidRequest, "initialize must be a request.");
        if (_state != UiToolingProtocolState.Created)
            return Error(UiJsonRpcErrorCodes.InvalidRequest, "initialize was already handled.");
        try
        {
            JsonElement parameters = RequiredObject(request.Parameters, "initialize params");
            JsonElement options = RequiredObject(Property(parameters, "initializationOptions"), "initializationOptions");
            JsonElement metadataValue = Property(options, "bindingMetadata");
            byte[] metadataJson = Encoding.UTF8.GetBytes(metadataValue.GetRawText());
            UiBindingContextMetadata metadata = UiBindingContextMetadataWire.Deserialize(metadataJson);
            UiBindingContext bindingContext = UiBindingContextMetadataWire.CreateBindingContext(metadata);

            _bindingContext = bindingContext;
            _state = UiToolingProtocolState.AwaitingInitialized;
            return Success(new
            {
                capabilities = new
                {
                    textDocumentSync = new { openClose = true, change = 1 },
                    diagnosticProvider = new
                    {
                        identifier = "hatifect-ui",
                        interFileDependencies = false,
                        workspaceDiagnostics = false
                    },
                    documentFormattingProvider = true
                },
                serverInfo = new { name = "Hatifect UI Tooling", version = "0" }
            });
        }
        catch (Exception error) when (IsInvalidInput(error))
        {
            return InvalidParams(error.Message);
        }
    }

    private UiJsonRpcDispatchResult Initialized(UiJsonRpcRequest request)
    {
        if (!request.IsNotification)
            return Error(UiJsonRpcErrorCodes.InvalidRequest, "initialized must be a notification.");
        if (_state != UiToolingProtocolState.AwaitingInitialized)
            return Error(UiJsonRpcErrorCodes.InvalidRequest, "initialized is not valid in the current lifecycle state.");
        _state = UiToolingProtocolState.Active;
        return UiJsonRpcDispatchResult.Success();
    }

    private UiJsonRpcDispatchResult Shutdown(UiJsonRpcRequest request)
    {
        if (request.IsNotification)
            return Error(UiJsonRpcErrorCodes.InvalidRequest, "shutdown must be a request.");
        if (_state is not (UiToolingProtocolState.AwaitingInitialized or UiToolingProtocolState.Active))
            return Error(UiJsonRpcErrorCodes.InvalidRequest, "shutdown is not valid in the current lifecycle state.");
        _workspace.Clear();
        _state = UiToolingProtocolState.Shutdown;
        return UiJsonRpcDispatchResult.Success();
    }

    private UiJsonRpcDispatchResult Exit(UiJsonRpcRequest request)
    {
        if (!request.IsNotification)
            return Error(UiJsonRpcErrorCodes.InvalidRequest, "exit must be a notification.");
        _workspace.Clear();
        _bindingContext = null;
        _state = UiToolingProtocolState.Exited;
        return UiJsonRpcDispatchResult.Success();
    }

    private UiJsonRpcDispatchResult WithActive(
        UiJsonRpcRequest request,
        Func<UiJsonRpcRequest, UiJsonRpcDispatchResult> operation)
    {
        if (_state != UiToolingProtocolState.Active || _bindingContext is null)
            return Error(UiJsonRpcErrorCodes.InvalidRequest, "The tooling session is not initialized and active.");
        try
        {
            return operation(request);
        }
        catch (Exception error) when (IsInvalidInput(error))
        {
            return InvalidParams(error.Message);
        }
    }

    private UiJsonRpcDispatchResult DidOpen(UiJsonRpcRequest request)
    {
        RequireNotification(request, "textDocument/didOpen");
        JsonElement textDocument = TextDocument(request.Parameters);
        string uri = RequiredString(textDocument, "uri");
        int version = RequiredNonNegativeInt32(textDocument, "version");
        string text = RequiredString(textDocument, "text", allowEmpty: true);
        if (_workspace.TryGet(uri, out _))
            throw new InvalidDataException($"Editor document '{uri}' is already open.");
        _workspace.Update(uri, version, text, _bindingContext!);
        return UiJsonRpcDispatchResult.Success();
    }

    private UiJsonRpcDispatchResult DidChange(UiJsonRpcRequest request)
    {
        RequireNotification(request, "textDocument/didChange");
        JsonElement parameters = RequiredObject(request.Parameters, "didChange params");
        JsonElement textDocument = RequiredObject(Property(parameters, "textDocument"), "textDocument");
        string uri = RequiredString(textDocument, "uri");
        int version = RequiredNonNegativeInt32(textDocument, "version");
        if (!_workspace.TryGet(uri, out UiEditorDocumentSnapshot? current) || current is null)
            throw new InvalidDataException($"Editor document '{uri}' is not open.");
        if (version <= current.Version)
            throw new InvalidDataException(
                $"Editor document '{uri}' requires a version newer than {current.Version}.");
        JsonElement changes = Property(parameters, "contentChanges");
        if (changes.ValueKind != JsonValueKind.Array || changes.GetArrayLength() != 1)
            throw new InvalidDataException("v0 didChange requires exactly one full-document change.");
        JsonElement change = RequiredObject(changes[0], "content change");
        foreach (JsonProperty property in change.EnumerateObject())
            if (property.Name != "text")
                throw new InvalidDataException("v0 didChange accepts only a full-document text replacement.");
        string text = RequiredString(change, "text", allowEmpty: true);
        _workspace.Update(uri, version, text, _bindingContext!);
        return UiJsonRpcDispatchResult.Success();
    }

    private UiJsonRpcDispatchResult DidClose(UiJsonRpcRequest request)
    {
        RequireNotification(request, "textDocument/didClose");
        string uri = RequiredString(TextDocument(request.Parameters), "uri");
        _workspace.Remove(uri);
        return UiJsonRpcDispatchResult.Success();
    }

    private UiJsonRpcDispatchResult Diagnostic(UiJsonRpcRequest request)
    {
        RequireRequest(request, "textDocument/diagnostic");
        string uri = RequiredString(TextDocument(request.Parameters), "uri");
        UiEditorDocumentSnapshot snapshot = OpenDocument(uri);
        var items = snapshot.Diagnostics.Select(diagnostic => new
        {
            range = Range(snapshot.Source, diagnostic.Span),
            severity = diagnostic.Severity switch
            {
                UiDiagnosticSeverity.Error => 1,
                UiDiagnosticSeverity.Warning => 2,
                _ => 3
            },
            code = diagnostic.Id,
            source = "hatifect-ui",
            message = diagnostic.Message
        }).ToArray();
        return Success(new { kind = "full", items });
    }

    private UiJsonRpcDispatchResult Formatting(UiJsonRpcRequest request)
    {
        RequireRequest(request, "textDocument/formatting");
        string uri = RequiredString(TextDocument(request.Parameters), "uri");
        UiEditorDocumentSnapshot snapshot = OpenDocument(uri);
        if (!snapshot.Format.CanApply || !snapshot.Format.Changed)
            return Success(Array.Empty<object>());
        return Success(new[]
        {
            new
            {
                range = new
                {
                    start = new { line = 0, character = 0 },
                    end = PositionAt(snapshot.Source, snapshot.Source.Length)
                },
                newText = snapshot.Format.Text
            }
        });
    }

    private UiEditorDocumentSnapshot OpenDocument(string uri)
        => _workspace.TryGet(uri, out UiEditorDocumentSnapshot? snapshot) && snapshot is not null
            ? snapshot
            : throw new InvalidDataException($"Editor document '{uri}' is not open.");

    private static object Range(string source, UiTextSpan span)
    {
        int start = Math.Clamp(span.Start, 0, source.Length);
        int end = Math.Clamp(span.End, start, source.Length);
        int line = Math.Max(0, span.Line);
        int character = Math.Max(0, span.Column);
        object startPosition = new { line, character };
        for (int index = start; index < end; index++)
        {
            if (source[index] == '\n')
            {
                line++;
                character = 0;
            }
            else if (source[index] != '\r')
            {
                character++;
            }
        }
        return new { start = startPosition, end = new { line, character } };
    }

    private static object PositionAt(string source, int offset)
    {
        int line = 0;
        int character = 0;
        for (int index = 0; index < offset; index++)
        {
            if (source[index] == '\n')
            {
                line++;
                character = 0;
            }
            else if (source[index] != '\r')
            {
                character++;
            }
        }
        return new { line, character };
    }

    private static JsonElement TextDocument(JsonElement? parameters)
    {
        JsonElement value = RequiredObject(parameters, "text document params");
        return RequiredObject(Property(value, "textDocument"), "textDocument");
    }

    private static JsonElement RequiredObject(JsonElement? value, string description)
        => value is { ValueKind: JsonValueKind.Object } item
            ? item
            : throw new InvalidDataException($"{description} must be an object.");

    private static JsonElement Property(JsonElement value, string name)
    {
        JsonElement result = default;
        bool found = false;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Name != name) continue;
            if (found) throw new InvalidDataException($"Property '{name}' is duplicated.");
            found = true;
            result = property.Value;
        }
        return found
            ? result
            : throw new InvalidDataException($"Required property '{name}' is missing.");
    }

    private static string RequiredString(JsonElement value, string name, bool allowEmpty = false)
    {
        JsonElement property = Property(value, name);
        if (property.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Property '{name}' must be a string.");
        string result = property.GetString()!;
        if (!allowEmpty && string.IsNullOrWhiteSpace(result))
            throw new InvalidDataException($"Property '{name}' must not be empty.");
        return result;
    }

    private static int RequiredNonNegativeInt32(JsonElement value, string name)
    {
        JsonElement property = Property(value, name);
        if (!property.TryGetInt32(out int result) || result < 0)
            throw new InvalidDataException($"Property '{name}' must be a non-negative 32-bit integer.");
        return result;
    }

    private static void RequireNotification(UiJsonRpcRequest request, string method)
    {
        if (!request.IsNotification)
            throw new InvalidDataException($"{method} must be a notification.");
    }

    private static void RequireRequest(UiJsonRpcRequest request, string method)
    {
        if (request.IsNotification)
            throw new InvalidDataException($"{method} must be a request.");
    }

    private static bool IsInvalidInput(Exception error)
        => error is InvalidDataException or JsonException or FormatException or ArgumentException or
            InvalidOperationException;

    private static UiJsonRpcDispatchResult InvalidParams(string message)
        => Error(UiJsonRpcErrorCodes.InvalidParams, message);

    private static UiJsonRpcDispatchResult Error(int code, string message)
        => UiJsonRpcDispatchResult.Error(code, message);

    private static UiJsonRpcDispatchResult Success<T>(T value)
        => UiJsonRpcDispatchResult.Success(JsonSerializer.SerializeToElement(value));
}
