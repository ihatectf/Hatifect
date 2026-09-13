using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.Flow.Domain.Checkpoints;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Ports;

namespace Hatifect.Flow.Application.Dispatch;

internal sealed partial class FlowRuntime
{
    // Attach validated logical journals to same-save inventories without replaying any physical transfer.
    internal void AttachSavePorts(object owner, Func<StationId, ICheckpointCargoPort> create)
    {
        using MutationScope mutation = EnterMutation();
        RequireCheckpointOwner(owner);
        ArgumentNullException.ThrowIfNull(create);
        if (_intentSink is not null) throw new InvalidOperationException("Independent durable providers cannot be rebound to game saves.");
        var replacements = new Dictionary<StationId, ICargoPort>();
        foreach (var entry in _ports)
        {
            if (entry.Value is not InMemoryCargoPort previous)
                throw new InvalidOperationException("Only a freshly restored checkpoint can attach game ports.");
            ICheckpointCargoPort replacement = create(entry.Key);
            PortCheckpoint before = previous.CaptureCheckpoint();
            PortCheckpoint after = replacement.CaptureCheckpoint();
            if (before.MaxCargoBatches != after.MaxCargoBatches || before.MaxReceipts != after.MaxReceipts
                || before.AcceptDeposits != after.AcceptDeposits || before.AcceptExtractions != after.AcceptExtractions
                || !before.Inventory.SequenceEqual(after.Inventory) || !before.Receipts.SequenceEqual(after.Receipts)
                || replacements.Values.Any(port => ReferenceEquals(port, replacement)))
                throw new InvalidOperationException("Game port must retain the exact validated custody projection and receipts.");
            replacement.Bind(_authority, entry.Key);
            replacements.Add(entry.Key, replacement);
        }
        _ports.Clear();
        foreach (var entry in replacements) _ports.Add(entry.Key, entry.Value);
    }
}
