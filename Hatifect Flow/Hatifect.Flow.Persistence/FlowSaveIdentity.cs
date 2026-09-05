using System;
using System.Globalization;

namespace Hatifect.Flow.Infrastructure.Persistence;

// Persisted world identity supplied by the authoritative host, never display text
// or the path/UUID of the process which happens to load the save.
internal readonly record struct FlowSaveIdentity
{
    public FlowSaveIdentity(ulong value)
    {
        if (value == 0) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }

    public ulong Value { get; }
    public override string ToString() => Value.ToString("x16", CultureInfo.InvariantCulture);
}
