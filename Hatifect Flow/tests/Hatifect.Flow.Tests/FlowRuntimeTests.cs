using System;
using System.Linq;
using System.Threading;
using Hatifect.Flow.Application.Dispatch;
using Hatifect.Flow.Application.Planning;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Policies;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Scheduling;
using Hatifect.Flow.Domain.Shipments;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class FlowRuntimeTests
{
    [Fact]
    public void ExplicitLinkDelivery_ExecutesOrderedStatesAndEvents()
    {
        var scenario = new Scenario();
        Parcel parcel = scenario.CreateBatch(1, 5);
        FlowRuntime runtime = scenario.Runtime;
        Shipment shipment = runtime.GetShipment(parcel.ShipmentId);
        Assert.Equal(Scenario.Origin, shipment.Origin);
        Assert.Equal(Scenario.Destination, shipment.Destination);
        Assert.Equal(parcel.Id, shipment.ParcelId);
        Assert.Equal(ParcelState.Created, parcel.State);
        AssertCargo(runtime, parcel.Id, 5, Scenario.Origin);

        Assert.Equal(RouteStatus.Found, runtime.PlanRoute(Scenario.Origin, Scenario.Destination).Status);
        Assert.True(runtime.TryReserve(parcel.Id));
        Assert.Equal(ParcelState.Reserved, runtime.GetParcel(parcel.Id).State);
        Assert.Equal(5, runtime.ReservedUnits(Scenario.Link));
        AssertCargo(runtime, parcel.Id, 5, Scenario.Origin);
        Assert.Equal(0, runtime.AdvanceTo(0));

        Assert.Equal(1, runtime.AdvanceTo(1, 1));
        Assert.Equal(ParcelState.InTransit, runtime.GetParcel(parcel.Id).State);
        AssertCargo(runtime, parcel.Id, 5, null);
        Assert.Equal(0, runtime.AdvanceTo(3));

        Assert.Equal(1, runtime.AdvanceTo(4, 1));
        Assert.Equal(ParcelState.Arrived, runtime.GetParcel(parcel.Id).State);
        Assert.Equal(Scenario.Destination, runtime.GetParcel(parcel.Id).CurrentStation);
        Assert.Equal(0, runtime.ReservedUnits(Scenario.Link));
        AssertCargo(runtime, parcel.Id, 5, null);

        Assert.Equal(1, runtime.AdvanceTo(4, 1));
        Assert.Equal(ParcelState.Delivered, runtime.GetParcel(parcel.Id).State);
        AssertCargo(runtime, parcel.Id, 5, Scenario.Destination);
        Assert.Null(runtime.PeekNextOperation());
        Assert.Equal(0, runtime.PendingOperationCount);
        Assert.Equal(new[] { OperationKind.Reservation, OperationKind.Departure, OperationKind.Arrival, OperationKind.Delivery },
            runtime.Events.Select(item => item.Kind));
        Assert.Equal(new long[] { 0, 1, 4, 4 }, runtime.Events.Select(item => item.Tick));
        Assert.Equal(new long[] { 1, 2, 3, 4 }, runtime.Events.Select(item => item.Sequence));
        Assert.Equal(new[] { CargoOwner.AtStation(Scenario.Origin), CargoOwner.InParcel(parcel.Id),
            CargoOwner.InParcel(parcel.Id), CargoOwner.AtStation(Scenario.Destination) },
            runtime.Events.Select(item => item.Owner));
        Assert.Equal(1, scenario.DestinationPort.AdmissionCalls);
        Assert.Equal(new CargoManifest("ore", 5), scenario.DestinationPort.LastManifest);
    }

    [Fact]
    public void ShipmentPolicyChange_DoesNotChangeExistingParcelSnapshot()
    {
        var scenario = new Scenario();
        Parcel parcel = scenario.CreateBatch(1, 3);
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));
        scenario.Runtime.AdvanceTo(1);
        var replacement = new ServicePolicy(ServiceClass.Express, DeliveryGuarantee.Exact);

        scenario.Runtime.ChangePolicy(parcel.ShipmentId, replacement);
        scenario.Runtime.AdvanceTo(4);

        Assert.Equal(replacement, scenario.Runtime.GetShipment(parcel.ShipmentId).Policy);
        Parcel delivered = scenario.Runtime.GetParcel(parcel.Id);
        Assert.Equal(ServiceClass.Standard, delivered.PolicySnapshot.ServiceClass);
        Assert.Equal(DeliveryGuarantee.Flexible, delivered.PolicySnapshot.Guarantee);
        Assert.Equal(ParcelState.Delivered, delivered.State);
        AssertCargo(scenario.Runtime, parcel.Id, 3, Scenario.Destination);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisconnectedOrReverseRoute_RejectsReservationWithoutCargoMutation(bool reverse)
    {
        var scenario = new Scenario(connect: reverse);
        StationId origin = reverse ? Scenario.Destination : Scenario.Origin;
        StationId destination = reverse ? Scenario.Origin : Scenario.Destination;
        Parcel parcel = scenario.CreateBatch(1, 3, origin, destination);

        Assert.False(scenario.Runtime.TryReserve(parcel.Id));

        Assert.Equal(parcel, scenario.Runtime.GetParcel(parcel.Id));
        Assert.Equal(0, scenario.Runtime.PendingOperationCount);
        Assert.Equal(0, scenario.Runtime.ReservedUnits(Scenario.Link));
        Assert.Empty(scenario.Runtime.Events);
        AssertCargo(scenario.Runtime, parcel.Id, 3, origin);
    }

    [Fact]
    public void Capacity_AtLimitReservesAndOverflowDoesNotMutate()
    {
        var scenario = new Scenario(capacity: 5);
        Parcel first = scenario.CreateBatch(1, 3);
        Parcel second = scenario.CreateBatch(2, 2);
        Parcel overflow = scenario.CreateBatch(3, 1);

        Assert.True(scenario.Runtime.TryReserve(first.Id));
        Assert.True(scenario.Runtime.TryReserve(second.Id));
        Assert.False(scenario.Runtime.TryReserve(overflow.Id));

        Assert.Equal(5, scenario.Runtime.ReservedUnits(Scenario.Link));
        Assert.Equal(2, scenario.Runtime.PendingOperationCount);
        Assert.Equal(overflow, scenario.Runtime.GetParcel(overflow.Id));
        AssertCargo(scenario.Runtime, overflow.Id, 1, Scenario.Origin);
        Assert.True(scenario.Runtime.Cancel(first.Id));
        Assert.True(scenario.Runtime.TryReserve(overflow.Id));
        Assert.Equal(3, scenario.Runtime.ReservedUnits(Scenario.Link));
    }

    [Fact]
    public void MultiLinkCapacityFailure_DoesNotReserveEarlierLinks()
    {
        var scenario = new Scenario(connect: false);
        var middle = new StationId(Id(9));
        var secondLink = new LinkId(Id(12));
        scenario.Runtime.AddStation(middle, new AdmissionPort());
        scenario.Runtime.AddLink(Scenario.Link, Scenario.Origin, middle, 10, 2);
        scenario.Runtime.AddLink(secondLink, middle, Scenario.Destination, 2, 2);
        Parcel parcel = scenario.CreateBatch(1, 3);

        Assert.False(scenario.Runtime.TryReserve(parcel.Id));

        Assert.Equal(0, scenario.Runtime.ReservedUnits(Scenario.Link));
        Assert.Equal(0, scenario.Runtime.ReservedUnits(secondLink));
        Assert.Equal(parcel, scenario.Runtime.GetParcel(parcel.Id));
        AssertCargo(scenario.Runtime, parcel.Id, 3, Scenario.Origin);
    }

    [Fact]
    public void InvalidOrTamperedOperation_LeavesStateOwnershipAndQueueUnchanged()
    {
        var scenario = new Scenario();
        Parcel parcel = scenario.CreateBatch(1, 3);
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));
        ScheduledOperation pending = scenario.Runtime.PeekNextOperation()!;
        Parcel before = scenario.Runtime.GetParcel(parcel.Id);

        Assert.False(scenario.Runtime.ApplyOperation(pending));
        Assert.False(scenario.Runtime.ApplyOperation(pending with { DueTick = 0 }));
        Assert.False(scenario.Runtime.ApplyOperation(pending with { Kind = OperationKind.Delivery, DueTick = 0 }));
        Assert.False(scenario.Runtime.ApplyOperation(pending with { Sequence = pending.Sequence + 1, DueTick = 0 }));
        Assert.False(scenario.Runtime.RetryDelivery(parcel.Id));

        Assert.Equal(before, scenario.Runtime.GetParcel(parcel.Id));
        Assert.Equal(pending, scenario.Runtime.PeekNextOperation());
        Assert.Equal(1, scenario.Runtime.PendingOperationCount);
        Assert.Single(scenario.Runtime.Events);
        AssertCargo(scenario.Runtime, parcel.Id, 3, Scenario.Origin);
        scenario.Runtime.AdvanceTo(1);
        Assert.False(scenario.Runtime.Cancel(parcel.Id));
        Assert.Equal(ParcelState.InTransit, scenario.Runtime.GetParcel(parcel.Id).State);
        AssertCargo(scenario.Runtime, parcel.Id, 3, null);
    }

    [Fact]
    public void RepeatedAuthoritativeOperation_DoesNotRepeatCargoTransfer()
    {
        var scenario = new Scenario(capacity: 10);
        Parcel first = scenario.CreateBatch(1, 2);
        Parcel second = scenario.CreateBatch(2, 3);
        Assert.True(scenario.Runtime.TryReserve(second.Id));
        Assert.True(scenario.Runtime.TryReserve(first.Id));
        Assert.Equal(1, scenario.Runtime.AdvanceTo(1, 1));
        ScheduledOperation operation = scenario.Runtime.PeekNextOperation()!;
        Assert.Equal(second.Id, operation.ParcelId);

        Assert.True(scenario.Runtime.ApplyOperation(operation));
        Parcel committed = scenario.Runtime.GetParcel(second.Id);
        int eventCount = scenario.Runtime.Events.Count;
        Assert.False(scenario.Runtime.ApplyOperation(operation));
        Assert.False(scenario.Runtime.TryReserve(second.Id));

        Assert.Equal(committed, scenario.Runtime.GetParcel(second.Id));
        Assert.Equal(eventCount, scenario.Runtime.Events.Count);
        Assert.Equal(2, scenario.Runtime.PendingOperationCount);
        Assert.Equal(5, scenario.Runtime.ReservedUnits(Scenario.Link));
        AssertCargo(scenario.Runtime, second.Id, 3, null);
        scenario.Runtime.AdvanceTo(4);
        Assert.False(scenario.Runtime.ApplyOperation(operation));
        AssertCargo(scenario.Runtime, second.Id, 3, Scenario.Destination);
        Assert.Equal(2, scenario.DestinationPort.AdmissionCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelBeforeDeparture_LeavesCargoAtSourceAndReleasesReservation(bool reserve)
    {
        var scenario = new Scenario();
        Parcel parcel = scenario.CreateBatch(1, 5);
        if (reserve)
        {
            Assert.True(scenario.Runtime.TryReserve(parcel.Id));
        }
        ScheduledOperation? stale = scenario.Runtime.PeekNextOperation();

        Assert.True(scenario.Runtime.Cancel(parcel.Id));
        Parcel cancelled = scenario.Runtime.GetParcel(parcel.Id);
        Assert.False(scenario.Runtime.Cancel(parcel.Id));
        Assert.False(scenario.Runtime.TryReserve(parcel.Id));
        Assert.Equal(0, scenario.Runtime.AdvanceTo(100));
        if (stale is not null)
        {
            Assert.False(scenario.Runtime.ApplyOperation(stale));
        }

        Assert.Equal(ParcelState.Cancelled, cancelled.State);
        Assert.Equal(cancelled, scenario.Runtime.GetParcel(parcel.Id));
        Assert.Equal(0, scenario.Runtime.ReservedUnits(Scenario.Link));
        Assert.Equal(0, scenario.Runtime.PendingOperationCount);
        AssertCargo(scenario.Runtime, parcel.Id, 5, Scenario.Origin);
        Assert.Equal(0, scenario.DestinationPort.AdmissionCalls);
    }

    [Fact]
    public void RejectedDelivery_RetainsCargoUntilExplicitRetrySucceeds()
    {
        var scenario = new Scenario();
        scenario.DestinationPort.Accept = false;
        Parcel parcel = scenario.CreateBatch(1, 4);
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));
        Assert.Equal(3, scenario.Runtime.AdvanceTo(4));
        Parcel rejected = scenario.Runtime.GetParcel(parcel.Id);

        Assert.Equal(ParcelState.DeliveryRejected, rejected.State);
        Assert.Equal(1, rejected.DeliveryAttempts);
        Assert.Equal(0, scenario.Runtime.PendingOperationCount);
        Assert.Equal(0, scenario.Runtime.ReservedUnits(Scenario.Link));
        AssertCargo(scenario.Runtime, parcel.Id, 4, null);
        Assert.Equal(0, scenario.Runtime.AdvanceTo(50));
        Assert.Equal(1, scenario.DestinationPort.AdmissionCalls);

        scenario.DestinationPort.Accept = true;
        Assert.True(scenario.Runtime.RetryDelivery(parcel.Id));
        Assert.False(scenario.Runtime.RetryDelivery(parcel.Id));
        Assert.Equal(0, scenario.Runtime.AdvanceTo(50));
        Assert.Equal(1, scenario.Runtime.AdvanceTo(51));

        Assert.Equal(ParcelState.Delivered, scenario.Runtime.GetParcel(parcel.Id).State);
        Assert.Equal(2, scenario.Runtime.GetParcel(parcel.Id).DeliveryAttempts);
        AssertCargo(scenario.Runtime, parcel.Id, 4, Scenario.Destination);
        Assert.False(scenario.Runtime.RetryDelivery(parcel.Id));
        Assert.Equal(2, scenario.DestinationPort.AdmissionCalls);
    }

    [Fact]
    public void FailureRetryAndReplay_ConserveAllCargoBatches()
    {
        var scenario = new Scenario(capacity: 20);
        Parcel cancelled = scenario.CreateBatch(1, 3);
        Parcel failed = scenario.CreateBatch(2, 7);
        Assert.True(scenario.Runtime.TryReserve(cancelled.Id));
        Assert.True(scenario.Runtime.TryReserve(failed.Id));
        Assert.True(scenario.Runtime.Cancel(cancelled.Id));
        scenario.DestinationPort.Failure = new InvalidOperationException("admission unavailable");
        Assert.Equal(2, scenario.Runtime.AdvanceTo(4, 2));
        ScheduledOperation faultedOperation = scenario.Runtime.PeekNextOperation()!;

        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.ApplyOperation(faultedOperation));
        ResolveMissingDeliveryFault(scenario, failed.Id);
        Parcel beforeRetry = scenario.Runtime.GetParcel(failed.Id);
        Assert.Equal(ParcelState.DeliveryFaulted, beforeRetry.State);
        Assert.Equal(1, beforeRetry.DeliveryAttempts);
        Assert.Equal(OperationKind.Delivery, faultedOperation.Kind);
        Assert.Equal(0, scenario.Runtime.PendingOperationCount);
        Assert.False(scenario.Runtime.ApplyOperation(faultedOperation));
        Assert.Equal(0, scenario.Runtime.AdvanceTo(4));
        AssertCargo(scenario.Runtime, cancelled.Id, 3, Scenario.Origin);
        AssertCargo(scenario.Runtime, failed.Id, 7, null);
        Assert.Equal(10, scenario.Runtime.GetCargo(cancelled.CargoId).Manifest.Quantity + scenario.Runtime.GetCargo(failed.CargoId).Manifest.Quantity);

        scenario.DestinationPort.Failure = null;
        Assert.True(scenario.Runtime.RetryDelivery(failed.Id));
        ScheduledOperation retry = scenario.Runtime.PeekNextOperation()!;
        Assert.Equal(1, scenario.Runtime.AdvanceTo(5));
        Parcel delivered = scenario.Runtime.GetParcel(failed.Id);
        Assert.False(scenario.Runtime.ApplyOperation(retry));

        Assert.Equal(ParcelState.Delivered, delivered.State);
        Assert.Equal(2, delivered.DeliveryAttempts);
        Assert.Equal(delivered, scenario.Runtime.GetParcel(failed.Id));
        AssertCargo(scenario.Runtime, cancelled.Id, 3, Scenario.Origin);
        AssertCargo(scenario.Runtime, failed.Id, 7, Scenario.Destination);
        Assert.Equal(10, scenario.Runtime.GetCargo(cancelled.CargoId).Manifest.Quantity + scenario.Runtime.GetCargo(failed.CargoId).Manifest.Quantity);
        Assert.Equal(0, scenario.Runtime.ReservedUnits(Scenario.Link));
        Assert.Equal(0, scenario.Runtime.PendingOperationCount);
    }

    [Fact]
    public void TopologyChangeBeforeDeparture_CancelsAndReleasesCargoSafely()
    {
        var scenario = new Scenario();
        Parcel parcel = scenario.CreateBatch(1, 3);
        RoutePlan plan = scenario.Runtime.PlanRoute(Scenario.Origin, Scenario.Destination);
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));

        Assert.True(scenario.Runtime.RemoveLink(Scenario.Link));
        Assert.False(scenario.Runtime.IsPlanCurrent(plan));
        Assert.Equal(1, scenario.Runtime.AdvanceTo(1));

        Assert.Equal(ParcelState.Cancelled, scenario.Runtime.GetParcel(parcel.Id).State);
        Assert.Equal(OperationKind.ReservationInvalidated, scenario.Runtime.Events.Last().Kind);
        Assert.Equal(0, scenario.Runtime.PendingOperationCount);
        Assert.Equal(0, scenario.Runtime.ReservedUnits(Scenario.Link));
        AssertCargo(scenario.Runtime, parcel.Id, 3, Scenario.Origin);
    }

    [Fact]
    public void MultiHopTransit_TopologyRemovalHonorsAlreadyDepartedReservation()
    {
        var scenario = new Scenario(connect: false);
        var middle = new StationId(Id(9));
        var secondLink = new LinkId(Id(12));
        scenario.Runtime.AddStation(middle, new AdmissionPort());
        scenario.Runtime.AddLink(Scenario.Link, Scenario.Origin, middle, 10, 2);
        scenario.Runtime.AddLink(secondLink, middle, Scenario.Destination, 10, 3);
        Parcel parcel = scenario.CreateBatch(1, 3);
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));
        scenario.Runtime.AdvanceTo(1);

        Assert.True(scenario.Runtime.RemoveLink(secondLink));
        Assert.Equal(1, scenario.Runtime.AdvanceTo(3, 1));
        Assert.Equal(ParcelState.Arrived, scenario.Runtime.GetParcel(parcel.Id).State);
        Assert.Equal(middle, scenario.Runtime.GetParcel(parcel.Id).CurrentStation);
        Assert.Equal(0, scenario.Runtime.ReservedUnits(Scenario.Link));
        Assert.Equal(3, scenario.Runtime.ReservedUnits(secondLink));
        AssertCargo(scenario.Runtime, parcel.Id, 3, null);
        Assert.Equal(1, scenario.Runtime.AdvanceTo(3, 1));
        Assert.Equal(ParcelState.InTransit, scenario.Runtime.GetParcel(parcel.Id).State);
        AssertCargo(scenario.Runtime, parcel.Id, 3, null);
        Assert.Equal(2, scenario.Runtime.AdvanceTo(6));

        AssertCargo(scenario.Runtime, parcel.Id, 3, Scenario.Destination);
        Assert.Equal(0, scenario.Runtime.ReservedUnits(secondLink));
        Assert.Equal(new[] { OperationKind.Reservation, OperationKind.Departure, OperationKind.Arrival,
            OperationKind.Transfer, OperationKind.Arrival, OperationKind.Delivery }, scenario.Runtime.Events.Select(item => item.Kind));
        Assert.Equal(new long[] { 0, 1, 3, 3, 6, 6 }, scenario.Runtime.Events.Select(item => item.Tick));
        Assert.Equal(1, scenario.Runtime.RouteSearchCount);
    }

    [Fact]
    public void IdleAdvanceAndScheduledWork_DoNotRescanGraph()
    {
        var scenario = new Scenario();
        Parcel parcel = scenario.CreateBatch(1, 3);
        RoutePlan plan = scenario.Runtime.PlanRoute(Scenario.Origin, Scenario.Destination);
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));
        Assert.Equal(1, scenario.Runtime.RouteSearchCount);

        for (int tick = 0; tick <= 100; tick++)
        {
            scenario.Runtime.AdvanceTo(tick);
        }

        Assert.Equal(1, scenario.Runtime.RouteSearchCount);
        Assert.Equal(1, scenario.Runtime.RouteCacheCount);
        Assert.Equal(0, plan.ValidationPassCount);
        Assert.Equal(4, scenario.Runtime.Events.Count);
        Assert.Equal(0, scenario.Runtime.PendingOperationCount);
        AssertCargo(scenario.Runtime, parcel.Id, 3, Scenario.Destination);
    }

    [Fact]
    public void QueueAndAdvanceBudgets_RejectOverflowWithoutPartialReservations()
    {
        var scenario = new Scenario(capacity: 20,
            limits: new FlowLimits(maxPendingOperations: 2, maxOperationsPerAdvance: 2));
        Parcel first = scenario.CreateBatch(1, 3);
        Parcel second = scenario.CreateBatch(2, 4);
        Parcel overflow = scenario.CreateBatch(3, 5);
        Assert.True(scenario.Runtime.TryReserve(second.Id));
        Assert.True(scenario.Runtime.TryReserve(first.Id));

        Assert.False(scenario.Runtime.TryReserve(overflow.Id));
        Assert.Equal(overflow, scenario.Runtime.GetParcel(overflow.Id));
        Assert.Equal(7, scenario.Runtime.ReservedUnits(Scenario.Link));
        Assert.Equal(2, scenario.Runtime.PendingOperationCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => scenario.Runtime.AdvanceTo(10, 3));
        Assert.Equal(0, scenario.Runtime.Now);
        Assert.Equal(2, scenario.Runtime.AdvanceTo(10));
        Assert.Equal(2, scenario.Runtime.PendingOperationCount);
        Assert.Equal(ParcelState.InTransit, scenario.Runtime.GetParcel(first.Id).State);
        Assert.Equal(ParcelState.InTransit, scenario.Runtime.GetParcel(second.Id).State);
        Assert.Equal(2, scenario.Runtime.AdvanceTo(10));
        Assert.Equal(2, scenario.Runtime.AdvanceTo(10));

        Assert.Equal(0, scenario.Runtime.PendingOperationCount);
        AssertCargo(scenario.Runtime, first.Id, 3, Scenario.Destination);
        AssertCargo(scenario.Runtime, second.Id, 4, Scenario.Destination);
        Assert.True(scenario.Runtime.TryReserve(overflow.Id));
        Assert.Equal(1, scenario.Runtime.PendingOperationCount);
    }

    [Fact]
    public void RetryLimit_LastAllowedAttemptAndLaterRejectionPreserveCargo()
    {
        var scenario = new Scenario(limits: new FlowLimits(maxDeliveryAttempts: 3));
        scenario.DestinationPort.Accept = false;
        Parcel parcel = scenario.CreateBatch(1, 3);
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));
        scenario.Runtime.AdvanceTo(4);
        Assert.True(scenario.Runtime.RetryDelivery(parcel.Id));
        scenario.Runtime.AdvanceTo(5);
        Assert.Equal(2, scenario.Runtime.GetParcel(parcel.Id).DeliveryAttempts);
        Assert.True(scenario.Runtime.RetryDelivery(parcel.Id));
        scenario.Runtime.AdvanceTo(6);
        Parcel exhausted = scenario.Runtime.GetParcel(parcel.Id);

        Assert.Equal(3, exhausted.DeliveryAttempts);
        Assert.False(scenario.Runtime.RetryDelivery(parcel.Id));
        scenario.Runtime.AdvanceTo(100);
        Assert.False(scenario.Runtime.RetryDelivery(parcel.Id));

        Assert.Equal(exhausted, scenario.Runtime.GetParcel(parcel.Id));
        Assert.Equal(ParcelState.DeliveryRejected, exhausted.State);
        Assert.Equal(3, scenario.DestinationPort.AdmissionCalls);
        Assert.Equal(0, scenario.Runtime.PendingOperationCount);
        AssertCargo(scenario.Runtime, parcel.Id, 3, null);
    }

    [Fact]
    public void DuplicateOrMismatchedBatch_RejectsAssignmentWithoutCreatingCargo()
    {
        var scenario = new Scenario();
        Parcel first = scenario.CreateBatch(1, 3);
        var secondShipment = new ShipmentId(Id(102));
        scenario.Runtime.CreateShipment(secondShipment, Scenario.Origin, Scenario.Destination, new CargoManifest("ore", 3), Scenario.Policy);
        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.SplitShipment(secondShipment, first.Id, first.CargoId));
        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.RegisterCargo(first.CargoId, Scenario.Origin, new CargoManifest("ore", 3)));
        var wrong = new CargoId(Id(22));
        scenario.SourcePort.Seed(wrong, new CargoManifest("wood", 3));
        scenario.Runtime.RegisterCargo(wrong, Scenario.Origin, new CargoManifest("wood", 3));

        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.SplitShipment(secondShipment, new ParcelId(Id(22)), wrong));

        Assert.Null(scenario.Runtime.GetShipment(secondShipment).ParcelId);
        Assert.Equal(first, scenario.Runtime.GetParcel(first.Id));
        AssertCargo(scenario.Runtime, first.Id, 3, Scenario.Origin);
        Assert.Equal(new CargoManifest("wood", 3), scenario.Runtime.GetCargo(wrong).Manifest);
        Assert.Equal(CargoOwner.AtStation(Scenario.Origin), scenario.Runtime.GetCargo(wrong).Owner);
        Assert.Equal(0, scenario.Runtime.PendingOperationCount);
    }

    [Fact]
    public void ReentrantPortMutation_PropagatesAndRequiresExplicitRecovery()
    {
        var scenario = new Scenario();
        Parcel parcel = scenario.CreateBatch(1, 3);
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));
        scenario.DestinationPort.OnAdmission = () => scenario.Runtime.Cancel(parcel.Id);

        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.AdvanceTo(4));
        ResolveMissingDeliveryFault(scenario, parcel.Id);

        Assert.Equal(ParcelState.DeliveryFaulted, scenario.Runtime.GetParcel(parcel.Id).State);
        Assert.Equal(1, scenario.Runtime.GetParcel(parcel.Id).DeliveryAttempts);
        Assert.Null(scenario.Runtime.PeekNextOperation());
        AssertCargo(scenario.Runtime, parcel.Id, 3, null);
        scenario.DestinationPort.OnAdmission = null;
        Assert.Equal(0, scenario.Runtime.AdvanceTo(4));
        Assert.True(scenario.Runtime.RetryDelivery(parcel.Id));
        Assert.Equal(1, scenario.Runtime.AdvanceTo(5));
        AssertCargo(scenario.Runtime, parcel.Id, 3, Scenario.Destination);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ForeignOrClonedOperation_DoesNotConferRuntimeAuthority(bool sameNetwork)
    {
        var local = new Scenario();
        var foreign = new Scenario(network: sameNetwork ? 1 : 2);
        foreach (Scenario scenario in new[] { local, foreign })
        {
            Parcel earlier = scenario.CreateBatch(1, 2);
            Parcel target = scenario.CreateBatch(2, 3);
            Assert.True(scenario.Runtime.TryReserve(target.Id));
            Assert.True(scenario.Runtime.TryReserve(earlier.Id));
            Assert.Equal(1, scenario.Runtime.AdvanceTo(1, 1));
        }
        ScheduledOperation pending = local.Runtime.PeekNextOperation()!;
        ScheduledOperation foreignTicket = foreign.Runtime.PeekNextOperation()!;
        Parcel before = local.Runtime.GetParcel(pending.ParcelId);
        Assert.Equal(pending, foreignTicket);
        Assert.NotSame(pending, foreignTicket);

        Assert.False(local.Runtime.ApplyOperation(foreignTicket));
        Assert.False(local.Runtime.ApplyOperation(pending with { }));

        Assert.Equal(before, local.Runtime.GetParcel(pending.ParcelId));
        Assert.Same(pending, local.Runtime.PeekNextOperation());
        AssertCargo(local.Runtime, pending.ParcelId, 3, Scenario.Origin);
        Assert.True(local.Runtime.ApplyOperation(pending));
        AssertCargo(local.Runtime, pending.ParcelId, 3, null);
        Assert.False(local.Runtime.ApplyOperation(pending));
    }

    [Fact]
    public void ThrowingPort_DoesNotBlockOtherParcelOrAutomaticallyRetry()
    {
        var scenario = new Scenario();
        Parcel faulted = scenario.CreateBatch(1, 3);
        Parcel unaffected = scenario.CreateBatch(2, 4);
        Assert.True(scenario.Runtime.TryReserve(faulted.Id));
        Assert.True(scenario.Runtime.TryReserve(unaffected.Id));
        scenario.DestinationPort.Failure = new InvalidOperationException("offline");

        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.AdvanceTo(4));
        ResolveMissingDeliveryFault(scenario, faulted.Id);

        Assert.Equal(ParcelState.DeliveryFaulted, scenario.Runtime.GetParcel(faulted.Id).State);
        Assert.Equal(1, scenario.Runtime.GetParcel(faulted.Id).DeliveryAttempts);
        Assert.Equal(1, scenario.Runtime.PendingOperationCount);
        scenario.DestinationPort.Failure = null;
        Assert.Equal(2, scenario.Runtime.AdvanceTo(4));
        Assert.Equal(0, scenario.Runtime.AdvanceTo(100));
        Assert.Equal(ParcelState.DeliveryFaulted, scenario.Runtime.GetParcel(faulted.Id).State);
        Assert.Equal(ParcelState.Delivered, scenario.Runtime.GetParcel(unaffected.Id).State);
        AssertCargo(scenario.Runtime, faulted.Id, 3, null);
        AssertCargo(scenario.Runtime, unaffected.Id, 4, Scenario.Destination);
        Assert.Equal(2, scenario.DestinationPort.AdmissionCalls);
    }

    [Fact]
    public void FaultedDelivery_ConsumesBoundedAttemptsWithoutLosingCargo()
    {
        var scenario = new Scenario(limits: new FlowLimits(maxDeliveryAttempts: 3));
        Parcel parcel = scenario.CreateBatch(1, 3);
        scenario.DestinationPort.Failure = new InvalidOperationException("offline");
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));
        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.AdvanceTo(4));
        ResolveMissingDeliveryFault(scenario, parcel.Id);

        for (int attempt = 2; attempt <= 3; attempt++)
        {
            Assert.True(scenario.Runtime.RetryDelivery(parcel.Id));
            Assert.Throws<InvalidOperationException>(() => scenario.Runtime.AdvanceTo(attempt + 3));
            ResolveMissingDeliveryFault(scenario, parcel.Id);
            Assert.Equal(attempt, scenario.Runtime.GetParcel(parcel.Id).DeliveryAttempts);
            AssertCargo(scenario.Runtime, parcel.Id, 3, null);
        }

        Assert.False(scenario.Runtime.RetryDelivery(parcel.Id));
        Assert.Equal(0, scenario.Runtime.AdvanceTo(100));
        Assert.False(scenario.Runtime.RetryDelivery(parcel.Id));
        Assert.Equal(ParcelState.DeliveryFaulted, scenario.Runtime.GetParcel(parcel.Id).State);
        Assert.Equal(3, scenario.DestinationPort.AdmissionCalls);
        Assert.Equal(0, scenario.Runtime.PendingOperationCount);
    }

    [Fact]
    public void TimelineOverflow_RejectsBeforeCapacityOrOwnershipMutation()
    {
        var scenario = new Scenario();
        Parcel parcel = scenario.CreateBatch(1, 3);
        scenario.Runtime.AdvanceTo(long.MaxValue - 2);

        Assert.Throws<OverflowException>(() => scenario.Runtime.TryReserve(parcel.Id));

        Assert.Equal(parcel, scenario.Runtime.GetParcel(parcel.Id));
        Assert.Equal(0, scenario.Runtime.ReservedUnits(Scenario.Link));
        Assert.Equal(0, scenario.Runtime.PendingOperationCount);
        Assert.Empty(scenario.Runtime.Events);
        AssertCargo(scenario.Runtime, parcel.Id, 3, Scenario.Origin);
    }

    [Fact]
    public void DefaultIdentitiesAndBackwardTime_AreRejectedAtRuntimeBoundary()
    {
        var scenario = new Scenario();
        Assert.Throws<ArgumentException>(() => new FlowRuntime(default));
        Assert.Throws<ArgumentException>(() => scenario.Runtime.AddStation(default, new AdmissionPort()));
        Assert.Throws<ArgumentException>(() => scenario.Runtime.AddLink(default, Scenario.Origin, Scenario.Destination, 1, 1));
        Assert.Throws<ArgumentException>(() => scenario.Runtime.RegisterCargo(default, Scenario.Origin, new CargoManifest("ore", 1)));
        Assert.Throws<ArgumentException>(() => scenario.Runtime.CreateShipment(default, Scenario.Origin, Scenario.Destination, new CargoManifest("ore", 1), Scenario.Policy));
        scenario.Runtime.AdvanceTo(5);

        Assert.Throws<ArgumentOutOfRangeException>(() => scenario.Runtime.AdvanceTo(4));

        Assert.Equal(5, scenario.Runtime.Now);
        Assert.Equal(0, scenario.Runtime.PendingOperationCount);
        Assert.Empty(scenario.Runtime.Events);
    }

    [Fact]
    public void DiagnosticHistory_IsBoundedAndRetainsNewestCommittedEvents()
    {
        var scenario = new Scenario(limits: new FlowLimits(maxEvents: 3));
        Parcel parcel = scenario.CreateBatch(1, 3);
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));
        ScheduledOperation stale = scenario.Runtime.PeekNextOperation()!;

        scenario.Runtime.AdvanceTo(4);

        Assert.Equal(new[] { OperationKind.Departure, OperationKind.Arrival, OperationKind.Delivery }, scenario.Runtime.Events.Select(item => item.Kind));
        Assert.Equal(new long[] { 2, 3, 4 }, scenario.Runtime.Events.Select(item => item.Sequence));
        Assert.False(scenario.Runtime.ApplyOperation(stale));
        AssertCargo(scenario.Runtime, parcel.Id, 3, Scenario.Destination);
    }

    [Fact]
    public void StationAndLifetimeLinkLimits_RejectNewIdentitiesAtCapacity()
    {
        var scenario = new Scenario(limits: new FlowLimits(maxStations: 3, maxLinks: 2));
        var third = new StationId(Id(3));
        var secondLink = new LinkId(Id(11));
        scenario.Runtime.AddStation(third, new AdmissionPort());
        scenario.Runtime.AddLink(secondLink, Scenario.Destination, third, 5, 2);

        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.AddStation(new StationId(Id(4)), new AdmissionPort()));
        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.AddLink(new LinkId(Id(12)), Scenario.Origin, third, 5, 2));
        Assert.True(scenario.Runtime.RemoveLink(secondLink));
        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.AddLink(new LinkId(Id(12)), Scenario.Origin, third, 5, 2));

        Assert.Equal(RouteStatus.NoRoute, scenario.Runtime.PlanRoute(Scenario.Origin, third).Status);
        Assert.Equal(new[] { Scenario.Link }, scenario.Runtime.PlanRoute(Scenario.Origin, Scenario.Destination).Links.Select(link => link.Id));
        Assert.Throws<ArgumentException>(() => scenario.Runtime.PlanRoute(Scenario.Origin, new StationId(Id(4))));
    }

    [Fact]
    public void SessionParcelLimit_RejectsExtraCargoAndShipmentWithoutChangingAdmittedBatches()
    {
        var scenario = new Scenario(limits: new FlowLimits(maxParcels: 2));
        Parcel first = scenario.CreateBatch(1, 2);
        Parcel second = scenario.CreateBatch(2, 3);
        var extra = new CargoId(Id(3));
        scenario.SourcePort.Seed(extra, new CargoManifest("ore", 4));

        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.RegisterCargo(extra, Scenario.Origin, new CargoManifest("ore", 4)));
        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.CreateShipment(new ShipmentId(Id(3)),
            Scenario.Origin, Scenario.Destination, new CargoManifest("ore", 4), Scenario.Policy));

        Assert.Equal(first, scenario.Runtime.GetParcel(first.Id));
        Assert.Equal(second, scenario.Runtime.GetParcel(second.Id));
        AssertCargo(scenario.Runtime, first.Id, 2, Scenario.Origin);
        AssertCargo(scenario.Runtime, second.Id, 3, Scenario.Origin);
        Assert.Equal(0, scenario.Runtime.PendingOperationCount);
    }

    [Fact]
    public void CargoUnitLimit_AtBoundarySucceedsAndRejectedIntentDoesNotClaimIdentity()
    {
        var scenario = new Scenario(limits: new FlowLimits(maxCargoUnits: 5));
        var id = new CargoId(Id(1));
        var parcelId = new ParcelId(Id(1));
        var shipment = new ShipmentId(Id(1));

        Assert.Throws<ArgumentOutOfRangeException>(() => scenario.Runtime.RegisterCargo(id, Scenario.Origin, new CargoManifest("ore", 6)));
        Assert.Throws<ArgumentOutOfRangeException>(() => scenario.Runtime.CreateShipment(shipment,
            Scenario.Origin, Scenario.Destination, new CargoManifest("ore", 6), Scenario.Policy));
        scenario.SourcePort.Seed(id, new CargoManifest("ore", 5));
        scenario.Runtime.RegisterCargo(id, Scenario.Origin, new CargoManifest("ore", 5));
        scenario.Runtime.CreateShipment(shipment, Scenario.Origin, Scenario.Destination, new CargoManifest("ore", 5), Scenario.Policy);
        Parcel admitted = scenario.Runtime.SplitShipment(shipment, parcelId, id);

        Assert.Equal(5, admitted.Manifest.Quantity);
        AssertCargo(scenario.Runtime, parcelId, 5, Scenario.Origin);
        Assert.True(scenario.Runtime.TryReserve(parcelId));
        Assert.Equal(5, scenario.Runtime.ReservedUnits(Scenario.Link));
    }

    [Fact]
    public void MutationFromAnotherThread_IsRejectedWithoutLosingOwningThreadAccess()
    {
        var scenario = new Scenario();
        Parcel parcel = scenario.CreateBatch(1, 3);
        Exception? failure = null;
        var thread = new Thread(() => failure = Record.Exception(() => scenario.Runtime.Cancel(parcel.Id)));

        thread.Start();
        thread.Join();

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(parcel, scenario.Runtime.GetParcel(parcel.Id));
        AssertCargo(scenario.Runtime, parcel.Id, 3, Scenario.Origin);
        Assert.True(scenario.Runtime.Cancel(parcel.Id));
        Assert.Equal(ParcelState.Cancelled, scenario.Runtime.GetParcel(parcel.Id).State);
    }

    private static void AssertCargo(FlowRuntime runtime, ParcelId parcel, int quantity, StationId? station)
    {
        CargoId cargo = runtime.GetParcel(parcel).CargoId;
        CargoBatch batch = runtime.GetCargo(cargo);
        Assert.Equal(cargo, batch.Id);
        Assert.Equal(new CargoManifest("ore", quantity), batch.Manifest);
        Assert.Equal(station, batch.Owner.Station);
        Assert.Equal(station is null ? parcel : (ParcelId?)null, batch.Owner.Parcel);
        Assert.Null(batch.Owner.Transfer);
    }

    private static void ResolveMissingDeliveryFault(Scenario scenario, ParcelId parcelId)
    {
        Parcel uncertain = scenario.Runtime.GetParcel(parcelId);
        Assert.Equal(ParcelState.DeliveryUncertain, uncertain.State);
        Assert.NotNull(uncertain.PendingTransfer);
        CargoBatch batch = scenario.Runtime.GetCargo(uncertain.CargoId);
        Assert.Equal(uncertain.PendingTransfer.Id, batch.Owner.Transfer);
        Assert.Null(batch.Owner.Station);
        Assert.Null(batch.Owner.Parcel);
        Assert.False(scenario.Runtime.RetryDelivery(parcelId));

        Assert.True(scenario.Runtime.ReconcileTransfer(parcelId));

        Parcel faulted = scenario.Runtime.GetParcel(parcelId);
        Assert.Equal(ParcelState.DeliveryFaulted, faulted.State);
        Assert.Equal(uncertain.DeliveryAttempts, faulted.DeliveryAttempts);
        Assert.Null(faulted.PendingTransfer);
        Assert.Equal(CargoOwner.InParcel(parcelId), scenario.Runtime.GetCargo(uncertain.CargoId).Owner);
    }

    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);

    private sealed class Scenario
    {
        internal static readonly StationId Origin = new(Id(1));
        internal static readonly StationId Destination = new(Id(2));
        internal static readonly LinkId Link = new(Id(10));
        internal static readonly ServicePolicy Policy = new(ServiceClass.Standard, DeliveryGuarantee.Flexible);
        internal FlowRuntime Runtime { get; }
        internal AdmissionPort SourcePort { get; } = new();
        internal AdmissionPort DestinationPort { get; } = new();

        internal Scenario(bool connect = true, int capacity = 10, FlowLimits? limits = null, int network = 1)
        {
            Runtime = new FlowRuntime(new NetworkId(Id(network)), limits);
            Runtime.AddStation(Origin, SourcePort);
            Runtime.AddStation(Destination, DestinationPort);
            if (connect)
            {
                Runtime.AddLink(Link, Origin, Destination, capacity, 3);
            }
        }

        internal Parcel CreateBatch(int id, int quantity, StationId? origin = null, StationId? destination = null)
        {
            var parcel = new ParcelId(Id(id));
            var shipment = new ShipmentId(Id(id));
            var cargo = new CargoId(Id(id));
            var manifest = new CargoManifest("ore", quantity);
            AdmissionPort source = origin == Destination ? DestinationPort : SourcePort;
            source.Seed(cargo, manifest);
            Runtime.RegisterCargo(cargo, origin ?? Origin, manifest);
            Runtime.CreateShipment(shipment, origin ?? Origin, destination ?? Destination, manifest, Policy);
            return Runtime.SplitShipment(shipment, parcel, cargo);
        }
    }

    private sealed class AdmissionPort : ICargoPort
    {
        private readonly InMemoryCargoPort _inner = new();
        internal bool Accept
        {
            get => _inner.AcceptDeposits;
            set => _inner.AcceptDeposits = value;
        }
        internal Exception? Failure { get; set; }
        internal Action? OnAdmission { get; set; }
        internal int AdmissionCalls { get; private set; }
        internal CargoManifest? LastManifest { get; private set; }

        internal void Seed(CargoId cargo, CargoManifest manifest) => _inner.Seed(cargo, manifest);
        public void Bind(PortAuthority authority, StationId station) => _inner.Bind(authority, station);
        public CargoManifest? ReadCargo(CargoId cargo) => _inner.ReadCargo(cargo);
        public PortResult ReadResult(PortTransfer transfer) => _inner.ReadResult(transfer);

        public PortResult Apply(PortTransfer transfer)
        {
            if (transfer.Kind == PortTransferKind.Deposit)
            {
                AdmissionCalls++;
                LastManifest = transfer.Manifest;
                OnAdmission?.Invoke();
                if (Failure is not null)
                {
                    throw Failure;
                }
            }
            return _inner.Apply(transfer);
        }
    }
}
