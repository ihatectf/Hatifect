using System;
using System.Diagnostics;

namespace Hatifect.Flow.Diagnostics;

// Brackets one existing operation. The caller prepares delegates and records observations
// outside this interval; a thrown operation is never repeated to obtain a measurement.
internal readonly record struct FlowResourceCost(long ElapsedTicks, long AllocatedBytes)
{
    internal static T Measure<T>(Func<T> operation, out FlowResourceCost cost)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread(), started = Stopwatch.GetTimestamp();
        T result = operation();
        long elapsed = Stopwatch.GetTimestamp() - started;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        cost = new FlowResourceCost(elapsed, allocated);
        return result;
    }

    internal static FlowResourceCost Measure(Action operation)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread(), started = Stopwatch.GetTimestamp();
        operation();
        long elapsed = Stopwatch.GetTimestamp() - started;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        return new FlowResourceCost(elapsed, allocated);
    }
}
