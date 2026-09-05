using Hatifect.Flow.Domain.Identity;

namespace Hatifect.Flow.Domain.Network;

internal sealed record Link(LinkId Id, StationId Origin, StationId Destination,
    int Capacity, long TransitTicks);
