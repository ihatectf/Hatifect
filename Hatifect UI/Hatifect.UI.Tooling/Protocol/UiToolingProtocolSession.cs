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
internal sealed partial class UiToolingProtocolSession
{
    private const int ProtocolVersion = 0;
    private static readonly string[] ProtocolCapabilities =
    {
        "bindingMetadata.v1", "bindingMetadata.v2", "bindingUpdates",
        "diagnostics", "definitions", "references", "compilation"
    };

    private readonly IUiToolingPlanner? _planner;
    private readonly string[] _protocolCapabilities;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly UiEditorWorkspace _workspace;
    private readonly UiCompiler _authoringCompiler;
    private UiBindingContext? _bindingContext;
    private UiToolingProtocolState _state;
    private bool _hierarchicalSymbols;
    private bool _versionedCodeActions;
    private int _maximumPayloadBytes = UiLspMessageStream.DefaultMaximumPayloadBytes;
    private int _maximumResultBytes = UiLspMessageStream.DefaultMaximumPayloadBytes - 128;

    public UiToolingProtocolSession(
        UiSemanticCatalog? catalog = null,
        int documentCapacity = UiEditorWorkspace.DefaultCapacity,
        int maximumSourceLength = UiEditorWorkspace.DefaultMaximumSourceLength,
        IUiToolingPlanner? planner = null)
    {
        _planner = planner;
        _protocolCapabilities = planner is null ? ProtocolCapabilities : ProtocolCapabilities.Append("plannerTrace").ToArray();
        catalog ??= UiSemanticCatalog.CreateFoundation();
        _workspace = new UiEditorWorkspace(catalog, documentCapacity, maximumSourceLength);
        _authoringCompiler = new UiCompiler(catalog);
    }

    public UiToolingProtocolState State => _state;
    public bool ExitRequested => _state == UiToolingProtocolState.Exited;
    public int DocumentCount => _workspace.Count;

    internal void ConfigurePayloadLimit(int maximumPayloadBytes)
    {
        if (maximumPayloadBytes < 256) throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        _maximumPayloadBytes = maximumPayloadBytes;
    }

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
        _maximumResultBytes = _maximumPayloadBytes - 132;
        if (request.Id is { } candidateId &&
            !UiLspJsonRpcDispatcher.IsValidLspRequestId(candidateId, _maximumPayloadBytes))
            return Error(UiJsonRpcErrorCodes.InvalidRequest, "Invalid Request");
        // Reserve the JSON-RPC envelope and the ID's actual escaped UTF-8 representation.
        _maximumResultBytes = _maximumPayloadBytes - 128 -
            (request.Id is { } id ? JsonSerializer.SerializeToUtf8Bytes(id).Length : 4);
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
            "workspace/diagnostic" => WithActive(request, WorkspaceDiagnostic),
            "hatifect/compilation" => WithActive(request, Compilation),
            "hatifect/plannerTrace" => WithActive(request, PlannerTrace),
            "hatifect/updateBindings" => WithActive(request, UpdateBindings),
            "textDocument/formatting" => WithActive(request, Formatting),
            "textDocument/completion" => WithActive(request, Completion),
            "textDocument/hover" => WithActive(request, Hover),
            "textDocument/documentSymbol" => WithActive(request, DocumentSymbols),
            "textDocument/foldingRange" => WithActive(request, FoldingRanges),
            "textDocument/semanticTokens/full" => WithActive(request, SemanticTokens),
            "textDocument/references" => WithActive(request, References),
            "textDocument/documentHighlight" => WithActive(request, DocumentHighlights),
            "textDocument/definition" => WithActive(request, Definition),
            "textDocument/codeAction" => WithActive(request, CodeActions),
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
            ValidateProtocolRequirements(options);
            BindingConfiguration configuration = ReadBindingConfiguration(options);
            int bindingRevision = OptionalProperty(options, "bindingRevision") != null
                ? RequiredNonNegativeInt32(options, "bindingRevision") : 0;
            bool hierarchicalSymbols = NestedProperty(parameters, "capabilities", "textDocument",
                "documentSymbol", "hierarchicalDocumentSymbolSupport") is { ValueKind: JsonValueKind.True };
            bool versionedCodeActions = NestedProperty(parameters, "capabilities", "workspace", "workspaceEdit",
                "documentChanges") is { ValueKind: JsonValueKind.True }
                && NestedProperty(parameters, "capabilities", "textDocument", "codeAction", "codeActionLiteralSupport")
                    is { ValueKind: JsonValueKind.Object };

