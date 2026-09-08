using System.IO;
using System.Text.Json;
using Hatifect.UI.Tooling.Protocol;

namespace Hatifect.UI.Tooling.Client;

/// <summary>A bounded authoring client over borrowed stdio streams. The caller owns the server process.</summary>
public sealed class UiToolingClient : IDisposable
{
    private readonly UiLspMessageStream _frames;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, int> _documents = new(StringComparer.Ordinal);
    private HashSet<string> _capabilities = new(StringComparer.Ordinal);
    private int _nextId;
    private int _bindingRevision;
    private volatile bool _active;
    private volatile bool _faulted;
    private volatile bool _disposed;

    public UiToolingClient(Stream input, Stream output)
        => _frames = new UiLspMessageStream(input, output);

    public bool Supports(string capability) => _active && !_faulted && !_disposed && _capabilities.Contains(capability);

    public async Task InitializeAsync(JsonElement bindingMetadata, IEnumerable<string>? requiredCapabilities = null,
        int bindingRevision = 0, CancellationToken cancellationToken = default)
    {
        if (bindingRevision < 0) throw new ArgumentOutOfRangeException(nameof(bindingRevision));
        string[] required = (requiredCapabilities ?? Array.Empty<string>()).Take(33).ToArray();
        if (required.Length > 32 || required.Any(c => string.IsNullOrWhiteSpace(c) || c.Length > 128)
            || required.Distinct(StringComparer.Ordinal).Count() != required.Length)
            throw new ArgumentException("Required capabilities must contain at most 32 unique names of 1–128 characters.", nameof(requiredCapabilities));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            if (_active) throw new InvalidOperationException("The client is already initialized.");
            JsonElement result = await RequestAsync("initialize", new { initializationOptions = new
            {
                bindingMetadata, bindingRevision, protocolVersion = 0, requiredCapabilities = required
            } }, cancellationToken).ConfigureAwait(false);
            _capabilities = UiToolingClientResponses.Capabilities(result, required);
            await NotifyAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
            _bindingRevision = bindingRevision;
            _active = true;
        }
        catch { _faulted = true; throw; }
        finally { _gate.Release(); }
    }

    /// <summary>Publishes the complete source with a strictly increasing document version.</summary>
    public async Task SynchronizeAsync(string uri, int version, string source, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(uri, version);
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > 1024 * 1024) throw new ArgumentException("Source exceeds the authoring limit.", nameof(source));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive();
            bool open = _documents.TryGetValue(uri, out int prior);
            if (open && version <= prior) throw new ArgumentException("Document version must increase.", nameof(version));
            if (!open && _documents.Count >= 64) throw new InvalidOperationException("Close a document before opening more than 64.");
            object parameters = open
                ? new { textDocument = new { uri, version }, contentChanges = new[] { new { text = source } } }
                : new { textDocument = new { uri, version, text = source } };
            await NotifyAsync(open ? "textDocument/didChange" : "textDocument/didOpen", parameters, cancellationToken).ConfigureAwait(false);
            _documents[uri] = version;
        }
        finally { _gate.Release(); }
    }

    public async Task<UiToolingCompilation> CompileAsync(string uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive();
            if (!_capabilities.Contains("compilation") || !_capabilities.Contains("diagnostics"))
                throw new NotSupportedException("The server does not advertise complete compilation and diagnostics.");
            if (!_documents.TryGetValue(uri, out int version)) throw new InvalidOperationException("Synchronize the document first.");
            JsonElement diagnostics = await RequestAsync("textDocument/diagnostic", new { textDocument = new { uri } }, cancellationToken).ConfigureAwait(false);
            string resultId = UiToolingClientResponses.FullDiagnosticIdentity(diagnostics);
            JsonElement result = await RequestAsync("hatifect/compilation", new
            {
                textDocument = new { uri, version }, bindingRevision = _bindingRevision, resultId
            }, cancellationToken).ConfigureAwait(false);
            return UiToolingClientResponses.Compilation(result, uri, version, _bindingRevision, resultId);
        }
        catch (InvalidDataException) { _faulted = true; throw; }
        finally { _gate.Release(); }
    }

    public async Task CloseAsync(string uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive();
            if (!_documents.ContainsKey(uri)) return;
            await NotifyAsync("textDocument/didClose", new { textDocument = new { uri } }, cancellationToken).ConfigureAwait(false);
            _documents.Remove(uri);
        }
        finally { _gate.Release(); }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive();
            JsonElement result = await RequestAsync("shutdown", new { }, cancellationToken).ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Null) throw new InvalidDataException("Invalid shutdown response.");
            await NotifyAsync("exit", new { }, cancellationToken).ConfigureAwait(false);
            _active = false;
            _faulted = true;
            _documents.Clear();
        }
        catch (InvalidDataException) { _faulted = true; throw; }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _disposed = true;
        _frames.Dispose();
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken token)
    {
        try
        {
            int id = checked(++_nextId);
            await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, token).ConfigureAwait(false);
            byte[] bytes = await _frames.ReadFrameAsync(token).ConfigureAwait(false)
                ?? throw new EndOfStreamException("The server closed before returning a complete response.");
            return UiToolingClientResponses.Response(bytes, id);
        }
        catch { _faulted = true; throw; }
    }

    private async Task NotifyAsync(string method, object parameters, CancellationToken token)
    {
        try { await WriteAsync(new { jsonrpc = "2.0", method, @params = parameters }, token).ConfigureAwait(false); }
        catch { _faulted = true; throw; }
    }

    private async Task WriteAsync(object message, CancellationToken token)
    {
        var buffer = new UiLspJsonRpcDispatcher.UiBoundedBufferWriter(UiLspMessageStream.DefaultMaximumPayloadBytes);
        using (var writer = new Utf8JsonWriter(buffer)) JsonSerializer.Serialize(writer, message);
        await _frames.WriteFrameAsync(buffer.WrittenMemory, token).ConfigureAwait(false);
    }

    private void EnsureUsable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UiToolingClient));
        if (_faulted) throw new InvalidOperationException("The client session cannot be reused after a failed exchange or shutdown.");
    }

    private void EnsureActive()
    {
        EnsureUsable();
        if (!_active) throw new InvalidOperationException("Initialize the client first.");
    }

    private static void ValidateIdentity(string uri, int version)
    {
        if (string.IsNullOrWhiteSpace(uri)) throw new ArgumentException("A document URI is required.", nameof(uri));
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
    }
}

/// <summary>A complete compiler outcome for one immutable snapshot; invalid compilation is not transport failure.</summary>
public sealed record UiToolingCompilation(string Uri, int Version, int BindingRevision, string ResultId,
    bool IsValid, JsonElement Diagnostics);
