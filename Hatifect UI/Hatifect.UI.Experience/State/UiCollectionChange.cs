namespace Hatifect.UI.Experience;

public abstract record UiCollectionOperation<T>;
public sealed record UiCollectionInsert<T>(int Index, T Value) : UiCollectionOperation<T>;
public sealed record UiCollectionRemove<T>(int Index, UiSymbolId ItemId) : UiCollectionOperation<T>;
public sealed record UiCollectionMove<T>(int FromIndex, int ToIndex, UiSymbolId ItemId) : UiCollectionOperation<T>;
public sealed record UiCollectionUpdate<T>(int Index, UiSymbolId ItemId, T Value) : UiCollectionOperation<T>;

/// <summary>A complete replacement. The collection structure is copied; application values must be immutable.</summary>
public sealed record UiCollectionReset<T> : UiCollectionOperation<T>
{
    public UiCollectionReset(IEnumerable<T> values)
        => Values = Array.AsReadOnly((values ?? throw new ArgumentNullException(nameof(values))).ToArray());
    public IReadOnlyList<T> Values { get; }
}

/// <summary>Indices address the candidate after preceding operations. Selection is the final stable ID,
/// or null to clear it. A Reset must be the only operation. A selection-only change has no operations.</summary>
public sealed class UiCollectionChange<T>
{
    public const int MaximumOperations = 128;
    public UiCollectionChange(long baseVersion, long version, IEnumerable<UiCollectionOperation<T>> operations,
        UiSymbolId? selectedItemId)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var copied = new List<UiCollectionOperation<T>>();
        foreach (var operation in operations)
        {
            if (copied.Count == MaximumOperations)
                throw new ArgumentException($"A collection change supports at most {MaximumOperations} operations.", nameof(operations));
            copied.Add(operation ?? throw new ArgumentException("Collection operations cannot be null.", nameof(operations)));
        }
        BaseVersion = baseVersion;
        Version = version;
        Operations = copied.AsReadOnly();
        SelectedItemId = selectedItemId;
    }
    public long BaseVersion { get; }
    public long Version { get; }
    public IReadOnlyList<UiCollectionOperation<T>> Operations { get; }
    public UiSymbolId? SelectedItemId { get; }
    public bool IsReset => Operations.Count == 1 && Operations[0] is UiCollectionReset<T>;

    internal bool Equivalent(UiCollectionChange<T> other)
    {
        if (BaseVersion != other.BaseVersion || Version != other.Version || SelectedItemId != other.SelectedItemId
            || Operations.Count != other.Operations.Count) return false;
        for (int index = 0; index < Operations.Count; index++)
        {
            if (Operations[index] is UiCollectionReset<T> left && other.Operations[index] is UiCollectionReset<T> right)
            {
                if (!left.Values.SequenceEqual(right.Values)) return false;
            }
            else if (!Equals(Operations[index], other.Operations[index])) return false;
        }
        return true;
    }
}

/// <summary>A bounded read of collection changes through one committed source version.
/// If the requested history was retired, Changes contains a complete immutable Reset.</summary>
public sealed class UiCollectionChanges<T>
{
    internal UiCollectionChanges(long version, IEnumerable<UiCollectionChange<T>> changes)
    { Version = version; Changes = Array.AsReadOnly(changes.ToArray()); }
    public long Version { get; }
    public IReadOnlyList<UiCollectionChange<T>> Changes { get; }
}
