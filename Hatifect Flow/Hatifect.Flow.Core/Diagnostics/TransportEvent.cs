using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Scheduling;
using Hatifect.Flow.Domain.Shipments;

namespace Hatifect.Flow.Diagnostics;

internal sealed record TransportEvent(ParcelId ParcelId, long Sequence, long Tick,
    OperationKind Kind, ParcelState State, CargoOwner Owner);
