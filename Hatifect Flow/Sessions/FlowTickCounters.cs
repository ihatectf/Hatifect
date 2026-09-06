namespace Hatifect.Flow.Sessions;

// Owner counters only: reading these never captures a checkpoint or creates a UI projection.
internal readonly record struct FlowTickCounters(long TickInvocations, long Now, int PendingOperations,
    long RouteSearches, long ProcessedOperations, int LastTickProcessedOperations, long RefreshRequests,
    long CheckpointCaptures, long PhysicalApplyCalls);