            UiJsonRpcDispatchResult response = Success(new
            {
                capabilities = new
                {
                    experimental = new
                    {
                        hatifectUi = new
                        {
                            protocolVersion = ProtocolVersion,
                            capabilities = _protocolCapabilities,
                            bindingMetadataVersions = new[] { 1, 2 }
                        }
                    },
                    positionEncoding = "utf-16",
                    textDocumentSync = new { openClose = true, change = 2 },
                    diagnosticProvider = new
                    {
                        identifier = "hatifect-ui",
                        interFileDependencies = false,
                        workspaceDiagnostics = true
                    },
                    documentFormattingProvider = true,
                    hoverProvider = true,
                    documentSymbolProvider = true,
                    foldingRangeProvider = true,
                    referencesProvider = true,
                    documentHighlightProvider = true,
                    definitionProvider = true,
                    codeActionProvider = versionedCodeActions,
                    semanticTokensProvider = new
                    {
                        legend = new { tokenTypes = UiEditorAnalysis.TokenTypes, tokenModifiers = new[] { "declaration" } },
                        full = true, range = false
                    },
                    completionProvider = new { triggerCharacters = new[] { ".", "@", "=" }, resolveProvider = false }
                },
                serverInfo = new { name = "Hatifect UI Tooling", version = "0" }
            });
            if (response.IsError) return response;
            _bindingContext = configuration.Default;
            _documentBindings = configuration.Documents;
            _declarations = configuration.Declarations;
            _bindingRevision = bindingRevision;
            _hierarchicalSymbols = hierarchicalSymbols;
            _versionedCodeActions = versionedCodeActions;
            _state = UiToolingProtocolState.AwaitingInitialized;
            return response;
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

