using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Shipments;

namespace Hatifect.Flow.Domain.Ports;

internal enum PortTransferKind { Extract, Deposit }
internal enum PortResult { Missing, Applied, Rejected }

internal sealed record PortTransferId(object Session, ParcelId ParcelId,
    PortTransferKind Kind, int Attempt);

internal sealed record PortTransfer(PortTransferId Id, CargoId CargoId,
    StationId StationId, CargoManifest Manifest)
{
    public PortTransferKind Kind => Id.Kind;
}
