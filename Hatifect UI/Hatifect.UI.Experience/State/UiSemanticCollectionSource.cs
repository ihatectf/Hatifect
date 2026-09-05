using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Hatifect.UI.Experience;

public sealed record UiSemanticCollectionItem
{
    public UiSemanticCollectionItem(
        UiSymbolId id,
        string label,
        object? value,
        string? supportingText = null,
        long contentVersion = 0)
    {
        if (!id.IsValid) throw new ArgumentException("A stable semantic item ID is required.", nameof(id));
        Label = label ?? throw new ArgumentNullException(nameof(label));
        Id = id;
        Value = value;
        SupportingText = supportingText;
        ContentVersion = contentVersion;
    }

    public UiSymbolId Id { get; }
    public string Label { get; }
    public object? Value { get; }
    public string? SupportingText { get; }
    public long ContentVersion { get; }
}

/// <summary>Random-access semantic collection contract used by bounded runtime virtualization.</summary>
public interface IUiSemanticCollectionSource : IUiSemanticSource
{
    int Count { get; }
    UiSemanticCollectionItem GetItem(int index);
}

/// <summary>Friend-assembly metadata for runtime invalidation and stable-ID anchor lookup.</summary>
internal interface IUiCollectionRuntimeMetadata
{
    long Revision { get; }
    bool HasSupportingText { get; }
    bool TryGetIndex(UiSymbolId item, out int index);
}

/// <summary>
/// Semantic collection whose single-selection state is owned by the Experience.
/// Presentation runtimes may request selection by stable item ID, but never own the selected value.
/// </summary>
public interface IUiSelectableCollectionSource : IUiSemanticCollectionSource
{
    UiSymbolId? SelectedItemId { get; }
    bool TrySelect(UiSymbolId item);
}

/// <summary>Immutable collection source with explicit, stable item identity.</summary>
public sealed class UiCollectionSource<T> :
    IUiSemanticSource<IReadOnlyList<T>>,
    IUiSemanticCollectionSource,
    IUiCollectionRuntimeMetadata
{
    private readonly ReadOnlyCollection<T> _values;
    private readonly ReadOnlyCollection<UiSemanticCollectionItem> _items;
    private readonly IReadOnlyDictionary<UiSymbolId, int> _indices;
    private readonly bool _hasSupportingText;

    public UiCollectionSource(
        IReadOnlyList<T> values,
        Func<T, UiSymbolId> identify,
        Func<T, string>? label = null,
        Func<T, string?>? supportingText = null)
    {
        var snapshot = UiSemanticCollectionSnapshot.Create(values, identify, label, supportingText);
        _values = snapshot.Values;
        _items = snapshot.Items;
        _indices = UiSemanticCollectionSnapshot.Indices(_items);
        _hasSupportingText = UiSemanticCollectionSnapshot.HasSupportingText(_items);
    }

    public IReadOnlyList<T> Value => _values;
    public Type ValueType => typeof(IReadOnlyList<T>);
    public object UntypedValue => _values;
    public int Count => _items.Count;
    public UiSemanticCollectionItem GetItem(int index) => _items[index];
    long IUiCollectionRuntimeMetadata.Revision => 0;
    bool IUiCollectionRuntimeMetadata.HasSupportingText => _hasSupportingText;
    bool IUiCollectionRuntimeMetadata.TryGetIndex(UiSymbolId item, out int index)
        => _indices.TryGetValue(item, out index);

    public event Action? Changed
    {
        add { }
        remove { }
    }
}

