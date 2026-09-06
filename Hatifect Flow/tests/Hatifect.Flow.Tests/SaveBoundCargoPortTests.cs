using System;
using System.Linq;
using Hatifect.Flow.Application.Dispatch;
using Hatifect.Flow.Domain.Checkpoints;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Infrastructure.Persistence;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class SaveBoundCargoPortTests
{
    [Fact]
    public void SaveBoundJourney_ReplaysReceiptsWithoutRepeatingPhysicalMutation()
    {
        var fixture = new CheckpointFixture();
        FlowCheckpoint image = fixture.Runtime.CaptureCheckpoint();
        FlowRuntime runtime = FlowRuntime.RestoreCheckpoint(image);
        object owner = new();
        runtime.AttachCheckpointOwner(owner);
        int extractions = 0, deposits = 0;
        PortTransfer? extraction = null;
        SaveBoundCargoPort? source = null;
        runtime.AttachSavePorts(owner, station =>
        {
            var port = new SaveBoundCargoPort(transfer =>
            {
                if (transfer.Kind == PortTransferKind.Extract) { extractions++; extraction = transfer; }
                else deposits++;
                return PortResult.Applied;
            }, image.Stations.Single(item => item.Id == station.Value).Port);
            if (station == CheckpointFixture.Origin) source = port;
            return port;
        });
        Assert.True(runtime.TryReserve(CheckpointFixture.Parcel));
        runtime.AdvanceTo(4);

        Assert.Equal(PortResult.Applied, source!.Apply(extraction!));
        Assert.Equal(1, extractions);
        Assert.Equal(1, deposits);
        Assert.Equal(ParcelState.Delivered, runtime.GetParcel(CheckpointFixture.Parcel).State);
        FlowCheckpoint delivered = runtime.CaptureCheckpoint();
        Assert.Empty(delivered.Stations.Single(item => item.Id == CheckpointFixture.Origin.Value).Port.Inventory);
        Assert.Single(delivered.Stations.Single(item => item.Id == CheckpointFixture.Destination.Value).Port.Inventory);
        FlowRuntime restored = FlowRuntime.RestoreCheckpoint(delivered);
        object restoredOwner = new();
        restored.AttachCheckpointOwner(restoredOwner);
        restored.AttachSavePorts(restoredOwner, station => new SaveBoundCargoPort(_ => throw new Exception("restore must not move items"),
            delivered.Stations.Single(item => item.Id == station.Value).Port));
        Assert.Equal(0, restored.AdvanceTo(100));
        Assert.Equal(ParcelState.Delivered, restored.GetParcel(CheckpointFixture.Parcel).State);
    }

    [Fact]
    public void RejectedDestination_RetainsCargoUntilNewAttemptSucceeds()
    {
        var fixture = new CheckpointFixture();
        FlowCheckpoint checkpoint = fixture.Runtime.CaptureCheckpoint();
        FlowRuntime runtime = FlowRuntime.RestoreCheckpoint(checkpoint);
        object owner = new();
        runtime.AttachCheckpointOwner(owner);
        bool accept = false;
        int attempts = 0;
        runtime.AttachSavePorts(owner, station => new SaveBoundCargoPort(transfer =>
        {
            if (transfer.Kind == PortTransferKind.Extract) return PortResult.Applied;
            attempts++;
            return accept ? PortResult.Applied : PortResult.Rejected;
        }, checkpoint.Stations.Single(item => item.Id == station.Value).Port));
        runtime.TryReserve(CheckpointFixture.Parcel);
        runtime.AdvanceTo(4);
        Assert.Equal(ParcelState.DeliveryRejected, runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(CheckpointFixture.Parcel, runtime.GetCargo(CheckpointFixture.Cargo).Owner.Parcel);
        accept = true;
        Assert.True(runtime.RetryDelivery(CheckpointFixture.Parcel));
        runtime.AdvanceTo(5);
        Assert.Equal(2, attempts);
        Assert.Equal(ParcelState.Delivered, runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(CheckpointFixture.Destination, runtime.GetCargo(CheckpointFixture.Cargo).Owner.Station);
    }

    [Fact]
    public void Attachment_RejectsChangedProjectionWithoutReplacingOriginalPort()
    {
        var fixture = new CheckpointFixture();
        FlowRuntime runtime = FlowRuntime.RestoreCheckpoint(fixture.Runtime.CaptureCheckpoint());
        object owner = new();
        runtime.AttachCheckpointOwner(owner);
        Assert.Throws<InvalidOperationException>(() => runtime.AttachSavePorts(owner,
            _ => new SaveBoundCargoPort(_ => throw new Exception("must not apply"))));
        Assert.Single(runtime.CaptureCheckpoint().Stations.Single(item => item.Id == CheckpointFixture.Origin.Value).Port.Inventory);
        Assert.True(runtime.TryReserve(CheckpointFixture.Parcel));
        runtime.AdvanceTo(4);
        Assert.Equal(ParcelState.Delivered, runtime.GetParcel(CheckpointFixture.Parcel).State);
    }
}
