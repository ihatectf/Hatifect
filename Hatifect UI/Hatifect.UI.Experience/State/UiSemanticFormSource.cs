using System.Collections.ObjectModel;
using System.Globalization;

namespace Hatifect.UI.Experience;

public enum UiFormFieldKind { Text, Number, Toggle, Choice }
public sealed record UiFormOption(string Value, string Label);

/// <summary>A semantic field. The string constructor preserves immediate binding; typed factories own a draft.</summary>
public class UiSemanticFormField
{
    public UiSemanticFormField(UiSymbolId id, string label, IUiMutableSemanticSource<string> value)
    {
        if (!id.IsValid) throw new ArgumentException("A stable form-field ID is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A field label is required.", nameof(label));
        Id = id;
        Label = label;
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public UiSymbolId Id { get; }
    public string Label { get; }
    public IUiMutableSemanticSource<string> Value { get; }
    public UiFormFieldKind Kind { get; internal init; }
    public IReadOnlyList<UiFormOption> Options { get; internal init; } = Array.Empty<UiFormOption>();
    public string? Error { get; internal set; }
    internal virtual object? Prepare(string draft, out string? error) { error = null; return draft; }
    internal virtual void Commit(object? value) { }
    internal virtual void ResetDraft() { }
}

/// <summary>Typed committed value with an independently editable string draft. Validators must be pure.</summary>
public sealed class UiFormField<T> : UiSemanticFormField
{
    private readonly Func<string, (bool Valid, T Value)> _parse;
    private readonly Func<T, string> _format;
    private readonly Func<T, string?>? _validate;

    internal UiFormField(UiSymbolId id, string label, T initial, UiFormFieldKind kind,
        Func<string, (bool Valid, T Value)> parse, Func<T, string> format, Func<T, string?>? validate,
        IReadOnlyList<UiFormOption>? options = null)
        : base(id, label, new UiState<string>(format(initial)))
    {
        _parse = parse;
        _format = format;
        _validate = validate;
        Kind = kind;
        Options = options ?? Array.Empty<UiFormOption>();
        CommittedValue = initial;
    }

    public T CommittedValue { get; private set; }
    public T DraftValue
    {
        get => TryGetDraft(out T value) ? value : throw new FormatException($"Field '{Id}' has an invalid draft.");
        set => Value.Value = _format(value);
    }
    public bool TryGetDraft(out T value)
    {
        (bool valid, T parsed) = _parse(Value.Value);
        value = parsed;
        return valid;
    }
    internal override object? Prepare(string draft, out string? error)
    {
        (bool valid, T value) = _parse(draft);
        error = valid ? _validate?.Invoke(value) : "Enter a valid value.";
        return value;
    }
    internal override void Commit(object? value) => CommittedValue = (T)value!;
    internal override void ResetDraft() => DraftValue = CommittedValue;
}

public static class UiFormFields
{
    public static UiFormField<string> Text(UiSymbolId id, string label, string initial,
        Func<string, string?>? validate = null)
        => new(id, label, initial ?? throw new ArgumentNullException(nameof(initial)), UiFormFieldKind.Text,
            text => (true, text), text => text ?? throw new ArgumentNullException(nameof(text)), validate);

    /// <summary>Numeric drafts use invariant decimal notation; domain validators supply range policy.</summary>
    public static UiFormField<decimal> Number(UiSymbolId id, string label, decimal initial,
        Func<decimal, string?>? validate = null)
        => new(id, label, initial, UiFormFieldKind.Number,
            text => (decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out decimal value), value),
            value => value.ToString(CultureInfo.InvariantCulture), validate);

    public static UiFormField<bool> Toggle(UiSymbolId id, string label, bool initial,
        string onLabel = "On", string offLabel = "Off")
        => new(id, label, initial, UiFormFieldKind.Toggle,
            text => (bool.TryParse(text, out bool value), value), value => value.ToString(), null,
            Array.AsReadOnly(new[] { new UiFormOption("True", onLabel), new UiFormOption("False", offLabel) }));