/// <summary>Mutable semantic collection state; replacement raises one deterministic change signal.</summary>
public sealed class UiCollectionState<T> :
    IUiSemanticSource<IReadOnlyList<T>>,
    IUiSemanticCollectionSource,
    IUiCollectionRuntimeMetadata
{
    private readonly Func<T, UiSymbolId> _identify;
    private readonly Func<T, string>? _label;
    private readonly Func<T, string?>? _supportingText;
    private ReadOnlyCollection<T> _values;
    private ReadOnlyCollection<UiSemanticCollectionItem> _items;
    private IReadOnlyDictionary<UiSymbolId, int> _indices;
    private bool _hasSupportingText;
    private long _revision;

    public UiCollectionState(
        IReadOnlyList<T> values,
        Func<T, UiSymbolId> identify,
        Func<T, string>? label = null,
        Func<T, string?>? supportingText = null)
    {
        _identify = identify ?? throw new ArgumentNullException(nameof(identify));
        _label = label;
        _supportingText = supportingText;
        var snapshot = UiSemanticCollectionSnapshot.Create(values, identify, label, supportingText);
        _values = snapshot.Values;
        _items = snapshot.Items;
        _indices = UiSemanticCollectionSnapshot.Indices(_items);
        _hasSupportingText = UiSemanticCollectionSnapshot.HasSupportingText(_items);
    }

    public IReadOnlyList<T> Value => _values;
    public Type ValueType => typeof(IReadOnlyList<T>);
    public object UntypedValue => _values;
    public int Count => _items.Count;
    public UiSemanticCollectionItem GetItem(int index) => _items[index];
    long IUiCollectionRuntimeMetadata.Revision => _revision;
    bool IUiCollectionRuntimeMetadata.HasSupportingText => _hasSupportingText;
    bool IUiCollectionRuntimeMetadata.TryGetIndex(UiSymbolId item, out int index)
        => _indices.TryGetValue(item, out index);
    public event Action? Changed;

    public void Replace(IReadOnlyList<T> values)
    {
        (ReadOnlyCollection<T> nextValues, ReadOnlyCollection<UiSemanticCollectionItem> nextItems) =
            UiSemanticCollectionSnapshot.Create(values, _identify, _label, _supportingText);
        if (UiSemanticCollectionSnapshot.Equivalent(_items, nextItems)) return;
        _values = nextValues;
        _items = nextItems;
        _indices = UiSemanticCollectionSnapshot.Indices(_items);
        _hasSupportingText = UiSemanticCollectionSnapshot.HasSupportingText(_items);
        _revision++;
        Changed?.Invoke();
    }
}

/// <summary>
/// Mutable semantic collection with Experience-owned single selection. Collection replacement
/// preserves selection by stable ID and clears it when that ID no longer exists.
/// </summary>
public sealed class UiSelectableCollectionState<T> :
    IUiSemanticSource<IReadOnlyList<T>>,
    IUiSelectableCollectionSource,
    IUiCollectionRuntimeMetadata
{
    private readonly Func<T, UiSymbolId> _identify;
    private readonly Func<T, string>? _label;
    private readonly Func<T, string?>? _supportingText;
    private ReadOnlyCollection<T> _values;
    private ReadOnlyCollection<UiSemanticCollectionItem> _items;
    private HashSet<UiSymbolId> _ids;
    private UiSymbolId? _selectedItemId;
    private IReadOnlyDictionary<UiSymbolId, int> _indices;
    private long _revision;
    private bool _hasSupportingText;

    public UiSelectableCollectionState(
        IReadOnlyList<T> values,
        Func<T, UiSymbolId> identify,
        Func<T, string>? label = null,
        UiSymbolId? selectedItemId = null,
        Func<T, string?>? supportingText = null)
    {
        _identify = identify ?? throw new ArgumentNullException(nameof(identify));
        _label = label;
        _supportingText = supportingText;
        var snapshot = UiSemanticCollectionSnapshot.Create(values, identify, label, supportingText);
        _values = snapshot.Values;
        _items = snapshot.Items;
        _ids = Ids(_items);
        _indices = UiSemanticCollectionSnapshot.Indices(_items);
        _hasSupportingText = UiSemanticCollectionSnapshot.HasSupportingText(_items);
        if (selectedItemId is { } selected && !_ids.Contains(selected))
            throw new ArgumentException(
                $"Selected collection item '{selected}' is absent from the collection.",
                nameof(selectedItemId));
        _selectedItemId = selectedItemId;
    }

    public IReadOnlyList<T> Value => _values;
    public Type ValueType => typeof(IReadOnlyList<T>);
    public object UntypedValue => _values;
    public int Count => _items.Count;
    public UiSymbolId? SelectedItemId => _selectedItemId;
    public UiSemanticCollectionItem GetItem(int index) => _items[index];
    long IUiCollectionRuntimeMetadata.Revision => _revision;
    bool IUiCollectionRuntimeMetadata.HasSupportingText => _hasSupportingText;
    bool IUiCollectionRuntimeMetadata.TryGetIndex(UiSymbolId item, out int index)
        => _indices.TryGetValue(item, out index);
    public event Action? Changed;

    public bool TrySelect(UiSymbolId item)
    {
        if (!item.IsValid) throw new ArgumentException("A stable semantic item ID is required.", nameof(item));
        if (!_ids.Contains(item)) return false;
        if (_selectedItemId == item) return true;
        _selectedItemId = item;
        Changed?.Invoke();
        return true;
    }

    public bool ClearSelection()
    {
        if (_selectedItemId == null) return false;
        _selectedItemId = null;
        Changed?.Invoke();
        return true;
    }

    public void Replace(IReadOnlyList<T> values)
    {
        (ReadOnlyCollection<T> nextValues, ReadOnlyCollection<UiSemanticCollectionItem> nextItems) =
            UiSemanticCollectionSnapshot.Create(values, _identify, _label, _supportingText);
        HashSet<UiSymbolId> nextIds = Ids(nextItems);
        UiSymbolId? nextSelection = _selectedItemId is { } selected && nextIds.Contains(selected)
            ? selected
            : null;
        if (UiSemanticCollectionSnapshot.Equivalent(_items, nextItems) && nextSelection == _selectedItemId)
            return;
        _values = nextValues;
        _items = nextItems;
        _ids = nextIds;
        _indices = UiSemanticCollectionSnapshot.Indices(_items);
        _hasSupportingText = UiSemanticCollectionSnapshot.HasSupportingText(_items);
        _selectedItemId = nextSelection;
        _revision++;
        Changed?.Invoke();
    }

    private static HashSet<UiSymbolId> Ids(IEnumerable<UiSemanticCollectionItem> items)
        => new(items.Select(item => item.Id));
}

