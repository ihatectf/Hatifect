using System;
using System.Collections.Generic;

namespace Hatifect.UI.Stardew;

/// <summary>
/// Retains required acceptance captures while bounding optional diagnostic history.
/// </summary>
internal sealed class UiBoundedCaptureHistory<T>
{
    private readonly int _capacity;
    private readonly int _requiredReservation;
    private readonly List<T> _values;
    private readonly List<string?> _requiredKeys;
    private readonly HashSet<string> _retainedRequiredKeys = new(StringComparer.Ordinal);
    private int _nextSequence;

    internal UiBoundedCaptureHistory(int capacity, int requiredReservation)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (requiredReservation < 0 || requiredReservation > capacity)
            throw new ArgumentOutOfRangeException(nameof(requiredReservation));
        _capacity = capacity;
        _requiredReservation = requiredReservation;
        _values = new List<T>(capacity);
        _requiredKeys = new List<string?>(capacity);
    }

    internal int Count => _values.Count;
    internal IReadOnlyList<T> Values => _values;

    internal bool TryAdd(string? requiredKey, Func<int, T> create, Action<T>? onEvicted = null)
    {
        ArgumentNullException.ThrowIfNull(create);
        if (requiredKey is { Length: 0 }) throw new ArgumentException("A required capture key cannot be empty.", nameof(requiredKey));
        if (requiredKey is not null && _retainedRequiredKeys.Contains(requiredKey)) return false;

        if (requiredKey is null)
        {
            int outstandingReservation = Math.Max(0, _requiredReservation - _retainedRequiredKeys.Count);
            if (_values.Count >= _capacity - outstandingReservation) return false;
        }

        int eviction = -1;
        if (_values.Count == _capacity)
        {
            if (requiredKey is null) return false;
            eviction = _requiredKeys.FindIndex(value => value is null);
            if (eviction < 0)
                throw new InvalidOperationException("Required Window input capture budget exhausted.");
        }

        T value = create(_nextSequence);
        _nextSequence = checked(_nextSequence + 1);
        if (eviction >= 0)
        {
            T evicted = _values[eviction];
            _values.RemoveAt(eviction);
            _requiredKeys.RemoveAt(eviction);
            onEvicted?.Invoke(evicted);
        }

        _values.Add(value);
        _requiredKeys.Add(requiredKey);
        if (requiredKey is not null) _retainedRequiredKeys.Add(requiredKey);
        return true;
    }
}
