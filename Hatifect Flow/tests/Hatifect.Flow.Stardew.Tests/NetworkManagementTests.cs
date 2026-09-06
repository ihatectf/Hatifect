using System;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Diagnostics;
using Hatifect.Flow.Sessions;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class NetworkManagementTests
{
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
        for (int i = 0; i < FlowGameSession.MaxLinks; i++)
        {
            FlowSnapshot snapshot = session.ReadSnapshot();
            Guid link = Assert.Single(snapshot.Links).Id;
            Assert.Equal(FlowCommandStatus.Applied, session.Execute(new FlowNetworkCommand(snapshot.SessionId, snapshot.Revision, FlowNetworkAction.RemoveLink, Target: link)).Status);
            if (i < FlowGameSession.MaxLinks - 1) session.Link("source", "destination");
        }
        FlowResourceLimitException error = Assert.Throws<FlowResourceLimitException>(() => session.Link("source", "destination"));
        Assert.Equal(FlowAdmissionResource.LifetimeLinks, error.Resource);
        Assert.Equal(new FlowResourceUsage(128, 128), session.ReadResources().Runtime.LifetimeLinks);
        Assert.Equal(0, session.ReadResources().Runtime.ActiveLinks);
        Assert.Equal(new FlowAdmissionRejections(0, 1, 0), session.ReadResources().AdmissionRejections);
        Assert.False(session.IsFaulted);
        Assert.Empty(session.ReadSnapshot().Links);
        FlowGameSave saved = GameSessionWorld.Clone(session.BeginSave());
        using FlowGameSession restored = world.Clone().Open(saved);
        Assert.Throws<FlowResourceLimitException>(() => restored.Link("source", "destination"));
        Assert.Equal(new FlowResourceUsage(128, 128), restored.ReadResources().Runtime.LifetimeLinks);
    }
}
