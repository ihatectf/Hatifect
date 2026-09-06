using System;
using Hatifect.Flow.Application.Dispatch;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Infrastructure.Persistence;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class PortReadinessTests
{
    [Fact]
    public void InvalidatedWaitingRouteCancelsWithoutAcquiringTheSource()
    {
        var fixture = new CheckpointFixture();
        fixture.Runtime.TryReserve(CheckpointFixture.Parcel);
        Assert.Equal(0, fixture.Runtime.AdvanceTo(1, canAccessPort: _ => false));
        Assert.True(fixture.Runtime.RemoveLink(CheckpointFixture.Link));
        Assert.Equal(1, fixture.Runtime.AdvanceTo(2, canAccessPort: _ => throw new Exception("Invalid route has no physical work.")));
        Assert.Equal(ParcelState.Cancelled, fixture.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(CargoOwner.AtStation(CheckpointFixture.Origin), fixture.Runtime.GetCargo(CheckpointFixture.Cargo).Owner);
        Assert.Null(fixture.Runtime.GetCargo(CheckpointFixture.Cargo).ClaimedBy);
        Assert.NotNull(FlowRuntime.RestoreCheckpoint(fixture.Runtime.CaptureCheckpoint()));
    }

    [Fact]
    public void BusyExtractionPreservesTicketReservationAndCargoAcrossReload()
    {
        var fixture = new CheckpointFixture();
        Assert.True(fixture.Runtime.TryReserve(CheckpointFixture.Parcel));
        var before = fixture.Runtime.GetParcel(CheckpointFixture.Parcel);
        Assert.Equal(0, fixture.Runtime.AdvanceTo(1, canAccessPort: station => { Assert.Equal(CheckpointFixture.Origin, station); return false; }));
        Assert.Same(before, fixture.Runtime.GetParcel(CheckpointFixture.Parcel));
        Assert.Equal(CargoOwner.AtStation(CheckpointFixture.Origin), fixture.Runtime.GetCargo(CheckpointFixture.Cargo).Owner);
        var checkpoint = CheckpointCodec.Decode(CheckpointCodec.Encode(new CheckpointImage(0, fixture.Runtime.CaptureCheckpoint()))).Checkpoint;
        FlowRuntime restored = FlowRuntime.RestoreCheckpoint(checkpoint);
        Assert.Equal(0, restored.AdvanceTo(2, canAccessPort: _ => false));
        Assert.Equal(ParcelState.Reserved, restored.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(0, restored.GetParcel(CheckpointFixture.Parcel).DeliveryAttempts);
        Assert.True(restored.AdvanceTo(10, canAccessPort: _ => true) > 0);
        Assert.Equal(ParcelState.Delivered, restored.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(1, restored.GetParcel(CheckpointFixture.Parcel).DeliveryAttempts);
        Assert.Equal(CheckpointFixture.Manifest, restored.GetCheckpointPort(CheckpointFixture.Destination).ReadCargo(CheckpointFixture.Cargo));
        Assert.Null(restored.GetCheckpointPort(CheckpointFixture.Origin).ReadCargo(CheckpointFixture.Cargo));
    }

    [Fact]
    public void BusyDeliveryKeepsParcelCustodyAndDoesNotConsumeAttempts()
    {
        var fixture = new CheckpointFixture();
        fixture.Runtime.TryReserve(CheckpointFixture.Parcel);
        Assert.True(fixture.Runtime.AdvanceTo(4, canAccessPort: station => station != CheckpointFixture.Destination) > 0);
        var waiting = fixture.Runtime.GetParcel(CheckpointFixture.Parcel);
        Assert.Equal(ParcelState.Arrived, waiting.State);
        Assert.Equal(0, waiting.DeliveryAttempts);
        Assert.Equal(CargoOwner.InParcel(CheckpointFixture.Parcel), fixture.Runtime.GetCargo(CheckpointFixture.Cargo).Owner);
        Assert.Equal(0, fixture.Runtime.AdvanceTo(5, canAccessPort: _ => false));
        Assert.Same(waiting, fixture.Runtime.GetParcel(CheckpointFixture.Parcel));
        FlowRuntime restored = FlowRuntime.RestoreCheckpoint(fixture.Runtime.CaptureCheckpoint());
        Assert.Equal(1, restored.AdvanceTo(6, canAccessPort: station => station == CheckpointFixture.Destination));
        Assert.Equal(ParcelState.Delivered, restored.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(1, restored.GetParcel(CheckpointFixture.Parcel).DeliveryAttempts);
    }

    [Fact]
    public void BusyReturnWaitsForSourceAndRemainsSaveable()
    {
        var fixture = new CheckpointFixture();
        fixture.Reach(ParcelState.DeliveryRejected);
        Assert.True(fixture.Runtime.ReturnToSource(CheckpointFixture.Parcel));
        Assert.Equal(0, fixture.Runtime.AdvanceTo(5, canAccessPort: station => { Assert.Equal(CheckpointFixture.Origin, station); return false; }));
        Assert.Equal(ParcelState.ReturnRequested, fixture.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(1, fixture.Runtime.GetParcel(CheckpointFixture.Parcel).DeliveryAttempts);
        FlowRuntime restored = FlowRuntime.RestoreCheckpoint(fixture.Runtime.CaptureCheckpoint());
        Assert.Equal(1, restored.AdvanceTo(6, canAccessPort: _ => true));
        Assert.Equal(ParcelState.Returned, restored.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(CargoOwner.AtStation(CheckpointFixture.Origin), restored.GetCargo(CheckpointFixture.Cargo).Owner);
        Assert.Equal(2, restored.GetParcel(CheckpointFixture.Parcel).DeliveryAttempts);
    }

    [Fact]
    public void WaitingExtractionCanBeCancelledWithoutAnyPortAccess()
    {
        var fixture = new CheckpointFixture();
        fixture.Runtime.TryReserve(CheckpointFixture.Parcel);
        Assert.Equal(0, fixture.Runtime.AdvanceTo(1, canAccessPort: _ => false));
        Assert.True(fixture.Runtime.Cancel(CheckpointFixture.Parcel));
        Assert.Equal(0, fixture.Runtime.AdvanceTo(10, canAccessPort: _ => throw new Exception("Cancelled work must not request a port.")));
        Assert.Equal(ParcelState.Cancelled, fixture.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(CheckpointFixture.Manifest, fixture.Source.ReadCargo(CheckpointFixture.Cargo));
        Assert.Null(fixture.Runtime.GetCargo(CheckpointFixture.Cargo).ClaimedBy);
        Assert.NotNull(FlowRuntime.RestoreCheckpoint(fixture.Runtime.CaptureCheckpoint()));
    }
}
