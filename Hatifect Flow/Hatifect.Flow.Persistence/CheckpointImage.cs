using Hatifect.Flow.Domain.Checkpoints;

namespace Hatifect.Flow.Infrastructure.Persistence;

internal sealed record CheckpointImage(long Revision, FlowCheckpoint Checkpoint);