    private void ValidateProtocolRequirements(JsonElement options)
    {
        if (OptionalProperty(options, "protocolVersion") is not null
            && RequiredNonNegativeInt32(options, "protocolVersion") != ProtocolVersion)
        {
            throw new InvalidDataException($"Unsupported tooling protocol version. Supported version: {ProtocolVersion}.");
        }

        if (OptionalProperty(options, "requiredCapabilities") is not { } required)
        {
            return;
        }
        if (required.ValueKind != JsonValueKind.Array || required.GetArrayLength() > 32)
        {
            throw new InvalidDataException("requiredCapabilities must be an array of at most 32 capability names.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement value in required.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > 128)
            {
                throw new InvalidDataException("A required capability must be a non-empty string of at most 128 characters.");
            }
            string capability = value.GetString()!;
            if (!seen.Add(capability))
            {
                throw new InvalidDataException($"Required capability '{capability}' is duplicated.");
            }
            if (!_protocolCapabilities.Contains(capability, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"Unsupported tooling capability '{capability}'.");
            }
        }
    }

    private UiJsonRpcDispatchResult Shutdown(UiJsonRpcRequest request)
    {
        if (request.IsNotification)
            return Error(UiJsonRpcErrorCodes.InvalidRequest, "shutdown must be a request.");
        if (_state is not (UiToolingProtocolState.AwaitingInitialized or UiToolingProtocolState.Active))
            return Error(UiJsonRpcErrorCodes.InvalidRequest, "shutdown is not valid in the current lifecycle state.");
        _workspace.Clear();
        _declarations.Clear();
        _documentBindings.Clear();
        _bindingContext = null;
        _state = UiToolingProtocolState.Shutdown;
        return UiJsonRpcDispatchResult.Success();
    }

    private UiJsonRpcDispatchResult Exit(UiJsonRpcRequest request)
    {
        if (!request.IsNotification)
            return Error(UiJsonRpcErrorCodes.InvalidRequest, "exit must be a notification.");
        _workspace.Clear();
        _declarations.Clear();
        _documentBindings.Clear();
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
        if (_workspace.Count >= _workspace.Capacity)
            throw new InvalidDataException($"The session already has {_workspace.Capacity} open documents. Close a document first.");
        _workspace.Update(uri, version, text, ContextFor(uri));
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
        if (changes.ValueKind != JsonValueKind.Array || changes.GetArrayLength() is < 1 or > 128)
            throw new InvalidDataException("didChange requires between 1 and 128 content changes.");
        string text = current.Source;
        UiEditorText map = current.Text;
        foreach (JsonElement item in changes.EnumerateArray())
        {
            JsonElement change = RequiredObject(item, "content change");
            string replacement = RequiredString(change, "text", allowEmpty: true);
            int start = 0;
            int end = text.Length;
            if (OptionalProperty(change, "range") is { } rangeValue)
            {
                JsonElement range = RequiredObject(rangeValue, "range");
                start = Offset(map, Property(range, "start"));
                end = Offset(map, Property(range, "end"));
                if (end < start) throw new InvalidDataException("The edit range must be ordered.");
                if (OptionalProperty(change, "rangeLength") is not null
                    && RequiredNonNegativeInt32(change, "rangeLength") != end - start)
                    throw new InvalidDataException("rangeLength does not match the UTF-16 edit range.");
            }
            else if (OptionalProperty(change, "rangeLength") is not null)
                throw new InvalidDataException("rangeLength requires a range.");
            if ((long)text.Length - (end - start) + replacement.Length > _workspace.MaximumSourceLength)
                throw new InvalidDataException("The content change exceeds the workspace source limit.");
            text = string.Concat(text.AsSpan(0, start), replacement.AsSpan(), text.AsSpan(end));
            map = new UiEditorText(text);
        }
        _workspace.Update(uri, version, text, ContextFor(uri));
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
        JsonElement parameters = RequiredObject(request.Parameters, "diagnostic params");
        string? previous = OptionalProperty(parameters, "previousResultId") != null
            ? RequiredString(parameters, "previousResultId", allowEmpty: true) : null;
        return previous == snapshot.ResultId
            ? Success(new { kind = "unchanged", resultId = snapshot.ResultId })
            : Success(new { kind = "full", resultId = snapshot.ResultId, items = DiagnosticItems(snapshot) });
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
                    end = Position(snapshot.Text.PositionAt(snapshot.Source.Length))
                },
                newText = snapshot.Format.Text
            }
        });
    }

    private UiEditorDocumentSnapshot OpenDocument(string uri)
        => _workspace.TryGet(uri, out UiEditorDocumentSnapshot? snapshot) && snapshot is not null
            ? snapshot
            : throw new InvalidDataException($"Editor document '{uri}' is not open.");

    private static object Range(UiEditorText text, UiTextSpan span)
    {
        int start = Math.Clamp(span.Start, 0, text.Source.Length);
        int end = (int)Math.Clamp((long)span.Start + span.Length, start, text.Source.Length);
        return new { start = Position(text.PositionAt(start)), end = Position(text.PositionAt(end)) };
    }

    private static object Position(UiEditorPosition position) => new { line = position.Line, character = position.Character };

    private static int Offset(UiEditorText text, JsonElement value)
    {
        JsonElement position = RequiredObject(value, "position");
        return text.OffsetAt(RequiredNonNegativeInt32(position, "line"), RequiredNonNegativeInt32(position, "character"));
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
        => OptionalProperty(value, name) ?? throw new InvalidDataException($"Required property '{name}' is missing.");

    private static JsonElement? OptionalProperty(JsonElement value, string name)
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
        return found ? result : null;
    }

    private static JsonElement? NestedProperty(JsonElement value, params string[] path)
    {
        JsonElement? current = value;
        foreach (string name in path)
            current = current is { ValueKind: JsonValueKind.Object } item ? OptionalProperty(item, name) : null;
        return current;
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

    private UiJsonRpcDispatchResult InvalidParams(string message)
        => Error(UiJsonRpcErrorCodes.InvalidParams, message);

    private UiJsonRpcDispatchResult Error(int code, string message)
    {
        // JSON can escape each UTF-16 unit as six bytes. Error text may contain user input.
        int maximumCharacters = Math.Min(512, _maximumResultBytes / 6);
        if (message.Length > maximumCharacters)
        {
            int length = maximumCharacters - 1;
            if (length > 0 && char.IsHighSurrogate(message[length - 1])) length--;
            message = message[..length] + "…";
        }
        return UiJsonRpcDispatchResult.Error(code, message);
    }

    private UiJsonRpcDispatchResult Success<T>(T value)
    {
        try
        {
            var buffer = new UiLspJsonRpcDispatcher.UiBoundedBufferWriter(Math.Max(256, _maximumResultBytes));
            using (var writer = new Utf8JsonWriter(buffer)) JsonSerializer.Serialize(writer, value);
            if (buffer.WrittenMemory.Length > _maximumResultBytes) return ResponseTooLarge();
            using JsonDocument result = JsonDocument.Parse(buffer.WrittenMemory);
            return UiJsonRpcDispatchResult.Success(result.RootElement);
        }
        catch (InvalidDataException)
        {
            return ResponseTooLarge();
        }
    }

    private UiJsonRpcDispatchResult ResponseTooLarge()
        => Error(UiJsonRpcErrorCodes.InternalError, "Response exceeds the session payload budget.");
}
