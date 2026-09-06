using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.Flow.Application.Dispatch;
using Hatifect.Flow.Diagnostics;
using Hatifect.Flow.Domain.Checkpoints;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Policies;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Infrastructure.Persistence;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class ResourceRetentionTests
{
    [Theory]
    [InlineData(32, false)]
    [InlineData(128, false)]
    [InlineData(256, false)]
    [InlineData(32, true)]
    [InlineData(128, true)]
    [InlineData(256, true)]
    public void ProductionBounds_RetainEveryReceiptAndRejectAttemptSeventeenWithoutEffects(int count, bool returning)
    {
        var runtime = new FlowRuntime(new NetworkId(Guid.NewGuid()), new FlowLimits(maxStations: 32, maxLinks: 128,
            maxParcels: 256, maxRouteVisits: 32, maxRoutePlans: 64, maxDeliveryAttempts: 16,
            maxCargoUnits: 999, maxPendingOperations: 256));
        var origin = new StationId(Guid.NewGuid());
        var destination = new StationId(Guid.NewGuid());
        int effects = 0;
        PortTransfer? firstDelivery = null, firstExtraction = null;
        var source = new SaveBoundCargoPort(transfer =>
        {
            effects++;
            if (transfer.Kind == PortTransferKind.Extract) firstExtraction ??= transfer;
            return returning && transfer.Kind == PortTransferKind.Deposit ? PortResult.Rejected : PortResult.Applied;
        });
        var target = new SaveBoundCargoPort(transfer => { effects++; firstDelivery ??= transfer; return PortResult.Rejected; });
        runtime.AddStation(origin, source); runtime.AddStation(destination, target);
        runtime.AddLink(new LinkId(Guid.NewGuid()), origin, destination, 256, 1);
        var parcels = new List<ParcelId>();
        var manifest = new CargoManifest("wood", 1);
        var policy = new ServicePolicy(ServiceClass.Standard, DeliveryGuarantee.Reserved);
        for (int i = 0; i < count; i++)
        {
            var cargo = new CargoId(Guid.NewGuid());
            var shipment = new ShipmentId(Guid.NewGuid());
            var parcel = new ParcelId(Guid.NewGuid());
            source.Register(cargo, manifest);
            runtime.RegisterCargo(cargo, origin, manifest);
            runtime.CreateShipment(shipment, origin, destination, manifest, policy);
            runtime.SplitShipment(shipment, parcel, cargo);
            Assert.True(runtime.TryReserve(parcel));
            parcels.Add(parcel);
        }
        Assert.Equal(count, runtime.ReadResources().PendingOperations.Used);
        int processed = 0, maximumWork = 0;
        for (int attempt = 1; attempt <= 16; attempt++)
        {
            if (attempt > 1) foreach (ParcelId parcel in parcels) Assert.True(returning && attempt == 2 ? runtime.ReturnToSource(parcel) : runtime.RetryDelivery(parcel));
            int ticks = 0;
            while (runtime.PendingOperationCount > 0 && ticks++ < 100)
            {
                int work = runtime.AdvanceTo(runtime.Now + 1);
                Assert.InRange(work, 1, 64);
                maximumWork = Math.Max(maximumWork, work);
                processed += work;
            }
            Assert.Equal(0, runtime.PendingOperationCount);
            Assert.Equal(returning ? count : count * attempt, target.ReadResources().Receipts.Used);
            Assert.Equal(returning ? count * attempt : count, source.ReadResources().Receipts.Used);
            Assert.All(parcels, id =>
            {
                Parcel parcel = runtime.GetParcel(id);
                Assert.Equal(returning && attempt > 1 ? ParcelState.ReturnRejected : ParcelState.DeliveryRejected, parcel.State);
                Assert.Equal(attempt, parcel.DeliveryAttempts);
                Assert.Equal(CargoOwner.InParcel(id), runtime.GetCargo(parcel.CargoId).Owner);
            });
        }
        Assert.Equal(count * 18, processed);
        Assert.Equal(64, maximumWork);
        Assert.Equal(count * 17, effects);
        FlowRuntimeResources full = runtime.ReadResources();
        Assert.Equal(new FlowResourceUsage(count * 17, 4352), full.IssuedTransfers);
        Assert.Equal(count * 17, full.RetiredTransfers);
        Assert.Equal(new FlowResourceUsage(returning ? count * 16 : count, 4096), source.ReadResources().Receipts);
        Assert.Equal(new FlowResourceUsage(returning ? count : count * 16, 4096), target.ReadResources().Receipts);
        Assert.Equal(0, target.ReadResources().Custody.Used);
        Assert.Equal(0, source.ReadResources().Custody.Used);
        Assert.Equal(256, full.Events.Used);
        Assert.Equal(1, full.RouteSearches);
        foreach (ParcelId parcel in parcels)
        {
            Assert.False(runtime.RetryDelivery(parcel));
            Assert.False(runtime.ReturnToSource(parcel));
        }
        // Known receipts remain readable when the chosen port has reached 4096/4096.
        Assert.Equal(PortResult.Rejected, target.Apply(firstDelivery!));
        Assert.Equal(PortResult.Applied, source.Apply(firstExtraction!));
        Assert.Equal(count * 17, effects);
        Assert.Equal(full, runtime.ReadResources());

        // The owner read is constant work and must not allocate a checkpoint or event/route arrays.
        for (int i = 0; i < 100; i++) runtime.ReadResources();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) runtime.ReadResources();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(0, allocated);

        byte[] encoded = CheckpointCodec.Encode(new CheckpointImage(0, runtime.CaptureCheckpoint()));
        FlowCheckpoint saved = CheckpointCodec.Decode(encoded).Checkpoint;
        Assert.Equal(count * 17, saved.Stations.Sum(station => station.Port.Receipts.Length));
        FlowRuntime restored = FlowRuntime.RestoreCheckpoint(saved);
        object owner = new();
        restored.AttachCheckpointOwner(owner);
        var restoredPorts = new Dictionary<StationId, SaveBoundCargoPort>();
        restored.AttachSavePorts(owner, station =>
        {
            var port = new SaveBoundCargoPort(_ => throw new InvalidOperationException("Restore must not apply."),
                saved.Stations.Single(value => value.Id == station.Value).Port);
            restoredPorts.Add(station, port);
            return port;
        });
        // Reload reconstructs session capabilities; old live objects must lose their authority.
        Assert.Throws<InvalidOperationException>(() => restoredPorts[origin].Apply(firstExtraction!));
        Assert.Throws<InvalidOperationException>(() => restoredPorts[destination].Apply(firstDelivery!));
        Assert.Equal(full with { RouteSearches = 0, RouteCache = new(0, 64) }, restored.ReadResources());
        Assert.Equal(encoded, CheckpointCodec.Encode(new CheckpointImage(0, restored.CaptureCheckpoint())));
        Assert.Equal(0, restored.AdvanceTo(restored.Now + 1000));
        foreach (ParcelId parcel in parcels)
        {
            Assert.False(restored.RetryDelivery(parcel));
            Assert.False(restored.ReturnToSource(parcel));
            Assert.Equal(16, restored.GetParcel(parcel).DeliveryAttempts);
        }
        Assert.Equal(full.IssuedTransfers, restored.ReadResources().IssuedTransfers);
        restored.FenceCheckpointOwner(owner);
        Assert.Throws<InvalidOperationException>(() => restored.ReadResources());
    }
}
