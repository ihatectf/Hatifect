using Hatifect.Flow.Domain.Checkpoints;

namespace Hatifect.Flow.Domain.Ports;

// Complete transport-custody projection and journal. Fake providers also own
// their whole physical inventory; same-save adapters retain historical station
// custody without restoring items into player inventories. This is not a
// transaction contract for independently durable external inventories.
internal interface ICheckpointCargoPort : ICargoPort
{
    PortCheckpoint CaptureCheckpoint();
}
