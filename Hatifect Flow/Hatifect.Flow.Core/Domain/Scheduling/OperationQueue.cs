using System;
using System.Collections.Generic;

namespace Hatifect.Flow.Domain.Scheduling;

internal sealed class OperationQueue
{
    private readonly SortedSet<ScheduledOperation> _pending = new(Comparer<ScheduledOperation>.Create(Compare));
    private readonly int _limit;

    internal OperationQueue(int limit) => _limit = limit;
    internal int Count => _pending.Count;
    internal bool HasRoom => Count < _limit;
    internal ScheduledOperation? Peek() => _pending.Count > 0 ? _pending.Min : null;
    internal void Remove(ScheduledOperation operation) => _pending.Remove(operation);

    internal void Add(ScheduledOperation operation)
    {
        if (!HasRoom || !_pending.Add(operation))
        {
            throw new InvalidOperationException("Operation queue is full or already contains the operation.");
        }
    }

    private static int Compare(ScheduledOperation left, ScheduledOperation right)
    {
        int order = left.DueTick.CompareTo(right.DueTick);
        if (order == 0)
        {
            order = left.ParcelId.Value.CompareTo(right.ParcelId.Value);
        }
        return order != 0 ? order : left.Sequence.CompareTo(right.Sequence);
    }
}
