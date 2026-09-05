using Hatifect.Flow.Domain.Checkpoints;

namespace Hatifect.Flow.Domain.Ports;

// Complete fake-inventory state only. This is not a contract for independently
// durable external inventories. Fault wrappers may delegate to their inner fake.
internal interface ICheckpointCargoPort : ICargoPort
{
    PortCheckpoint CaptureCheckpoint();
}
