using System.Collections.ObjectModel;

namespace Hatifect.UI.Experience;

/// <summary>
/// Semantic field identity, label, mutable value and optional consumer validation.
/// Layout and presentation remain runtime-owned; null or whitespace validation means valid.
/// </summary>
public sealed record UiSemanticFormField
{
    public UiSemanticFormField(
        UiSymbolId id,
        string label,
        IUiMutableSemanticSource<string> value,
        IUiSemanticSource<string?>? validationMessage = null)
    {
        if (!id.IsValid) throw new ArgumentException("A stable form-field ID is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("A form-field label is required.", nameof(label));
        Id = id;
        Label = label;
        Value = value ?? throw new ArgumentNullException(nameof(value));
        ValidationMessage = validationMessage;
    }

    public UiSymbolId Id { get; }
    public string Label { get; }
    public IUiMutableSemanticSource<string> Value { get; }
    public IUiSemanticSource<string?>? ValidationMessage { get; }
}

internal interface IUiSemanticFormSource : IUiSemanticSource
{
    IReadOnlyList<UiSemanticFormField> Fields { get; }
}

/// <summary>
/// Fixed-shape semantic form. Field value changes are forwarded through one source notification so
/// the ordinary Experience invalidation path can recompose the affected text inputs.
/// </summary>
public sealed class UiFormState :
    IUiSemanticSource<IReadOnlyList<UiSemanticFormField>>,
    IUiSemanticFormSource, IDisposable
{
    private readonly ReadOnlyCollection<UiSemanticFormField> _fields;
    private readonly IUiSemanticSource[] _sources;
    private readonly HashSet<IUiSemanticSource> _attached = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

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
        }
        _fields = Array.AsReadOnly(copy);
        var sources = new HashSet<IUiSemanticSource>(ReferenceEqualityComparer.Instance);
        foreach (UiSemanticFormField field in copy)
        {
            sources.Add(field.Value);
            if (field.ValidationMessage is not null) sources.Add(field.ValidationMessage);
        }
        _sources = sources.ToArray();
        int attached = 0;
        try
        {
            while (attached < _sources.Length)
            {
                IUiSemanticSource source = _sources[attached++];
                _attached.Add(source);
                source.Changed += OnFieldChanged;
            }
        }
        catch
        {
            for (int index = 0; index < attached; index++)
                try { _sources[index].Changed -= OnFieldChanged; } catch { /* preserve the original subscription failure */ }
            throw;
        }
    }

    public IReadOnlyList<UiSemanticFormField> Fields => _fields;
    public IReadOnlyList<UiSemanticFormField> Value => _fields;
    public Type ValueType => typeof(IReadOnlyList<UiSemanticFormField>);
    public object UntypedValue => _fields;
    public event Action? Changed;

    public bool IsValid
    {
        get
        {
            foreach (UiSemanticFormField field in _fields)
                if (!string.IsNullOrWhiteSpace(field.ValidationMessage?.Value)) return false;
            return true;
        }
    }

    /// <summary>Detaches owned subscriptions. Borrowed field sources remain usable.</summary>
    public void Dispose()
    {
        if (_disposed && _attached.Count == 0) return;
        _disposed = true;
        List<Exception>? failures = null;
        foreach (IUiSemanticSource source in _sources)
            try { if (_attached.Contains(source)) { source.Changed -= OnFieldChanged; _attached.Remove(source); } }
            catch (Exception error) { (failures ??= new()).Add(error); }
        Changed = null;
        if (failures is not null) throw new AggregateException(failures);
    }

    private void OnFieldChanged() { if (!_disposed) Changed?.Invoke(); }
}
