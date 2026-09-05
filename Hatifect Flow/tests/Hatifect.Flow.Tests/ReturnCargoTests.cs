using System;
using System.Linq;
using Hatifect.Flow.Application.Dispatch;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Infrastructure.Persistence;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class ReturnCargoTests
{
    [Fact]
    public void ScheduledReturnSurvivesCodecReloadAndSettlesOnlyAtSource()
    {
        var fixture = new CheckpointFixture();
        fixture.Reach(ParcelState.DeliveryRejected);
        Assert.True(fixture.Runtime.ReturnToSource(CheckpointFixture.Parcel));
        Assert.False(fixture.Runtime.ReturnToSource(CheckpointFixture.Parcel));
        var bytes = CheckpointCodec.Encode(new CheckpointImage(0, fixture.Runtime.CaptureCheckpoint()));
        FlowRuntime restored = FlowRuntime.RestoreCheckpoint(CheckpointCodec.Decode(bytes).Checkpoint);
        Assert.Equal(ParcelState.ReturnRequested, restored.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(1, restored.AdvanceTo(5));
        Assert.Equal(ParcelState.Returned, restored.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(CargoOwner.AtStation(CheckpointFixture.Origin), restored.GetCargo(CheckpointFixture.Cargo).Owner);
        Assert.Null(restored.GetCargo(CheckpointFixture.Cargo).ClaimedBy);
        Assert.Equal(CheckpointFixture.Manifest, restored.GetCheckpointPort(CheckpointFixture.Origin).ReadCargo(CheckpointFixture.Cargo));
        Assert.Null(restored.GetCheckpointPort(CheckpointFixture.Destination).ReadCargo(CheckpointFixture.Cargo));
        FlowRuntime again = FlowRuntime.RestoreCheckpoint(restored.CaptureCheckpoint());
        Assert.False(again.RetryDelivery(CheckpointFixture.Parcel));
        Assert.False(again.ReturnToSource(CheckpointFixture.Parcel));
        Assert.Equal(0, again.AdvanceTo(100));
        // A returned identity can be claimed again; its continuous lineage starts at the source.
        again.CreateShipment(new ShipmentId(CheckpointFixture.Id(2)), CheckpointFixture.Origin, CheckpointFixture.Destination,
            CheckpointFixture.Manifest, CheckpointFixture.Policy);
        again.SplitShipment(new ShipmentId(CheckpointFixture.Id(2)), new ParcelId(CheckpointFixture.Id(2)), CheckpointFixture.Cargo);
        Assert.NotNull(again.CaptureCheckpoint());
    }

    [Fact]
    public void RejectedReturnRetainsCargoAndRetriesOnlyTheSourceWithinAttemptBudget()
    {
        var fixture = new CheckpointFixture();
        fixture.Reach(ParcelState.DeliveryRejected);
        fixture.Source.Inner.AcceptDeposits = false;
        Assert.True(fixture.Runtime.ReturnToSource(CheckpointFixture.Parcel));
        fixture.Runtime.AdvanceTo(5);
        Assert.Equal(ParcelState.ReturnRejected, fixture.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        FlowRuntime restored = FlowRuntime.RestoreCheckpoint(fixture.Runtime.CaptureCheckpoint());
        restored.GetCheckpointPort(CheckpointFixture.Origin).AcceptDeposits = true;
        Assert.True(restored.RetryDelivery(CheckpointFixture.Parcel));
        restored.AdvanceTo(6);
        Assert.Equal(ParcelState.Returned, restored.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(3, restored.GetParcel(CheckpointFixture.Parcel).DeliveryAttempts);
        Assert.Equal(CheckpointFixture.Manifest, restored.GetCheckpointPort(CheckpointFixture.Origin).ReadCargo(CheckpointFixture.Cargo));
        Assert.Null(restored.GetCheckpointPort(CheckpointFixture.Destination).ReadCargo(CheckpointFixture.Cargo));
        Assert.NotNull(restored.CaptureCheckpoint());
    }

    [Theory]
    [InlineData(false, ParcelState.ReturnFaulted)]
    [InlineData(true, ParcelState.Returned)]
    public void UncertainReturnUsesTheOriginalReceiptAfterReloadWithoutReapplying(bool committed, ParcelState expected)
    {
        var fixture = new CheckpointFixture();
        fixture.Reach(ParcelState.DeliveryRejected);
        fixture.Source.Throw = true;
        fixture.Source.AfterCommit = committed;
        Assert.True(fixture.Runtime.ReturnToSource(CheckpointFixture.Parcel));
        Assert.Throws<CheckpointFixture.PortFailure>(() => fixture.Runtime.AdvanceTo(5));
        Assert.Equal(ParcelState.ReturnUncertain, fixture.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.False(fixture.Runtime.ReturnToSource(CheckpointFixture.Parcel));
        FlowRuntime restored = FlowRuntime.RestoreCheckpoint(fixture.Runtime.CaptureCheckpoint());
        Assert.True(restored.ReconcileTransfer(CheckpointFixture.Parcel));
        Assert.Equal(expected, restored.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(committed ? CheckpointFixture.Manifest : null, restored.GetCheckpointPort(CheckpointFixture.Origin).ReadCargo(CheckpointFixture.Cargo));
        Assert.False(restored.ReconcileTransfer(CheckpointFixture.Parcel));
        Assert.NotNull(restored.CaptureCheckpoint());
    }

    [Fact]
    public void ForgedReturnReceiptBeforeAnyFailedDeliveryIsRejected()
    {
        var fixture = new CheckpointFixture();
        fixture.Reach(ParcelState.Delivered);
        var checkpoint = fixture.Runtime.CaptureCheckpoint();
        int index = Array.FindIndex(checkpoint.Transfers, transfer => transfer.Key.Kind == (int)PortTransferKind.Deposit);
        checkpoint.Transfers[index] = checkpoint.Transfers[index] with { StationId = CheckpointFixture.Origin.Value };
        Assert.Throws<ArgumentException>(() => FlowRuntime.RestoreCheckpoint(checkpoint));
    }
}
