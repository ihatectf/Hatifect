using System;
using Hatifect.Flow.Domain.Checkpoints;

namespace Hatifect.Flow.Infrastructure.Persistence;

internal sealed record DurableProviderImage(Guid PairId, Guid NetworkId, long Revision,
    StationCheckpoint[] Stations, DurableProviderReceipt[] Receipts, long ConfigurationRevision = 0);
internal sealed record DurableProviderReceipt(TransferKey Key, Guid CargoId, Guid StationId,
    ManifestCheckpoint Manifest, int Result);
