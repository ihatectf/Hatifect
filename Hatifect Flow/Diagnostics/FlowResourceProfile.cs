using System;
using Hatifect.Flow.Application;
using Hatifect.Flow.Sessions;

namespace Hatifect.Flow.Diagnostics;

internal sealed record FlowResourceSaveSample(int Index, FlowGameResources Resources, int CheckpointBytes,
    string CheckpointHash, FlowResourceCost Capture, FlowResourceCost Write);
internal sealed record FlowResourceLoadSample(int Index, bool HasAggregate, FlowGameResources Resources,
    int CheckpointBytes, string CheckpointHash, FlowResourceCost Read, FlowResourceCost Restore);
internal sealed record FlowResourceRouteSample(string Kind, int Stations, int Origin, int Destination,
    int Hops, long SearchesBefore, long SearchesAfter, long CacheCount, FlowResourceCost Cost);
internal sealed record FlowResourceWorkSample(int Attempt, Guid SessionId, int ThreadId, int Frames, int WorkTicks,
    int MaximumOperationsPerTick, FlowTickCounters Start, FlowTickCounters End,
    double P50TickMs, double P95TickMs, double P99TickMs, double MaxTickMs,
    long AllocatedBytesTotal, long MaximumAllocatedBytesPerTick);

// Keeps one fixed wave in memory and records only the host's actual Tick observations.
internal sealed class FlowResourceWorkWindow
{
    private const int MaximumFrames = 600;
    private readonly long[] _elapsed = new long[MaximumFrames];
    private readonly Guid _session;
    private readonly int _thread, _attempt;
    private readonly long _frequency;
    private readonly FlowTickCounters _start;
    private FlowTickCounters _end;
    private int _frames, _workTicks, _maximumWork, _operations;
    private long _allocatedTotal, _allocatedMaximum;

    internal FlowResourceWorkWindow(int attempt, Guid session, int thread, long frequency, FlowTickCounters start)
    {
        if (attempt is < 1 or > 16 || session == Guid.Empty || thread <= 0 || frequency <= 0 || start.PendingOperations != 256)
            throw new ArgumentException("A resource wave requires the full admitted queue and a live owner.");
        _attempt = attempt; _session = session; _thread = thread; _frequency = frequency; _start = start; _end = start;
    }

    internal void Observe(Guid session, int thread, bool timePasses, long elapsed, long allocated,
        FlowTickCounters counters, FlowSnapshot snapshot)
    {
        if (_frames >= MaximumFrames || session != _session || snapshot.SessionId != _session || thread != _thread
            || !timePasses || snapshot.State != FlowApplicationState.Active || elapsed < 0 || allocated < 0
            || counters.TickInvocations != _end.TickInvocations + 1 || counters.Now != _end.Now + 1
            || counters.LastTickProcessedOperations is < 0 or > 64)
            throw new InvalidOperationException("The resource wave lost its bounded actual owning tick.");
        _elapsed[_frames++] = elapsed;
        _allocatedTotal = checked(_allocatedTotal + allocated); _allocatedMaximum = Math.Max(_allocatedMaximum, allocated);
        if (counters.LastTickProcessedOperations > 0) _workTicks++;
        _maximumWork = Math.Max(_maximumWork, counters.LastTickProcessedOperations);
        _operations += counters.LastTickProcessedOperations;
        _end = counters;
    }

    internal FlowResourceWorkSample Finish()
    {
        int expectedOperations = _attempt == 1 ? 768 : 256, expectedEffects = _attempt == 1 ? 512 : 256;
        if (_frames == 0 || _end.PendingOperations != 0 || _end.LastTickProcessedOperations <= 0 || _maximumWork != 64
            || _operations != expectedOperations || _end.ProcessedOperations - _start.ProcessedOperations != expectedOperations
            || _end.PhysicalApplyCalls - _start.PhysicalApplyCalls != expectedEffects
            || _end.RefreshRequests - _start.RefreshRequests != _workTicks
            || _end.CheckpointCaptures != _start.CheckpointCaptures || _end.RouteSearches != _start.RouteSearches)
            throw new InvalidOperationException("The full resource wave did not settle within its operation and effect budget.");
        var sorted = new long[_frames]; Array.Copy(_elapsed, sorted, _frames); Array.Sort(sorted);
        double Percentile(int value) => sorted[(_frames * value + 99) / 100 - 1] * (1000.0 / _frequency);
        return new(_attempt, _session, _thread, _frames, _workTicks, _maximumWork, _start, _end,
            Percentile(50), Percentile(95), Percentile(99), Percentile(100), _allocatedTotal, _allocatedMaximum);
    }
}
