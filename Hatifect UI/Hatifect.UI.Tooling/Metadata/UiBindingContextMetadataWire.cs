using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Tooling.Metadata;

/// <summary>
/// Internal deterministic schema-v1 wire contract. Discovery remains an embedding concern: callers
/// pass this snapshot explicitly through tooling initialization rather than scanning C# or files.
/// </summary>
internal static class UiBindingContextMetadataWire
{
    public const int SchemaVersion = 1;
    private const int MaximumJsonDepth = 32;

    public static byte[] Serialize(UiBindingContextMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("ownerId", metadata.OwnerId.ToString());
            writer.WriteBoolean("requireDeclaredElements", metadata.RequireDeclaredElements);
            writer.WriteBoolean("requireDeclaredRoles", metadata.RequireDeclaredRoles);
            writer.WritePropertyName("elements");
            writer.WriteStartArray();
            foreach (UiBindingElementMetadata element in metadata.Elements)
            {
                writer.WriteStartObject();
                writer.WriteString("id", element.Id.ToString());
                writer.WriteString("name", element.Name);
                writer.WritePropertyName("capabilities");
                writer.WriteStartArray();
                foreach (UiSymbolId capability in element.Capabilities)
                    writer.WriteStringValue(capability.ToString());
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("roles");
            writer.WriteStartArray();
            foreach (UiBindingRoleMetadata role in metadata.Roles)
            {
                writer.WriteStartObject();
                writer.WriteString("id", role.Id.ToString());
                writer.WriteString("name", role.Name);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }
        return buffer.WrittenSpan.ToArray();
    }

    public static UiBindingContextMetadata Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        JsonDocumentOptions options = new()
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaximumJsonDepth
        };
        using JsonDocument document = JsonDocument.Parse(utf8Json.ToArray(), options);
        IReadOnlyDictionary<string, JsonElement> root = ReadObject(
            document.RootElement,
            "binding metadata",
            "schemaVersion",
            "ownerId",
            "requireDeclaredElements",
            "requireDeclaredRoles",
            "elements",
            "roles");
        if (RequiredInt32(root, "schemaVersion") != SchemaVersion)
            throw new InvalidDataException("Unsupported binding metadata schemaVersion.");
        UiSymbolId owner = RequiredSymbol(root, "ownerId");
        bool requireElements = RequiredBoolean(root, "requireDeclaredElements");
        bool requireRoles = RequiredBoolean(root, "requireDeclaredRoles");
        JsonElement elementsValue = Required(root, "elements");
        JsonElement rolesValue = Required(root, "roles");
        if (elementsValue.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Binding metadata elements must be an array.");
        if (rolesValue.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Binding metadata roles must be an array.");

        var elementNames = new HashSet<string>(StringComparer.Ordinal);
        var elementIds = new HashSet<UiSymbolId>();
        var elements = new List<UiBindingElementMetadata>();
        foreach (JsonElement value in elementsValue.EnumerateArray())
        {
            IReadOnlyDictionary<string, JsonElement> item = ReadObject(
                value,
                "binding element",
                "id",
                "name",
                "capabilities");
            string name = RequiredNonEmptyString(item, "name");
            UiSymbolId id = RequiredSymbol(item, "id");
            if (id != owner.Child($"element/{name}"))
                throw new InvalidDataException($"Binding element '{name}' has a conflicting identity.");
            if (!elementNames.Add(name) || !elementIds.Add(id))
                throw new InvalidDataException($"Binding element '{name}' is duplicated.");
            JsonElement capabilitiesValue = Required(item, "capabilities");
            if (capabilitiesValue.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"Binding element '{name}' capabilities must be an array.");
            var capabilities = new HashSet<UiSymbolId>();
            foreach (JsonElement capabilityValue in capabilitiesValue.EnumerateArray())
            {
                UiSymbolId capability = ParseSymbol(capabilityValue, "capability");
                if (!capabilities.Add(capability))
                    throw new InvalidDataException(
                        $"Binding element '{name}' contains duplicate capability '{capability}'.");
            }
            elements.Add(new UiBindingElementMetadata(
                id,
                name,
                Array.AsReadOnly(capabilities
                    .OrderBy(capability => capability.ToString(), StringComparer.Ordinal)
                    .ToArray())));
        }

        var roleNames = new HashSet<string>(StringComparer.Ordinal);
        var roleIds = new HashSet<UiSymbolId>();
        var roles = new List<UiBindingRoleMetadata>();
        foreach (JsonElement value in rolesValue.EnumerateArray())
        {
            IReadOnlyDictionary<string, JsonElement> item = ReadObject(
                value,
                "binding role",
                "id",
                "name");
            string name = RequiredNonEmptyString(item, "name");
            UiSymbolId id = RequiredSymbol(item, "id");
            if (id != owner.Child($"role/{name}"))
                throw new InvalidDataException($"Binding role '{name}' has a conflicting identity.");
            if (!roleNames.Add(name) || !roleIds.Add(id))
                throw new InvalidDataException($"Binding role '{name}' is duplicated.");
            roles.Add(new UiBindingRoleMetadata(id, name));
        }

        return new UiBindingContextMetadata(
            owner,
            requireElements,
            requireRoles,
            elements.OrderBy(element => element.Name, StringComparer.Ordinal).ToArray(),
            roles.OrderBy(role => role.Name, StringComparer.Ordinal).ToArray());
    }

    public static UiBindingContext CreateBindingContext(UiBindingContextMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var context = new UiBindingContext(metadata.OwnerId)
        {
            RequireDeclaredElements = metadata.RequireDeclaredElements,
            RequireDeclaredRoles = metadata.RequireDeclaredRoles
        };
        foreach (UiBindingElementMetadata element in metadata.Elements)
            context.DeclareElement(element.Name, element.Capabilities.ToArray());
        foreach (UiBindingRoleMetadata role in metadata.Roles)
            context.DeclareRole(role.Name);
        return context;
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadObject(
        JsonElement value,
        string description,
        params string[] allowedProperties)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"The {description} must be an object.");
        var allowed = new HashSet<string>(allowedProperties, StringComparer.Ordinal);
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new InvalidDataException(
                    $"The {description} contains unknown property '{property.Name}'.");
            if (!properties.TryAdd(property.Name, property.Value.Clone()))
                throw new InvalidDataException(
                    $"The {description} contains duplicate property '{property.Name}'.");
        }
        return properties;
    }

    private static JsonElement Required(IReadOnlyDictionary<string, JsonElement> values, string name)
        => values.TryGetValue(name, out JsonElement value)
            ? value
            : throw new InvalidDataException($"Binding metadata is missing required property '{name}'.");

    private static int RequiredInt32(IReadOnlyDictionary<string, JsonElement> values, string name)
        => Required(values, name).TryGetInt32(out int value)
            ? value
            : throw new InvalidDataException($"Binding metadata property '{name}' must be an integer.");

    private static bool RequiredBoolean(IReadOnlyDictionary<string, JsonElement> values, string name)
    {
        JsonElement value = Required(values, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException(
                $"Binding metadata property '{name}' must be a boolean.")
        };
    }

    private static string RequiredNonEmptyString(
        IReadOnlyDictionary<string, JsonElement> values,
        string name)
    {
        JsonElement value = Required(values, name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException(
                $"Binding metadata property '{name}' must be a non-empty string.");
        return value.GetString()!;
    }

    private static UiSymbolId RequiredSymbol(
        IReadOnlyDictionary<string, JsonElement> values,
        string name)
        => ParseSymbol(Required(values, name), name);

    private static UiSymbolId ParseSymbol(JsonElement value, string description)
    {
        if (value.ValueKind != JsonValueKind.String ||
            !UiSymbolId.TryParse(value.GetString(), out UiSymbolId symbol))
            throw new InvalidDataException(
                $"Binding metadata {description} must be a valid globally-scoped symbol ID.");
        return symbol;
    }
}
