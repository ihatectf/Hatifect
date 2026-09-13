using System;
using Hatifect.Flow.Diagnostics;

namespace Hatifect.Flow.Application.Dispatch;

internal sealed partial class FlowRuntime
{
    internal FlowRuntimeResources ReadResources()
    {
        if (_retired || _mutating || _ownerThread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Resource diagnostics require the idle, live owning thread.");
        return new FlowRuntimeResources(
            new(_ports.Count, _limits.MaxStations), new(_network.LifetimeLinkCount, _limits.MaxLinks), _network.ActiveLinkCount,
            new(_cargo.Count, _limits.MaxParcels), new(_shipments.Count, _limits.MaxParcels), new(_parcels.Count, _limits.MaxParcels),
            new(_operations.Count, _limits.MaxPendingOperations), new(_planner.CacheCount, _limits.MaxRoutePlans),
            new(_events.Count, _limits.MaxEvents), new(_authority.IssuedCount, _authority.Limit), _authority.RetiredCount,
            _planner.SearchCount, _limits.MaxRouteVisits, _limits.MaxOperationsPerAdvance, _limits.MaxDeliveryAttempts);
    }
}
