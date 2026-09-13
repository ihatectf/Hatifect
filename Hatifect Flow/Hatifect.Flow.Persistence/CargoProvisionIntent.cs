using System;
using Hatifect.Flow.Domain.Checkpoints;

namespace Hatifect.Flow.Infrastructure.Persistence;

internal sealed record CargoProvisionIntent(Guid CargoId, Guid StationId, ManifestCheckpoint Manifest);
