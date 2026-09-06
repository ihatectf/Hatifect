using System;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Checkpoints;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Infrastructure.Persistence;
using Hatifect.Flow.Sessions;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class RecoveryGameSessionTests
{
    [Fact]
    public void KnownAppliedReceiptSettlesWithoutPhysicalReplayAndClearsFenceOnlyAfterReconciliation()
    {
        var world = new GameSessionWorld();
        using FlowGameSession original = world.Open();
        world.Configure(original);
        original.Send("source", "destination", 0);
        for (int i = 0; i < 5; i++) original.Tick(true);
        FlowGameSave saved = GameSessionWorld.Clone(original.BeginSave());
        // Crash boundary: port receipt is durable but Core settlement has not completed.
        FlowCheckpoint checkpoint = CheckpointCodec.Decode(saved.Checkpoint).Checkpoint;
        TransferCheckpoint transfer = Assert.Single(checkpoint.Transfers, value => value.Key.Kind == (int)PortTransferKind.Deposit);
        int transferIndex = Array.IndexOf(checkpoint.Transfers, transfer);
        checkpoint.Transfers[transferIndex] = transfer with { Retired = false };
        checkpoint.Parcels[0] = checkpoint.Parcels[0] with { State = (int)ParcelState.DeliveryUncertain, PendingTransfer = transfer.Key };
        checkpoint.Cargo[0] = checkpoint.Cargo[0] with { ClaimedBy = checkpoint.Parcels[0].Id, Owner = new OwnerCheckpoint(null, null, transfer.Key) };
        saved = saved with { RequiresRecovery = true, Checkpoint = CheckpointCodec.Encode(new CheckpointImage(0, checkpoint)) };
        var nextWorld = world.Clone();
        using FlowGameSession next = nextWorld.Open(saved);
        FlowRecoveryIssue issue = Assert.Single(next.ReadRecovery());
        Assert.Equal("Applied", issue.Receipt);
        Assert.True(issue.CanReconcile);
        FlowSnapshot snapshot = next.ReadSnapshot();
        Assert.Equal(FlowApplicationState.RecoveryRequired, snapshot.State);
        var command = new FlowRecoveryCommand(snapshot.SessionId, snapshot.Revision, issue.ParcelId);
        nextWorld.Authority = false;
        Assert.Equal(FlowCommandStatus.Rejected, next.Execute(command).Status);
        nextWorld.Authority = true;
        Assert.Equal(FlowCommandStatus.Conflict, next.Execute(command with { SessionId = Guid.NewGuid() }).Status);
        Assert.Equal(FlowCommandStatus.Applied, next.Execute(command).Status);
        Assert.False(next.IsFaulted);
        Assert.Equal(FlowApplicationState.Active, next.ReadSnapshot().State);
        Assert.Equal(ParcelState.Delivered, Assert.Single(next.ReadSnapshot().Parcels).State);
        Assert.Empty(next.ReadRecovery());
        Assert.Equal(8, Assert.Single(nextWorld.Destination.Items).Stack);
        Assert.Equal(FlowCommandStatus.Rejected, next.Execute(command).Status);
        Assert.False(next.BeginSave().RequiresRecovery);
    }

    [Fact]
    public void MissingReceiptRemainsBlockedEvenWhenTheBoundSourceLooksEmpty()
    {
        var world = new GameSessionWorld();
        world.Source.Items.Add(new StardewValley.Object { ItemId = "388", Stack = 1 });
        using FlowGameSession session = world.Open();
        world.Configure(session);
        session.Send("source", "destination", 0);
        bool entered = false;
        world.Source.Items.OnSlotChanged += (inventory, _, _, item) =>
        {
            if (entered || item is not null) return;
            entered = true;
            inventory.RemoveEmptySlots();
            throw new InvalidOperationException("ambiguous observer");
        };
        session.Tick(true);
        FlowRecoveryIssue issue = Assert.Single(session.ReadRecovery());
        Assert.Equal("Missing", issue.Receipt);
        Assert.False(issue.CanReconcile);
        FlowSnapshot snapshot = session.ReadSnapshot();
        Assert.Equal(FlowCommandStatus.Rejected, session.Execute(new FlowRecoveryCommand(snapshot.SessionId, snapshot.Revision, issue.ParcelId)).Status);
        Assert.Equal(FlowApplicationState.RecoveryRequired, session.ReadSnapshot().State);
        Assert.True(session.BeginSave().RequiresRecovery);
        Assert.Empty(world.Destination.Items);
        Assert.Equal("(O)388", Assert.Single(world.Source.Items).QualifiedItemId);
    }
}
