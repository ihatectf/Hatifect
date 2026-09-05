using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Runtime.Visual.Theming;

public abstract class UiThemeToken
{
    protected UiThemeToken(UiSymbolId id, UiSemanticType semanticType, Type valueType)
    {
        if (!id.IsValid) throw new ArgumentException("A stable theme token ID is required.", nameof(id));
        Id = id;
        SemanticType = semanticType ?? throw new ArgumentNullException(nameof(semanticType));
        ValueType = valueType ?? throw new ArgumentNullException(nameof(valueType));
    }

    public UiSymbolId Id { get; }
    public UiSemanticType SemanticType { get; }
    public Type ValueType { get; }
}

public sealed class UiThemeToken<T> : UiThemeToken where T : notnull
{
    public UiThemeToken(UiSymbolId id, UiSemanticType semanticType)
        : base(id, semanticType, typeof(T)) { }
}

public sealed class UiThemeBuilder
{
    private readonly UiTheme? _baseTheme;
    private readonly Dictionary<UiSymbolId, UiThemeEntry> _values = new();
    private bool _built;

    public UiThemeBuilder(UiTheme? baseTheme = null) => _baseTheme = baseTheme;

    public UiThemeBuilder Set<T>(UiThemeToken<T> token, T value) where T : notnull
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(token);
        if (value is null) throw new ArgumentNullException(nameof(value));
        if (!_values.TryAdd(token.Id, new UiThemeEntry(token.SemanticType, token.ValueType, value)))
            throw new InvalidOperationException($"Theme token '{token.Id}' is assigned more than once.");
        return this;
    }

    public UiTheme Build(UiSymbolId id)
    {
        EnsureMutable();
        if (!id.IsValid) throw new ArgumentException("A stable theme ID is required.", nameof(id));
        _built = true;
        return new UiTheme(id, _baseTheme, new Dictionary<UiSymbolId, UiThemeEntry>(_values));
    }

    private void EnsureMutable()
    {
        if (_built) throw new InvalidOperationException("A theme builder is immutable after Build().");
    }
}

public sealed class UiTheme
{
    private readonly UiTheme? _baseTheme;
    private readonly IReadOnlyDictionary<UiSymbolId, UiThemeEntry> _values;

    internal UiTheme(UiSymbolId id, UiTheme? baseTheme, IDictionary<UiSymbolId, UiThemeEntry> values)
    {
        Id = id;
        _baseTheme = baseTheme;
        _values = new ReadOnlyDictionary<UiSymbolId, UiThemeEntry>(values);
    }

    public UiSymbolId Id { get; }

    public T Resolve<T>(UiThemeToken<T> token) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(token);
        UiThemeEntry entry = ResolveEntry(token.Id);
        if (entry.ValueType != token.ValueType || entry.SemanticType != token.SemanticType)
            throw new InvalidOperationException($"Theme token '{token.Id}' is bound to an incompatible value type.");
        return (T)entry.Value;
    }

    internal object Resolve(UiSymbolValue token)
    {
        UiThemeEntry entry = ResolveEntry(token.Symbol);
        if (entry.SemanticType != token.Type)
            throw new InvalidOperationException(
                $"Theme token '{token.Name}' has semantic type {entry.SemanticType}, expected {token.Type}.");
        return entry.Value;
    }

    private UiThemeEntry ResolveEntry(UiSymbolId id)
    {
        if (_values.TryGetValue(id, out UiThemeEntry? entry)) return entry;
        if (_baseTheme != null) return _baseTheme.ResolveEntry(id);
        throw new KeyNotFoundException($"Theme '{Id}' does not define token '{id}'.");
    }
}

internal sealed record UiThemeEntry(UiSemanticType SemanticType, Type ValueType, object Value);
