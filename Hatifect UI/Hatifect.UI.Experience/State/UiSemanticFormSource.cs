using System.Collections.ObjectModel;

namespace Hatifect.UI.Experience;

/// <summary>
/// Provisional first-party form-field contract used while configuration dogfooding establishes the
/// final public authoring surface. It intentionally carries semantic identity, label, and mutable
/// value only; layout and presentation remain runtime-owned.
/// </summary>
internal sealed record UiSemanticFormField
{
    public UiSemanticFormField(
        UiSymbolId id,
        string label,
        IUiMutableSemanticSource<string> value)
    {
        if (!id.IsValid) throw new ArgumentException("A stable form-field ID is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("A form-field label is required.", nameof(label));
        Id = id;
        Label = label;
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public UiSymbolId Id { get; }
    public string Label { get; }
    public IUiMutableSemanticSource<string> Value { get; }
}

internal interface IUiSemanticFormSource : IUiSemanticSource
{
    IReadOnlyList<UiSemanticFormField> Fields { get; }
}

/// <summary>
/// Fixed-shape semantic form. Field value changes are forwarded through one source notification so
/// the ordinary Experience invalidation path can recompose the affected text inputs.
/// </summary>
internal sealed class UiFormState :
    IUiSemanticSource<IReadOnlyList<UiSemanticFormField>>,
    IUiSemanticFormSource
{
    private readonly ReadOnlyCollection<UiSemanticFormField> _fields;

    public UiFormState(params UiSemanticFormField[] fields)
    {
        if (fields == null || fields.Length == 0)
            throw new ArgumentException("A semantic form requires at least one field.", nameof(fields));

        var ids = new HashSet<UiSymbolId>();
        var copy = new UiSemanticFormField[fields.Length];
        for (int index = 0; index < fields.Length; index++)
        {
            UiSemanticFormField field = fields[index]
                ?? throw new ArgumentException("Semantic form fields cannot contain null.", nameof(fields));
            if (!ids.Add(field.Id))
                throw new ArgumentException($"Semantic form field '{field.Id}' is duplicated.", nameof(fields));
            copy[index] = field;
            field.Value.Changed += OnFieldChanged;
        }
        _fields = Array.AsReadOnly(copy);
    }

    public IReadOnlyList<UiSemanticFormField> Fields => _fields;
    public IReadOnlyList<UiSemanticFormField> Value => _fields;
    public Type ValueType => typeof(IReadOnlyList<UiSemanticFormField>);
    public object UntypedValue => _fields;
    public event Action? Changed;

    private void OnFieldChanged() => Changed?.Invoke();
}
