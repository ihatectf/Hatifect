using System;
using Hatifect.Flow.Application;
using Hatifect.Flow.Sessions;

namespace Hatifect.Flow.Diagnostics;

// Receives observations AFTER the one production Tick. All storage and sorting are outside its timing interval.
internal sealed class FlowPerformanceWindow
{
    internal const int WarmupFrames = 120, MeasurementFrames = 600;
    private readonly long[] _elapsed = new long[MeasurementFrames];
    private readonly long[] _allocated = new long[MeasurementFrames];
    private readonly Guid _session;
    private readonly int _thread;
    private readonly bool _timePasses;
    private readonly long _frequency;
    private long _lastTick;
    private int _seen;
    private FlowTickCounters _start, _end;
    private FlowSnapshot? _snapshot;

    internal FlowPerformanceWindow(Guid session, int thread, bool timePasses, long frequency, long previousTick)
    {
        if (session == Guid.Empty || thread <= 0 || frequency <= 0 || previousTick < 0)
            throw new ArgumentException("Invalid performance window identity or clock.");
        _session = session; _thread = thread; _timePasses = timePasses; _frequency = frequency; _lastTick = previousTick;
    }

    internal bool Complete => _seen == WarmupFrames + MeasurementFrames;
    internal void Observe(Guid session, int thread, bool timePasses, long elapsed, long allocated,
        FlowTickCounters counters, FlowSnapshot snapshot)
    {
        if (Complete || session != _session || snapshot.SessionId != session || thread != _thread
            || timePasses != _timePasses || elapsed < 0 || allocated < 0 || counters.TickInvocations != _lastTick + 1
            || snapshot.State != FlowApplicationState.Active)
            throw new InvalidOperationException("The performance window lost its single active owning tick, identity or clock.");
        _lastTick = counters.TickInvocations;
        _seen++;
        if (_seen <= WarmupFrames)
        {
            if (_seen == WarmupFrames) { _start = counters; _snapshot = snapshot; }
            return;
        }
        if (!ReferenceEquals(_snapshot, snapshot) || counters.LastTickProcessedOperations != 0)
            throw new InvalidOperationException("A steady tick changed its cached projection or processed work.");
        int index = _seen - WarmupFrames - 1;
        _elapsed[index] = elapsed; _allocated[index] = allocated; _end = counters;
    }

    internal FlowPerformanceWindowReport Finish()
    {
        if (!Complete || _end.TickInvocations - _start.TickInvocations != MeasurementFrames
            || _end.Now - _start.Now != (_timePasses ? MeasurementFrames : 0)
            || _end.PendingOperations != _start.PendingOperations || _end.RouteSearches != _start.RouteSearches
            || _end.ProcessedOperations != _start.ProcessedOperations || _end.RefreshRequests != _start.RefreshRequests
            || _end.CheckpointCaptures != _start.CheckpointCaptures || _end.PhysicalApplyCalls != _start.PhysicalApplyCalls)
            throw new InvalidOperationException("The complete steady window did unexpected work or advanced the wrong clock.");
        long allocatedTotal = 0, allocatedMax = 0;
        foreach (long value in _allocated) { allocatedTotal = checked(allocatedTotal + value); allocatedMax = Math.Max(allocatedMax, value); }
        long[] sorted = (long[])_elapsed.Clone(); Array.Sort(sorted);
        double Milliseconds(int percentile) => sorted[(MeasurementFrames * percentile + 99) / 100 - 1] * (1000.0 / _frequency);
        return new(_session, _thread, WarmupFrames, MeasurementFrames, _timePasses, _start, _end, true,
            Milliseconds(50), Milliseconds(95), Milliseconds(99), Milliseconds(100), allocatedTotal, allocatedMax);
    }
}

internal sealed record FlowPerformanceWindowReport(Guid SessionId, int ThreadId, int WarmupFrames, int Frames, bool TimePasses,
    FlowTickCounters Start, FlowTickCounters End, bool CachedSnapshotUnchanged,
    double P50TickMs, double P95TickMs, double P99TickMs, double MaxTickMs,
    long AllocatedBytesTotal, long MaximumAllocatedBytesPerTick);
