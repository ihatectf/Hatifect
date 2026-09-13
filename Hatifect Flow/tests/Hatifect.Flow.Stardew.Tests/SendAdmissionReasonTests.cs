using System;
using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Diagnostics;
using Hatifect.Flow.Inventory;
using Hatifect.Flow.Sessions;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class SendAdmissionReasonTests
{
    [Fact]
    public void RemovedRouteReportsOwnerReasonWithoutAdmissionAndCanBeRepairedThroughTypedCommands()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        FlowSnapshot initial = session.ReadSnapshot();
        FlowLinkSnapshot link = Assert.Single(initial.Links);
        FlowInventorySlot slot = Assert.Single(session.ReadInventory(link.Origin));
        var send = new FlowSendCommand(initial.SessionId, initial.Revision, link.Origin, link.Destination, slot.Index, slot.Fingerprint);
        string originalSource = FlowItemCodec.Encode(Assert.Single(world.Source.Items));
        Assert.Equal(FlowCommandStatus.Applied, session.Execute(new FlowNetworkCommand(initial.SessionId, initial.Revision,
            FlowNetworkAction.RemoveLink, Target: link.Id)).Status);
        FlowSnapshot removed = session.ReadSnapshot();
        FlowTickCounters beforeRefusal = session.TickCounters;

        FlowCommandResult stale = session.Execute(send);
        Assert.Equal(FlowCommandStatus.Conflict, stale.Status);
        Assert.Equal(FlowRejectionCode.StaleRevision, stale.Code);
        Assert.Equal(beforeRefusal, session.TickCounters);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            FlowCommandResult rejected = session.Execute(send with { ExpectedRevision = removed.Revision });
            Assert.Equal(FlowCommandStatus.Rejected, rejected.Status);
            Assert.Equal(FlowRejectionCode.RouteUnavailable, rejected.Code);
            Assert.Equal("flow.reason.RouteUnavailable", rejected.ReasonKey);
            Assert.Equal(removed.Revision, rejected.Revision);
            Assert.Same(removed, session.ReadSnapshot());
            Assert.Empty(session.ReadSnapshot().Parcels);
            Assert.Equal(originalSource, FlowItemCodec.Encode(Assert.Single(world.Source.Items)));
            Assert.Empty(world.Destination.Items);
            Assert.False(session.IsFaulted);
            FlowGameResources resources = session.ReadResources();
            Assert.Equal(0, resources.Payloads.Used);
            Assert.Equal(0, resources.PayloadCharacters.Used);
            Assert.Equal(0, resources.Runtime.Cargo.Used);
            Assert.Equal(0, resources.Runtime.Shipments.Used);
            Assert.Equal(0, resources.Runtime.Parcels.Used);
            Assert.Equal(0, resources.Runtime.PendingOperations.Used);
            Assert.Equal(beforeRefusal.PhysicalApplyCalls, resources.TickCounters.PhysicalApplyCalls);
            Assert.Equal(beforeRefusal.RouteSearches + 1, resources.TickCounters.RouteSearches);
            Assert.All(resources.Ports, port =>
            {
                Assert.Equal(0, port.Port.Custody.Used);
                Assert.Equal(0, port.Port.Receipts.Used);
            });
        }

        Assert.Equal(FlowCommandStatus.Applied, session.Execute(new FlowNetworkCommand(removed.SessionId, removed.Revision,
            FlowNetworkAction.AddLink, Station: link.Origin, Destination: link.Destination, TransitTicks: 3)).Status);
        FlowSnapshot repaired = session.ReadSnapshot();
        FlowCommandResult accepted = session.Execute(send with { ExpectedRevision = repaired.Revision });
        Assert.Equal(FlowCommandStatus.Applied, accepted.Status);
        Assert.Equal(beforeRefusal.RouteSearches + 2, session.TickCounters.RouteSearches);
        Assert.Equal(FlowRejectionCode.None, accepted.Code);
        Assert.Equal(string.Empty, accepted.ReasonKey);
        FlowParcelSnapshot parcel = Assert.Single(session.ReadSnapshot().Parcels);
        Assert.Equal(ParcelState.Reserved, parcel.State);
        Assert.Equal(8, parcel.Quantity);
        for (int tick = 0; tick < 8; tick++) session.Tick(true);
        Assert.Equal(ParcelState.Delivered, Assert.Single(session.ReadSnapshot().Parcels).State);
        Assert.Equal(8, Assert.Single(world.Destination.Items).Stack);
        Assert.DoesNotContain(world.Source.Items, item => item is not null);
        Assert.Empty(world.Errors);
    }
}
