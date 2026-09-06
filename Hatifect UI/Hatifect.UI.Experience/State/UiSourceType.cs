using Hatifect.UI.Semantics;

namespace Hatifect.UI.Experience;

/// <summary>Explicit link between a transport-neutral descriptor and a CLR source value.</summary>
public sealed class UiSourceType<T>
{
    public UiSourceType(UiDataType descriptor) => Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
    public UiDataType Descriptor { get; }
}

public static class UiSourceTypes
{
    public static readonly UiSourceType<string> String = new(UiDataTypes.String);
    public static readonly UiSourceType<bool> Boolean = new(UiDataTypes.Boolean);
    public static readonly UiSourceType<decimal> Number = new(UiDataTypes.Number);
    public static readonly UiSourceType<UiSymbolId> Symbol = new(UiDataTypes.Symbol);
    public static UiSourceType<T> Scalar<T>(UiSymbolId id, bool nullable) => new(UiDataTypes.Scalar(id, nullable));
    public static UiSourceType<IReadOnlyList<T>> Collection<T>(UiSourceType<T> item) => new(UiDataTypes.Collection(item.Descriptor));
    public static UiSourceType<UiSymbolId?> Selection<T>(UiSourceType<T> item) => new(UiDataTypes.Selection(item.Descriptor));
    public static UiSourceType<IReadOnlyList<UiSemanticFormField>> Form(UiSymbolId schema) => new(UiDataTypes.Form(schema));
    public static UiSourceType<UiValidationResult> Validation(UiSymbolId schema) => new(UiDataTypes.Validation(schema));
}

public sealed record UiValidationMessage(UiSymbolId FieldId, string Message);

public sealed class UiValidationResult
{
    public UiValidationResult(UiSymbolId schemaId, IEnumerable<UiValidationMessage> messages)
    {
        if (!schemaId.IsValid) throw new ArgumentException("A form schema ID is required.", nameof(schemaId));
        SchemaId = schemaId;
        Messages = Array.AsReadOnly((messages ?? throw new ArgumentNullException(nameof(messages))).ToArray());
        if (Messages.Any(message => message is null || !message.FieldId.IsValid || string.IsNullOrWhiteSpace(message.Message)))
            throw new ArgumentException("Validation messages require field identity and text.", nameof(messages));
    }
    public UiSymbolId SchemaId { get; }
    public IReadOnlyList<UiValidationMessage> Messages { get; }
    public bool IsValid => Messages.Count == 0;
}

/// <summary>Observes the collection's existing selection; it never creates a second selection state.</summary>
public sealed class UiSelectionSource : IUiSemanticSource<UiSymbolId?>, IUiPublicationReadableSource
{
    private readonly IUiSelectableCollectionSource _collection;
    public UiSelectionSource(IUiSelectableCollectionSource collection)
        => _collection = collection ?? throw new ArgumentNullException(nameof(collection));
    public UiSymbolId? Value => _collection.SelectedItemId;
    public Type ValueType => typeof(UiSymbolId?);
    public object? UntypedValue => Value;
    public UiPublication? Publication => (_collection as IUiPublicationReadableSource)?.Publication;
    public IUiSemanticSource ReadSnapshot(UiPublicationView view)
    {
        if (_collection is not IUiPublicationReadableSource source || source.Publication is null)
            throw new InvalidOperationException("This selection does not have a publication owner.");
        if (source.ReadSnapshot(view) is not IUiSemanticCollectionSnapshot snapshot)
            throw new InvalidOperationException("The publication source did not return a collection snapshot.");
        return new UiPublishedValueSnapshot<UiSymbolId?>(snapshot.SelectedItemId, snapshot.Version);
    }
    public event Action? Changed { add => _collection.Changed += value; remove => _collection.Changed -= value; }
}

