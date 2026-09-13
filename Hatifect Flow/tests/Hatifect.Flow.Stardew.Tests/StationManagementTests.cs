using System;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Sessions;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class StationManagementTests
{
    [Fact]
    public void Rename_PreservesIdentityAndCargoAcrossReloadAndInvalidatesOldCommands()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        Guid parcel = session.Send("source", "destination", 0);
        FlowSnapshot before = session.Application.ReadSnapshot();
        Guid station = before.Parcels[0].Origin;
        session.RenameStation("source", "Orchard");
        Assert.Equal("Orchard", session.StationName(station));
        Assert.Equal(FlowCommandStatus.Conflict, session.Application.Execute(new FlowParcelCommand(before.SessionId, before.Revision, parcel, FlowParcelAction.Cancel)).Status);
        Assert.Throws<InvalidOperationException>(() => session.RenameStation("Orchard", "DESTINATION"));
        FlowGameSave saved = GameSessionWorld.Clone(session.BeginSave());
        using FlowGameSession restored = world.Clone().Open(saved);
        Assert.Equal("Orchard", restored.StationName(station));
        Assert.Equal(station, Assert.Single(restored.Application.ReadSnapshot().Parcels).Origin);
        Assert.Equal(8, world.Source.Items[0].Stack);
    }

    [Fact]
    public void RebindRejectedDestination_RetainsReceiptsAndDeliversToExplicitReplacementOnce()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        Guid parcel = session.Send("source", "destination", 0);
        var previous = world.Destination;
        world.Destination = InventoryFixture.CreateChest();
        for (int i = 0; i < 5; i++) session.Tick(true);
        Assert.Equal(ParcelState.DeliveryRejected, Assert.Single(session.Application.ReadSnapshot().Parcels).State);
        session.RebindStation("destination", "Farm", 1, 0, world.Destination);
        FlowSnapshot snapshot = session.Application.ReadSnapshot();
        Assert.Equal(FlowCommandStatus.Applied, session.Application.Execute(new FlowParcelCommand(snapshot.SessionId, snapshot.Revision, parcel, FlowParcelAction.RetryDelivery)).Status);
        session.Tick(true);
        Assert.Equal(8, Assert.Single(world.Destination.Items).Stack);
        Assert.Empty(previous.Items);
        FlowGameSave saved = GameSessionWorld.Clone(session.BeginSave());
        var nextWorld = world.Clone();
        using FlowGameSession restored = nextWorld.Open(saved);
        for (int i = 0; i < 10; i++) restored.Tick(true);
        Assert.Equal(8, Assert.Single(nextWorld.Destination.Items).Stack);
        Assert.Equal(ParcelState.Delivered, Assert.Single(restored.Application.ReadSnapshot().Parcels).State);
    }

    [Fact]
    public void RebindSourceWithQueuedCargoOrForeignStation_RejectsWithoutMovingItems()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        session.Send("source", "destination", 0);
        Assert.Throws<InvalidOperationException>(() => session.RebindStation("source", "Farm", 1, 0, world.Destination));
        world.Destination = InventoryFixture.CreateChest();
        Assert.Throws<InvalidOperationException>(() => session.RebindStation("source", "Farm", 1, 0, world.Destination));
        Assert.False(world.Destination.modData.ContainsKey(FlowGameSession.StationKey));
        Assert.Equal(8, world.Source.Items[0].Stack);
        Assert.Equal(ParcelState.Reserved, Assert.Single(session.Application.ReadSnapshot().Parcels).State);
    }

    [Fact]
    public void RebindQueuedSourceToFreeChest_RejectsThenSucceedsAfterCancellation()
    {
        var world = new GameSessionWorld();
        var replacement = InventoryFixture.CreateChest();
        using var session = new FlowGameSession(1, 1, () => true,
            binding => binding.X switch { 0 => world.Source, 1 => world.Destination, 2 => replacement, _ => null }, world.Errors.Add);
        world.Configure(session);
        Guid parcel = session.Send("source", "destination", 0);
        FlowSnapshot before = session.ReadSnapshot();
        Guid source = Assert.Single(before.Parcels).Origin;

        Assert.Throws<InvalidOperationException>(() => session.RebindStation("source", "Farm", 2, 0, replacement));
        Assert.Equal(before.Revision, session.ReadSnapshot().Revision);
        Assert.False(replacement.modData.ContainsKey(FlowGameSession.StationKey));
        Assert.Equal(source.ToString("D"), world.Source.modData[FlowGameSession.StationKey]);
        Assert.Equal(8, Assert.Single(world.Source.Items).Stack);

        Assert.Equal(FlowCommandStatus.Applied,
            session.Execute(new FlowParcelCommand(before.SessionId, before.Revision, parcel, FlowParcelAction.Cancel)).Status);
        session.RebindStation("source", "Farm", 2, 0, replacement);
        Assert.Equal(source.ToString("D"), replacement.modData[FlowGameSession.StationKey]);
        Assert.False(world.Source.modData.ContainsKey(FlowGameSession.StationKey));
        Assert.Empty(replacement.Items);
        Assert.Equal(8, Assert.Single(world.Source.Items).Stack);
        Assert.Equal(ParcelState.Cancelled, Assert.Single(session.ReadSnapshot().Parcels).State);
    }

    [Fact]
    public void TypedRebindReportsOperationPendingWithoutMovingQueuedSource()
    {
        var world = new GameSessionWorld();
        var replacement = InventoryFixture.CreateChest();
        using var session = new FlowGameSession(1, 1, () => true,
            binding => binding.X switch { 0 => world.Source, 1 => world.Destination, 2 => replacement, _ => null }, world.Errors.Add);
        world.Configure(session);
        session.Send("source", "destination", 0);
        Guid source = Assert.Single(session.ReadSnapshot().Parcels).Origin;
        session.PreparePlayerTarget("Farm", 2, 0, replacement);
        FlowNetworkSnapshot network = session.ReadNetwork();

        FlowCommandResult rejected = session.Execute(new FlowNetworkCommand(network.Transport.SessionId,
            network.Transport.Revision, FlowNetworkAction.RebindStation, Station: source, Target: network.Target));

        Assert.Equal(FlowCommandStatus.Rejected, rejected.Status);
        Assert.Equal(FlowRejectionCode.OperationPending, rejected.Code);
        Assert.Equal("flow.reason.OperationPending", rejected.ReasonKey);
        Assert.Equal(network.Transport.Revision, rejected.Revision);
        Assert.Same(network.Transport, session.ReadSnapshot());
        Assert.False(replacement.modData.ContainsKey(FlowGameSession.StationKey));
        Assert.Equal(source.ToString("D"), world.Source.modData[FlowGameSession.StationKey]);
        Assert.Equal(8, Assert.Single(world.Source.Items).Stack);
        Assert.Equal(ParcelState.Reserved, Assert.Single(session.ReadSnapshot().Parcels).State);
        Assert.False(session.IsFaulted);
        Assert.Empty(world.Errors);
    }
}
