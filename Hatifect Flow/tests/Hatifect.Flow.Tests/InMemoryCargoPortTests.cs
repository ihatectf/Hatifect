using System;
using System.Collections.Generic;
using System.Threading;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Policies;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Shipments;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class InMemoryCargoPortTests
{
    [Fact]
    public void AppliedReceipts_ReplayWithoutMovingRedispatchedCargo()
    {
        var fixture = new Fixture();
        fixture.Port.Seed(Fixture.Cargo, Fixture.Manifest);
        PortTransfer extraction = fixture.Issue(1, PortTransferKind.Extract);
        Assert.Equal(PortResult.Applied, fixture.Port.Apply(extraction));
        Assert.Empty(fixture.Port.Inventory);
        fixture.Authority.Retire(extraction);

        PortTransfer delivery = fixture.Issue(1, PortTransferKind.Deposit);
        Assert.Equal(PortResult.Applied, fixture.Port.Apply(delivery));
        fixture.Authority.Retire(delivery);
        Assert.Equal(PortResult.Applied, fixture.Port.Apply(extraction));
        Assert.Equal(PortResult.Applied, fixture.Port.Apply(delivery));
        Assert.Single(fixture.Port.Inventory);
        Assert.Equal(Fixture.Manifest, fixture.Port.ReadCargo(Fixture.Cargo));

        PortTransfer nextExecution = fixture.Issue(2, PortTransferKind.Extract);
        Assert.Equal(PortResult.Applied, fixture.Port.Apply(nextExecution));
        Assert.Empty(fixture.Port.Inventory);
        Assert.Equal(PortResult.Applied, fixture.Port.Apply(delivery));
        Assert.Equal(PortResult.Applied, fixture.Port.Apply(extraction));
        Assert.Empty(fixture.Port.Inventory);
        Assert.Equal(PortResult.Applied, fixture.Port.ReadResult(delivery));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void RejectedReceipt_RemainsRejectedWhenAdmissionChanges(int direction)
    {
        PortTransferKind kind = (PortTransferKind)direction;
        var fixture = new Fixture();
        if (kind == PortTransferKind.Extract)
        {
            fixture.Port.Seed(Fixture.Cargo, Fixture.Manifest);
        }
        fixture.Port.AcceptExtractions = false;
        fixture.Port.AcceptDeposits = false;
        PortTransfer original = fixture.Issue(1, kind);
        Assert.Equal(PortResult.Rejected, fixture.Port.Apply(original));
        fixture.Authority.Retire(original);
        fixture.Port.AcceptExtractions = true;
        fixture.Port.AcceptDeposits = true;

        Assert.Equal(PortResult.Rejected, fixture.Port.Apply(original));
        Assert.Equal(PortResult.Rejected, fixture.Port.ReadResult(original));
        Assert.Equal(kind == PortTransferKind.Extract ? Fixture.Manifest : null,
            fixture.Port.ReadCargo(Fixture.Cargo));
        Assert.Equal(PortResult.Applied, fixture.Port.Apply(fixture.Issue(2, kind)));
        Assert.Equal(kind == PortTransferKind.Deposit ? Fixture.Manifest : null,
            fixture.Port.ReadCargo(Fixture.Cargo));
    }

    [Fact]
    public void ReceiptLimit_PreservesInventoryAndOldReplayEvidence()
    {
        var fixture = new Fixture(maxReceipts: 1);
        fixture.Port.Seed(Fixture.Cargo, Fixture.Manifest);
        PortTransfer extraction = fixture.Issue(1, PortTransferKind.Extract);
        Assert.Equal(PortResult.Applied, fixture.Port.Apply(extraction));
        fixture.Authority.Retire(extraction);
        PortTransfer blocked = fixture.Issue(1, PortTransferKind.Deposit);

        Assert.Throws<InvalidOperationException>(() => fixture.Port.Apply(blocked));
        Assert.Equal(PortResult.Missing, fixture.Port.ReadResult(blocked));
        Assert.Empty(fixture.Port.Inventory);
        fixture.Authority.Retire(blocked);
        Assert.Throws<InvalidOperationException>(() => fixture.Port.Apply(blocked));
        Assert.Equal(PortResult.Applied, fixture.Port.Apply(extraction));
        Assert.Equal(PortResult.Applied, fixture.Port.ReadResult(extraction));
        Assert.Empty(fixture.Port.Inventory);
    }

    [Fact]
    public void InventoryLimit_RecordsRefusalBeforeCapacityIsFreed()
    {
        var fixture = new Fixture(maxCargoBatches: 1);
        var blocker = new CargoId(Id(81));
        fixture.Port.Seed(blocker, Fixture.Manifest);
        PortTransfer refused = fixture.Issue(1, PortTransferKind.Deposit);
        Assert.Equal(PortResult.Rejected, fixture.Port.Apply(refused));
        Assert.Null(fixture.Port.ReadCargo(Fixture.Cargo));
        Assert.Equal(Fixture.Manifest, fixture.Port.ReadCargo(blocker));

        Assert.Equal(PortResult.Applied, fixture.Port.Apply(fixture.Issue(2,
            PortTransferKind.Extract, blocker)));
        Assert.Empty(fixture.Port.Inventory);
        Assert.Equal(PortResult.Rejected, fixture.Port.Apply(refused));
        Assert.Empty(fixture.Port.Inventory);
        Assert.Equal(PortResult.Applied, fixture.Port.Apply(fixture.Issue(3, PortTransferKind.Deposit)));
        Assert.Single(fixture.Port.Inventory);
        Assert.Equal(Fixture.Manifest, fixture.Port.ReadCargo(Fixture.Cargo));
    }

    [Fact]
    public void TransferCapability_RejectsClonesChangedPayloadAndAnotherSession()
    {
        var fixture = new Fixture();
        fixture.Port.Seed(Fixture.Cargo, Fixture.Manifest);
        PortTransfer issued = fixture.Issue(1, PortTransferKind.Extract);
        var otherSession = new Fixture();
        var invalid = new[]
        {
            issued with { },
            issued with { CargoId = new CargoId(Id(82)) },
            issued with { Manifest = new CargoManifest("stone", 7) },
            issued with { StationId = new StationId(Id(2)) },
            issued with { Id = issued.Id with { Attempt = 2 } },
            issued with { Id = issued.Id with { Kind = PortTransferKind.Deposit } },
            otherSession.Issue(1, PortTransferKind.Extract)
        };
        foreach (PortTransfer forged in invalid)
        {
            Assert.Throws<InvalidOperationException>(() => fixture.Port.Apply(forged));
            Assert.Throws<InvalidOperationException>(() => fixture.Port.ReadResult(forged));
            Assert.Single(fixture.Port.Inventory);
            Assert.Equal(Fixture.Manifest, fixture.Port.ReadCargo(Fixture.Cargo));
            Assert.Equal(PortResult.Missing, fixture.Port.ReadResult(issued));
        }
        Assert.Equal(PortResult.Applied, fixture.Port.Apply(issued));
        Assert.Empty(fixture.Port.Inventory);
    }

    [Fact]
    public void InventorySnapshot_IsDetachedAndRejectsMutation()
    {
        var fixture = new Fixture();
        fixture.Port.Seed(Fixture.Cargo, Fixture.Manifest);
        IReadOnlyDictionary<CargoId, CargoManifest> snapshot = fixture.Port.Inventory;
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<CargoId, CargoManifest>)snapshot).Remove(Fixture.Cargo));
        Assert.Equal(PortResult.Applied, fixture.Port.Apply(fixture.Issue(1, PortTransferKind.Extract)));
        Assert.Empty(fixture.Port.Inventory);
        Assert.Single(snapshot);
        Assert.Equal(Fixture.Manifest, snapshot[Fixture.Cargo]);
    }

    [Fact]
    public void PortAccess_FromAnotherThreadCannotMutateInventoryOrReadJournal()
    {
        var fixture = new Fixture();
        fixture.Port.Seed(Fixture.Cargo, Fixture.Manifest);
        PortTransfer issued = fixture.Issue(1, PortTransferKind.Extract);
        Exception? mutationError = null;
        Exception? readError = null;
        var worker = new Thread(() =>
        {
            mutationError = Record.Exception(() => fixture.Port.Apply(issued));
            readError = Record.Exception(() => fixture.Port.ReadResult(issued));
        });
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(mutationError);
        Assert.IsType<InvalidOperationException>(readError);
        Assert.Single(fixture.Port.Inventory);
        Assert.Equal(Fixture.Manifest, fixture.Port.ReadCargo(Fixture.Cargo));
        Assert.Equal(PortResult.Missing, fixture.Port.ReadResult(issued));
        Assert.Equal(PortResult.Applied, fixture.Port.Apply(issued));
        Assert.Empty(fixture.Port.Inventory);
    }

    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);

    private sealed class Fixture
    {
        internal static readonly StationId Station = new(Id(1));
        internal static readonly CargoId Cargo = new(Id(80));
        internal static readonly CargoManifest Manifest = new("ore", 7);
        internal PortAuthority Authority { get; } = new(new FlowLimits());
        internal InMemoryCargoPort Port { get; }

        internal Fixture(int maxCargoBatches = 1024, int maxReceipts = 4096)
        {
            Port = new InMemoryCargoPort(maxCargoBatches, maxReceipts);
            Port.Bind(Authority, Station);
        }

        internal PortTransfer Issue(int execution, PortTransferKind kind, CargoId? cargo = null)
        {
            var parcel = new Parcel(new ParcelId(Id(execution)), new ShipmentId(Id(execution)),
                cargo ?? Cargo, Manifest, new ServicePolicy(ServiceClass.Standard, DeliveryGuarantee.Flexible),
                ParcelState.Created, 0, 0, Station, null);
            return Authority.Issue(parcel, Station, kind, 1);
        }
    }
}
