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
    /// <summary>Monotonic publication content revision, independent of the label measurement hash.
    /// Includes payload, icon and text changes. Legacy sources use zero until their first change.</summary>
    private long _itemRevision;
    public long ItemRevision
    {
        get => _itemRevision;
        init
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Item revision must be nonnegative.");
            _itemRevision = value;
        }
    }
    private UiSymbolId? _icon;
    public UiSymbolId? Icon
    {
        get => _icon;
        init
        {
            if (value is { IsValid: false }) throw new ArgumentException("An icon requires a stable asset ID.", nameof(value));
            _icon = value;
        }
    }
}

/// <summary>Random-access semantic collection contract used by bounded runtime virtualization.</summary>
public interface IUiSemanticCollectionSource : IUiSemanticSource
{
    int Count { get; }
    UiSemanticCollectionItem GetItem(int index);
}

/// <summary>Optional capability required for adaptive virtualization of external sources.
/// Revision is nonnegative and changes with content, order or count, but not selection alone.
/// HasSupportingText describes the entire snapshot without enumeration. TryGetIndex uses stable IDs
/// and must be bounded (constant or logarithmic time). Publish complete snapshots before Changed;
/// do not mutate a source during a UI dispatch or layout pass.</summary>
public interface IUiSemanticCollectionMetadata
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
    IUiVersionedSemanticSource,
    IUiSemanticCollectionSnapshotSource,
    IUiSemanticCollectionMetadata
{
    private readonly ReadOnlyCollection<T> _values;
    private readonly ReadOnlyCollection<UiSemanticCollectionItem> _items;
    private readonly IReadOnlyDictionary<UiSymbolId, int> _indices;
    private readonly bool _hasSupportingText;
    private readonly UiCollectionReadSnapshot<T> _readSnapshot;

    public UiCollectionSource(
        IReadOnlyList<T> values,
        Func<T, UiSymbolId> identify,
        Func<T, string>? label = null,
        Func<T, string?>? supportingText = null)
        : this(values, identify, label, supportingText, null) { }

    public UiCollectionSource(
        IReadOnlyList<T> values,
        Func<T, UiSymbolId> identify,
        Func<T, string>? label,
        Func<T, string?>? supportingText,
        Func<T, UiSymbolId?>? icon)
    {
        var snapshot = UiSemanticCollectionSnapshot.Create(values, identify, label, supportingText, icon);
        _values = snapshot.Values;
        _items = snapshot.Items;
        _indices = UiSemanticCollectionSnapshot.Indices(_items);
        _hasSupportingText = UiSemanticCollectionSnapshot.HasSupportingText(_items);
        _readSnapshot = new(_values, _items, _indices, _hasSupportingText, 0);
    }

    public IReadOnlyList<T> Value => _values;
    public long Version => 0;
    public Type ValueType => typeof(IReadOnlyList<T>);
    public object UntypedValue => _values;
    public int Count => _items.Count;
    public UiSemanticCollectionItem GetItem(int index) => _items[index];
    public IUiSemanticCollectionSnapshot CaptureSnapshot() => _readSnapshot;
    long IUiSemanticCollectionMetadata.Revision => 0;
    bool IUiSemanticCollectionMetadata.HasSupportingText => _hasSupportingText;
    bool IUiSemanticCollectionMetadata.TryGetIndex(UiSymbolId item, out int index)
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
    IUiVersionedSemanticSource,
    IUiSemanticCollectionSnapshotSource,
    IUiSemanticCollectionMetadata
{
    private readonly Func<T, UiSymbolId> _identify;
    private readonly Func<T, string>? _label;
    private readonly Func<T, string?>? _supportingText;
    private readonly Func<T, UiSymbolId?>? _icon;
    private ReadOnlyCollection<T> _values;
    private ReadOnlyCollection<UiSemanticCollectionItem> _items;
    private IReadOnlyDictionary<UiSymbolId, int> _indices;
    private bool _hasSupportingText;
    private long _revision;
    private UiCollectionReadSnapshot<T> _readSnapshot;

    public UiCollectionState(
        IReadOnlyList<T> values,
        Func<T, UiSymbolId> identify,
        Func<T, string>? label = null,
        Func<T, string?>? supportingText = null)
        : this(values, identify, label, supportingText, null) { }

    public UiCollectionState(
        IReadOnlyList<T> values,
        Func<T, UiSymbolId> identify,
        Func<T, string>? label,
        Func<T, string?>? supportingText,
        Func<T, UiSymbolId?>? icon)
    {
        _identify = identify ?? throw new ArgumentNullException(nameof(identify));
        _label = label;
        _supportingText = supportingText;
        _icon = icon;
        var snapshot = UiSemanticCollectionSnapshot.Create(values, identify, label, supportingText, icon);
        _values = snapshot.Values;
        _items = snapshot.Items;
        _indices = UiSemanticCollectionSnapshot.Indices(_items);
        _hasSupportingText = UiSemanticCollectionSnapshot.HasSupportingText(_items);
        _readSnapshot = new(_values, _items, _indices, _hasSupportingText, 0);
    }

    public IReadOnlyList<T> Value => _values;
    public long Version => _revision;
    public Type ValueType => typeof(IReadOnlyList<T>);
    public object UntypedValue => _values;
    public int Count => _items.Count;
    public UiSemanticCollectionItem GetItem(int index) => _items[index];
    public IUiSemanticCollectionSnapshot CaptureSnapshot() => _readSnapshot;
    long IUiSemanticCollectionMetadata.Revision => _revision;
    bool IUiSemanticCollectionMetadata.HasSupportingText => _hasSupportingText;
    bool IUiSemanticCollectionMetadata.TryGetIndex(UiSymbolId item, out int index)
        => _indices.TryGetValue(item, out index);
    public event Action? Changed;

    public void Replace(IReadOnlyList<T> values)
    {
        (ReadOnlyCollection<T> nextValues, ReadOnlyCollection<UiSemanticCollectionItem> nextItems) =
            UiSemanticCollectionSnapshot.Create(values, _identify, _label, _supportingText, _icon);
        if (UiSemanticCollectionSnapshot.Equivalent(_items, nextItems)) return;
        long revision = checked(_revision + 1);
        nextItems = UiSemanticCollectionSnapshot.Revise(_items, _indices, nextItems, revision);
        _values = nextValues;
        _items = nextItems;
        _indices = UiSemanticCollectionSnapshot.Indices(_items);
        _hasSupportingText = UiSemanticCollectionSnapshot.HasSupportingText(_items);
        _revision = revision;
        _readSnapshot = new(_values, _items, _indices, _hasSupportingText, _revision);
        Changed?.Invoke();
    }
}

/// <summary>
/// Mutable semantic collection with Experience-owned single selection. Collection replacement
/// preserves selection by stable ID and clears it when that ID no longer exists.
/// </summary>
public sealed class UiSelectableCollectionState<T> :
    IUiSemanticSource<IReadOnlyList<T>>,
    IUiVersionedSemanticSource,
    IUiSelectableCollectionSource,
    IUiSemanticCollectionSnapshotSource,
    IUiSemanticCollectionMetadata
{
    private readonly Func<T, UiSymbolId> _identify;
    private readonly Func<T, string>? _label;
    private readonly Func<T, string?>? _supportingText;
    private readonly Func<T, UiSymbolId?>? _icon;
    private ReadOnlyCollection<T> _values;
    private ReadOnlyCollection<UiSemanticCollectionItem> _items;
    private HashSet<UiSymbolId> _ids;
    private UiSymbolId? _selectedItemId;
    private IReadOnlyDictionary<UiSymbolId, int> _indices;
    private long _revision;
    private long _version;
    private bool _hasSupportingText;
    private UiCollectionReadSnapshot<T> _readSnapshot;

    public UiSelectableCollectionState(
        IReadOnlyList<T> values,
        Func<T, UiSymbolId> identify,
        Func<T, string>? label = null,
        UiSymbolId? selectedItemId = null,
        Func<T, string?>? supportingText = null)
        : this(values, identify, label, selectedItemId, supportingText, null) { }

    public UiSelectableCollectionState(
        IReadOnlyList<T> values,
        Func<T, UiSymbolId> identify,
        Func<T, string>? label,
        UiSymbolId? selectedItemId,
        Func<T, string?>? supportingText,
        Func<T, UiSymbolId?>? icon)
    {
        _identify = identify ?? throw new ArgumentNullException(nameof(identify));
        _label = label;
        _supportingText = supportingText;
        _icon = icon;
        var snapshot = UiSemanticCollectionSnapshot.Create(values, identify, label, supportingText, icon);
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
        _readSnapshot = new(_values, _items, _indices, _hasSupportingText, 0, _selectedItemId);
    }

    public IReadOnlyList<T> Value => _values;
    public long Version => _version;
    public Type ValueType => typeof(IReadOnlyList<T>);
    public object UntypedValue => _values;
    public int Count => _items.Count;
    public UiSymbolId? SelectedItemId => _selectedItemId;
    public UiSemanticCollectionItem GetItem(int index) => _items[index];
    public IUiSemanticCollectionSnapshot CaptureSnapshot() => _readSnapshot;
    long IUiSemanticCollectionMetadata.Revision => _revision;
    bool IUiSemanticCollectionMetadata.HasSupportingText => _hasSupportingText;
    bool IUiSemanticCollectionMetadata.TryGetIndex(UiSymbolId item, out int index)
        => _indices.TryGetValue(item, out index);
    public event Action? Changed;

    public bool TrySelect(UiSymbolId item)
    {
        if (!item.IsValid) throw new ArgumentException("A stable semantic item ID is required.", nameof(item));
        if (!_ids.Contains(item)) return false;
        if (_selectedItemId == item) return true;
        long version = checked(_version + 1);
        _selectedItemId = item;
        _version = version;
        _readSnapshot = new(_values, _items, _indices, _hasSupportingText, _revision, _selectedItemId, _version);
        Changed?.Invoke();
        return true;
    }

    public bool ClearSelection()
    {
        if (_selectedItemId == null) return false;
        long version = checked(_version + 1);
        _selectedItemId = null;
        _version = version;
        _readSnapshot = new(_values, _items, _indices, _hasSupportingText, _revision, version: _version);
        Changed?.Invoke();
        return true;
    }

    public void Replace(IReadOnlyList<T> values)
    {
        (ReadOnlyCollection<T> nextValues, ReadOnlyCollection<UiSemanticCollectionItem> nextItems) =
            UiSemanticCollectionSnapshot.Create(values, _identify, _label, _supportingText, _icon);
        HashSet<UiSymbolId> nextIds = Ids(nextItems);
        UiSymbolId? nextSelection = _selectedItemId is { } selected && nextIds.Contains(selected)
            ? selected
            : null;
        if (UiSemanticCollectionSnapshot.Equivalent(_items, nextItems) && nextSelection == _selectedItemId)
            return;
        long revision = checked(_revision + 1), version = checked(_version + 1);
        nextItems = UiSemanticCollectionSnapshot.Revise(_items, _indices, nextItems, version);
        _values = nextValues;
        _items = nextItems;
        _ids = nextIds;
        _indices = UiSemanticCollectionSnapshot.Indices(_items);
        _hasSupportingText = UiSemanticCollectionSnapshot.HasSupportingText(_items);
        _selectedItemId = nextSelection;
        _revision = revision;
        _version = version;
        _readSnapshot = new(_values, _items, _indices, _hasSupportingText, _revision, _selectedItemId, _version);
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
        Func<T, string?>? supportingText,
        Func<T, UiSymbolId?>? icon = null)
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
                ContentVersion(display, supporting)) { Icon = icon?.Invoke(value) };
        }
        return (Array.AsReadOnly(valueSnapshot), Array.AsReadOnly(itemSnapshot));
    }

    public static bool Equivalent(
        IReadOnlyList<UiSemanticCollectionItem> left,
        IReadOnlyList<UiSemanticCollectionItem> right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left.Count != right.Count) return false;
        for (int index = 0; index < left.Count; index++)
        {
            if (!ItemEquivalent(left[index], right[index])) return false;
        }
        return true;
    }

    internal static bool ItemEquivalent(UiSemanticCollectionItem left, UiSemanticCollectionItem right)
        => left.Id == right.Id && string.Equals(left.Label, right.Label, StringComparison.Ordinal)
            && string.Equals(left.SupportingText, right.SupportingText, StringComparison.Ordinal)
            && left.ContentVersion == right.ContentVersion && left.Icon == right.Icon && Equals(left.Value, right.Value);

    internal static ReadOnlyCollection<UiSemanticCollectionItem> Revise(IReadOnlyList<UiSemanticCollectionItem> previous,
        IReadOnlyDictionary<UiSymbolId, int> previousIndices, IReadOnlyList<UiSemanticCollectionItem> next, long version)
    {
        var result = new UiSemanticCollectionItem[next.Count];
        for (int index = 0; index < next.Count; index++)
        {
            var item = next[index];
            var old = previousIndices.TryGetValue(item.Id, out int oldIndex) ? previous[oldIndex] : null;
            result[index] = old is not null && ItemEquivalent(old, item) ? old : item with { ItemRevision = version };
        }
        return Array.AsReadOnly(result);
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
