using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Policies;

namespace Hatifect.Flow.Domain.Shipments;

internal sealed record Shipment(ShipmentId Id, StationId Origin, StationId Destination,
    CargoManifest Manifest, ServicePolicy Policy, ParcelId? ParcelId = null);
