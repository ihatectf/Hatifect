using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Policies;
using Hatifect.Flow.Domain.Scheduling;
using Hatifect.Flow.Domain.Ports;

namespace Hatifect.Flow.Domain.Shipments;

internal sealed record Parcel(ParcelId Id, ShipmentId ShipmentId, CargoId CargoId, CargoManifest Manifest,
    ServicePolicy PolicySnapshot, ParcelState State, long Version, int DeliveryAttempts,
    StationId CurrentStation, ScheduledOperation? PendingOperation, PortTransfer? PendingTransfer = null,
    int CargoDispatch = 0);
