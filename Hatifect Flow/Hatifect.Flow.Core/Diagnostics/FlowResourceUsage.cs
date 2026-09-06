namespace Hatifect.Flow.Diagnostics;

internal readonly record struct FlowResourceUsage(long Used, long Limit)
{
    internal long Remaining => Limit - Used;
}

// Counts read directly from their owners. Reading diagnostics must not capture a checkpoint,
// build an application projection, query a port or run a route search.
internal readonly record struct FlowRuntimeResources(
    FlowResourceUsage Stations, FlowResourceUsage LifetimeLinks, int ActiveLinks,
    FlowResourceUsage Cargo, FlowResourceUsage Shipments, FlowResourceUsage Parcels,
    FlowResourceUsage PendingOperations, FlowResourceUsage RouteCache, FlowResourceUsage Events,
    FlowResourceUsage IssuedTransfers, int RetiredTransfers, long RouteSearches,
    int MaxRouteVisits, int MaxOperationsPerAdvance, int MaxDeliveryAttempts);

internal readonly record struct FlowPortResources(FlowResourceUsage Custody, FlowResourceUsage Receipts);
