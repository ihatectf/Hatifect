using System;
using System.Text.Json;
using Hatifect.Flow.Application;
using Hatifect.Flow.Multiplayer;
using Hatifect.Flow.Sessions;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class PeerProtocolTests
{
    [Fact]
    public void ProjectionRoundtripIsDetachedAndDoesNotTransmitHostPhysicalTarget()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        session.CaptureTarget("Farm", 0, 0, world.Source);
        session.Send("source", "destination", 0);
        FlowPeerProjection wire = JsonSerializer.Deserialize<FlowPeerProjection>(JsonSerializer.Serialize(FlowPeerProtocol.Capture(session)))!;
        Assert.True(FlowPeerProtocol.Valid(wire));
        FlowNetworkSnapshot projection = FlowPeerProtocol.Materialize(wire);
        Assert.Equal(Guid.Empty, projection.Target);
        Assert.NotEqual(Guid.Empty, session.ReadNetwork().Target);
        Assert.Equal(session.ReadSnapshot().SessionId, projection.Transport.SessionId);
        Assert.Equal(FlowProviderMode.GameInventory, projection.Transport.ProviderMode);
        Assert.Equal(FlowParcelActions.All, projection.Transport.SupportedOperations);
        Assert.Equal(8, Assert.Single(projection.Transport.Parcels).Quantity);
        Assert.Equal(session.ReadSnapshot().Parcels[0].Availability, projection.Transport.Parcels[0].Availability);
        wire.Parcels[0] = wire.Parcels[0] with { Quantity = 999 };
        wire.Stations[0] = wire.Stations[0] with { Name = "mutated" };
        Assert.Equal(8, Assert.Single(projection.Transport.Parcels).Quantity);
        Assert.DoesNotContain(projection.Stations, station => station.Name == "mutated");
    }

    [Fact]
    public void ProviderAndActionReasonsRoundtripThroughSmapiSerializerAndRejectForgedCapabilities()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        session.Send("source", "destination", 0);
        FlowSnapshot snapshot = session.ReadSnapshot();
        FlowCommandResult rejected = session.Execute(new FlowParcelCommand(Guid.NewGuid(), snapshot.Revision, snapshot.Parcels[0].Id, FlowParcelAction.Cancel));
        var reply = new FlowPeerReply(1, 1, Guid.NewGuid(), 1, FlowPeerProtocol.Capture(session), rejected);
        reply = Newtonsoft.Json.JsonConvert.DeserializeObject<FlowPeerReply>(Newtonsoft.Json.JsonConvert.SerializeObject(reply))!;
        Assert.True(FlowPeerProtocol.Valid(reply));
        Assert.Equal(FlowRejectionCode.StaleSession, reply.Result!.Code);
        Assert.Equal("flow.reason.StaleSession", reply.Result.ReasonKey);
        Assert.Equal(FlowProviderMode.GameInventory, FlowPeerProtocol.Materialize(reply.Projection).Transport.ProviderMode);
        FlowParcelSnapshot parcel = reply.Projection.Parcels[0];
        Assert.False(FlowPeerProtocol.Valid(reply.Projection with { ProviderMode = (FlowProviderMode)999 }));
        Assert.False(FlowPeerProtocol.Valid(reply.Projection with { SupportedOperations = FlowParcelActions.None }));
        Assert.False(FlowPeerProtocol.Valid(reply.Projection with { Parcels = new[] { parcel with { Availability = null! } } }));
        Assert.False(FlowPeerProtocol.Valid(reply.Projection with { Parcels = new[] { parcel with { Availability = parcel.Availability with
            { Reserve = parcel.Availability.Reserve with { Available = true } } } } }));
        Assert.False(FlowPeerProtocol.Valid(reply.Projection with { Parcels = new[] { parcel with { Availability = parcel.Availability with
            { Reserve = parcel.Availability.Reserve with { ReasonKey = "foreign.reason" } } } } }));
    }

    [Fact]
    public void MissingOversizedDuplicateAndDanglingProjectionDataAreRejected()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        FlowPeerProjection wire = FlowPeerProtocol.Capture(session);
        Assert.False(FlowPeerProtocol.Valid(wire with { Stations = null! }));
        Assert.False(FlowPeerProtocol.Valid(wire with { Stations = new FlowStationDetails[33] }));
        Assert.False(FlowPeerProtocol.Valid(wire with { Stations = new[] { wire.Stations[0], wire.Stations[0] } }));
        Assert.False(FlowPeerProtocol.Valid(wire with { Links = new[] { wire.Links[0] with { Origin = Guid.NewGuid() } } }));
        Assert.False(FlowPeerProtocol.Valid(wire with { State = (FlowApplicationState)999 }));
        Assert.False(FlowPeerProtocol.Valid(wire with { TargetDescription = new string('x', 301) }));
        Assert.Throws<ArgumentException>(() => FlowPeerProtocol.Materialize(wire with { Revision = -1 }));
    }

    [Fact]
    public void TypedIntentRejectsMixedBodiesWrongVersionAndForeignInnerSession()
    {
        Guid session = Guid.NewGuid();
        var parcel = new FlowParcelCommand(session, 3, Guid.NewGuid(), FlowParcelAction.Cancel);
        var request = new FlowPeerRequest(1, 1, Guid.NewGuid(), session, 1, new FlowPeerIntent(FlowPeerOperation.Parcel, Parcel: parcel));
        Assert.True(FlowPeerProtocol.Valid(request));
        Assert.True(FlowPeerProtocol.Valid(JsonSerializer.Deserialize<FlowPeerRequest>(JsonSerializer.Serialize(request))!));
        Assert.False(FlowPeerProtocol.Valid(request with { Version = 2 }));
        Assert.False(FlowPeerProtocol.Valid(request with { Sequence = 0 }));
        Assert.False(FlowPeerProtocol.Valid(request with { Intent = request.Intent with { Parcel = parcel with { SessionId = Guid.NewGuid() } } }));
        Assert.False(FlowPeerProtocol.Valid(request with { Intent = request.Intent with { Operation = FlowPeerOperation.Refresh } }));
        Assert.False(FlowPeerProtocol.Valid(request with { Intent = request.Intent with { Network = new FlowNetworkCommand(session, 3, FlowNetworkAction.AddLink) } }));
    }
}