internal static class UiSemanticCollectionSnapshot
{
    public static (
        ReadOnlyCollection<T> Values,
        ReadOnlyCollection<UiSemanticCollectionItem> Items) Create<T>(
        IReadOnlyList<T> values,
        Func<T, UiSymbolId> identify,
        Func<T, string>? label,
        Func<T, string?>? supportingText)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(identify);
        T[] valueSnapshot = new T[values.Count];
        var itemSnapshot = new UiSemanticCollectionItem[values.Count];
        var ids = new HashSet<UiSymbolId>();
        for (int index = 0; index < values.Count; index++)
        {
            T value = values[index];
            UiSymbolId id = identify(value);
            if (!id.IsValid)
                throw new ArgumentException($"Collection item {index} has no stable ID.", nameof(identify));
            if (!ids.Add(id))
                throw new InvalidOperationException($"Collection item ID '{id}' is duplicated.");
            string display = label?.Invoke(value) ?? (value is null ? string.Empty : value.ToString() ?? string.Empty);
            string? supporting = supportingText?.Invoke(value);
            valueSnapshot[index] = value;
            itemSnapshot[index] = new UiSemanticCollectionItem(
                id,
                display,
                value,
                supporting,
                ContentVersion(display, supporting));
        }
        return (Array.AsReadOnly(valueSnapshot), Array.AsReadOnly(itemSnapshot));
    }

    public static bool Equivalent(
        IReadOnlyList<UiSemanticCollectionItem> left,
        IReadOnlyList<UiSemanticCollectionItem> right)
    {
        if (left.Count != right.Count) return false;
        for (int index = 0; index < left.Count; index++)
        {
            if (left[index].Id != right[index].Id ||
                !string.Equals(left[index].Label, right[index].Label, StringComparison.Ordinal) ||
                !string.Equals(left[index].SupportingText, right[index].SupportingText, StringComparison.Ordinal) ||
                left[index].ContentVersion != right[index].ContentVersion ||
                !Equals(left[index].Value, right[index].Value))
                return false;
        }
        return true;
    }

    public static IReadOnlyDictionary<UiSymbolId, int> Indices(
        IReadOnlyList<UiSemanticCollectionItem> items)
    {
        var result = new Dictionary<UiSymbolId, int>(items.Count);
        for (int index = 0; index < items.Count; index++) result.Add(items[index].Id, index);
        return new ReadOnlyDictionary<UiSymbolId, int>(result);
    }

    public static bool HasSupportingText(IReadOnlyList<UiSemanticCollectionItem> items)
    {
        foreach (UiSemanticCollectionItem item in items)
            if (!string.IsNullOrWhiteSpace(item.SupportingText)) return true;
        return false;
    }

    private static long ContentVersion(string label, string? supportingText)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        Add(label);
        hash ^= 0xFF;
        hash *= prime;
        Add(supportingText ?? string.Empty);
        return unchecked((long)hash);

        void Add(string value)
        {
            foreach (char character in value)
            {
                hash ^= character;
                hash *= prime;
            }
        }
    }
}