internal static class UiSourceTypeValidation
{
    internal static void Validate(UiSemanticElementDefinition binding, Type declaredClrType,
        Dictionary<UiSymbolId, Type> nominalTypes, List<UiGraphDiagnostic> errors)
    {
        UiDataType descriptor = binding.DataType!;
        if (binding.Source.ValueType != declaredClrType)
            Error("Source ValueType differs from its declared CLR type.");
        object? value = binding.Source.UntypedValue;
        if (value is null && !descriptor.Nullable)
            Error("Required source currently contains null.");
        if (value is not null && !declaredClrType.IsInstanceOfType(value)) Error("Source payload differs from its declared CLR type.");
        if (value is UiSymbolId symbol && !symbol.IsValid) Error("Symbol/selection payload must contain a valid stable ID.");
        if (value is UiValidationResult validation && validation.SchemaId != descriptor.TypeId) Error("Validation result belongs to a different form schema.");
        CheckClr(descriptor, declaredClrType);
        if (descriptor.Shape == UiDataShape.Collection && binding.Source is not IUiSemanticCollectionSource)
            Error("A collection declaration requires the semantic collection source contract.");
        if (descriptor.Shape == UiDataShape.Collection && binding.Source is IUiSemanticCollectionSource collection
            && descriptor.ItemType is { } itemType && declaredClrType.IsGenericType)
        {
            Type itemClr = declaredClrType.GetGenericArguments()[0];
            int count = collection.Count;
            if (count < 0) Error("Collection count must be nonnegative.");
            for (int index = 0; index < count; index++)
            {
                object? item = collection.GetItem(index).Value;
                if ((item is null && !itemType.Nullable) || (item is not null && !itemClr.IsInstanceOfType(item)))
                    Error($"Collection item {index} violates its CLR type or required nullability.");
            }
        }
        if (descriptor.Shape == UiDataShape.Form && binding.Source is not IUiSemanticFormSource)
            Error("A form declaration requires the semantic form source contract.");

        void CheckClr(UiDataType type, Type clr)
        {
            Type? underlying = Nullable.GetUnderlyingType(clr);
            if (clr.IsValueType && type.Nullable != (underlying is not null)) Error("CLR value nullability differs from the descriptor.");
            Type value = underlying ?? clr;
            if (type.Shape == UiDataShape.Collection)
            {
                if (!value.IsGenericType || value.GetGenericTypeDefinition() != typeof(IReadOnlyList<>))
                    Error("Collection CLR type must be IReadOnlyList<T>.");
                else if (type.ItemType is { } item) CheckClr(item, value.GetGenericArguments()[0]);
                return;
            }
            if (type.Shape == UiDataShape.Selection)
            {
                if (clr != typeof(UiSymbolId?)) Error("Selection CLR payload must be UiSymbolId?.");
                return;
            }
            if (type.Shape == UiDataShape.Action) { Error("An action is declared using Action metadata, not a data source."); return; }
            if (type.Shape == UiDataShape.Form) { if (value != typeof(IReadOnlyList<UiSemanticFormField>)) Error("Form CLR payload must be the semantic field list."); return; }
            if (type.Shape == UiDataShape.Validation) { if (value != typeof(UiValidationResult)) Error("Validation CLR payload must be UiValidationResult."); return; }
            if ((type.TypeId == UiDataTypes.StringId && value != typeof(string))
                || (type.TypeId == UiDataTypes.BooleanId && value != typeof(bool))
                || (type.TypeId == UiDataTypes.NumberId && value != typeof(decimal))
                || (type.TypeId == UiDataTypes.SymbolId && value != typeof(UiSymbolId)))
                Error("Foundation data type does not match the CLR payload.");
            if (nominalTypes.TryGetValue(type.TypeId, out Type? previous) && previous != value)
                Error($"Nominal type '{type.TypeId}' was already declared for CLR type '{previous}'.");
            else nominalTypes[type.TypeId] = value;
        }
        void Error(string message) => errors.Add(new("UIG022", message, binding.Id));
    }
}
