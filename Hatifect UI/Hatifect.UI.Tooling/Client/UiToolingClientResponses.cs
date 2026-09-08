using System.IO;
using System.Text.Json;

namespace Hatifect.UI.Tooling.Client;

internal static class UiToolingClientResponses
{
    internal static JsonElement Response(byte[] bytes, int expectedId)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement response = document.RootElement;
        if (String(response, "jsonrpc") != "2.0" || Integer(response, "id") != expectedId)
            throw new InvalidDataException("JSON-RPC response identity does not match the request.");
        JsonElement? error = Optional(response, "error");
        JsonElement? result = Optional(response, "result");
        if ((error is null) == (result is null)) throw new InvalidDataException("Response must contain exactly one result or error.");
        if (error is { } failure)
            throw new UiToolingRequestException(Integer(failure, "code"), String(failure, "message"));
        return result!.Value.Clone();
    }

    internal static HashSet<string> Capabilities(JsonElement result, string[] required)
    {
        JsonElement? capabilities = Optional(result, "capabilities");
        JsonElement? experimental = capabilities is { } c ? Optional(c, "experimental") : null;
        JsonElement? hatifect = experimental is { } e ? Optional(e, "hatifectUi") : null;
        if (hatifect is null || Optional(hatifect.Value, "protocolVersion") is null || Optional(hatifect.Value, "capabilities") is null) throw new NotSupportedException("The server does not advertise the Hatifect tooling protocol.");
        if (Integer(hatifect.Value, "protocolVersion") != 0) throw new NotSupportedException("Unsupported Hatifect tooling protocol version.");
        JsonElement names = Property(hatifect.Value, "capabilities");
        if (names.ValueKind != JsonValueKind.Array || names.GetArrayLength() > 32)
            throw new InvalidDataException("Invalid server capability list.");
        var supported = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement name in names.EnumerateArray())
        {
            if (name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString())
                || name.GetString()!.Length > 128 || !supported.Add(name.GetString()!))
                throw new InvalidDataException("Server capability names must be bounded and unique.");
        }
        foreach (string name in required)
            if (!supported.Contains(name)) throw new NotSupportedException($"The server does not advertise '{name}'.");
        return supported;
    }

    internal static string FullDiagnosticIdentity(JsonElement result)
    {
        if (String(result, "kind") != "full") throw new InvalidDataException("A full diagnostic response is required.");
        ValidateDiagnostics(Property(result, "items"));
        return String(result, "resultId");
    }

    internal static UiToolingCompilation Compilation(JsonElement result, string uri, int version, int bindingRevision, string resultId)
    {
        if (String(result, "uri") != uri || Integer(result, "version") != version
            || Integer(result, "bindingRevision") != bindingRevision || String(result, "resultId") != resultId)
            throw new InvalidDataException("Compilation response belongs to another snapshot.");
        string status = String(result, "status");
        if (status is not ("valid" or "invalid")) throw new InvalidDataException("A complete compilation status is required.");
        JsonElement diagnostics = Property(result, "diagnostics");
        bool hasError = ValidateDiagnostics(diagnostics);
        if ((status == "invalid") != hasError) throw new InvalidDataException("Compilation status contradicts diagnostics.");
        return new UiToolingCompilation(uri, version, bindingRevision, resultId, status == "valid", diagnostics.Clone());
    }

    private static bool ValidateDiagnostics(JsonElement diagnostics)
    {
        if (diagnostics.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Complete diagnostics must be an array.");
        bool hasError = false;
        foreach (JsonElement diagnostic in diagnostics.EnumerateArray())
        {
            int severity = Integer(diagnostic, "severity");
            if (severity is < 1 or > 4) throw new InvalidDataException("Invalid diagnostic severity.");
            hasError |= severity == 1;
            _ = String(diagnostic, "code");
            _ = String(diagnostic, "source");
            _ = String(diagnostic, "message");
            JsonElement range = Property(diagnostic, "range");
            var start = Position(Property(range, "start"));
            var end = Position(Property(range, "end"));
            if (start.CompareTo(end) > 0) throw new InvalidDataException("Diagnostic range is reversed.");
        }
        return hasError;
    }

    private static (int Line, int Character) Position(JsonElement value)
    {
        int line = Integer(value, "line"), character = Integer(value, "character");
        if (line < 0 || character < 0) throw new InvalidDataException("Diagnostic positions must be non-negative.");
        return (line, character);
    }

    private static JsonElement Property(JsonElement value, string name)
        => Optional(value, name) ?? throw new InvalidDataException($"Required response field '{name}' is missing.");

    private static JsonElement? Optional(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Response member must be an object.");
        JsonElement? result = null;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Name != name) continue;
            if (result is not null) throw new InvalidDataException($"Response field '{name}' is duplicated.");
            result = property.Value;
        }
        return result;
    }

    private static string String(JsonElement value, string name)
    {
        JsonElement property = Property(value, name);
        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
            throw new InvalidDataException($"Response field '{name}' must be a nonempty string.");
        return property.GetString()!;
    }

    private static int Integer(JsonElement value, string name)
    {
        JsonElement property = Property(value, name);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out int number))
            throw new InvalidDataException($"Response field '{name}' must be a 32-bit integer.");
        return number;
    }
}

/// <summary>The server rejected a request; no successful result accompanies this exception.</summary>
public sealed class UiToolingRequestException : IOException
{
    public UiToolingRequestException(int code, string message) : base(message) => Code = code;
    public int Code { get; }
}
