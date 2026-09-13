using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Hatifect.Flow.Application;
using Hatifect.Flow.Inventory;
using Hatifect.Flow.Multiplayer;
using Hatifect.Flow.Sessions;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

public sealed class PeerHostTests
{
    [Fact]
    public void HostDisposalPublishesClosedProjectionWithoutRetainingLiveCapabilities()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        session.Send("source", "destination", 0);
        var replies = new List<FlowPeerReply>();
        using var host = new FlowPeerHost(1, session, (_, _) => session.CapturePeerTarget("Farm", 0, 0, world.Source),
            (_, reply) => replies.Add(reply), world.Errors.Add);
        Guid client = Guid.NewGuid();
        Assert.True(host.Connect(2, new FlowPeerHello(1, 1, client)));
        host.Pump();
        Assert.True(Assert.Single(replies[0].Projection.Parcels).Availability.Cancel.Available);
        var request = new FlowPeerRequest(1, 1, client, session.ReadSnapshot().SessionId, 1,
            new FlowPeerIntent(FlowPeerOperation.Target, Location: "Farm"));
        Assert.Equal(FlowPeerAdmission.Queued, host.Receive(2, request));
        host.Pump();
        Assert.NotEqual(Guid.Empty, replies.Last().Projection.Target);
        host.Dispose();
        FlowPeerReply terminal = replies.Last();
        Assert.True(FlowPeerProtocol.Valid(terminal));
        Assert.Equal(FlowApplicationState.Closed, terminal.Projection.State);
        Assert.Empty(terminal.Projection.Parcels);
        Assert.Equal(Guid.Empty, terminal.Projection.Target);
        Assert.Equal(FlowPeerAdmission.Rejected, host.Receive(2, request));
        Assert.Equal(FlowApplicationState.Active, session.ReadSnapshot().State);
        Assert.Single(session.ReadSnapshot().Parcels);
        Assert.Equal(8, world.Source.Items[0].Stack);
    }

    [Fact]
    public void LostSendReplyCanBeRetriedWithoutCreatingASecondPhysicalShipment()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        Guid client = Guid.NewGuid();
        var replies = new List<FlowPeerReply>();
        bool loseReply = true;
        using var host = new FlowPeerHost(1, session, (_, _) => null, (_, reply) =>
        {
            if (reply.Sequence == 1 && loseReply) throw new InvalidOperationException("reply lost");
            // SMAPI uses Newtonsoft; the real protocol must survive that serializer too.
            replies.Add(JsonConvert.DeserializeObject<FlowPeerReply>(JsonConvert.SerializeObject(reply))!);
        }, world.Errors.Add);
        Assert.True(host.Connect(2, new FlowPeerHello(1, 1, client)));
        Assert.Empty(replies);
        host.Pump();
        Assert.True(FlowPeerProtocol.Valid(Assert.Single(replies)));
        FlowSnapshot snapshot = session.ReadSnapshot();
        FlowLinkSnapshot link = Assert.Single(snapshot.Links);
        FlowInventorySlot slot = Assert.Single(session.ReadInventory(link.Origin));
        var request = new FlowPeerRequest(1, 1, client, snapshot.SessionId, 1,
            new FlowPeerIntent(FlowPeerOperation.Send, Send: new FlowSendCommand(snapshot.SessionId, snapshot.Revision,
                link.Origin, link.Destination, slot.Index, slot.Fingerprint)));
        request = JsonConvert.DeserializeObject<FlowPeerRequest>(JsonConvert.SerializeObject(request))!;
        Assert.Equal(FlowPeerAdmission.Queued, host.Receive(2, request));
        Assert.Equal(FlowPeerAdmission.DuplicatePending, host.Receive(2, request));
        Assert.Empty(session.ReadSnapshot().Parcels);
        host.Pump();
        Assert.Single(session.ReadSnapshot().Parcels);
        Assert.Single(world.Errors);
        loseReply = false;
        Assert.Equal(FlowPeerAdmission.Replayed, host.Receive(2, request));
        host.Pump();
        Assert.Equal(FlowCommandStatus.Applied, Assert.Single(replies, r => r.Sequence == 1).Result!.Status);
        for (int tick = 0; tick < 20; tick++) { session.Tick(true); host.Pump(); }
        Assert.Null(world.Source.Items[0]);
        Assert.Equal(8, Assert.Single(world.Destination.Items).Stack);
        Assert.Single(session.ReadSnapshot().Parcels);
        Assert.All(replies, reply => Assert.True(FlowPeerProtocol.Valid(reply)));
    }

    [Fact]
    public void EachPeerUsesItsOwnCapturedTargetAndCannotReuseAnotherPeersCapability()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        session.CaptureTarget("Farm", 0, 0, world.Source);
        Guid hostTarget = session.ReadNetwork().Target;
        var replies = new Dictionary<long, FlowPeerReply>();
        using var host = new FlowPeerHost(1, session, (id, _) => id == 2
            ? session.CapturePeerTarget("Farm", 1, 0, world.Destination)
            : session.CapturePeerTarget("Farm", 0, 0, world.Source), (id, reply) => replies[id] = reply, world.Errors.Add);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        Assert.True(host.Connect(2, new FlowPeerHello(1, 1, a)));
        Assert.True(host.Connect(3, new FlowPeerHello(1, 1, b)));
        host.Pump();
        Assert.All(replies.Values, reply => Assert.Equal(Guid.Empty, reply.Projection.Target));
        FlowSnapshot snapshot = session.ReadSnapshot();
        var target = new FlowPeerIntent(FlowPeerOperation.Target, Location: "Farm", X: 1);
        host.Receive(2, new FlowPeerRequest(1, 1, a, snapshot.SessionId, 1, target));
        host.Pump();
        Guid token = replies[2].Projection.Target;
        Assert.NotEqual(Guid.Empty, token);
        Assert.Equal(Guid.Empty, replies[3].Projection.Target);
        Assert.Equal(hostTarget, session.ReadNetwork().Target);
        var bind = new FlowPeerIntent(FlowPeerOperation.Network,
            Network: new FlowNetworkCommand(snapshot.SessionId, snapshot.Revision, FlowNetworkAction.RegisterStation, Target: token, Name: "PeerStation"));
        host.Receive(3, new FlowPeerRequest(1, 1, b, snapshot.SessionId, 1, bind));
        host.Pump();
        Assert.Equal(FlowCommandStatus.Conflict, replies[3].Result!.Status);
        Assert.Empty(session.ReadSnapshot().Stations);
        host.Receive(2, new FlowPeerRequest(1, 1, a, snapshot.SessionId, 2, bind));
        host.Pump();
        Assert.Equal("PeerStation", Assert.Single(session.ReadNetwork().Stations).Name);
        Assert.True(world.Destination.modData.ContainsKey(FlowGameSession.StationKey));
        Assert.False(world.Source.modData.ContainsKey(FlowGameSession.StationKey));
    }

    [Fact]
    public void SaveBarrierRetainsPendingWorkAndDisconnectRevokesItBeforePhysicalAdmission()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        var replies = new List<FlowPeerReply>();
        using var host = new FlowPeerHost(1, session, (_, _) => null, (_, reply) => replies.Add(reply), world.Errors.Add);
        Guid client = Guid.NewGuid();
        host.Connect(2, new FlowPeerHello(1, 1, client));
        host.Pump();
        session.BeginSave();
        var snapshot = session.ReadSnapshot();
        var link = Assert.Single(snapshot.Links);
        var rename = new FlowPeerRequest(1, 1, client, snapshot.SessionId, 1,
            new FlowPeerIntent(FlowPeerOperation.Network,
                Network: new FlowNetworkCommand(snapshot.SessionId, snapshot.Revision, FlowNetworkAction.RenameStation, Station: link.Origin, Name: "Renamed")));
        Assert.Equal(FlowPeerAdmission.Queued, host.Receive(2, rename));
        host.Pump();
        Assert.DoesNotContain(replies, reply => reply.Sequence == 1);
        Assert.Equal("source", session.StationName(link.Origin));
        host.Disconnect(2);
        session.EndSave();
        host.Pump();
        Assert.Equal("source", session.StationName(link.Origin));
        Assert.Equal(FlowPeerAdmission.Rejected, host.Receive(2, rename));
        Assert.True(host.Connect(2, new FlowPeerHello(1, 1, Guid.NewGuid())));
        Assert.Equal(FlowPeerAdmission.Rejected, host.Receive(2, rename));
        Assert.False(host.Connect(4, new FlowPeerHello(2, 1, Guid.NewGuid())));
        Assert.False(host.Connect(4, new FlowPeerHello(1, 2, Guid.NewGuid())));
    }

    [Fact]
    public void UnexpectedEndpointFailureIsFencedAndRetainedInsteadOfReexecuted()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        int executions = 0;
        var replies = new List<FlowPeerReply>();
        using var host = new FlowPeerHost(1, session, (_, _) =>
        {
            executions++;
            throw new InvalidOperationException("world adapter failed");
        }, (_, reply) => replies.Add(reply), world.Errors.Add);
        Guid client = Guid.NewGuid();
        host.Connect(2, new FlowPeerHello(1, 1, client));
        host.Pump();
        var request = new FlowPeerRequest(1, 1, client, session.ReadSnapshot().SessionId, 1,
            new FlowPeerIntent(FlowPeerOperation.Target, Location: "Farm"));
        host.Receive(2, request);
        host.Pump();
        Assert.True(session.IsFaulted);
        Assert.Equal(FlowCommandStatus.Faulted, Assert.Single(replies, reply => reply.Sequence == 1).Result!.Status);
        Assert.Equal(FlowPeerAdmission.Replayed, host.Receive(2, request));
        host.Pump();
        Assert.Equal(1, executions);
        Assert.Equal(2, replies.Count(reply => reply.Sequence == 1 && reply.Result!.Status == FlowCommandStatus.Faulted));
        Assert.True(session.BeginSave().RequiresRecovery);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TargetDuringAuthorityLossIsRejectedWithoutPersistingRecovery(bool publishPause)
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        int captures = 0;
        var replies = new List<FlowPeerReply>();
        using var host = new FlowPeerHost(1, session, (_, _) =>
        {
            captures++;
            return session.CapturePeerTarget("Farm", 0, 0, world.Source);
        }, (_, reply) => replies.Add(reply), world.Errors.Add);
        Guid client = Guid.NewGuid();
        Assert.True(host.Connect(2, new FlowPeerHello(1, 1, client)));
        host.Pump();
        var request = new FlowPeerRequest(1, 1, client, session.ReadSnapshot().SessionId, 1,
            new FlowPeerIntent(FlowPeerOperation.Target, Location: "Farm"));
        Assert.Equal(FlowPeerAdmission.Queued, host.Receive(2, request));
        world.Authority = false;
        if (publishPause) session.Tick(false);
        host.Pump();
        Assert.Equal(FlowCommandStatus.Rejected, Assert.Single(replies, reply => reply.Sequence == 1).Result!.Status);
        Assert.False(session.IsFaulted);
        Assert.False(session.BeginSave().RequiresRecovery);
        session.EndSave();
        world.Authority = true;
        session.Tick(false);
        Assert.Equal(FlowPeerAdmission.Replayed, host.Receive(2, request));
        host.Pump();
        Assert.Equal(0, captures);
        Assert.Equal(2, replies.Count(reply => reply.Sequence == 1 && reply.Result!.Status == FlowCommandStatus.Rejected));
        Assert.Equal(FlowPeerAdmission.Queued, host.Receive(2, request with { Sequence = 2 }));
        host.Pump();
        FlowPeerReply applied = Assert.Single(replies, reply => reply.Sequence == 2);
        Assert.Equal(FlowCommandStatus.Applied, applied.Result!.Status);
        Assert.NotEqual(Guid.Empty, applied.Projection.Target);
        Assert.Equal(1, captures);
        Assert.Empty(world.Errors);
        Assert.False(world.Source.modData.ContainsKey(FlowGameSession.StationKey));
    }
}
