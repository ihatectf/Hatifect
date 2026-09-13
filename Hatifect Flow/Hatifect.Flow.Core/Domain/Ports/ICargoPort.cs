using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Domain.Identity;

namespace Hatifect.Flow.Domain.Ports;

// One session and Station own a Port. Apply atomically commits a whole-batch
// inventory mutation and its immutable receipt. No completion may occur after
// Apply returns/throws. Reads use the same non-evicting journal. No reentrant
// runtime mutation is permitted. This is an internal in-memory contract only.
internal interface ICargoPort
{
    void Bind(PortAuthority authority, StationId station);
    CargoManifest? ReadCargo(CargoId cargo);
    PortResult Apply(PortTransfer transfer);
    PortResult ReadResult(PortTransfer transfer);
}
