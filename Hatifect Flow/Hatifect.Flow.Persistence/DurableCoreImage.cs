using System;
using Hatifect.Flow.Domain.Checkpoints;

namespace Hatifect.Flow.Infrastructure.Persistence;

internal sealed record DurableCoreImage(Guid PairId, long ProviderRevision,
    TransferKey? Intent, CheckpointImage Core, AdmissionIntent? Admission = null,
    long ProviderConfigurationRevision = 0, StationRegistrationIntent? Registration = null,
    CargoProvisionIntent? Provision = null, PortCapacityIntent? Capacity = null);
