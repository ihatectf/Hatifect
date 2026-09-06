using System.Buffers;
using System.Text.Json;
using Hatifect.UI.Language.Text;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Tooling.Metadata;

internal static partial class UiBindingContextMetadataWire
{
    private static byte[] SerializeV2(UiBindingContextMetadata metadata)
    {
        UiSemanticGraph graph = metadata.Graph!;
        var diagnostics = UiGraphBinder.Validate(graph);
        if (diagnostics.Count != 0) throw new UiGraphValidationException(diagnostics);
        var typeBudget = new TypeWriteBudget();
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 2);
            writer.WriteString("ownerId", metadata.OwnerId.ToString());
            writer.WriteBoolean("requireDeclaredElements", metadata.RequireDeclaredElements);
            writer.WriteBoolean("requireDeclaredRoles", metadata.RequireDeclaredRoles);
            writer.WriteStartObject("graph");
            writer.WriteStartArray("nodes");
            foreach (UiSemanticNode node in graph.Nodes.OrderBy(node => node.Id.ToString(), StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", node.Id.ToString());
                writer.WriteString("alias", node.Alias);
                writer.WriteString("label", node.Label);
                WriteType(writer, "dataType", node.DataType, typeBudget);
                WriteSymbols(writer, "capabilities", node.Capabilities.OrderBy(id => id.ToString(), StringComparer.Ordinal));
                writer.WriteStartArray("inputs");
                foreach (UiProjectionInput input in node.Inputs.OrderBy(input => input.Id.ToString(), StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", input.Id.ToString());
                    writer.WriteString("name", input.Name);
                    WriteType(writer, "acceptedType", input.AcceptedType, typeBudget);
                    writer.WriteBoolean("required", input.Required);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("relations");
            foreach (UiSemanticRelation relation in graph.Relations.OrderBy(relation => relation.Id.ToString(), StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", relation.Id.ToString());
                writer.WriteString("kind", relation.Kind.ToString());
                writer.WriteString("source", relation.Source.ToString());
                writer.WriteString("target", relation.Target.ToString());
                WriteSymbol(writer, "targetInput", relation.TargetInput);
                WriteType(writer, "mapping", relation.Mapping, typeBudget);
                writer.WritePropertyName("provenance");
                if (relation.Provenance is not { } provenance) writer.WriteNullValue();
                else
                {
                    writer.WriteStartObject();
                    writer.WriteString("sourceName", provenance.SourceName);
                    WriteSymbol(writer, "declarationId", provenance.DeclarationId);
                    writer.WriteNumber("start", provenance.Span.Start);
                    writer.WriteNumber("length", provenance.Span.Length);
                    writer.WriteNumber("line", provenance.Span.Line);
                    writer.WriteNumber("column", provenance.Span.Column);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            // Membership order is authoring order, and can later drive presentation planning.
            WriteSymbols(writer, "presentedNodes", graph.PresentedNodes);
            writer.WriteStartArray("roles");
            foreach (UiGraphRole role in graph.Roles.OrderBy(role => role.Alias, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", role.Id.ToString());
                writer.WriteString("alias", role.Alias);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static UiBindingContextMetadata DeserializeV2(JsonElement value)
    {
        var root = ReadObject(value, "binding metadata v2", "schemaVersion", "ownerId", "requireDeclaredElements", "requireDeclaredRoles", "graph");
        if (RequiredInt32(root, "schemaVersion") != 2) throw new InvalidDataException("Unsupported binding metadata schemaVersion.");
        UiSymbolId owner = RequiredSymbol(root, "ownerId");
        var graph = ReadObject(Required(root, "graph"), "semantic graph", "nodes", "relations", "presentedNodes", "roles");
        var nodes = new List<UiSemanticNode>();
        foreach (JsonElement entry in ReadArray(graph, "nodes"))
        {
            var node = ReadObject(entry, "semantic node", "id", "alias", "label", "dataType", "capabilities", "inputs");
            var inputs = new List<UiProjectionInput>();
            foreach (JsonElement inputValue in ReadArray(node, "inputs"))
            {
                var input = ReadObject(inputValue, "projection input", "id", "name", "acceptedType", "required");
                inputs.Add(new(RequiredSymbol(input, "id"), RequiredNonEmptyString(input, "name"),
                    ReadType(Required(input, "acceptedType")) ?? throw new InvalidDataException("An input type is required."), RequiredBoolean(input, "required")));
            }
            nodes.Add(new(RequiredSymbol(node, "id"), RequiredNonEmptyString(node, "alias"), RequiredNonEmptyString(node, "label"),
                ReadType(Required(node, "dataType")), ReadArray(node, "capabilities").Select(item => ParseSymbol(item, "capability")), inputs));
        }
        var relations = new List<UiSemanticRelation>();
        foreach (JsonElement entry in ReadArray(graph, "relations"))
        {
            var relation = ReadObject(entry, "semantic relation", "id", "kind", "source", "target", "targetInput", "mapping", "provenance");
            UiGraphProvenance? provenance = null;
            if (Required(relation, "provenance") is { ValueKind: not JsonValueKind.Null } location)
            {
                var fields = ReadObject(location, "relation provenance", "sourceName", "declarationId", "start", "length", "line", "column");
                int start = NonNegative(fields, "start"), length = NonNegative(fields, "length");
                if ((long)start + length > int.MaxValue) throw new InvalidDataException("Provenance span overflows.");
                provenance = new(RequiredNonEmptyString(fields, "sourceName"),
                    new UiTextSpan(start, length, NonNegative(fields, "line"), NonNegative(fields, "column")), OptionalSymbol(fields, "declarationId"));
            }
            relations.Add(new(RequiredSymbol(relation, "id"), ReadEnum<UiRelationKind>(relation, "kind"),
                RequiredSymbol(relation, "source"), RequiredSymbol(relation, "target"), OptionalSymbol(relation, "targetInput"),
                ReadType(Required(relation, "mapping")), provenance));
        }
        var roles = new List<UiGraphRole>();
        foreach (JsonElement entry in ReadArray(graph, "roles"))
        {
            var role = ReadObject(entry, "graph role", "id", "alias");
            roles.Add(new(RequiredSymbol(role, "id"), RequiredNonEmptyString(role, "alias")));
        }
        var model = new UiSemanticGraph(owner, nodes, relations,
            ReadArray(graph, "presentedNodes").Select(item => ParseSymbol(item, "presented node")), roles);
        try
        {
            return UiBindingContextMetadataExporter.Export(new UiBindingContext(model)
            {
                RequireDeclaredElements = RequiredBoolean(root, "requireDeclaredElements"),
                RequireDeclaredRoles = RequiredBoolean(root, "requireDeclaredRoles")
            });
        }
        catch (UiGraphValidationException error) { throw new InvalidDataException(error.Message, error); }
    }

    private static void WriteType(Utf8JsonWriter writer, string name, UiDataType? type, TypeWriteBudget budget)
    {
        writer.WritePropertyName(name);
        if (type is null) { writer.WriteNullValue(); return; }
        if (--budget.Remaining < 0) throw new InvalidDataException("Metadata exceeds the 65536 expanded data type entries budget.");
        writer.WriteStartObject();
        writer.WriteString("typeId", type.TypeId.ToString());
        writer.WriteString("shape", type.Shape.ToString());
        writer.WriteBoolean("nullable", type.Nullable);
        WriteType(writer, "itemType", type.ItemType, budget);
        WriteType(writer, "inputType", type.InputType, budget);
        WriteType(writer, "resultType", type.ResultType, budget);
        WriteType(writer, "targetType", type.TargetType, budget);
        WriteSymbol(writer, "targetCapability", type.TargetCapability);
        writer.WriteEndObject();
    }

    private sealed class TypeWriteBudget { internal int Remaining = 65536; }

    private static UiDataType? ReadType(JsonElement value, int depth = 0)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (depth > 16) throw new InvalidDataException("Data type nesting exceeds 16 levels.");
        var type = ReadObject(value, "data type", "typeId", "shape", "nullable", "itemType", "inputType", "resultType", "targetType", "targetCapability");
        return new(RequiredSymbol(type, "typeId"), ReadEnum<UiDataShape>(type, "shape"), RequiredBoolean(type, "nullable"),
            ReadType(Required(type, "itemType"), depth + 1), ReadType(Required(type, "inputType"), depth + 1),
            ReadType(Required(type, "resultType"), depth + 1), ReadType(Required(type, "targetType"), depth + 1), OptionalSymbol(type, "targetCapability"));
    }

    private static void WriteSymbol(Utf8JsonWriter writer, string name, UiSymbolId? id)
    {
        if (id is { } symbol) writer.WriteString(name, symbol.ToString()); else writer.WriteNull(name);
    }
    private static void WriteSymbols(Utf8JsonWriter writer, string name, IEnumerable<UiSymbolId> ids)
    {
        writer.WriteStartArray(name);
        foreach (UiSymbolId id in ids) writer.WriteStringValue(id.ToString());
        writer.WriteEndArray();
    }
    private static UiSymbolId? OptionalSymbol(IReadOnlyDictionary<string, JsonElement> fields, string name)
        => Required(fields, name) is { ValueKind: not JsonValueKind.Null } value ? ParseSymbol(value, name) : null;
    private static T ReadEnum<T>(IReadOnlyDictionary<string, JsonElement> fields, string name) where T : struct, Enum
    {
        string text = RequiredNonEmptyString(fields, name);
        return Enum.TryParse(text, out T value) && Enum.IsDefined(typeof(T), value) && value.ToString() == text
            ? value : throw new InvalidDataException($"Unknown {name} '{text}'.");
    }
    private static JsonElement.ArrayEnumerator ReadArray(IReadOnlyDictionary<string, JsonElement> fields, string name)
    {
        JsonElement value = Required(fields, name);
        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray()
            : throw new InvalidDataException($"Graph property '{name}' must be an array.");
    }
    private static int NonNegative(IReadOnlyDictionary<string, JsonElement> fields, string name)
    {
        int value = RequiredInt32(fields, name);
        return value >= 0 ? value : throw new InvalidDataException($"Provenance {name} must be nonnegative.");
    }
}
