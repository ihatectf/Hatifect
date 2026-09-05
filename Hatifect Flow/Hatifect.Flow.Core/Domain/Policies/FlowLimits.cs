using System;

namespace Hatifect.Flow.Domain.Policies;

internal sealed class FlowLimits
{
    public int MaxStations { get; }
    public int MaxLinks { get; }
    public int MaxParcels { get; }
    public int MaxRouteVisits { get; }
    public int MaxRoutePlans { get; }
    public int MaxDeliveryAttempts { get; }
    public int MaxEvents { get; }
    public int MaxOperationsPerAdvance { get; }
    public int MaxCargoUnits { get; }
    public int MaxPendingOperations { get; }

    public FlowLimits(int maxStations = 2048, int maxLinks = 4096,
        int maxParcels = 1024, int maxRouteVisits = 2048, int maxRoutePlans = 256,
        int maxDeliveryAttempts = 3, int maxEvents = 256,
        int maxOperationsPerAdvance = 64, int maxCargoUnits = 1000000,
        int maxPendingOperations = 1024)
    {
        MaxStations = Positive(maxStations);
        MaxLinks = Positive(maxLinks);
        MaxParcels = Positive(maxParcels);
        MaxRouteVisits = Positive(maxRouteVisits);
        MaxRoutePlans = Positive(maxRoutePlans);
        MaxDeliveryAttempts = Positive(maxDeliveryAttempts);
        MaxEvents = Positive(maxEvents);
        MaxOperationsPerAdvance = Positive(maxOperationsPerAdvance);
        MaxCargoUnits = Positive(maxCargoUnits);
        MaxPendingOperations = Positive(maxPendingOperations);
    }

    private static int Positive(int value) => value > 0
        ? value : throw new ArgumentOutOfRangeException(nameof(value), "Limits must be positive.");
}
