using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Hatifect.UI.Runtime.Caching;

/// <summary>
/// Main-thread least-recently-used cache with an explicit entry budget.
/// By default the cache owns only its index and linked-list nodes. An owner may
/// supply a release callback to transfer value lifetime to the cache; that callback
/// runs on replacement, eviction, and clear.
/// </summary>
internal sealed class UiBoundedCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries;
    private readonly LinkedList<Entry> _recency = new();
    private readonly Action<TValue>? _release;

    public UiBoundedCache(
        int capacity,
        IEqualityComparer<TKey>? comparer = null,
        Action<TValue>? release = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _entries = new Dictionary<TKey, LinkedListNode<Entry>>(capacity, comparer);
        _release = release;
    }

    public int Capacity => _capacity;
    public int Count => _entries.Count;

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (!_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
        {
            value = default;
            return false;
        }

        _recency.Remove(node);
        _recency.AddLast(node);
        value = node.Value.Value;
        return true;
    }

    public void Set(TKey key, TValue value)
    {
        if (_entries.TryGetValue(key, out LinkedListNode<Entry>? existing))
        {
            TValue previous = existing.Value.Value;
            existing.Value = new Entry(key, value);
            _recency.Remove(existing);
            _recency.AddLast(existing);
            if (!ReferenceEquals(previous, value))
                _release?.Invoke(previous);
            return;
        }

        TValue? evicted = default;
        bool hasEvicted = false;
        if (_entries.Count == _capacity)
        {
            LinkedListNode<Entry> oldest = _recency.First
                ?? throw new InvalidOperationException("A full bounded cache has no eviction candidate.");
            _recency.RemoveFirst();
            _entries.Remove(oldest.Value.Key);
            evicted = oldest.Value.Value;
            hasEvicted = true;
        }

        var node = new LinkedListNode<Entry>(new Entry(key, value));
        _recency.AddLast(node);
        _entries.Add(key, node);
        if (hasEvicted)
            _release?.Invoke(evicted!);
    }

    public void Clear()
    {
        if (_release == null)
        {
            _entries.Clear();
            _recency.Clear();
            return;
        }

        TValue[] values = _recency.Select(entry => entry.Value).ToArray();
        _entries.Clear();
        _recency.Clear();

        List<Exception>? failures = null;
        foreach (TValue value in values)
        {
            try
            {
                _release(value);
            }
            catch (Exception ex)
            {
                (failures ??= new List<Exception>()).Add(ex);
            }
        }

        if (failures != null)
            throw new AggregateException("One or more bounded-cache values failed to release.", failures);
    }

    private readonly record struct Entry(TKey Key, TValue Value);
}
