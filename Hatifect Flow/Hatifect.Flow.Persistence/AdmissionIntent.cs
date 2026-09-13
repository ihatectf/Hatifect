using System;

namespace Hatifect.Flow.Infrastructure.Persistence;

internal sealed record AdmissionIntent(Guid StationId, bool AcceptDeposits, bool AcceptExtractions);