    public static UiFormField<T> Choice<T>(UiSymbolId id, string label, T initial,
        IReadOnlyList<T> values, Func<T, string> describe, Func<T, string?>? validate = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(describe);
        if (values.Count == 0) throw new ArgumentException("A choice requires options.", nameof(values));
        T[] copy = values.ToArray();
        var unique = new HashSet<T>();
        foreach (T value in copy)
            if (!unique.Add(value)) throw new ArgumentException("Choice values must be unique.", nameof(values));
        string Format(T value)
        {
            int index = Array.IndexOf(copy, value);
            if (index < 0) throw new ArgumentException("The value is absent from the choices.", nameof(value));
            return index.ToString(CultureInfo.InvariantCulture);
        }
        var options = Array.AsReadOnly(copy.Select((value, index) =>
            new UiFormOption(index.ToString(CultureInfo.InvariantCulture), describe(value))).ToArray());
        if (options.Any(option => string.IsNullOrWhiteSpace(option.Label)))
            throw new ArgumentException("Choice labels are required.", nameof(describe));
        return new UiFormField<T>(id, label, initial, UiFormFieldKind.Choice,
            text => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                && (uint)index < (uint)copy.Length ? (true, copy[index]) : (false, default!),
            Format, validate, options);
    }
}

public interface IUiSemanticFormSource : IUiSemanticSource
{
    IReadOnlyList<UiSemanticFormField> Fields { get; }
}

/// <summary>Fixed-shape form. Apply validates every draft before publishing all typed committed values.</summary>
public sealed class UiFormState : IUiSemanticSource<IReadOnlyList<UiSemanticFormField>>, IUiSemanticFormSource, IDisposable
{
    private readonly ReadOnlyCollection<UiSemanticFormField> _fields;
    private readonly HashSet<IUiMutableSemanticSource<string>> _sources = new(ReferenceEqualityComparer.Instance);
    private bool _operating;
    private bool _disposed;

    public UiFormState(params UiSemanticFormField[] fields)
    {
        if (fields == null || fields.Length == 0)
            throw new ArgumentException("A semantic form requires at least one field.", nameof(fields));
        var ids = new HashSet<UiSymbolId>();
        var copy = (UiSemanticFormField[])fields.Clone();
        foreach (UiSemanticFormField field in copy)
        {
            if (field == null || !ids.Add(field.Id))
                throw new ArgumentException("Form fields must be non-null with unique IDs.", nameof(fields));
            _sources.Add(field.Value);
        }
        _fields = Array.AsReadOnly(copy);
        try { foreach (var source in _sources) source.Changed += OnFieldChanged; }
        catch { foreach (var source in _sources) source.Changed -= OnFieldChanged; throw; }
    }

    public IReadOnlyList<UiSemanticFormField> Fields => _fields;
    public IReadOnlyList<UiSemanticFormField> Value => _fields;
    public Type ValueType => typeof(IReadOnlyList<UiSemanticFormField>);
    public object UntypedValue => _fields;
    public event Action? Changed;

    public bool Apply()
    {
        EnsureActive();
        _operating = true;
        bool valid = true;
        try
        {
            string[] drafts = _fields.Select(field => field.Value.Value).ToArray();
            object?[] values = new object?[_fields.Count];
            string?[] errors = new string?[_fields.Count];
            for (int index = 0; index < _fields.Count; index++)
            {
                values[index] = _fields[index].Prepare(drafts[index], out errors[index]);
                valid &= errors[index] == null;
            }
            for (int index = 0; index < _fields.Count; index++)
                if (_fields[index].Value.Value != drafts[index])
                    throw new InvalidOperationException("A validator changed the form draft.");
            for (int index = 0; index < _fields.Count; index++)
            {
                _fields[index].Error = errors[index];
                if (valid) _fields[index].Commit(values[index]);
            }
        }
        finally { _operating = false; }
        Changed?.Invoke();
        return valid;
    }

    public void Reset()
    {
        EnsureActive();
        _operating = true;
        try
        {
            foreach (UiSemanticFormField field in _fields) { field.Error = null; field.ResetDraft(); }
        }
        finally { _operating = false; }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var source in _sources) source.Changed -= OnFieldChanged;
        _disposed = true;
    }

    private void EnsureActive()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UiFormState));
        if (_operating) throw new InvalidOperationException("A form operation is already in progress.");
    }
    private void OnFieldChanged()
    {
        if (_operating || _disposed) return;
        foreach (var field in _fields) field.Error = null;
        Changed?.Invoke();
    }
}
