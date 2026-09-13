using System;

namespace Hatifect.Flow.Infrastructure.Persistence;

internal sealed record PortCapacityIntent(Guid StationId, int MaxCargoBatches, int MaxReceipts);
