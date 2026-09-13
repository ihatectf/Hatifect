using System;

namespace Hatifect.Flow.Infrastructure.Persistence;

internal sealed record StationRegistrationIntent(Guid StationId, int MaxCargoBatches, int MaxReceipts,
    bool AcceptDeposits, bool AcceptExtractions);
