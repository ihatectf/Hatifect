using Hatifect.Flow.Domain.Identity;

namespace Hatifect.Flow.Domain.Scheduling;

internal sealed record ScheduledOperation(ParcelId ParcelId, long Sequence,
    OperationKind Kind, long DueTick);
