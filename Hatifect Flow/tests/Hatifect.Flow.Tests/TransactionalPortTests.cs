using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.Flow.Application.Dispatch;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Policies;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Scheduling;
using Hatifect.Flow.Domain.Shipments;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class TransactionalPortTests
{
    [Fact]
    public void CargoId_RemainsStableAcrossSuccessiveParcelExecutions()
    {
        var scenario = new Scenario();
        Parcel first = scenario.CreateBatch();
        Assert.True(scenario.Runtime.TryReserve(first.Id));
        Assert.Equal(3, scenario.Runtime.AdvanceTo(4));
        Parcel deliveredFirst = scenario.Runtime.GetParcel(first.Id);
        Assert.Equal(ParcelState.Delivered, deliveredFirst.State);
        Assert.Null(scenario.Runtime.GetCargo(first.CargoId).ClaimedBy);
        AssertConserved(scenario, first, escrow: false);

        scenario.Runtime.AddLink(new LinkId(Id(12)), Scenario.Destination, Scenario.Origin, 20, 3);
        Parcel second = scenario.Assign(first.CargoId, 2, Scenario.Destination, Scenario.Origin);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.CargoId, second.CargoId);
        Assert.True(scenario.Runtime.TryReserve(second.Id));
        Assert.Equal(3, scenario.Runtime.AdvanceTo(8));

        Assert.Equal(deliveredFirst, scenario.Runtime.GetParcel(first.Id));
        Assert.Equal(ParcelState.Delivered, scenario.Runtime.GetParcel(second.Id).State);
        Assert.Equal(CargoOwner.AtStation(Scenario.Origin), scenario.Runtime.GetCargo(first.CargoId).Owner);
        Assert.Null(scenario.Runtime.GetCargo(first.CargoId).ClaimedBy);
        Assert.Equal(Scenario.Manifest, scenario.SourcePort.ReadCargo(first.CargoId));
        Assert.Null(scenario.DestinationPort.ReadCargo(first.CargoId));
        Assert.Equal(PortResult.Applied, scenario.SourcePort.Inner.Apply(scenario.SourcePort.ApplyCalls[0]));
        Assert.Equal(PortResult.Applied, scenario.DestinationPort.Inner.Apply(scenario.DestinationPort.ApplyCalls[0]));
        Assert.Equal(Scenario.Manifest, scenario.SourcePort.ReadCargo(first.CargoId));
        Assert.Null(scenario.DestinationPort.ReadCargo(first.CargoId));
        AssertConserved(scenario, second, escrow: false);
    }

    [Fact]
    public void ActiveCargoClaim_RejectsSecondParcelUntilCancellationReleasesIt()
    {
        var scenario = new Scenario();
        Parcel first = scenario.CreateBatch();
        var secondShipment = new ShipmentId(Id(2));
        scenario.Runtime.CreateShipment(secondShipment, Scenario.Origin, Scenario.Destination,
            Scenario.Manifest, Scenario.Policy);

        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.SplitShipment(secondShipment,
            new ParcelId(Id(2)), first.CargoId));

        Assert.Equal(first.Id, scenario.Runtime.GetCargo(first.CargoId).ClaimedBy);
        Assert.Null(scenario.Runtime.GetShipment(secondShipment).ParcelId);
        Assert.True(scenario.Runtime.Cancel(first.Id));
        Parcel replacement = scenario.Runtime.SplitShipment(secondShipment, new ParcelId(Id(2)), first.CargoId);
        Assert.Equal(replacement.Id, scenario.Runtime.GetCargo(first.CargoId).ClaimedBy);
        Assert.Equal(ParcelState.Cancelled, scenario.Runtime.GetParcel(first.Id).State);
        AssertConserved(scenario, replacement, escrow: false);
    }

    [Fact]
    public void ExtractionRejected_CancelsWithoutMovingCargoAndReleasesCapacity()
    {
        var scenario = new Scenario();
        Parcel parcel = scenario.CreateBatch();
        scenario.SourcePort.Inner.AcceptExtractions = false;
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));

        Assert.Equal(1, scenario.Runtime.AdvanceTo(1));

        Parcel cancelled = scenario.Runtime.GetParcel(parcel.Id);
        Assert.Equal(ParcelState.Cancelled, cancelled.State);
        Assert.Equal(0, cancelled.DeliveryAttempts);
        Assert.Equal(0, scenario.Runtime.ReservedUnits(Scenario.Link));
        Assert.Equal(0, scenario.Runtime.PendingOperationCount);
        Assert.Null(scenario.Runtime.GetCargo(parcel.CargoId).ClaimedBy);
        Assert.Equal(CargoOwner.AtStation(Scenario.Origin), scenario.Runtime.GetCargo(parcel.CargoId).Owner);
        Assert.Single(scenario.SourcePort.ApplyCalls);
        Assert.Empty(scenario.SourcePort.ReadCalls);
        scenario.SourcePort.Inner.AcceptExtractions = true;
        Assert.Equal(PortResult.Rejected, scenario.SourcePort.Inner.Apply(scenario.SourcePort.ApplyCalls[0]));
        AssertConserved(scenario, parcel, escrow: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtractionException_BeforeOrAfterCommitRequiresReadOnlyReconciliation(bool afterCommit)
    {
        var scenario = new Scenario();
        Parcel parcel = scenario.CreateBatch();
        scenario.SourcePort.Fault = afterCommit ? FaultPoint.AfterCommit : FaultPoint.BeforeCommit;
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));

        Assert.Throws<PortUnavailableException>(() => scenario.Runtime.AdvanceTo(1));

        PortTransfer transfer = AssertUncertain(scenario, parcel, ParcelState.ExtractionUncertain);
        Assert.Equal(PortTransferKind.Extract, transfer.Kind);
        Assert.Equal(afterCommit ? PortResult.Applied : PortResult.Missing, scenario.SourcePort.Inner.ReadResult(transfer));
        Assert.Equal(afterCommit ? null : Scenario.Manifest, scenario.SourcePort.ReadCargo(parcel.CargoId));
        Assert.Null(scenario.DestinationPort.ReadCargo(parcel.CargoId));
        Assert.Equal(7, scenario.Runtime.ReservedUnits(Scenario.Link));
        Assert.False(scenario.Runtime.Cancel(parcel.Id));
        Assert.False(scenario.Runtime.RetryDelivery(parcel.Id));
        Assert.Equal(0, scenario.Runtime.AdvanceTo(20));
        Assert.Single(scenario.SourcePort.ApplyCalls);
        AssertConserved(scenario, parcel, escrow: afterCommit);

        scenario.SourcePort.Fault = FaultPoint.None;
        Assert.True(scenario.Runtime.ReconcileTransfer(parcel.Id));

        Assert.Single(scenario.SourcePort.ApplyCalls);
        Assert.Single(scenario.SourcePort.ReadCalls);
        Assert.Same(transfer, scenario.SourcePort.ReadCalls[0]);
        Assert.False(scenario.Runtime.ReconcileTransfer(parcel.Id));
        if (afterCommit)
        {
            Assert.Equal(ParcelState.InTransit, scenario.Runtime.GetParcel(parcel.Id).State);
            Assert.Equal(CargoOwner.InParcel(parcel.Id), scenario.Runtime.GetCargo(parcel.CargoId).Owner);
            Assert.Equal(1, scenario.Runtime.PendingOperationCount);
            Assert.Equal(2, scenario.Runtime.AdvanceTo(20));
            Assert.Equal(ParcelState.Delivered, scenario.Runtime.GetParcel(parcel.Id).State);
            AssertConserved(scenario, parcel, escrow: false);
        }
        else
        {
            Assert.Equal(ParcelState.Cancelled, scenario.Runtime.GetParcel(parcel.Id).State);
            Assert.Equal(0, scenario.Runtime.ReservedUnits(Scenario.Link));
            Assert.Null(scenario.Runtime.GetCargo(parcel.CargoId).ClaimedBy);
            AssertConserved(scenario, parcel, escrow: false);
        }
    }

    [Fact]
    public void ExtractionReconciliation_RecordedRejectionCancelsWithoutReapplying()
    {
        var scenario = new Scenario();
        Parcel parcel = scenario.CreateBatch();
        scenario.SourcePort.Inner.AcceptExtractions = false;
        scenario.SourcePort.Fault = FaultPoint.AfterCommit;
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));
        Assert.Throws<PortUnavailableException>(() => scenario.Runtime.AdvanceTo(1));
        PortTransfer transfer = AssertUncertain(scenario, parcel, ParcelState.ExtractionUncertain);
        AssertConserved(scenario, parcel, escrow: false);

        scenario.SourcePort.Inner.AcceptExtractions = true;
        scenario.SourcePort.Fault = FaultPoint.None;
        Assert.True(scenario.Runtime.ReconcileTransfer(parcel.Id));

        Assert.Equal(PortResult.Rejected, scenario.SourcePort.Inner.ReadResult(transfer));
        Assert.Equal(ParcelState.Cancelled, scenario.Runtime.GetParcel(parcel.Id).State);
        Assert.Equal(0, scenario.Runtime.ReservedUnits(Scenario.Link));
        Assert.Single(scenario.SourcePort.ApplyCalls);
        AssertConserved(scenario, parcel, escrow: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DepositException_BeforeOrAfterCommitRefusesRetryUntilReconciled(bool afterCommit)
    {
        var scenario = new Scenario();
        Parcel parcel = scenario.CreateBatch();
        scenario.DestinationPort.Fault = afterCommit ? FaultPoint.AfterCommit : FaultPoint.BeforeCommit;
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));

        Assert.Throws<PortUnavailableException>(() => scenario.Runtime.AdvanceTo(4));

        PortTransfer transfer = AssertUncertain(scenario, parcel, ParcelState.DeliveryUncertain);
        Assert.Equal(PortTransferKind.Deposit, transfer.Kind);
        Assert.Equal(afterCommit ? PortResult.Applied : PortResult.Missing, scenario.DestinationPort.Inner.ReadResult(transfer));
        Assert.Equal(afterCommit ? Scenario.Manifest : null, scenario.DestinationPort.ReadCargo(parcel.CargoId));
        Assert.Null(scenario.SourcePort.ReadCargo(parcel.CargoId));
        Assert.Equal(1, transfer.Id.Attempt);
        Assert.Equal(1, scenario.Runtime.GetParcel(parcel.Id).DeliveryAttempts);
        Assert.False(scenario.Runtime.RetryDelivery(parcel.Id));
        Assert.Equal(0, scenario.Runtime.AdvanceTo(20));
        Assert.Single(scenario.DestinationPort.ApplyCalls);
        AssertConserved(scenario, parcel, escrow: !afterCommit);
        scenario.DestinationPort.Fault = FaultPoint.None;

        Assert.True(scenario.Runtime.ReconcileTransfer(parcel.Id));

        Assert.Single(scenario.DestinationPort.ReadCalls);
        Assert.Same(transfer, scenario.DestinationPort.ReadCalls[0]);
        Assert.Single(scenario.DestinationPort.ApplyCalls);
        Assert.False(scenario.Runtime.ReconcileTransfer(parcel.Id));
        if (afterCommit)
        {
            Assert.Equal(ParcelState.Delivered, scenario.Runtime.GetParcel(parcel.Id).State);
            Assert.False(scenario.Runtime.RetryDelivery(parcel.Id));
            Assert.Null(scenario.Runtime.GetCargo(parcel.CargoId).ClaimedBy);
        }
        else
        {
            Assert.Equal(ParcelState.DeliveryFaulted, scenario.Runtime.GetParcel(parcel.Id).State);
            Assert.Equal(CargoOwner.InParcel(parcel.Id), scenario.Runtime.GetCargo(parcel.CargoId).Owner);
            Assert.True(scenario.Runtime.RetryDelivery(parcel.Id));
            Assert.Equal(1, scenario.Runtime.AdvanceTo(21));
            Assert.Equal(ParcelState.Delivered, scenario.Runtime.GetParcel(parcel.Id).State);
            Assert.Equal(2, scenario.Runtime.GetParcel(parcel.Id).DeliveryAttempts);
            Assert.Equal(2, scenario.DestinationPort.ApplyCalls.Count);
            Assert.NotEqual(transfer.Id, scenario.DestinationPort.ApplyCalls[1].Id);
        }
        AssertConserved(scenario, parcel, escrow: false);
    }

    [Fact]
    public void DepositReconciliation_RecordedRejectionRequiresNewExplicitAttempt()
    {
        var scenario = new Scenario();
        Parcel parcel = scenario.CreateBatch();
        scenario.DestinationPort.Inner.AcceptDeposits = false;
        scenario.DestinationPort.Fault = FaultPoint.AfterCommit;
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));
        Assert.Throws<PortUnavailableException>(() => scenario.Runtime.AdvanceTo(4));
        PortTransfer rejected = AssertUncertain(scenario, parcel, ParcelState.DeliveryUncertain);
        scenario.DestinationPort.Fault = FaultPoint.None;
        scenario.DestinationPort.Inner.AcceptDeposits = true;

        Assert.True(scenario.Runtime.ReconcileTransfer(parcel.Id));

        Assert.Equal(ParcelState.DeliveryRejected, scenario.Runtime.GetParcel(parcel.Id).State);
        Assert.Equal(PortResult.Rejected, scenario.DestinationPort.Inner.Apply(rejected));
        Assert.Null(scenario.DestinationPort.ReadCargo(parcel.CargoId));
        AssertConserved(scenario, parcel, escrow: true);
        Assert.True(scenario.Runtime.RetryDelivery(parcel.Id));
        Assert.Equal(1, scenario.Runtime.AdvanceTo(5));
        Assert.Equal(2, scenario.DestinationPort.ApplyCalls.Count);
        Assert.Equal(2, scenario.Runtime.GetParcel(parcel.Id).DeliveryAttempts);
        AssertConserved(scenario, parcel, escrow: false);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReconciliationReadFailure_RetainsUncertainTransferAndDoesNotReapply(bool deposit, bool afterCommit)
    {
        var scenario = new Scenario();
        Parcel parcel = scenario.CreateBatch();
        FaultPort target = deposit ? scenario.DestinationPort : scenario.SourcePort;
        target.Fault = afterCommit ? FaultPoint.AfterCommit : FaultPoint.BeforeCommit;
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));
        Assert.Throws<PortUnavailableException>(() => scenario.Runtime.AdvanceTo(deposit ? 4 : 1));
        Parcel before = scenario.Runtime.GetParcel(parcel.Id);
        CargoBatch beforeCargo = scenario.Runtime.GetCargo(parcel.CargoId);
        target.ReadFailure = true;

        Assert.Throws<PortUnavailableException>(() => scenario.Runtime.ReconcileTransfer(parcel.Id));
        Assert.Throws<PortUnavailableException>(() => scenario.Runtime.ReconcileTransfer(parcel.Id));

        Assert.Equal(before, scenario.Runtime.GetParcel(parcel.Id));
        Assert.Equal(beforeCargo, scenario.Runtime.GetCargo(parcel.CargoId));
        Assert.Single(target.ApplyCalls);
        Assert.Equal(2, target.ReadCalls.Count);
        Assert.All(target.ReadCalls, transfer => Assert.Same(before.PendingTransfer, transfer));
        Assert.False(scenario.Runtime.RetryDelivery(parcel.Id));
        AssertConserved(scenario, parcel, escrow: deposit ? !afterCommit : afterCommit);
        target.ReadFailure = false;
        target.Fault = FaultPoint.None;
        Assert.True(scenario.Runtime.ReconcileTransfer(parcel.Id));
        Assert.Null(scenario.Runtime.GetParcel(parcel.Id).PendingTransfer);
        Assert.Single(target.ApplyCalls);
    }

    [Fact]
    public void Restart_FencesOldWriterAndOldTicketsWhilePreservingScheduledWork()
    {
        var scenario = new Scenario();
        Parcel first = scenario.CreateBatch();
        Parcel second = scenario.CreateBatch(2);
        Assert.True(scenario.Runtime.TryReserve(second.Id));
        Assert.True(scenario.Runtime.TryReserve(first.Id));
        Assert.Equal(1, scenario.Runtime.AdvanceTo(1, 1));
        FlowRuntime old = scenario.Runtime;
        ScheduledOperation oldTicket = old.PeekNextOperation()!;
        Parcel before = old.GetParcel(second.Id);

        scenario.Runtime = old.Restart();

        Assert.Equal(1, scenario.Runtime.Now);
        Assert.Equal(2, scenario.Runtime.PendingOperationCount);
        Assert.Equal(14, scenario.Runtime.ReservedUnits(Scenario.Link));
        Assert.Equal(before with { PendingOperation = scenario.Runtime.PeekNextOperation() }, scenario.Runtime.GetParcel(second.Id));
        Assert.Equal(oldTicket, scenario.Runtime.PeekNextOperation());
        Assert.NotSame(oldTicket, scenario.Runtime.PeekNextOperation());
        Assert.Throws<InvalidOperationException>(() => old.AdvanceTo(4));
        Assert.Throws<InvalidOperationException>(() => old.Cancel(second.Id));
        Assert.Throws<InvalidOperationException>(() => old.Restart());
        Assert.Throws<InvalidOperationException>(() => old.ApplyOperation(oldTicket));
        Assert.False(scenario.Runtime.ApplyOperation(oldTicket));
        Assert.True(scenario.Runtime.ApplyOperation(scenario.Runtime.PeekNextOperation()!));
        Assert.Equal(4, scenario.Runtime.AdvanceTo(4));
        Assert.Equal(ParcelState.Delivered, scenario.Runtime.GetParcel(first.Id).State);
        Assert.Equal(ParcelState.Delivered, scenario.Runtime.GetParcel(second.Id).State);
        AssertConserved(scenario, first, escrow: false);
        AssertConserved(scenario, second, escrow: false);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Restart_DuringUncertainTransferReconcilesWithoutDuplicateCargo(bool deposit, bool afterCommit)
    {
        var scenario = new Scenario();
        Parcel parcel = scenario.CreateBatch();
        FaultPort target = deposit ? scenario.DestinationPort : scenario.SourcePort;
        target.Fault = afterCommit ? FaultPoint.AfterCommit : FaultPoint.BeforeCommit;
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));
        Assert.Throws<PortUnavailableException>(() => scenario.Runtime.AdvanceTo(deposit ? 4 : 1));
        Parcel before = scenario.Runtime.GetParcel(parcel.Id);
        FlowRuntime previous = scenario.Runtime;

        scenario.Runtime = previous.Restart();
        target.Fault = FaultPoint.None;

        Assert.Equal(before, scenario.Runtime.GetParcel(parcel.Id));
        Assert.Same(before.PendingTransfer, scenario.Runtime.GetParcel(parcel.Id).PendingTransfer);
        Assert.Throws<InvalidOperationException>(() => previous.ReconcileTransfer(parcel.Id));
        Assert.True(scenario.Runtime.ReconcileTransfer(parcel.Id));
        Assert.Single(target.ApplyCalls);
        Assert.Single(target.ReadCalls);
        Assert.False(scenario.Runtime.ReconcileTransfer(parcel.Id));
        if (!afterCommit && !deposit)
        {
            Assert.Equal(ParcelState.Cancelled, scenario.Runtime.GetParcel(parcel.Id).State);
        }
        else
        {
            if (deposit && !afterCommit)
            {
                Assert.True(scenario.Runtime.RetryDelivery(parcel.Id));
            }
            scenario.Runtime.AdvanceTo(10);
            Assert.Equal(ParcelState.Delivered, scenario.Runtime.GetParcel(parcel.Id).State);
        }
        AssertConserved(scenario, parcel, escrow: false);
    }

    [Fact]
    public void MissingReconciliation_RevokesTransferBeforeCargoCanBeReassigned()
    {
        var scenario = new Scenario();
        Parcel cancelled = scenario.CreateBatch();
        scenario.SourcePort.Fault = FaultPoint.BeforeCommit;
        Assert.True(scenario.Runtime.TryReserve(cancelled.Id));
        Assert.Throws<PortUnavailableException>(() => scenario.Runtime.AdvanceTo(1));
        PortTransfer stale = scenario.Runtime.GetParcel(cancelled.Id).PendingTransfer!;
        scenario.SourcePort.Fault = FaultPoint.None;
        Assert.True(scenario.Runtime.ReconcileTransfer(cancelled.Id));
        Assert.Equal(PortResult.Missing, scenario.SourcePort.Inner.ReadResult(stale));
        Parcel next = scenario.Assign(cancelled.CargoId, 2, Scenario.Origin, Scenario.Destination);

        Assert.Throws<InvalidOperationException>(() => scenario.SourcePort.Inner.Apply(stale));

        Assert.Equal(next.Id, scenario.Runtime.GetCargo(cancelled.CargoId).ClaimedBy);
        Assert.Equal(Scenario.Manifest, scenario.SourcePort.ReadCargo(cancelled.CargoId));
        Assert.True(scenario.Runtime.TryReserve(next.Id));
        Assert.Equal(3, scenario.Runtime.AdvanceTo(5));
        Assert.Throws<InvalidOperationException>(() => scenario.SourcePort.Inner.Apply(stale));
        Assert.Equal(ParcelState.Cancelled, scenario.Runtime.GetParcel(cancelled.Id).State);
        Assert.Equal(ParcelState.Delivered, scenario.Runtime.GetParcel(next.Id).State);
        AssertConserved(scenario, next, escrow: false);
    }

    [Fact]
    public void MissingDepositReconciliation_RevokesUnseenDepositBeforeRetry()
    {
        var scenario = new Scenario();
        Parcel parcel = scenario.CreateBatch();
        scenario.DestinationPort.Fault = FaultPoint.BeforeCommit;
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));
        Assert.Throws<PortUnavailableException>(() => scenario.Runtime.AdvanceTo(4));
        PortTransfer stale = scenario.Runtime.GetParcel(parcel.Id).PendingTransfer!;
        scenario.DestinationPort.Fault = FaultPoint.None;
        Assert.True(scenario.Runtime.ReconcileTransfer(parcel.Id));
        Assert.True(scenario.Runtime.RetryDelivery(parcel.Id));

        Assert.Throws<InvalidOperationException>(() => scenario.DestinationPort.Inner.Apply(stale));

        AssertConserved(scenario, parcel, escrow: true);
        Assert.Equal(1, scenario.Runtime.AdvanceTo(5));
        Assert.Throws<InvalidOperationException>(() => scenario.DestinationPort.Inner.Apply(stale));
        AssertConserved(scenario, parcel, escrow: false);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void InvalidApplyResult_RemainsUncertainUntilActualReceiptIsRead(int result)
    {
        var scenario = new Scenario();
        Parcel parcel = scenario.CreateBatch();
        scenario.DestinationPort.ApplyResultOverride = (PortResult)result;
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));

        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.AdvanceTo(4));

        AssertUncertain(scenario, parcel, ParcelState.DeliveryUncertain);
        Assert.False(scenario.Runtime.RetryDelivery(parcel.Id));
        AssertConserved(scenario, parcel, escrow: false);
        Assert.True(scenario.Runtime.ReconcileTransfer(parcel.Id));
        Assert.Equal(ParcelState.Delivered, scenario.Runtime.GetParcel(parcel.Id).State);
        Assert.Single(scenario.DestinationPort.ApplyCalls);
        AssertConserved(scenario, parcel, escrow: false);
    }

    [Fact]
    public void InvalidReadResult_RetainsSameTransferAndCargoUntilValidResolution()
    {
        var scenario = new Scenario();
        Parcel parcel = scenario.CreateBatch();
        scenario.SourcePort.Fault = FaultPoint.AfterCommit;
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));
        Assert.Throws<PortUnavailableException>(() => scenario.Runtime.AdvanceTo(1));
        Parcel before = scenario.Runtime.GetParcel(parcel.Id);
        scenario.SourcePort.ReadResultOverride = (PortResult)99;

        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.ReconcileTransfer(parcel.Id));

        Assert.Equal(before, scenario.Runtime.GetParcel(parcel.Id));
        Assert.Single(scenario.SourcePort.ApplyCalls);
        AssertConserved(scenario, parcel, escrow: true);
        scenario.SourcePort.ReadResultOverride = null;
        Assert.True(scenario.Runtime.ReconcileTransfer(parcel.Id));
        Assert.Equal(ParcelState.InTransit, scenario.Runtime.GetParcel(parcel.Id).State);
    }

    [Fact]
    public void CargoRegistration_RequiresMatchingUniquePhysicalInventory()
    {
        var scenario = new Scenario();
        var missing = new CargoId(Id(91));
        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.RegisterCargo(missing, Scenario.Origin, Scenario.Manifest));
        scenario.SourcePort.Inner.Seed(missing, new CargoManifest("wood", 7));
        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.RegisterCargo(missing, Scenario.Origin, Scenario.Manifest));
        var duplicate = new CargoId(Id(92));
        scenario.SourcePort.Inner.Seed(duplicate, Scenario.Manifest);
        scenario.DestinationPort.Inner.Seed(duplicate, Scenario.Manifest);

        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.RegisterCargo(duplicate, Scenario.Origin, Scenario.Manifest));

        Assert.Throws<KeyNotFoundException>(() => scenario.Runtime.GetCargo(missing));
        Assert.Throws<KeyNotFoundException>(() => scenario.Runtime.GetCargo(duplicate));
        Assert.Equal(new CargoManifest("wood", 7), scenario.SourcePort.ReadCargo(missing));
        Assert.Equal(Scenario.Manifest, scenario.SourcePort.ReadCargo(duplicate));
        Assert.Equal(Scenario.Manifest, scenario.DestinationPort.ReadCargo(duplicate));
        Assert.Empty(scenario.SourcePort.ApplyCalls);
        Assert.Empty(scenario.DestinationPort.ApplyCalls);
    }

    [Fact]
    public void ExtractionReconciliation_QueueFullRetainsClaimUntilContinuationFits()
    {
        var scenario = new Scenario(new FlowLimits(maxPendingOperations: 2), capacity: 30);
        Parcel uncertain = scenario.CreateBatch();
        scenario.SourcePort.Fault = FaultPoint.AfterCommit;
        Assert.True(scenario.Runtime.TryReserve(uncertain.Id));
        Assert.Throws<PortUnavailableException>(() => scenario.Runtime.AdvanceTo(1));
        scenario.SourcePort.Fault = FaultPoint.None;
        Parcel queued = scenario.CreateBatch(2);
        Parcel alsoQueued = scenario.CreateBatch(3);
        Assert.True(scenario.Runtime.TryReserve(queued.Id));
        Assert.True(scenario.Runtime.TryReserve(alsoQueued.Id));
        Parcel before = scenario.Runtime.GetParcel(uncertain.Id);
        CargoBatch cargoBefore = scenario.Runtime.GetCargo(uncertain.CargoId);

        Assert.False(scenario.Runtime.ReconcileTransfer(uncertain.Id));

        Assert.Equal(before, scenario.Runtime.GetParcel(uncertain.Id));
        Assert.Equal(cargoBefore, scenario.Runtime.GetCargo(uncertain.CargoId));
        Assert.Equal(2, scenario.Runtime.PendingOperationCount);
        Assert.Equal(21, scenario.Runtime.ReservedUnits(Scenario.Link));
        Assert.Empty(scenario.SourcePort.ReadCalls);
        Assert.Single(scenario.SourcePort.ApplyCalls);
        AssertConserved(scenario, uncertain, escrow: true);
        Assert.True(scenario.Runtime.Cancel(queued.Id));

        Assert.True(scenario.Runtime.ReconcileTransfer(uncertain.Id));

        Assert.Equal(ParcelState.InTransit, scenario.Runtime.GetParcel(uncertain.Id).State);
        Assert.Equal(4, scenario.Runtime.GetParcel(uncertain.Id).PendingOperation!.DueTick);
        Assert.Equal(2, scenario.Runtime.PendingOperationCount);
        Assert.Equal(14, scenario.Runtime.ReservedUnits(Scenario.Link));
        Assert.Single(scenario.SourcePort.ReadCalls);
        Assert.Equal(3, scenario.Runtime.AdvanceTo(4));
        Assert.Equal(ParcelState.Delivered, scenario.Runtime.GetParcel(uncertain.Id).State);
        AssertConserved(scenario, uncertain, escrow: false);
        AssertConserved(scenario, queued, escrow: false);
    }

    [Fact]
    public void AppliedExtractionReconciliation_HonorsAdmittedRouteAndOriginalDepartureTick()
    {
        var scenario = new Scenario();
        Parcel parcel = scenario.CreateBatch();
        scenario.SourcePort.Fault = FaultPoint.AfterCommit;
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));
        Assert.Throws<PortUnavailableException>(() => scenario.Runtime.AdvanceTo(1));
        Assert.True(scenario.Runtime.RemoveLink(Scenario.Link));
        Assert.Equal(0, scenario.Runtime.AdvanceTo(10));
        scenario.SourcePort.Fault = FaultPoint.None;

        Assert.True(scenario.Runtime.ReconcileTransfer(parcel.Id));

        Assert.Equal(ParcelState.InTransit, scenario.Runtime.GetParcel(parcel.Id).State);
        Assert.Equal(4, scenario.Runtime.PeekNextOperation()!.DueTick);
        Assert.Equal(2, scenario.Runtime.AdvanceTo(10));
        Assert.Equal(ParcelState.Delivered, scenario.Runtime.GetParcel(parcel.Id).State);
        Assert.Equal(new long[] { 0, 1, 1, 4, 4 }, scenario.Runtime.Events.Select(item => item.Tick));
        Assert.Equal(1, scenario.Runtime.RouteSearchCount);
        Assert.Equal(0, scenario.Runtime.ReservedUnits(Scenario.Link));
        AssertConserved(scenario, parcel, escrow: false);
    }

    [Fact]
    public void RegisteredCargo_RejectsReseedingAndPreseededStationWithoutChangingTopology()
    {
        var scenario = new Scenario();
        Parcel parcel = scenario.CreateBatch();
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));
        Assert.Equal(1, scenario.Runtime.AdvanceTo(1));
        var previousPlan = scenario.Runtime.PlanRoute(Scenario.Origin, Scenario.Destination);
        var intruder = new InMemoryCargoPort();
        intruder.Seed(parcel.CargoId, Scenario.Manifest);
        var newStation = new StationId(Id(33));

        Assert.Throws<InvalidOperationException>(() => scenario.SourcePort.Inner.Seed(parcel.CargoId, Scenario.Manifest));
        Assert.Throws<InvalidOperationException>(() => scenario.DestinationPort.Inner.Seed(parcel.CargoId, Scenario.Manifest));
        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.AddStation(newStation, intruder));

        Assert.Throws<ArgumentException>(() => scenario.Runtime.PlanRoute(Scenario.Origin, newStation));
        Assert.True(scenario.Runtime.IsPlanCurrent(previousPlan));
        Assert.Equal(CargoOwner.InParcel(parcel.Id), scenario.Runtime.GetCargo(parcel.CargoId).Owner);
        Assert.Equal(parcel.Id, scenario.Runtime.GetCargo(parcel.CargoId).ClaimedBy);
        AssertConserved(scenario, parcel, escrow: true);
        Assert.Equal(2, scenario.Runtime.AdvanceTo(4));
        AssertConserved(scenario, parcel, escrow: false);
    }

    [Fact]
    public void PortRegistration_RejectsCompetingSessionAuthority()
    {
        var scenario = new Scenario();
        Parcel parcel = scenario.CreateBatch();
        var competitor = new FlowRuntime(scenario.Runtime.NetworkId);

        Assert.Throws<InvalidOperationException>(() => competitor.AddStation(Scenario.Origin, scenario.SourcePort));
        Assert.Throws<InvalidOperationException>(() => scenario.Runtime.AddStation(new StationId(Id(33)), scenario.SourcePort));

        Assert.Throws<ArgumentException>(() => competitor.PlanRoute(Scenario.Origin, Scenario.Destination));
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));
        Assert.Equal(3, scenario.Runtime.AdvanceTo(4));
        AssertConserved(scenario, parcel, escrow: false);
    }

    [Fact]
    public void TransferSnapshot_ExposesOpaqueSessionWithoutMutableAuthority()
    {
        var scenario = new Scenario();
        Parcel parcel = scenario.CreateBatch();
        scenario.SourcePort.Fault = FaultPoint.AfterCommit;
        Assert.True(scenario.Runtime.TryReserve(parcel.Id));
        Assert.Throws<PortUnavailableException>(() => scenario.Runtime.AdvanceTo(1));
        PortTransfer transfer = scenario.Runtime.GetParcel(parcel.Id).PendingTransfer!;

        Assert.Equal(typeof(object), typeof(PortTransferId).GetProperty(nameof(PortTransferId.Session))!.PropertyType);
        Assert.IsType<object>(transfer.Id.Session);
        Assert.IsNotType<PortAuthority>(transfer.Id.Session);
        Type[] snapshots = { typeof(Parcel), typeof(CargoBatch), typeof(CargoOwner), typeof(PortTransfer), typeof(PortTransferId) };
        Assert.DoesNotContain(snapshots.SelectMany(type => type.GetProperties()), property =>
            property.PropertyType == typeof(PortAuthority) || property.PropertyType == typeof(FlowRuntime));
        AssertConserved(scenario, parcel, escrow: true);
    }

    private static PortTransfer AssertUncertain(Scenario scenario, Parcel original, ParcelState state)
    {
        Parcel parcel = scenario.Runtime.GetParcel(original.Id);
        Assert.Equal(state, parcel.State);
        Assert.Null(parcel.PendingOperation);
        Assert.NotNull(parcel.PendingTransfer);
        CargoBatch batch = scenario.Runtime.GetCargo(parcel.CargoId);
        Assert.Equal(parcel.Id, batch.ClaimedBy);
        Assert.Equal(parcel.PendingTransfer.Id, batch.Owner.Transfer);
        Assert.Null(batch.Owner.Parcel);
        Assert.Null(batch.Owner.Station);
        return parcel.PendingTransfer;
    }

    private static void AssertConserved(Scenario scenario, Parcel parcel, bool escrow)
    {
        CargoManifest? source = scenario.SourcePort.ReadCargo(parcel.CargoId);
        CargoManifest? destination = scenario.DestinationPort.ReadCargo(parcel.CargoId);
        CargoBatch canonical = scenario.Runtime.GetCargo(parcel.CargoId);
        bool actualEscrow = canonical.Owner.Parcel is not null;
        if (canonical.Owner.Transfer is PortTransferId pendingId)
        {
            PortTransfer? pending = scenario.Runtime.GetParcel(parcel.Id).PendingTransfer;
            Assert.NotNull(pending);
            Assert.Equal(pendingId, pending.Id);
            FaultPort port = pending.StationId == Scenario.Origin ? scenario.SourcePort : scenario.DestinationPort;
            PortResult receipt = port.Inner.ReadResult(pending);
            actualEscrow = pending.Kind == PortTransferKind.Extract
                ? receipt == PortResult.Applied : receipt != PortResult.Applied;
        }
        Assert.Equal(escrow, actualEscrow);
        int transitUnits = actualEscrow ? canonical.Manifest.Quantity : 0;
        Assert.Equal(parcel.Manifest.Quantity, (source?.Quantity ?? 0) + (destination?.Quantity ?? 0) + transitUnits);
        if (source is not null)
        {
            Assert.Equal(parcel.Manifest, source);
        }
        if (destination is not null)
        {
            Assert.Equal(parcel.Manifest, destination);
        }
        Assert.Equal(parcel.CargoId, canonical.Id);
        Assert.Equal(parcel.Manifest, canonical.Manifest);
        Assert.Equal(1, (canonical.Owner.Station is null ? 0 : 1) +
            (canonical.Owner.Parcel is null ? 0 : 1) + (canonical.Owner.Transfer is null ? 0 : 1));
    }

    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);

    private sealed class Scenario
    {
        internal static readonly StationId Origin = new(Id(11));
        internal static readonly StationId Destination = new(Id(22));
        internal static readonly LinkId Link = new(Id(11));
        internal static readonly CargoManifest Manifest = new("ore", 7);
        internal static readonly ServicePolicy Policy = new(ServiceClass.Standard, DeliveryGuarantee.Flexible);
        internal FlowRuntime Runtime { get; set; }
        internal FaultPort SourcePort { get; } = new();
        internal FaultPort DestinationPort { get; } = new();

        internal Scenario(FlowLimits? limits = null, int capacity = 20)
        {
            Runtime = new FlowRuntime(new NetworkId(Id(1)), limits);
            Runtime.AddStation(Origin, SourcePort);
            Runtime.AddStation(Destination, DestinationPort);
            Runtime.AddLink(Link, Origin, Destination, capacity, 3);
        }

        internal Parcel CreateBatch(int execution = 1)
        {
            var cargo = new CargoId(Id(9000 + execution));
            SourcePort.Inner.Seed(cargo, Manifest);
            Runtime.RegisterCargo(cargo, Origin, Manifest);
            return Assign(cargo, execution, Origin, Destination);
        }

        internal Parcel Assign(CargoId cargo, int execution, StationId origin, StationId destination)
        {
            var shipment = new ShipmentId(Id(execution));
            Runtime.CreateShipment(shipment, origin, destination, Manifest, Policy);
            return Runtime.SplitShipment(shipment, new ParcelId(Id(execution)), cargo);
        }
    }

    private enum FaultPoint { None, BeforeCommit, AfterCommit }
    private sealed class PortUnavailableException : Exception
    {
    }

    private sealed class FaultPort : ICargoPort
    {
        internal InMemoryCargoPort Inner { get; } = new();
        internal FaultPoint Fault { get; set; }
        internal bool ReadFailure { get; set; }
        internal PortResult? ApplyResultOverride { get; set; }
        internal PortResult? ReadResultOverride { get; set; }
        internal List<PortTransfer> ApplyCalls { get; } = new();
        internal List<PortTransfer> ReadCalls { get; } = new();

        public void Bind(PortAuthority authority, StationId station) => Inner.Bind(authority, station);
        public CargoManifest? ReadCargo(CargoId cargo) => Inner.ReadCargo(cargo);

        public PortResult Apply(PortTransfer transfer)
        {
            ApplyCalls.Add(transfer);
            if (Fault == FaultPoint.BeforeCommit)
            {
                throw new PortUnavailableException();
            }
            PortResult result = Inner.Apply(transfer);
            if (Fault == FaultPoint.AfterCommit)
            {
                throw new PortUnavailableException();
            }
            return ApplyResultOverride ?? result;
        }

        public PortResult ReadResult(PortTransfer transfer)
        {
            ReadCalls.Add(transfer);
            if (ReadFailure)
            {
                throw new PortUnavailableException();
            }
            return ReadResultOverride ?? Inner.ReadResult(transfer);
        }
    }
}
