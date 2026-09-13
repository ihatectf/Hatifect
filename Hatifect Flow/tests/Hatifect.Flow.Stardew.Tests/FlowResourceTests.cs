using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Hatifect.Flow.Application;
using Hatifect.Flow.Diagnostics;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Inventory;
using Hatifect.Flow.Sessions;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class FlowResourceTests
{
    [Fact]
    public void Diagnostics_AreDetachedAndDoNotSearchCaptureAcquireInventoryOrNotify()
    {
        var world = new GameSessionWorld();
        var mutex = new CountingMutex();
        using FlowGameSession session = world.Open(locks: new FlowChestLocks(() => true, world.Errors.Add, _ => mutex));
        world.Configure(session);
        session.Send("source", "destination", 0);
        FlowSnapshot snapshot = session.ReadSnapshot();
        FlowTickCounters counters = session.TickCounters;
        int acquisitions = mutex.Requests, notifications = 0;
        session.RevisionChanged += _ => notifications++;
        FlowGameResources resources = session.ReadResources();
        for (int i = 0; i < 100; i++)
        {
            FlowGameResources next = session.ReadResources();
            Assert.Equal(resources.Runtime, next.Runtime);
            Assert.Equal(resources.Ports.ToArray(), next.Ports.ToArray());
        }
        Assert.Equal(counters, session.TickCounters);
        Assert.Same(snapshot, session.ReadSnapshot());
        Assert.Equal(acquisitions, mutex.Requests);
        Assert.Equal(0, notifications);
        Assert.Equal(new FlowResourceUsage(2, 32), resources.Runtime.Stations);
        Assert.Equal(new FlowResourceUsage(1, 256), resources.Runtime.PendingOperations);
        Assert.Equal(new FlowResourceUsage(0, 4352), resources.Runtime.IssuedTransfers);
        Assert.Equal(65536, resources.MaxCharactersPerPayload);
        Assert.Equal(64 * 1024 * 1024, resources.MaxCoreCheckpointBytes);
        Assert.Equal(2, resources.Ports.Count);
        Assert.Equal(0, resources.Ports.Sum(port => port.Port.Receipts.Used));
        Assert.Throws<NotSupportedException>(() => ((IList<FlowStationResources>)resources.Ports).Clear());
        string report = resources.Format();
        Assert.DoesNotContain("saved & <metadata>", report);
        Assert.DoesNotContain("<Item", report);
        Assert.Contains("remaining=255", report);
        Assert.True(report.Length < 12000);
        for (int i = 0; i < 5; i++) session.Tick(true);
        Assert.Equal(2, session.ReadResources().Runtime.IssuedTransfers.Used);
        Assert.Equal(0, resources.Runtime.IssuedTransfers.Used);
        Assert.Equal(0, resources.Ports.Sum(port => port.Port.Receipts.Used));
        Assert.Empty(world.Errors);
    }

    [Fact]
    public void Diagnostics_RejectWrongThreadReentryAndRetiredSession()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open(); world.Configure(session);
        Exception? failure = null;
        var thread = new Thread(() => failure = Record.Exception(() => session.ReadResources()));
        thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(failure);
        session.Send("source", "destination", 0);
        int callbacks = 0;
        world.Source.Items.OnSlotChanged += (_, _, _, item) =>
        {
            if (item is not null) return;
            callbacks++;
            Assert.Throws<InvalidOperationException>(() => session.ReadResources());
        };
        session.Tick(true);
        Assert.Equal(1, callbacks);
        Assert.False(session.IsFaulted);
        session.BeginSave();
        Assert.Equal(FlowApplicationState.Paused, session.ReadResources().State);
        session.Dispose();
        Assert.Throws<InvalidOperationException>(() => session.ReadResources());
        Assert.Empty(world.Errors);
    }

    [Fact]
    public void CancelledCargo_ExhaustsRetentionAndRejectsBeforeLeaseOrTaggingAcrossReload()
    {
        var world = new GameSessionWorld();
        var mutex = new CountingMutex();
        using FlowGameSession session = world.Open(locks: new FlowChestLocks(() => true, world.Errors.Add, _ => mutex));
        world.Configure(session);
        for (int i = 0; i < 256; i++)
        {
            Guid parcel = session.Send("source", "destination", 0);
            FlowSnapshot current = session.ReadSnapshot();
            Assert.Equal(FlowCommandStatus.Applied, session.Execute(new FlowParcelCommand(current.SessionId, current.Revision, parcel, FlowParcelAction.Cancel)).Status);
        }
        Assert.All(session.ReadSnapshot().Parcels, parcel => Assert.Equal(ParcelState.Cancelled, parcel.State));
        FlowGameResources full = session.ReadResources();
        Assert.Equal(new FlowResourceUsage(256, 256), full.Runtime.Cargo);
        Assert.Equal(256, full.Runtime.Events.Used);
        Assert.Equal(0, full.Runtime.PendingOperations.Used);
        Assert.Equal(0, full.Runtime.IssuedTransfers.Used);
        Assert.Equal(0, full.Payloads.Remaining);
        FlowSnapshot snapshot = session.ReadSnapshot();
        FlowLinkSnapshot link = Assert.Single(snapshot.Links);
        FlowInventorySlot slot = Assert.Single(session.ReadInventory(link.Origin));
        var typedSend = new FlowSendCommand(snapshot.SessionId, snapshot.Revision,
            link.Origin, link.Destination, slot.Index, slot.Fingerprint);
        string sourceXml = FlowItemCodec.Encode(world.Source.Items[0]);
        int acquisitions = mutex.Requests;
        FlowCommandResult typedRejection = session.Execute(typedSend);
        Assert.Equal(FlowCommandStatus.Rejected, typedRejection.Status);
        Assert.Equal(FlowRejectionCode.RetainedCargoLimit, typedRejection.Code);
        Assert.Equal("flow.reason.RetainedCargoLimit", typedRejection.ReasonKey);
        Assert.Equal(snapshot.Revision, typedRejection.Revision);
        for (int i = 0; i < 3; i++)
        {
            FlowResourceLimitException error = Assert.Throws<FlowResourceLimitException>(() => session.Send("source", "destination", 0));
            Assert.Equal(FlowAdmissionResource.RetainedCargo, error.Resource);
            Assert.Equal(256, error.Used); Assert.Equal(256, error.Limit);
        }
        Assert.Equal(acquisitions, mutex.Requests);
        Assert.Equal(sourceXml, FlowItemCodec.Encode(world.Source.Items[0]));
        Assert.Equal(8, world.Source.Items[0].Stack);
        Assert.Same(snapshot, session.ReadSnapshot());
        Assert.Equal(full.Runtime, session.ReadResources().Runtime);
        Assert.Equal(new FlowAdmissionRejections(0, 0, 4), session.ReadResources().AdmissionRejections);
        Assert.False(session.IsFaulted);
        FlowGameSave saved = GameSessionWorld.Clone(session.BeginSave());
        Assert.Equal(saved.Payloads.Sum(payload => (long)payload.Xml.Length), full.PayloadCharacters.Used);
        using FlowGameSession restored = world.Clone().Open(saved);
        Assert.Equal(256, restored.ReadResources().Runtime.Cargo.Used);
        Assert.Equal(default, restored.ReadResources().AdmissionRejections);
        Assert.Throws<FlowResourceLimitException>(() => restored.Send("source", "destination", 0));
        Assert.False(restored.IsFaulted);
        Assert.Equal(0, restored.TickCounters.PhysicalApplyCalls);
        Assert.Empty(world.Errors);
    }

    [Fact]
    public void StationLimit_RejectsTheThirtyThirdChestBeforeBinding()
    {
        var chests = Enumerable.Range(0, 33).Select(_ => InventoryFixture.CreateChest()).ToArray();
        var errors = new List<Exception>();
        using var session = new FlowGameSession(1, 1, () => true, binding => chests[binding.X], errors.Add);
        for (int i = 0; i < 32; i++) session.RegisterStation("station" + i, "Farm", i, 0, chests[i]);
        session.CaptureTarget("Farm", 32, 0, chests[32]);
        FlowNetworkSnapshot network = session.ReadNetwork();
        FlowSnapshot before = session.ReadSnapshot();
        FlowCommandResult typedRejection = session.Execute(new FlowNetworkCommand(before.SessionId, before.Revision,
            FlowNetworkAction.RegisterStation, Target: network.Target, Name: "overflow"));
        Assert.Equal(FlowCommandStatus.Rejected, typedRejection.Status);
        Assert.Equal(FlowRejectionCode.StationLimit, typedRejection.Code);
        Assert.Equal("flow.reason.StationLimit", typedRejection.ReasonKey);
        Assert.Equal(before.Revision, typedRejection.Revision);
        FlowResourceLimitException error = Assert.Throws<FlowResourceLimitException>(() => session.RegisterStation("overflow", "Farm", 32, 0, chests[32]));
        Assert.Equal(FlowAdmissionResource.Stations, error.Resource);
        Assert.False(chests[32].modData.ContainsKey(FlowGameSession.StationKey));
        Assert.Same(before, session.ReadSnapshot());
        FlowGameResources resources = session.ReadResources();
        Assert.Equal(0, resources.Runtime.Stations.Remaining);
        Assert.Equal(32, resources.Ports.Count);
        Assert.Equal(new FlowAdmissionRejections(2, 0, 0), resources.AdmissionRejections);
        Assert.True(resources.Format().Length < 12000);
        Assert.False(session.IsFaulted);
        Assert.Empty(errors);
    }

    private sealed class CountingMutex : IFlowInventoryMutex
    {
        internal int Requests;
        public bool IsLocked => IsHeld;
        public bool IsHeld { get; private set; }
        public void Request(Action acquired, Action failed) { Requests++; IsHeld = true; acquired(); }
        public void Release() => IsHeld = false;
    }
}
