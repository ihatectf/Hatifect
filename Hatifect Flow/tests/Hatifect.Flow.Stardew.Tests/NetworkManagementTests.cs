using System;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Diagnostics;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Inventory;
using Hatifect.Flow.Sessions;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class NetworkManagementTests
{
    [Fact]
    public void CleanNetworkTypedCommandsSurviveRestartAndDeliverSelectedRealItemsExactlyOnce()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        Assert.Empty(session.ReadNetwork().Stations);
        Assert.Empty(session.ReadSnapshot().Links);
        void Bind(string name, int x, StardewValley.Objects.Chest chest)
        {
            session.PreparePlayerTarget("Farm", x, 0, chest);
            FlowNetworkSnapshot network = session.ReadNetwork();
            var command = new FlowNetworkCommand(network.Transport.SessionId, network.Transport.Revision,
                FlowNetworkAction.RegisterStation, Target: network.Target, Name: name);
            Assert.Equal(FlowCommandStatus.Applied, session.Execute(command).Status);
            Assert.Equal(FlowCommandStatus.Conflict, session.Execute(command).Status);
        }
        Bind("source", 0, world.Source);
        Bind("destination", 1, world.Destination);
        FlowNetworkSnapshot configured = session.ReadNetwork();
        Guid source = configured.Stations.Single(station => station.Name == "source").Id;
        Guid destination = configured.Stations.Single(station => station.Name == "destination").Id;
        Assert.Equal(FlowCommandStatus.Applied, session.Execute(new FlowNetworkCommand(configured.Transport.SessionId,
            configured.Transport.Revision, FlowNetworkAction.AddLink, Station: source, Destination: destination,
            Capacity: 999, TransitTicks: 3)).Status);
        Assert.Equal(new FlowRoutePreview(true, 1, 3, 999), session.PreviewRoute(source, destination));
        FlowInventorySlot slot = Assert.Single(session.ReadInventory(source));
        string sourceBeforeAdmission = FlowItemCodec.Encode(world.Source.Items[0]);
        FlowSnapshot ready = session.ReadSnapshot();
        var send = new FlowSendCommand(ready.SessionId, ready.Revision, source, destination, slot.Index, slot.Fingerprint)
        { Quantity = 3 };
        Assert.Equal(FlowCommandStatus.Applied, session.Execute(send).Status);
        Assert.Equal(FlowCommandStatus.Conflict, session.Execute(send).Status);
        FlowParcelSnapshot parcel = Assert.Single(session.ReadSnapshot().Parcels);
        Assert.Equal(ParcelState.Reserved, parcel.State);
        Assert.Equal(3, parcel.Quantity);
        Assert.Equal(source, parcel.CurrentStation);
        Assert.Equal(8, Assert.Single(world.Source.Items).Stack);
        Assert.Empty(world.Destination.Items);

        var expectedCargo = FlowItemCodec.Decode(FlowItemCodec.Encode(world.Source.Items[0]));
        expectedCargo.Stack = 3;
        string expectedDelivery = FlowItemCodec.Encode(expectedCargo);
        var expectedRemainder = FlowItemCodec.Decode(sourceBeforeAdmission);
        expectedRemainder.Stack = 5;
        string expectedSource = FlowItemCodec.Encode(expectedRemainder);
        FlowGameSave saved = GameSessionWorld.Clone(session.BeginSave());
        GameSessionWorld restartedWorld = world.Clone();
        session.Dispose();
        using FlowGameSession restarted = restartedWorld.Open(saved);
        Assert.Equal(FlowCommandStatus.Conflict, restarted.Execute(send).Status);
        Assert.Equal(2, restarted.ReadNetwork().Stations.Count);
        Assert.Single(restarted.ReadSnapshot().Links);
        Assert.Equal(Guid.Empty, restarted.ReadNetwork().Target);
        Assert.Equal(parcel.Id, Assert.Single(restarted.ReadSnapshot().Parcels).Id);
        for (int i = 0; i < 8; i++) restarted.Tick(true);
        FlowParcelSnapshot deliveredParcel = Assert.Single(restarted.ReadSnapshot().Parcels);
        Assert.Equal(ParcelState.Delivered, deliveredParcel.State);
        Assert.Equal(destination, deliveredParcel.CurrentStation);
        Assert.Equal(parcel.CargoId, deliveredParcel.CargoId);
        Assert.Equal(expectedSource, FlowItemCodec.Encode(Assert.Single(restartedWorld.Source.Items)));
        Assert.Equal(expectedDelivery, FlowItemCodec.Encode(Assert.Single(restartedWorld.Destination.Items)));

        FlowGameSave deliveredSave = GameSessionWorld.Clone(restarted.BeginSave());
        GameSessionWorld deliveredWorld = restartedWorld.Clone();
        restarted.Dispose();
        using FlowGameSession delivered = deliveredWorld.Open(deliveredSave);
        FlowSnapshot current = delivered.ReadSnapshot();
        Assert.NotEqual(FlowCommandStatus.Applied, delivered.Execute(new FlowParcelCommand(current.SessionId,
            current.Revision, parcel.Id, FlowParcelAction.RetryDelivery)).Status);
        for (int i = 0; i < 20; i++) delivered.Tick(true);
        FlowParcelSnapshot final = Assert.Single(delivered.ReadSnapshot().Parcels);
        Assert.Equal((deliveredParcel.Id, deliveredParcel.CargoId, deliveredParcel.State, deliveredParcel.CurrentStation, deliveredParcel.Quantity),
            (final.Id, final.CargoId, final.State, final.CurrentStation, final.Quantity));
        Assert.Equal(expectedSource, FlowItemCodec.Encode(Assert.Single(deliveredWorld.Source.Items)));
        Assert.Equal(expectedDelivery, FlowItemCodec.Encode(Assert.Single(deliveredWorld.Destination.Items)));
        Assert.Empty(world.Errors);
        Assert.Empty(restartedWorld.Errors);
        Assert.Empty(deliveredWorld.Errors);
    }

    [Fact]
    public void OpeningWithoutChestRevokesCapturedCapabilityAndItsPublishedRevision()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        session.PreparePlayerTarget("Farm", 1, 0, world.Destination);
        FlowNetworkSnapshot captured = session.ReadNetwork();
        var oldCommand = new FlowNetworkCommand(captured.Transport.SessionId, captured.Transport.Revision,
            FlowNetworkAction.RegisterStation, Target: captured.Target, Name: "Orchard");

        session.PreparePlayerTarget("Farm", 2, 0, null);

        FlowNetworkSnapshot cleared = session.ReadNetwork();
        Assert.Equal(Guid.Empty, cleared.Target);
        Assert.Equal(string.Empty, cleared.TargetDescription);
        Assert.True(cleared.Transport.Revision > captured.Transport.Revision);
        Assert.Equal(FlowCommandStatus.Conflict, session.Execute(oldCommand).Status);
        Assert.Equal(FlowCommandStatus.Conflict,
            session.Execute(oldCommand with { ExpectedRevision = cleared.Transport.Revision }).Status);
        Assert.Empty(session.ReadSnapshot().Stations);
        Assert.False(world.Destination.modData.ContainsKey(FlowGameSession.StationKey));
        session.PreparePlayerTarget("Farm", 2, 0, null);
        Assert.Same(cleared.Transport, session.ReadSnapshot());
    }

    [Fact]
    public void RecoveryInspectionCanForgetTargetWithoutResumingTransport()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        session.CaptureTarget("Farm", 1, 0, world.Destination);
        session.FencePeerFailure(new InvalidOperationException("uncertain physical outcome"));
        session.PreparePlayerTarget("Farm", 1, 0, world.Destination);
        Assert.Equal(Guid.Empty, session.ReadNetwork().Target);
        Assert.Equal(FlowApplicationState.RecoveryRequired, session.ReadSnapshot().State);
        Assert.True(session.IsFaulted);
        Assert.Empty(session.ReadSnapshot().Stations);
        Assert.Empty(session.ReadSnapshot().Parcels);
        session.Dispose();
        Assert.Throws<InvalidOperationException>(() => session.PreparePlayerTarget("Farm", 1, 0, world.Destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidCurrentChestNeverLeavesThePreviousCapabilityUsable(bool replaced)
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        session.PreparePlayerTarget("Farm", 0, 0, world.Source);
        Guid previous = session.ReadNetwork().Target;
        var candidate = world.Destination;
        if (replaced)
        {
            world.Destination = InventoryFixture.CreateChest();
            Assert.Throws<InvalidOperationException>(() => session.PreparePlayerTarget("Farm", 1, 0, candidate));
        }
        else
        {
            candidate.fridge.Value = true;
            session.PreparePlayerTarget("Farm", 1, 0, candidate);
        }
        FlowNetworkSnapshot current = session.ReadNetwork();
        Assert.Equal(Guid.Empty, current.Target);
        Assert.Equal(FlowCommandStatus.Conflict, session.Execute(new FlowNetworkCommand(current.Transport.SessionId,
            current.Transport.Revision, FlowNetworkAction.RegisterStation, Target: previous, Name: "stale")).Status);
        Assert.Empty(session.ReadSnapshot().Stations);
        Assert.Equal(8, Assert.Single(world.Source.Items).Stack);
        Assert.Empty(world.Destination.Items);
        Assert.False(session.IsFaulted);
    }

    [Fact]
    public void RoutePreviewAndLinkRemoval_RespectReservedCapacityAndKeepInFlightCustody()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        FlowLinkSnapshot link = Assert.Single(session.ReadSnapshot().Links);
        Assert.Equal(new FlowRoutePreview(true, 1, 3, 999), session.PreviewRoute(link.Origin, link.Destination));
        session.Send("source", "destination", 0);
        session.Tick(true);
        Assert.Equal(991, session.PreviewRoute(link.Origin, link.Destination).AvailableUnits);
        FlowSnapshot snapshot = session.ReadSnapshot();
        var command = new FlowNetworkCommand(snapshot.SessionId, snapshot.Revision, FlowNetworkAction.RemoveLink, Target: link.Id);
        Assert.Equal(FlowCommandStatus.Applied, session.Execute(command).Status);
        Assert.False(session.PreviewRoute(link.Origin, link.Destination).Found);
        Assert.Equal(FlowCommandStatus.Conflict, session.Execute(command).Status);
        Assert.Null(world.Source.Items[0]);
        for (int i = 0; i < 5; i++) session.Tick(true);
        Assert.Equal(8, Assert.Single(world.Destination.Items).Stack);
        Assert.Empty(session.ReadSnapshot().Links);
    }

    [Fact]
    public void CapturedPhysicalTarget_RejectsReplacementAndForeignSessionWithoutBindingEitherChest()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        session.CaptureTarget("Farm", 1, 0, world.Destination);
        FlowNetworkSnapshot network = session.ReadNetwork();
        var command = new FlowNetworkCommand(network.Transport.SessionId, network.Transport.Revision,
            FlowNetworkAction.RegisterStation, Target: network.Target, Name: "Orchard");
        Assert.Equal(FlowCommandStatus.Conflict, session.Execute(command with { SessionId = Guid.NewGuid() }).Status);
        var original = world.Destination;
        world.Destination = InventoryFixture.CreateChest();
        Assert.Equal(FlowCommandStatus.Conflict, session.Execute(command).Status);
        Assert.Empty(session.ReadSnapshot().Stations);
        Assert.False(original.modData.ContainsKey(FlowGameSession.StationKey));
        Assert.False(world.Destination.modData.ContainsKey(FlowGameSession.StationKey));
        session.CaptureTarget("Farm", 1, 0, world.Destination);
        network = session.ReadNetwork();
        Assert.Equal(FlowCommandStatus.Applied, session.Execute(command with { ExpectedRevision = network.Transport.Revision, Target = network.Target }).Status);
        Assert.Equal("Orchard", Assert.Single(session.ReadNetwork().Stations).Name);
    }

    [Fact]
    public void RemovedLinksStillCountTowardsBoundedHistory_WithoutFaultingSession()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        FlowLinkSnapshot endpoints = Assert.Single(session.ReadSnapshot().Links);
        for (int i = 0; i < FlowGameSession.MaxLinks; i++)
        {
            FlowSnapshot snapshot = session.ReadSnapshot();
            Guid link = Assert.Single(snapshot.Links).Id;
            Assert.Equal(FlowCommandStatus.Applied, session.Execute(new FlowNetworkCommand(snapshot.SessionId, snapshot.Revision, FlowNetworkAction.RemoveLink, Target: link)).Status);
            if (i < FlowGameSession.MaxLinks - 1) session.Link("source", "destination");
        }
        FlowSnapshot full = session.ReadSnapshot();
        FlowCommandResult typedRejection = session.Execute(new FlowNetworkCommand(full.SessionId, full.Revision,
            FlowNetworkAction.AddLink, Station: endpoints.Origin, Destination: endpoints.Destination));
        Assert.Equal(FlowCommandStatus.Rejected, typedRejection.Status);
        Assert.Equal(FlowRejectionCode.LifetimeLinkLimit, typedRejection.Code);
        Assert.Equal("flow.reason.LifetimeLinkLimit", typedRejection.ReasonKey);
        Assert.Equal(full.Revision, typedRejection.Revision);
        FlowResourceLimitException error = Assert.Throws<FlowResourceLimitException>(() => session.Link("source", "destination"));
        Assert.Equal(FlowAdmissionResource.LifetimeLinks, error.Resource);
        Assert.Equal(new FlowResourceUsage(128, 128), session.ReadResources().Runtime.LifetimeLinks);
        Assert.Equal(0, session.ReadResources().Runtime.ActiveLinks);
        Assert.Equal(new FlowAdmissionRejections(0, 2, 0), session.ReadResources().AdmissionRejections);
        Assert.False(session.IsFaulted);
        Assert.Empty(session.ReadSnapshot().Links);
        FlowGameSave saved = GameSessionWorld.Clone(session.BeginSave());
        using FlowGameSession restored = world.Clone().Open(saved);
        FlowSnapshot restoredFull = restored.ReadSnapshot();
        FlowCommandResult restoredRejection = restored.Execute(new FlowNetworkCommand(restoredFull.SessionId, restoredFull.Revision,
            FlowNetworkAction.AddLink, Station: endpoints.Origin, Destination: endpoints.Destination));
        Assert.Equal(FlowRejectionCode.LifetimeLinkLimit, restoredRejection.Code);
        Assert.Throws<FlowResourceLimitException>(() => restored.Link("source", "destination"));
        Assert.Equal(new FlowResourceUsage(128, 128), restored.ReadResources().Runtime.LifetimeLinks);
        Assert.Equal(new FlowAdmissionRejections(0, 2, 0), restored.ReadResources().AdmissionRejections);
    }
}
