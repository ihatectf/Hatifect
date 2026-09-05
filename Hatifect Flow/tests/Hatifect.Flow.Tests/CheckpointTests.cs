using System;
using System.Linq;
using Hatifect.Flow.Application.Dispatch;
using Hatifect.Flow.Domain.Checkpoints;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Policies;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Scheduling;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Infrastructure.Persistence;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class CheckpointTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void CheckpointRoundTrip_PreservesEachParcelStateAndCargoCustody(int stateValue)
    {
        var fixture = new CheckpointFixture();
        fixture.Reach((ParcelState)stateValue);
        FlowCheckpoint checkpoint = fixture.Runtime.CaptureCheckpoint();
        byte[] bytes = CheckpointCodec.Encode(new CheckpointImage(17, checkpoint));

        CheckpointImage image = CheckpointCodec.Decode(bytes);
        FlowRuntime restored = FlowRuntime.RestoreCheckpoint(image.Checkpoint);

        Assert.Equal(17, image.Revision);
        Assert.Equal((ParcelState)stateValue, restored.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(fixture.Runtime.Now, restored.Now);
        Assert.Equal(fixture.Runtime.GetParcel(CheckpointFixture.Parcel).Version,
            restored.GetParcel(CheckpointFixture.Parcel).Version);
        Assert.Equal(fixture.Runtime.ReservedUnits(CheckpointFixture.Link), restored.ReservedUnits(CheckpointFixture.Link));
        Assert.Equal(fixture.Runtime.PendingOperationCount, restored.PendingOperationCount);
        Assert.Equal(fixture.Runtime.GetCargo(CheckpointFixture.Cargo).ClaimedBy,
            restored.GetCargo(CheckpointFixture.Cargo).ClaimedBy);
        Assert.Equal(bytes, CheckpointCodec.Encode(new CheckpointImage(17, restored.CaptureCheckpoint())));
        AssertCustody(restored);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void CheckpointRoundTrip_UncertainTransferReconcilesFromRetainedReceipt(bool deposit, bool afterCommit, bool rejected)
    {
        var fixture = new CheckpointFixture();
        CheckpointFixture.FaultPort faulty = deposit ? fixture.DestinationPort : fixture.Source;
        faulty.AfterCommit = afterCommit;
        faulty.Throw = true;
        faulty.Inner.AcceptDeposits = !rejected;
        faulty.Inner.AcceptExtractions = !rejected;
        Assert.True(fixture.Runtime.TryReserve(CheckpointFixture.Parcel));
        Assert.Throws<CheckpointFixture.PortFailure>(() => fixture.Runtime.AdvanceTo(deposit ? 4 : 1));
        PortTransfer oldTransfer = fixture.Runtime.GetParcel(CheckpointFixture.Parcel).PendingTransfer!;
        FlowRuntime restored = FlowRuntime.RestoreCheckpoint(fixture.Runtime.CaptureCheckpoint());
        PortTransfer transfer = restored.GetParcel(CheckpointFixture.Parcel).PendingTransfer!;
        InMemoryCargoPort restoredPort = restored.GetCheckpointPort(transfer.StationId);
        PortResult result = !afterCommit ? PortResult.Missing : rejected ? PortResult.Rejected : PortResult.Applied;

        Assert.NotSame(oldTransfer, transfer);
        Assert.NotSame(oldTransfer.Id.Session, transfer.Id.Session);
        Assert.Throws<InvalidOperationException>(() => restoredPort.Apply(oldTransfer));
        Assert.Equal(result, restoredPort.ReadResult(transfer));
        Assert.False(restored.RetryDelivery(CheckpointFixture.Parcel));
        AssertCustody(restored);
        Assert.True(restored.ReconcileTransfer(CheckpointFixture.Parcel));
        Assert.False(restored.ReconcileTransfer(CheckpointFixture.Parcel));
        ParcelState expected = deposit
            ? result == PortResult.Applied ? ParcelState.Delivered : result == PortResult.Rejected ? ParcelState.DeliveryRejected : ParcelState.DeliveryFaulted
            : result == PortResult.Applied ? ParcelState.InTransit : ParcelState.Cancelled;
        Assert.Equal(expected, restored.GetParcel(CheckpointFixture.Parcel).State);
        if (result == PortResult.Missing)
        {
            Assert.Throws<InvalidOperationException>(() => restoredPort.Apply(transfer));
        }
        else
        {
            Assert.Equal(result, restoredPort.Apply(transfer));
        }
        if (expected == ParcelState.InTransit)
        {
            Assert.Equal(2, restored.AdvanceTo(4));
        }
        else if (expected is ParcelState.DeliveryFaulted or ParcelState.DeliveryRejected)
        {
            restoredPort.AcceptDeposits = true;
            Assert.True(restored.RetryDelivery(CheckpointFixture.Parcel));
            Assert.Equal(1, restored.AdvanceTo(5));
        }
        AssertCustody(restored);
        Assert.Equal(expected == ParcelState.Cancelled ? ParcelState.Cancelled : ParcelState.Delivered,
            restored.GetParcel(CheckpointFixture.Parcel).State);
    }

    [Fact]
    public void Restore_ReconstructsIndependentAuthorityAndRejectsOldDueTickets()
    {
        var fixture = new CheckpointFixture();
        Parcel second = fixture.AddBatch(2);
        Assert.True(fixture.Runtime.TryReserve(CheckpointFixture.Parcel));
        Assert.True(fixture.Runtime.TryReserve(second.Id));
        Assert.Equal(1, fixture.Runtime.AdvanceTo(1, 1));
        ScheduledOperation oldTicket = fixture.Runtime.PeekNextOperation()!;
        FlowRuntime restored = FlowRuntime.RestoreCheckpoint(fixture.Runtime.CaptureCheckpoint());

        Assert.Equal(1, oldTicket.DueTick);
        Assert.Equal(restored.Now, oldTicket.DueTick);
        Assert.NotSame(oldTicket, restored.PeekNextOperation());
        Assert.False(restored.ApplyOperation(oldTicket));
        Assert.True(restored.ApplyOperation(restored.PeekNextOperation()!));
        Assert.Equal(ParcelState.InTransit, restored.GetParcel(second.Id).State);
        Assert.Equal(ParcelState.Reserved, fixture.Runtime.GetParcel(second.Id).State);
        Assert.NotNull(fixture.Source.ReadCargo(second.CargoId));
        Assert.Null(restored.GetCheckpointPort(CheckpointFixture.Origin).ReadCargo(second.CargoId));
    }

    [Fact]
    public void CaptureAndRestore_DetachEveryMutableCheckpointArray()
    {
        var fixture = new CheckpointFixture();
        fixture.Reach(ParcelState.Delivered);
        FlowCheckpoint snapshot = fixture.Runtime.CaptureCheckpoint();
        FlowRuntime restored = FlowRuntime.RestoreCheckpoint(snapshot);
        byte[] expected = CheckpointCodec.Encode(new CheckpointImage(0, restored.CaptureCheckpoint()));

        snapshot.Stations[0].Port.Receipts[0] = null!;
        snapshot.Stations[1].Port.Inventory[0] = null!;
        snapshot.Parcels[0].Plan!.Links[0] = Guid.Empty;
        snapshot.Parcels[0].Plan!.Dependencies[0] = null!;
        snapshot.Stations[0] = null!;
        snapshot.Links[0] = null!;
        snapshot.Shipments[0] = null!;
        snapshot.Parcels[0] = null!;
        snapshot.Cargo[0] = null!;
        snapshot.Transfers[0] = null!;
        snapshot.Events[0] = null!;

        Assert.Equal(expected, CheckpointCodec.Encode(new CheckpointImage(0, restored.CaptureCheckpoint())));
        Assert.Equal(expected, CheckpointCodec.Encode(new CheckpointImage(0, fixture.Runtime.CaptureCheckpoint())));
        AssertCustody(restored);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restore_RemovedLinkRetainsAdmittedTransitButCancelsUndepartedParcel(bool departed)
    {
        var fixture = new CheckpointFixture();
        fixture.Reach(departed ? ParcelState.InTransit : ParcelState.Reserved);
        Assert.True(fixture.Runtime.RemoveLink(CheckpointFixture.Link));

        FlowRuntime restored = FlowRuntime.RestoreCheckpoint(fixture.Runtime.CaptureCheckpoint());
        Assert.Equal(7, restored.ReservedUnits(CheckpointFixture.Link));
        restored.AdvanceTo(4);

        Assert.Equal(departed ? ParcelState.Delivered : ParcelState.Cancelled,
            restored.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(0, restored.ReservedUnits(CheckpointFixture.Link));
        Assert.False(restored.RemoveLink(CheckpointFixture.Link));
        Assert.Throws<InvalidOperationException>(() => restored.AddLink(CheckpointFixture.Link,
            CheckpointFixture.Origin, CheckpointFixture.Destination, 20, 3));
        AssertCustody(restored);
    }

    [Fact]
    public void Restore_PreservesEditedShipmentAndOriginalParcelPolicy()
    {
        var fixture = new CheckpointFixture();
        var changed = new ServicePolicy(ServiceClass.Express, DeliveryGuarantee.Exact);
        fixture.Runtime.ChangePolicy(CheckpointFixture.Shipment, changed);

        FlowRuntime restored = FlowRuntime.RestoreCheckpoint(fixture.Runtime.CaptureCheckpoint());

        Assert.Equal(changed, restored.GetShipment(CheckpointFixture.Shipment).Policy);
        Assert.Equal(CheckpointFixture.Policy, restored.GetParcel(CheckpointFixture.Parcel).PolicySnapshot);
        Assert.True(restored.TryReserve(CheckpointFixture.Parcel));
        Assert.Equal(3, restored.AdvanceTo(4));
        Assert.Equal(CheckpointFixture.Policy, restored.GetParcel(CheckpointFixture.Parcel).PolicySnapshot);
    }

    [Fact]
    public void Restore_RedispatchedCargoRetainsHistoryAndOnePhysicalBatch()
    {
        var fixture = new CheckpointFixture();
        fixture.Reach(ParcelState.Delivered);
        fixture.Runtime.AddLink(new LinkId(CheckpointFixture.Id(12)), CheckpointFixture.Destination,
            CheckpointFixture.Origin, 20, 3);
        Parcel second = fixture.Assign(CheckpointFixture.Cargo, 2, CheckpointFixture.Destination, CheckpointFixture.Origin);
        Assert.True(fixture.Runtime.TryReserve(second.Id));
        FlowRuntime restored = FlowRuntime.RestoreCheckpoint(fixture.Runtime.CaptureCheckpoint());

        Assert.Equal(second.Id, restored.GetCargo(CheckpointFixture.Cargo).ClaimedBy);
        Assert.Equal(ParcelState.Delivered, restored.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(3, restored.AdvanceTo(8));
        Assert.Equal(ParcelState.Delivered, restored.GetParcel(second.Id).State);
        Assert.Equal(CargoOwner.AtStation(CheckpointFixture.Origin), restored.GetCargo(CheckpointFixture.Cargo).Owner);
        Assert.Null(restored.GetCargo(CheckpointFixture.Cargo).ClaimedBy);
        Assert.Equal(CheckpointFixture.Manifest, restored.GetCheckpointPort(CheckpointFixture.Origin).ReadCargo(CheckpointFixture.Cargo));
        Assert.Empty(restored.GetCheckpointPort(CheckpointFixture.Destination).Inventory);
        FlowRuntime again = FlowRuntime.RestoreCheckpoint(restored.CaptureCheckpoint());
        Assert.Equal(restored.GetCargo(CheckpointFixture.Cargo), again.GetCargo(CheckpointFixture.Cargo));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restore_RejectsDeliveryOrRedispatchThatPredatesKnownCargoTimeline(bool redispatch)
    {
        var fixture = new CheckpointFixture();
        fixture.Reach(ParcelState.Delivered);
        if (redispatch)
        {
            fixture.Runtime.AddLink(new LinkId(CheckpointFixture.Id(12)), CheckpointFixture.Destination,
                CheckpointFixture.Origin, 20, 3);
            Parcel second = fixture.Assign(CheckpointFixture.Cargo, 2, CheckpointFixture.Destination, CheckpointFixture.Origin);
            Assert.True(fixture.Runtime.TryReserve(second.Id));
            Assert.Equal(1, fixture.Runtime.AdvanceTo(5, 1));
            Assert.Equal(ParcelState.InTransit, fixture.Runtime.GetParcel(second.Id).State);
        }
        FlowCheckpoint data = fixture.Runtime.CaptureCheckpoint();
        data = data with { Events = Array.Empty<EventCheckpoint>() };
        int index = redispatch ? 1 : 0;
        ParcelCheckpoint parcel = data.Parcels[index];
        Assert.Equal(redispatch ? 5 : 4, parcel.TransferTick);
        data.Parcels[index] = parcel with
        {
            TransferTick = 1,
            PendingOperation = parcel.PendingOperation is null ? null : parcel.PendingOperation with { DueTick = 4 }
        };

        Assert.Throws<ArgumentException>(() => FlowRuntime.RestoreCheckpoint(data));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 2)]
    public void Restore_RejectsChangedAdmittedHopTimeline(int operations, int earlyTick)
    {
        var fixture = new CheckpointFixture(twoLinks: true);
        Assert.True(fixture.Runtime.TryReserve(CheckpointFixture.Parcel));
        fixture.Runtime.AdvanceTo(20, operations);
        FlowCheckpoint data = fixture.Runtime.CaptureCheckpoint();
        ParcelCheckpoint parcel = data.Parcels[0];
        data.Parcels[0] = parcel with { PendingOperation = parcel.PendingOperation! with { DueTick = earlyTick } };

        Assert.Throws<ArgumentException>(() => FlowRuntime.RestoreCheckpoint(data));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Restore_ContinuesEveryTwoHopCursorWithOriginalTimeAndRemainingReservations(int operations)
    {
        var fixture = new CheckpointFixture(twoLinks: true);
        Assert.True(fixture.Runtime.TryReserve(CheckpointFixture.Parcel));
        Assert.Equal(operations, fixture.Runtime.AdvanceTo(20, operations));
        FlowRuntime restored = FlowRuntime.RestoreCheckpoint(fixture.Runtime.CaptureCheckpoint());
        var secondLink = new LinkId(CheckpointFixture.Id(12));

        Assert.Equal(operations == 1 ? 7 : 0, restored.ReservedUnits(CheckpointFixture.Link));
        Assert.Equal(operations < 4 ? 7 : 0, restored.ReservedUnits(secondLink));
        Assert.Equal(5 - operations, restored.AdvanceTo(20));

        Assert.Equal(ParcelState.Delivered, restored.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(0, restored.ReservedUnits(CheckpointFixture.Link));
        Assert.Equal(0, restored.ReservedUnits(secondLink));
        Assert.Equal(0, restored.PendingOperationCount);
        Assert.Equal(9, restored.Events.Last().Tick);
        Assert.Equal(CheckpointFixture.Manifest, restored.GetCheckpointPort(CheckpointFixture.Destination).ReadCargo(CheckpointFixture.Cargo));
        Assert.Empty(restored.GetCheckpointPort(CheckpointFixture.Origin).Inventory);
        Assert.Empty(restored.GetCheckpointPort(CheckpointFixture.Middle).Inventory);
        AssertCustody(restored);
    }

    [Fact]
    public void Restore_PreservesConfiguredAdmissionRetryAndEventBounds()
    {
        var fixture = new CheckpointFixture();
        fixture.Reach(ParcelState.Reserved);
        FlowCheckpoint data = fixture.Runtime.CaptureCheckpoint();
        data = data with { Limits = new LimitsCheckpoint(2, 1, 1, 2, 1, 1, 2, 3, 7, 1) };
        FlowRuntime restored = FlowRuntime.RestoreCheckpoint(data);

        Assert.Throws<InvalidOperationException>(() => restored.AddStation(new StationId(CheckpointFixture.Id(99)), new InMemoryCargoPort()));
        Assert.Throws<InvalidOperationException>(() => restored.AddLink(new LinkId(CheckpointFixture.Id(99)), CheckpointFixture.Origin, CheckpointFixture.Destination, 7, 3));
        Assert.Throws<InvalidOperationException>(() => restored.CreateShipment(new ShipmentId(CheckpointFixture.Id(99)),
            CheckpointFixture.Origin, CheckpointFixture.Destination, CheckpointFixture.Manifest, CheckpointFixture.Policy));
        restored.GetCheckpointPort(CheckpointFixture.Destination).AcceptDeposits = false;
        Assert.Equal(3, restored.AdvanceTo(4));
        Assert.Equal(ParcelState.DeliveryRejected, restored.GetParcel(CheckpointFixture.Parcel).State);
        Assert.False(restored.RetryDelivery(CheckpointFixture.Parcel));
        Assert.Equal(2, restored.Events.Count);
        Assert.Equal(data.Limits, restored.CaptureCheckpoint().Limits);
        AssertCustody(restored);
    }

    [Fact]
    public void Restore_RejectsTerminalTeleportEvenWhenLedgerAndInventoryAgree()
    {
        var fixture = new CheckpointFixture();
        fixture.Reach(ParcelState.Delivered);
        FlowCheckpoint data = fixture.Runtime.CaptureCheckpoint();
        data.Stations[0] = data.Stations[0] with { Port = data.Stations[0].Port with { Inventory = data.Stations[1].Port.Inventory } };
        data.Stations[1] = data.Stations[1] with { Port = data.Stations[1].Port with { Inventory = Array.Empty<InventoryCheckpoint>() } };
        data.Cargo[0] = data.Cargo[0] with { Owner = new OwnerCheckpoint(CheckpointFixture.Origin.Value, null, null) };

        Assert.Throws<ArgumentException>(() => FlowRuntime.RestoreCheckpoint(data));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Restore_RejectsProgressFromFutureEvenWithoutDiagnosticEvents(int operations)
    {
        var fixture = new CheckpointFixture(twoLinks: true);
        Assert.True(fixture.Runtime.TryReserve(CheckpointFixture.Parcel));
        fixture.Runtime.AdvanceTo(20, operations);
        FlowCheckpoint data = fixture.Runtime.CaptureCheckpoint();
        data = data with { Now = data.Parcels[0].TransferTick, Events = Array.Empty<EventCheckpoint>() };

        Assert.Throws<ArgumentException>(() => FlowRuntime.RestoreCheckpoint(data));
    }

    [Fact]
    public void Restore_PreservesUnclaimedRegisteredCargoAndIntentOnlyShipment()
    {
        var fixture = new CheckpointFixture();
        var unclaimed = new CargoId(CheckpointFixture.Id(999));
        fixture.Source.Inner.Seed(unclaimed, CheckpointFixture.Manifest);
        fixture.Runtime.RegisterCargo(unclaimed, CheckpointFixture.Origin, CheckpointFixture.Manifest);
        var intent = new ShipmentId(CheckpointFixture.Id(998));
        fixture.Runtime.CreateShipment(intent, CheckpointFixture.Origin, CheckpointFixture.Destination,
            CheckpointFixture.Manifest, CheckpointFixture.Policy);

        FlowRuntime restored = FlowRuntime.RestoreCheckpoint(fixture.Runtime.CaptureCheckpoint());

        Assert.Null(restored.GetShipment(intent).ParcelId);
        Assert.Null(restored.GetCargo(unclaimed).ClaimedBy);
        Assert.Equal(CargoOwner.AtStation(CheckpointFixture.Origin), restored.GetCargo(unclaimed).Owner);
        Assert.Equal(CheckpointFixture.Manifest, restored.GetCheckpointPort(CheckpointFixture.Origin).ReadCargo(unclaimed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoredExtractionRequest_RetainsRevocationOrReceiptAcrossRedispatch(bool applied)
    {
        var fixture = new CheckpointFixture();
        fixture.Source.Throw = true;
        fixture.Source.AfterCommit = applied;
        Assert.True(fixture.Runtime.TryReserve(CheckpointFixture.Parcel));
        Assert.Throws<CheckpointFixture.PortFailure>(() => fixture.Runtime.AdvanceTo(1));
        FlowRuntime restored = FlowRuntime.RestoreCheckpoint(fixture.Runtime.CaptureCheckpoint());
        PortTransfer previous = restored.GetParcel(CheckpointFixture.Parcel).PendingTransfer!;
        Assert.True(restored.ReconcileTransfer(CheckpointFixture.Parcel));
        if (applied)
        {
            Assert.Equal(2, restored.AdvanceTo(4));
            restored.AddLink(new LinkId(CheckpointFixture.Id(12)), CheckpointFixture.Destination, CheckpointFixture.Origin, 20, 3);
        }
        var shipment = new ShipmentId(CheckpointFixture.Id(2));
        restored.CreateShipment(shipment, applied ? CheckpointFixture.Destination : CheckpointFixture.Origin,
            applied ? CheckpointFixture.Origin : CheckpointFixture.Destination, CheckpointFixture.Manifest, CheckpointFixture.Policy);
        Parcel second = restored.SplitShipment(shipment, new ParcelId(CheckpointFixture.Id(2)), CheckpointFixture.Cargo);
        Assert.True(restored.TryReserve(second.Id));
        Assert.Equal(3, restored.AdvanceTo(applied ? 8 : 5));
        byte[] before = CheckpointCodec.Encode(new CheckpointImage(0, restored.CaptureCheckpoint()));

        if (applied)
        {
            Assert.Equal(PortResult.Applied, restored.GetCheckpointPort(previous.StationId).Apply(previous));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => restored.GetCheckpointPort(previous.StationId).Apply(previous));
        }

        Assert.Equal(before, CheckpointCodec.Encode(new CheckpointImage(0, restored.CaptureCheckpoint())));
        Assert.Equal(ParcelState.Delivered, restored.GetParcel(second.Id).State);
        Assert.Null(restored.GetCargo(CheckpointFixture.Cargo).ClaimedBy);
        AssertCustody(restored);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Capture_RejectsIncompletePortOrReentrantCallbackBeforeChangingWorld(bool reentrant)
    {
        var fixture = new CheckpointFixture();
        fixture.Source.CaptureOverride = () =>
        {
            if (reentrant)
            {
                fixture.Runtime.AdvanceTo(1);
            }
            return fixture.Source.Inner.CaptureCheckpoint() with { Inventory = Array.Empty<InventoryCheckpoint>() };
        };

        if (reentrant)
        {
            Assert.Throws<InvalidOperationException>(() => fixture.Runtime.CaptureCheckpoint());
        }
        else
        {
            Assert.Throws<ArgumentException>(() => fixture.Runtime.CaptureCheckpoint());
        }

        Assert.Equal(0, fixture.Runtime.Now);
        Assert.Equal(ParcelState.Created, fixture.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(CheckpointFixture.Manifest, fixture.Source.ReadCargo(CheckpointFixture.Cargo));
        fixture.Source.CaptureOverride = null;
        FlowRuntime restored = FlowRuntime.RestoreCheckpoint(fixture.Runtime.CaptureCheckpoint());
        Assert.True(restored.TryReserve(CheckpointFixture.Parcel));
        Assert.Equal(3, restored.AdvanceTo(4));
    }

    [Theory]
    [InlineData("network")]
    [InlineData("time")]
    [InlineData("limit")]
    [InlineData("attempt-limit")]
    [InlineData("null-array")]
    [InlineData("null-entry")]
    [InlineData("duplicate-station")]
    [InlineData("duplicate-physical")]
    [InlineData("missing-physical")]
    [InlineData("owner-none")]
    [InlineData("owner-two")]
    [InlineData("orphan-claim")]
    [InlineData("missing-claim")]
    [InlineData("state")]
    [InlineData("version")]
    [InlineData("sequence")]
    [InlineData("schedule-kind")]
    [InlineData("manifest")]
    [InlineData("policy")]
    [InlineData("shipment-backlink")]
    [InlineData("route")]
    [InlineData("dependency")]
    public void CheckpointRestore_RejectsInconsistentAuthoritativeState(string corruption)
    {
        var fixture = new CheckpointFixture();
        fixture.Reach(ParcelState.Reserved);
        FlowCheckpoint data = fixture.Runtime.CaptureCheckpoint();
        ParcelCheckpoint parcel = data.Parcels[0];
        switch (corruption)
        {
            case "network": data = data with { NetworkId = Guid.Empty }; break;
            case "time": data = data with { Now = -1 }; break;
            case "limit": data = data with { Limits = data.Limits with { MaxStations = 65537 } }; break;
            case "attempt-limit": data = data with { Limits = data.Limits with { MaxDeliveryAttempts = 65 } }; break;
            case "null-array": data = data with { Cargo = null! }; break;
            case "null-entry": data.Cargo[0] = null!; break;
            case "duplicate-station": data.Stations[1] = data.Stations[0]; break;
            case "duplicate-physical": data.Stations[1] = data.Stations[1] with { Port = data.Stations[1].Port with { Inventory = data.Stations[0].Port.Inventory } }; break;
            case "missing-physical": data.Stations[0] = data.Stations[0] with { Port = data.Stations[0].Port with { Inventory = Array.Empty<InventoryCheckpoint>() } }; break;
            case "owner-none": data.Cargo[0] = data.Cargo[0] with { Owner = new OwnerCheckpoint(null, null, null) }; break;
            case "owner-two": data.Cargo[0] = data.Cargo[0] with { Owner = new OwnerCheckpoint(CheckpointFixture.Origin.Value, parcel.Id, null) }; break;
            case "orphan-claim": data.Cargo[0] = data.Cargo[0] with { ClaimedBy = CheckpointFixture.Id(888) }; break;
            case "missing-claim": data.Cargo[0] = data.Cargo[0] with { ClaimedBy = null }; break;
            case "state": data.Parcels[0] = parcel with { State = 100 }; break;
            case "version": data.Parcels[0] = parcel with { Version = long.MaxValue - 1, PendingOperation = parcel.PendingOperation! with { Sequence = long.MaxValue } }; break;
            case "sequence": data.Parcels[0] = parcel with { PendingOperation = parcel.PendingOperation! with { Sequence = 5 } }; break;
            case "schedule-kind": data.Parcels[0] = parcel with { PendingOperation = parcel.PendingOperation! with { Kind = (int)OperationKind.Delivery } }; break;
            case "manifest": data.Cargo[0] = data.Cargo[0] with { Manifest = new ManifestCheckpoint("ore", 8) }; break;
            case "policy": data.Parcels[0] = parcel with { Policy = new PolicyCheckpoint(100, 0) }; break;
            case "shipment-backlink": data.Shipments[0] = data.Shipments[0] with { ParcelId = null }; break;
            case "route": data.Parcels[0] = parcel with { Plan = parcel.Plan! with { Links = new[] { CheckpointFixture.Id(888) } } }; break;
            case "dependency": data.Parcels[0] = parcel with { Plan = parcel.Plan! with { Dependencies = Array.Empty<DependencyCheckpoint>() } }; break;
            default: throw new ArgumentException(nameof(corruption));
        }

        Assert.ThrowsAny<ArgumentException>(() => FlowRuntime.RestoreCheckpoint(data));
        Assert.Equal(ParcelState.Reserved, fixture.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(CheckpointFixture.Manifest, fixture.Source.ReadCargo(CheckpointFixture.Cargo));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("wrong-station")]
    [InlineData("retired-pending")]
    [InlineData("missing-transfer")]
    [InlineData("transfer-payload")]
    [InlineData("transfer-attempt")]
    public void CheckpointRestore_RejectsContradictoryTransferJournal(string corruption)
    {
        var fixture = new CheckpointFixture();
        fixture.Reach(ParcelState.ExtractionUncertain);
        FlowCheckpoint data = fixture.Runtime.CaptureCheckpoint();
        PortCheckpoint port = data.Stations[0].Port;
        switch (corruption)
        {
            case "missing": port = port with { Receipts = new[] { port.Receipts[0] with { Result = (int)PortResult.Missing } } }; break;
            case "unknown": port = port with { Receipts = new[] { port.Receipts[0] with { Result = 100 } } }; break;
            case "duplicate": port = port with { Receipts = new[] { port.Receipts[0], port.Receipts[0] } }; break;
            case "wrong-station": data.Stations[1] = data.Stations[1] with { Port = data.Stations[1].Port with { Receipts = port.Receipts } }; port = port with { Receipts = Array.Empty<ReceiptCheckpoint>() }; break;
            case "retired-pending": data.Transfers[0] = data.Transfers[0] with { Retired = true }; break;
            case "missing-transfer": data = data with { Transfers = Array.Empty<TransferCheckpoint>() }; break;
            case "transfer-payload": data.Transfers[0] = data.Transfers[0] with { CargoId = CheckpointFixture.Id(888) }; break;
            case "transfer-attempt": data.Transfers[0] = data.Transfers[0] with { Key = data.Transfers[0].Key with { Attempt = 2 } }; break;
            default: throw new ArgumentException(nameof(corruption));
        }
        data.Stations[0] = data.Stations[0] with { Port = port };

        Assert.ThrowsAny<ArgumentException>(() => FlowRuntime.RestoreCheckpoint(data));
    }

    private static void AssertCustody(FlowRuntime runtime)
    {
        CargoBatch cargo = runtime.GetCargo(CheckpointFixture.Cargo);
        int physical = runtime.GetCheckpointPort(CheckpointFixture.Origin).ReadCargo(cargo.Id)?.Quantity ?? 0;
        physical += runtime.GetCheckpointPort(CheckpointFixture.Destination).ReadCargo(cargo.Id)?.Quantity ?? 0;
        bool escrow = cargo.Owner.Parcel is not null;
        if (cargo.Owner.Transfer is PortTransferId transferId)
        {
            PortTransfer transfer = runtime.GetParcel(transferId.ParcelId).PendingTransfer!;
            Assert.Equal(transferId, transfer.Id);
            PortResult result = runtime.GetCheckpointPort(transfer.StationId).ReadResult(transfer);
            escrow = transfer.Kind == PortTransferKind.Extract ? result == PortResult.Applied : result != PortResult.Applied;
        }
        Assert.Equal(CheckpointFixture.Manifest.Quantity, physical + (escrow ? cargo.Manifest.Quantity : 0));
        Assert.Equal(1, (cargo.Owner.Station is null ? 0 : 1) + (cargo.Owner.Parcel is null ? 0 : 1) + (cargo.Owner.Transfer is null ? 0 : 1));
        Assert.Equal(CheckpointFixture.Manifest, cargo.Manifest);
    }
}

internal sealed class CheckpointFixture
{
    internal static readonly StationId Origin = new(Id(11));
    internal static readonly StationId Destination = new(Id(22));
    internal static readonly StationId Middle = new(Id(33));
    internal static readonly LinkId Link = new(Id(11));
    internal static readonly ParcelId Parcel = new(Id(1));
    internal static readonly ShipmentId Shipment = new(Id(1));
    internal static readonly CargoId Cargo = new(Id(9001));
    internal static readonly CargoManifest Manifest = new("ore", 7);
    internal static readonly ServicePolicy Policy = new(ServiceClass.Standard, DeliveryGuarantee.Flexible);
    internal FlowRuntime Runtime { get; }
    internal FaultPort Source { get; } = new();
    internal FaultPort DestinationPort { get; } = new();

    internal CheckpointFixture(bool twoLinks = false, FlowLimits? limits = null)
    {
        Runtime = new FlowRuntime(new NetworkId(Id(100)), limits);
        Runtime.AddStation(Origin, Source);
        Runtime.AddStation(Destination, DestinationPort);
        if (twoLinks)
        {
            Runtime.AddStation(Middle, new InMemoryCargoPort());
            Runtime.AddLink(Link, Origin, Middle, 20, 3);
            Runtime.AddLink(new LinkId(Id(12)), Middle, Destination, 20, 5);
        }
        else
        {
            Runtime.AddLink(Link, Origin, Destination, 20, 3);
        }
        AddBatch(1);
    }

    internal Parcel AddBatch(int execution)
    {
        var cargo = new CargoId(Id(9000 + execution));
        Source.Inner.Seed(cargo, Manifest);
        Runtime.RegisterCargo(cargo, Origin, Manifest);
        return Assign(cargo, execution, Origin, Destination);
    }

    internal Parcel Assign(CargoId cargo, int execution, StationId origin, StationId destination)
    {
        var shipment = new ShipmentId(Id(execution));
        Runtime.CreateShipment(shipment, origin, destination, Manifest, Policy);
        return Runtime.SplitShipment(shipment, new ParcelId(Id(execution)), cargo);
    }

    internal void Reach(ParcelState state)
    {
        if (state == ParcelState.Created)
        {
            return;
        }
        Assert.True(Runtime.TryReserve(Parcel));
        switch (state)
        {
            case ParcelState.Reserved: break;
            case ParcelState.Cancelled: Assert.True(Runtime.Cancel(Parcel)); break;
            case ParcelState.InTransit: Assert.Equal(1, Runtime.AdvanceTo(1)); break;
            case ParcelState.Arrived: Assert.Equal(2, Runtime.AdvanceTo(4, 2)); break;
            case ParcelState.Delivered: Assert.Equal(3, Runtime.AdvanceTo(4)); break;
            case ParcelState.DeliveryRejected: DestinationPort.Inner.AcceptDeposits = false; Assert.Equal(3, Runtime.AdvanceTo(4)); break;
            case ParcelState.DeliveryFaulted:
                DestinationPort.Throw = true;
                Assert.Throws<PortFailure>(() => Runtime.AdvanceTo(4));
                Assert.True(Runtime.ReconcileTransfer(Parcel));
                break;
            case ParcelState.ExtractionUncertain:
                Source.Throw = true;
                Source.AfterCommit = true;
                Assert.Throws<PortFailure>(() => Runtime.AdvanceTo(1));
                break;
            case ParcelState.DeliveryUncertain:
                DestinationPort.Throw = true;
                DestinationPort.AfterCommit = true;
                Assert.Throws<PortFailure>(() => Runtime.AdvanceTo(4));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(state));
        }
        Assert.Equal(state, Runtime.GetParcel(Parcel).State);
    }

    internal static Guid Id(int value) => new(value, 0, 0, new byte[8]);
    internal sealed class PortFailure : Exception { }
    internal sealed class FaultPort : ICheckpointCargoPort
    {
        internal InMemoryCargoPort Inner { get; } = new();
        internal bool Throw { get; set; }
        internal bool AfterCommit { get; set; }
        internal Func<PortCheckpoint>? CaptureOverride { get; set; }
        public void Bind(PortAuthority authority, StationId station) => Inner.Bind(authority, station);
        public CargoManifest? ReadCargo(CargoId cargo) => Inner.ReadCargo(cargo);
        public PortResult ReadResult(PortTransfer transfer) => Inner.ReadResult(transfer);
        public PortCheckpoint CaptureCheckpoint() => CaptureOverride?.Invoke() ?? Inner.CaptureCheckpoint();
        public PortResult Apply(PortTransfer transfer)
        {
            if (Throw && !AfterCommit)
            {
                throw new PortFailure();
            }
            PortResult result = Inner.Apply(transfer);
            if (Throw)
            {
                throw new PortFailure();
            }
            return result;
        }
    }
}
