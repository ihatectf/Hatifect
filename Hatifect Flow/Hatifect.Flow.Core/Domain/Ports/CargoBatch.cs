using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Shipments;

namespace Hatifect.Flow.Domain.Ports;

internal sealed record CargoBatch(CargoId Id, CargoManifest Manifest, CargoOwner Owner,
    StationId RegistrationStation, ParcelId? ClaimedBy = null, int DispatchCount = 0);
