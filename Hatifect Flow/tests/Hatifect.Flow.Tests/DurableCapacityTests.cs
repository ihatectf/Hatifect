using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Hatifect.Flow.Application.Dispatch;
using Hatifect.Flow.Domain.Checkpoints;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Policies;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Infrastructure.Persistence;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class DurableCapacityTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Capacity_FaultBoundaryRecoversOnlyCommittedLimits(int boundary)
    {
        using var directory = new DurableTestDirectory();
        int oldBatches, oldReceipts;
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime,
            stage => { if ((int)stage == boundary) throw new CapacityFailure(); }))
        {
            PortCheckpoint before = session.GetPortSnapshot(CheckpointFixture.Origin);
            oldBatches = before.MaxCargoBatches; oldReceipts = before.MaxReceipts;
            Assert.Throws<CapacityFailure>(() => session.SetPortCapacity(CheckpointFixture.Origin, 17, 23));
            Assert.Throws<ObjectDisposedException>(() => session.SetPortCapacity(CheckpointFixture.Origin, 17, 23));
        }
        byte[] core = File.ReadAllBytes(CorePath(directory)), provider = File.ReadAllBytes(directory.ProviderPath);
        bool committed = boundary >= (int)DurableCommitStage.AfterProvider;
        for (int iteration = 0; iteration < 2; iteration++)
        {
            using var restored = DurableFlowSession.Open(directory.Core, directory.Provider);
            PortCheckpoint port = restored.GetPortSnapshot(CheckpointFixture.Origin);
            Assert.Equal(committed ? 17 : oldBatches, port.MaxCargoBatches);
            Assert.Equal(committed ? 23 : oldReceipts, port.MaxReceipts);
            Assert.Equal(committed ? 1 : 0, restored.ProviderRevision);
            Assert.Equal(committed ? 1 : 0, ReadProvider(directory).ConfigurationRevision);
            Assert.Empty(ReadProvider(directory).Receipts);
            Assert.Equal(CheckpointFixture.Cargo.Value, Assert.Single(port.Inventory).CargoId);
            Assert.True(port.AcceptDeposits); Assert.True(port.AcceptExtractions);
            Assert.Equal(ParcelState.Created, restored.Runtime.GetParcel(CheckpointFixture.Parcel).State);
            Assert.Equal(CheckpointFixture.Parcel, restored.Runtime.GetCargo(CheckpointFixture.Cargo).ClaimedBy);
            Assert.Equal(core, File.ReadAllBytes(CorePath(directory))); Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
        }
    }

    [Theory]
    [InlineData(0, 5)] [InlineData(-1, 5)] [InlineData(65537, 5)]
    [InlineData(5, 0)] [InlineData(5, -1)] [InlineData(5, 65537)]
    public void Capacity_InvalidBoundsLeavePairAndSessionUsable(int batches, int receipts)
    {
        using var directory = new DurableTestDirectory();
        using var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime);
        byte[] core = File.ReadAllBytes(CorePath(directory)), provider = File.ReadAllBytes(directory.ProviderPath);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetPortCapacity(CheckpointFixture.Origin, batches, receipts));
        Assert.Equal(core, File.ReadAllBytes(CorePath(directory))); Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
        session.SetPortCapacity(CheckpointFixture.Origin, 1, 65536);
        Assert.Equal(1, session.GetPortSnapshot(CheckpointFixture.Origin).MaxCargoBatches);
        Assert.Equal(65536, session.GetPortSnapshot(CheckpointFixture.Origin).MaxReceipts);
        session.SetPortCapacity(CheckpointFixture.Origin, 65536, 1);
        Assert.Equal(65536, session.GetPortSnapshot(CheckpointFixture.Origin).MaxCargoBatches);
        Assert.Equal(1, session.GetPortSnapshot(CheckpointFixture.Origin).MaxReceipts);
        Assert.Equal(2, session.ProviderRevision);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Capacity_RequiresExistingStationBeforePublication(bool empty)
    {
        using var directory = new DurableTestDirectory();
        using var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime);
        byte[] core = File.ReadAllBytes(CorePath(directory)), provider = File.ReadAllBytes(directory.ProviderPath);
        Assert.Throws<ArgumentException>(() => session.SetPortCapacity(empty ? default : new StationId(CheckpointFixture.Id(999)), 1, 1));
        Assert.Equal(core, File.ReadAllBytes(CorePath(directory))); Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
        session.SetPortCapacity(CheckpointFixture.Destination, 1, 1); Assert.Equal(1, session.ProviderRevision);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Capacity_NoOpPrecedesExhaustedGenerationAndCoreRevision(bool exhausted)
    {
        using var directory = new DurableTestDirectory();
        using (var initial = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime)) { }
        if (exhausted)
        {
            File.WriteAllBytes(directory.ProviderPath, DurableProviderCodec.Encode(ReadProvider(directory) with { Revision = 262144, ConfigurationRevision = 262144 }));
            DurableCoreImage image = ReadCore(directory);
            File.WriteAllBytes(CorePath(directory), DurableCoreCodec.Encode(image with { ProviderRevision = 262144, ProviderConfigurationRevision = 262144, Core = image.Core with { Revision = long.MaxValue } }));
        }
        int callbacks = 0;
        using var session = DurableFlowSession.Open(directory.Core, directory.Provider, _ => callbacks++);
        PortCheckpoint before = session.GetPortSnapshot(CheckpointFixture.Origin);
        byte[] core = File.ReadAllBytes(CorePath(directory)), provider = File.ReadAllBytes(directory.ProviderPath);
        session.SetPortCapacity(CheckpointFixture.Origin, before.MaxCargoBatches, before.MaxReceipts);
        Assert.Equal(0, callbacks); Assert.Equal(core, File.ReadAllBytes(CorePath(directory))); Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
        if (exhausted) Assert.Throws<InvalidOperationException>(() => session.SetPortCapacity(CheckpointFixture.Origin, 17, 23));
        else session.SetPortCapacity(CheckpointFixture.Origin, 17, 23);
        Assert.Equal(exhausted ? 262144 : 1, session.ProviderRevision);
    }

    [Theory]
    [InlineData(long.MaxValue)] [InlineData(long.MaxValue - 1)]
    public void Capacity_BothCoreRevisionsMustFitBeforePublication(long revision)
    {
        using var directory = new DurableTestDirectory();
        using (var initial = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime)) { }
        DurableCoreImage image = ReadCore(directory);
        byte[] core = DurableCoreCodec.Encode(image with { Core = image.Core with { Revision = revision } }), provider = File.ReadAllBytes(directory.ProviderPath);
        File.WriteAllBytes(CorePath(directory), core); int callbacks = 0;
        using var session = DurableFlowSession.Open(directory.Core, directory.Provider, _ => callbacks++);
        Assert.Throws<OverflowException>(() => session.SetPortCapacity(CheckpointFixture.Origin, 17, 23));
        Assert.Equal(0, callbacks); Assert.Equal(revision, session.Revision); Assert.Equal(0, session.ProviderRevision);
        Assert.Equal(core, File.ReadAllBytes(CorePath(directory))); Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
    }

    [Fact]
    public void Capacity_IncreaseRequiresExplicitRetryAndPreservesRejectedReceiptAndCustody()
    {
        using var directory = new DurableTestDirectory();
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime))
        {
            session.SetPortCapacity(CheckpointFixture.Destination, 1, 5);
            session.ProvisionCargo(new CargoId(CheckpointFixture.Id(9044)), CheckpointFixture.Destination, new CargoManifest("wood", 5));
            session.Execute(runtime => { Assert.True(runtime.TryReserve(CheckpointFixture.Parcel)); Assert.Equal(3, runtime.AdvanceTo(4)); });
            Assert.Equal(ParcelState.DeliveryRejected, session.Runtime.GetParcel(CheckpointFixture.Parcel).State);
            FlowRuntime held = session.Runtime; CargoBatch cargo = held.GetCargo(CheckpointFixture.Cargo);
            DurableProviderImage before = ReadProvider(directory);
            session.SetPortCapacity(CheckpointFixture.Destination, 2, 5);
            Assert.Same(held, session.Runtime); Assert.Equal(cargo, held.GetCargo(CheckpointFixture.Cargo));
            Assert.Equal(before.Receipts, ReadProvider(directory).Receipts);
            Assert.Equal(ParcelState.DeliveryRejected, held.GetParcel(CheckpointFixture.Parcel).State);
            Assert.Equal(0, held.PendingOperationCount); Assert.Single(session.GetPortSnapshot(CheckpointFixture.Destination).Inventory);
            Assert.Null(ReadCore(directory).Capacity); Assert.Equal(before.Revision + 1, session.ProviderRevision);
        }
        using var reopened = DurableFlowSession.Open(directory.Core, directory.Provider);
        reopened.Execute(runtime => { Assert.True(runtime.RetryDelivery(CheckpointFixture.Parcel)); Assert.Equal(1, runtime.AdvanceTo(runtime.Now + 1)); });
        Assert.Equal(ParcelState.Delivered, reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(new[] { (int)PortResult.Rejected, (int)PortResult.Applied }, reopened.GetPortSnapshot(CheckpointFixture.Destination).Receipts.Select(r => r.Result));
        Assert.Equal(2, reopened.GetPortSnapshot(CheckpointFixture.Destination).Inventory.Length);
        Assert.Null(reopened.Runtime.GetCargo(CheckpointFixture.Cargo).ClaimedBy);
        byte[] core = File.ReadAllBytes(CorePath(directory)), provider = File.ReadAllBytes(directory.ProviderPath);
        Assert.Throws<InvalidOperationException>(() => reopened.SetPortCapacity(CheckpointFixture.Destination, 1, 5));
        Assert.Throws<InvalidOperationException>(() => reopened.SetPortCapacity(CheckpointFixture.Destination, 2, 1));
        Assert.Equal(core, File.ReadAllBytes(CorePath(directory))); Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
        reopened.SetPortCapacity(CheckpointFixture.Destination, 2, 2);
        Assert.Equal(2, reopened.GetPortSnapshot(CheckpointFixture.Destination).MaxReceipts);
        Assert.Equal(CheckpointFixture.Destination, reopened.Runtime.GetCargo(CheckpointFixture.Cargo).Owner.Station);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Capacity_CallbackCannotMutateOrReleaseLease(int boundary)
    {
        using var directory = new DurableTestDirectory(); DurableFlowSession? active = null; bool observed = false;
        using var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime, stage =>
        {
            if ((int)stage != boundary) return;
            Assert.Throws<InvalidOperationException>(() => active!.SetPortCapacity(CheckpointFixture.Origin, 19, 29));
            Assert.Throws<InvalidOperationException>(() => active!.ProvisionCargo(new CargoId(CheckpointFixture.Id(44)), CheckpointFixture.Origin, CheckpointFixture.Manifest));
            Assert.Throws<InvalidOperationException>(() => active!.RegisterStation(new StationId(CheckpointFixture.Id(55))));
            Assert.Throws<InvalidOperationException>(() => active!.SetAdmission(CheckpointFixture.Origin, false, false));
            Assert.Throws<InvalidOperationException>(() => active!.Execute(_ => { }));
            Assert.Throws<InvalidOperationException>(() => active!.Dispose());
            Assert.Throws<InvalidOperationException>(() => active!.Runtime.Cancel(CheckpointFixture.Parcel));
            Assert.Throws<IOException>(() => DurableFlowSession.Open(directory.Core, directory.Provider)); observed = true;
        });
        active = session; session.SetPortCapacity(CheckpointFixture.Origin, 17, 23);
        Assert.True(observed); Assert.Equal(1, session.ProviderRevision);
        Assert.Equal(17, session.GetPortSnapshot(CheckpointFixture.Origin).MaxCargoBatches);
        Assert.Equal(ParcelState.Created, session.Runtime.GetParcel(CheckpointFixture.Parcel).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Capacity_PreservesUnreconciledTransferAndItsReceipt(bool committed)
    {
        using var directory = new DurableTestDirectory(); bool armed = false;
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime,
            stage => { if (armed && stage == (committed ? DurableCommitStage.AfterProvider : DurableCommitStage.AfterIntent)) throw new CapacityFailure(); }))
        {
            session.Execute(runtime => Assert.True(runtime.TryReserve(CheckpointFixture.Parcel))); armed = true;
            Assert.Throws<CapacityFailure>(() => session.Execute(runtime => runtime.AdvanceTo(1)));
        }
        using (var restored = DurableFlowSession.Open(directory.Core, directory.Provider))
        {
            DurableProviderReceipt[] receipts = ReadProvider(directory).Receipts;
            PortTransfer held = restored.Runtime.GetParcel(CheckpointFixture.Parcel).PendingTransfer!;
            restored.SetPortCapacity(CheckpointFixture.Origin, 17, 23);
            Assert.Same(held, restored.Runtime.GetParcel(CheckpointFixture.Parcel).PendingTransfer);
            Assert.Equal(receipts, ReadProvider(directory).Receipts);
            Assert.Equal(ParcelState.ExtractionUncertain, restored.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        }
        using var reopened = DurableFlowSession.Open(directory.Core, directory.Provider);
        byte[] provider = File.ReadAllBytes(directory.ProviderPath);
        reopened.Execute(runtime => Assert.True(runtime.ReconcileTransfer(CheckpointFixture.Parcel)));
        Assert.Equal(committed ? ParcelState.InTransit : ParcelState.Cancelled, reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(committed ? 0 : 1, reopened.GetPortSnapshot(CheckpointFixture.Origin).Inventory.Length);
        Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath)); Assert.Equal(17, reopened.GetPortSnapshot(CheckpointFixture.Origin).MaxCargoBatches);
    }

    [Fact]
    public void Capacity_NestedAndWrongThreadCallsDoNotPublish()
    {
        using var directory = new DurableTestDirectory();
        using var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime);
        Exception? observed = null;
        var thread = new System.Threading.Thread(() => observed = Record.Exception(() => session.SetPortCapacity(CheckpointFixture.Origin, 17, 23)));
        thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(5))); Assert.IsType<InvalidOperationException>(observed);
        session.Execute(_ => Assert.Throws<InvalidOperationException>(() => session.SetPortCapacity(CheckpointFixture.Origin, 17, 23)));
        Assert.Equal(0, session.ProviderRevision);
        session.SetPortCapacity(CheckpointFixture.Origin, 17, 23); Assert.Equal(1, session.ProviderRevision);
    }

    [Theory]
    [InlineData("unarmed")] [InlineData("clone")] [InlineData("owner")] [InlineData("revision")]
    public void CapacityProvider_RequiresExactArmedPermission(string denial)
    {
        using var directory = new DurableTestDirectory();
        FlowCheckpoint setup = new CheckpointFixture().Runtime.CaptureCheckpoint();
        using var provider = DurableCargoProvider.Create(directory.Provider, CheckpointFixture.Id(999), setup.NetworkId, setup.Stations);
        object owner = new(); provider.BindAdmissionOwner(owner);
        var intent = new PortCapacityIntent(CheckpointFixture.Origin.Value, 17, 23);
        byte[] before = File.ReadAllBytes(directory.ProviderPath);
        if (denial != "unarmed") provider.ArmCapacity(owner, intent, 0);
        Assert.Throws<InvalidOperationException>(() => provider.ApplyCapacity(denial == "owner" ? new object() : owner,
            denial == "clone" ? intent with { } : intent, denial == "revision" ? 1 : 0));
        Assert.Equal(before, File.ReadAllBytes(directory.ProviderPath)); Assert.NotEqual(17, provider.GetPort(CheckpointFixture.Origin).CaptureCheckpoint().MaxCargoBatches);
        if (denial == "unarmed") provider.ArmCapacity(owner, intent, 0);
        provider.ApplyCapacity(owner, intent, 0);
        Assert.Equal(1, provider.Revision); Assert.Equal(1, provider.ConfigurationRevision);
        Assert.Equal(17, provider.GetPort(CheckpointFixture.Origin).CaptureCheckpoint().MaxCargoBatches);
        Assert.Empty(provider.CaptureImage().Receipts);
        Assert.Throws<InvalidOperationException>(() => provider.ApplyCapacity(owner, intent, 0));
        Assert.Equal(1, provider.Revision);
    }

    [Theory]
    [InlineData("admission", false)] [InlineData("admission", true)]
    [InlineData("registration", false)] [InlineData("registration", true)]
    [InlineData("transfer", false)] [InlineData("transfer", true)]
    [InlineData("provision", false)] [InlineData("provision", true)]
    public void CapacityProvider_AllOtherIntentPermissionsAreExclusive(string other, bool capacityFirst)
    {
        using var directory = new DurableTestDirectory();
        FlowCheckpoint setup = new CheckpointFixture().Runtime.CaptureCheckpoint();
        using var provider = DurableCargoProvider.Create(directory.Provider, CheckpointFixture.Id(999), setup.NetworkId, setup.Stations);
        object owner = new(); provider.BindAdmissionOwner(owner);
        var capacity = new PortCapacityIntent(CheckpointFixture.Origin.Value, 17, 23);
        var provision = new CargoProvisionIntent(CheckpointFixture.Id(9044), CheckpointFixture.Origin.Value, new ManifestCheckpoint("wood", 5));
        var admission = new AdmissionIntent(CheckpointFixture.Origin.Value, false, false);
        var registration = new StationRegistrationIntent(CheckpointFixture.Id(44), 17, 23, true, true);
        var authority = new PortAuthority(new FlowLimits());
        ICheckpointCargoPort port = provider.GetPort(CheckpointFixture.Origin); port.Bind(authority, CheckpointFixture.Origin);
        var parcel = new Parcel(CheckpointFixture.Parcel, CheckpointFixture.Shipment, CheckpointFixture.Cargo,
            CheckpointFixture.Manifest, CheckpointFixture.Policy, ParcelState.Created, 0, 0, CheckpointFixture.Origin, null);
        PortTransfer transfer = authority.Issue(parcel, CheckpointFixture.Origin, PortTransferKind.Extract, 1);
        Action armOther = () =>
        {
            if (other == "admission") provider.ArmAdmission(owner, admission, 0);
            else if (other == "registration") provider.ArmRegistration(owner, registration, 0);
            else if (other == "provision") provider.ArmProvision(owner, provision, 0);
            else provider.ArmIntent(transfer);
        };
        if (capacityFirst)
        {
            provider.ArmCapacity(owner, capacity, 0); Assert.Throws<InvalidOperationException>(armOther);
            provider.ApplyCapacity(owner, capacity, 0);
            Assert.Same(port, provider.GetPort(CheckpointFixture.Origin));
            Assert.Equal(17, port.CaptureCheckpoint().MaxCargoBatches); Assert.Equal(CheckpointFixture.Manifest, port.ReadCargo(CheckpointFixture.Cargo));
            Assert.True(port.CaptureCheckpoint().AcceptExtractions); Assert.Equal(2, provider.CaptureStations().Length);
            Assert.Empty(provider.CaptureImage().Receipts);
        }
        else
        {
            armOther(); Assert.Throws<InvalidOperationException>(() => provider.ArmCapacity(owner, capacity, 0));
            if (other == "admission") provider.ApplyAdmission(owner, admission, 0);
            else if (other == "registration") provider.ApplyRegistration(owner, registration, 0);
            else if (other == "provision") provider.ApplyProvision(owner, provision, 0);
            else Assert.Equal(PortResult.Applied, port.Apply(transfer));
            Assert.NotEqual(17, port.CaptureCheckpoint().MaxCargoBatches);
            Assert.Equal(other == "transfer" ? 1 : 0, provider.CaptureImage().Receipts.Length);
        }
        Assert.Equal(1, provider.Revision);
        Assert.Equal(!capacityFirst && other == "transfer" ? 0 : 1, provider.ConfigurationRevision);
    }

    [Theory]
    [InlineData("batch-limit")] [InlineData("receipt-limit")] [InlineData("flags")]
    [InlineData("manifest")] [InlineData("other-station")] [InlineData("extra-generation")]
    public void CapacityOpen_RejectsEveryUnjustifiedAheadState(string corruption)
    {
        using var directory = new DurableTestDirectory();
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime,
            stage => { if (stage == DurableCommitStage.AfterProvider) throw new CapacityFailure(); }))
            Assert.Throws<CapacityFailure>(() => session.SetPortCapacity(CheckpointFixture.Origin, 17, 23));
        DurableProviderImage image = ReadProvider(directory);
        int index = Array.FindIndex(image.Stations, station => station.Id == CheckpointFixture.Origin.Value);
        StationCheckpoint station = image.Stations[index]; PortCheckpoint port = station.Port;
        switch (corruption)
        {
            case "batch-limit": port = port with { MaxCargoBatches = 18 }; break;
            case "receipt-limit": port = port with { MaxReceipts = 24 }; break;
            case "flags": port = port with { AcceptExtractions = false }; break;
            case "manifest": port = port with { Inventory = new[] { port.Inventory[0] with { Manifest = new ManifestCheckpoint("ore", 8) } } }; break;
            case "other-station":
                int other = Array.FindIndex(image.Stations, item => item.Id == CheckpointFixture.Destination.Value);
                image.Stations[other] = image.Stations[other] with { Port = image.Stations[other].Port with { MaxCargoBatches = 19 } }; break;
            case "extra-generation": image = image with { Revision = image.Revision + 1, ConfigurationRevision = image.ConfigurationRevision + 1 }; break;
        }
        image.Stations[index] = station with { Port = port };
        byte[] bytes = DurableProviderCodec.Encode(image), core = File.ReadAllBytes(CorePath(directory));
        File.WriteAllBytes(directory.ProviderPath, bytes);
        Assert.Throws<InvalidDataException>(() => DurableFlowSession.Open(directory.Core, directory.Provider));
        Assert.Equal(bytes, File.ReadAllBytes(directory.ProviderPath)); Assert.Equal(core, File.ReadAllBytes(CorePath(directory)));
    }

    [Theory]
    [InlineData("noop")] [InlineData("unknown-station")] [InlineData("inventory")]
    [InlineData("generation")] [InlineData("core-revision")]
    public void CapacityOpen_RejectsInvalidIntentEvenAtEqualGeneration(string invalid)
    {
        using var directory = new DurableTestDirectory(); var fixture = new CheckpointFixture();
        if (invalid == "inventory") fixture.AddBatch(2);
        using (var initial = DurableFlowSession.Create(directory.Core, directory.Provider, fixture.Runtime)) { }
        DurableCoreImage image = ReadCore(directory);
        PortCheckpoint before = image.Core.Checkpoint.Stations.Single(station => station.Id == CheckpointFixture.Origin.Value).Port;
        if (invalid == "generation")
        {
            image = image with { ProviderRevision = 262144, ProviderConfigurationRevision = 262144 };
            File.WriteAllBytes(directory.ProviderPath, DurableProviderCodec.Encode(ReadProvider(directory) with { Revision = 262144, ConfigurationRevision = 262144 }));
        }
        if (invalid == "core-revision") image = image with { Core = image.Core with { Revision = long.MaxValue } };
        var intent = new PortCapacityIntent(invalid == "unknown-station" ? CheckpointFixture.Id(999) : CheckpointFixture.Origin.Value,
            invalid == "noop" ? before.MaxCargoBatches : invalid == "inventory" ? 1 : 17,
            invalid == "noop" ? before.MaxReceipts : 23);
        byte[] bytes = DurableCoreCodec.Encode(image with { Capacity = intent }), provider = File.ReadAllBytes(directory.ProviderPath);
        File.WriteAllBytes(CorePath(directory), bytes);
        Assert.Throws<InvalidDataException>(() => DurableFlowSession.Open(directory.Core, directory.Provider));
        Assert.Equal(bytes, File.ReadAllBytes(CorePath(directory))); Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
    }

    [Theory]
    [InlineData("discriminator")] [InlineData("reserved")] [InlineData("empty-station")]
    [InlineData("zero-batches")] [InlineData("high-batches")] [InlineData("zero-receipts")] [InlineData("high-receipts")]
    [InlineData("with-transfer")] [InlineData("with-admission")] [InlineData("with-registration")] [InlineData("with-provision")]
    [InlineData("truncated")] [InlineData("trailing")]
    public void CapacityCodec_RejectsMalformedV5AfterValidIntegrity(string corruption)
    {
        var image = new DurableCoreImage(CheckpointFixture.Id(999), 0, null, new CheckpointImage(0, new CheckpointFixture().Runtime.CaptureCheckpoint()),
            Capacity: new PortCapacityIntent(CheckpointFixture.Origin.Value, 17, 23));
        byte[] bytes = DurableCoreCodec.Encode(image);
        switch (corruption)
        {
            case "discriminator": bytes[156] = 2; break;
            case "reserved": bytes[156] = 0; break;
            case "empty-station": Array.Clear(bytes, 157, 16); break;
            case "zero-batches": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(173), 0); break;
            case "high-batches": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(173), 65537); break;
            case "zero-receipts": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(177), 0); break;
            case "high-receipts": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(177), 65537); break;
            case "with-admission": bytes[105] = 1; CheckpointFixture.Origin.Value.TryWriteBytes(bytes.AsSpan(106, 16)); break;
            case "with-registration": bytes[124] = 1; CheckpointFixture.Id(44).TryWriteBytes(bytes.AsSpan(125, 16));
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(141), 17); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(145), 23); break;
            case "with-transfer": bytes[36] = 1; CheckpointFixture.Parcel.Value.TryWriteBytes(bytes.AsSpan(37, 16));
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(53), (int)PortTransferKind.Extract); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(57), 1); break;
            case "with-provision":
                bytes = DurableCoreCodec.Encode(image with { Capacity = null, Provision = new CargoProvisionIntent(CheckpointFixture.Id(9044), CheckpointFixture.Origin.Value, new ManifestCheckpoint("wood", 5)) });
                bytes[156] = 1; CheckpointFixture.Origin.Value.TryWriteBytes(bytes.AsSpan(157, 16));
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(173), 17); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(177), 23); break;
            case "truncated": bytes = bytes[..^1]; break;
            case "trailing": bytes = bytes.Concat(new byte[] { 0 }).ToArray(); break;
        }
        Rehash(bytes); Assert.Throws<InvalidDataException>(() => DurableCoreCodec.Decode(bytes));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void Capacity_LegacyPairReadsWithoutRewriteThenUsesV5Intent(int version)
    {
        using var directory = new DurableTestDirectory();
        using (var initial = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime)) { }
        byte[] current = File.ReadAllBytes(CorePath(directory));
        int header = version == 1 ? 97 : version == 2 ? 124 : version == 3 ? 151 : 156;
        byte[] legacy = current[..header].Concat(current[DurableCoreCodec.HeaderLength..]).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(legacy.AsSpan(8), version); Rehash(legacy); File.WriteAllBytes(CorePath(directory), legacy);
        byte[] provider = File.ReadAllBytes(directory.ProviderPath);
        using (var old = DurableFlowSession.Open(directory.Core, directory.Provider,
            stage => { if (stage == DurableCommitStage.AfterProvider) throw new CapacityFailure(); }))
        {
            Assert.Equal(legacy, File.ReadAllBytes(CorePath(directory))); Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
            Assert.Throws<CapacityFailure>(() => old.SetPortCapacity(CheckpointFixture.Origin, 17, 23));
        }
        byte[] intent = File.ReadAllBytes(CorePath(directory)), committed = File.ReadAllBytes(directory.ProviderPath);
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(intent.AsSpan(8)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(committed.AsSpan(8)));
        using var restored = DurableFlowSession.Open(directory.Core, directory.Provider);
        Assert.Equal(17, restored.GetPortSnapshot(CheckpointFixture.Origin).MaxCargoBatches);
        Assert.Equal(23, restored.GetPortSnapshot(CheckpointFixture.Origin).MaxReceipts);
        Assert.Equal(intent, File.ReadAllBytes(CorePath(directory))); Assert.Equal(committed, File.ReadAllBytes(directory.ProviderPath));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Capacity_V4ProvisionPayloadRecoversBeforeNewCapacityCommand(bool committed)
    {
        using var directory = new DurableTestDirectory(); var cargo = new CargoId(CheckpointFixture.Id(9044));
        using (var initial = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime,
            stage => { if (stage == (committed ? DurableCommitStage.AfterProvider : DurableCommitStage.AfterIntent)) throw new CapacityFailure(); }))
            Assert.Throws<CapacityFailure>(() => initial.ProvisionCargo(cargo, CheckpointFixture.Origin, new CargoManifest("木🪵", 5)));
        byte[] current = File.ReadAllBytes(CorePath(directory));
        byte[] legacy = current[..156].Concat(current[DurableCoreCodec.HeaderLength..]).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(legacy.AsSpan(8), 4); Rehash(legacy); File.WriteAllBytes(CorePath(directory), legacy);
        byte[] provider = File.ReadAllBytes(directory.ProviderPath);
        using var restored = DurableFlowSession.Open(directory.Core, directory.Provider);
        Assert.Equal(legacy, File.ReadAllBytes(CorePath(directory))); Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
        Assert.Equal(committed ? 2 : 1, restored.GetPortSnapshot(CheckpointFixture.Origin).Inventory.Length);
        restored.SetPortCapacity(CheckpointFixture.Origin, 17, 23);
        Assert.Equal(committed ? 2 : 1, restored.ProviderRevision);
        Assert.Equal(committed ? 2 : 1, restored.GetPortSnapshot(CheckpointFixture.Origin).Inventory.Length);
        if (committed) Assert.Equal(new CargoManifest("木🪵", 5), restored.Runtime.GetCargo(cargo).Manifest);
        Assert.Null(ReadCore(directory).Provision); Assert.Null(ReadCore(directory).Capacity);
    }

    [Theory]
    [InlineData("missing-ledger")] [InlineData("provider-limit")] [InlineData("inventory-limit")]
    public void CapacityOpen_IntentCannotRepairInvalidBaseline(string invalid)
    {
        using var directory = new DurableTestDirectory();
        var fixture = new CheckpointFixture(); if (invalid == "inventory-limit") fixture.AddBatch(2);
        using (var initial = DurableFlowSession.Create(directory.Core, directory.Provider, fixture.Runtime)) { }
        DurableCoreImage image = ReadCore(directory); FlowCheckpoint baseline = image.Core.Checkpoint;
        if (invalid == "missing-ledger") baseline = baseline with { Cargo = Array.Empty<CargoCheckpoint>() };
        else
        {
            int index = Array.FindIndex(baseline.Stations, station => station.Id == CheckpointFixture.Origin.Value);
            baseline.Stations[index] = baseline.Stations[index] with { Port = baseline.Stations[index].Port with { MaxReceipts = invalid == "provider-limit" ? 65537 : baseline.Stations[index].Port.MaxReceipts, MaxCargoBatches = invalid == "inventory-limit" ? 1 : baseline.Stations[index].Port.MaxCargoBatches } };
        }
        byte[] core = DurableCoreCodec.Encode(image with { Core = image.Core with { Checkpoint = baseline }, Capacity = new PortCapacityIntent(CheckpointFixture.Origin.Value, 17, 23) });
        if (invalid != "missing-ledger")
        {
            DurableProviderImage actual = ReadProvider(directory);
            int index = Array.FindIndex(actual.Stations, station => station.Id == CheckpointFixture.Origin.Value);
            actual.Stations[index] = actual.Stations[index] with { Port = actual.Stations[index].Port with { MaxCargoBatches = 17, MaxReceipts = 23 } };
            File.WriteAllBytes(directory.ProviderPath, DurableProviderCodec.Encode(actual with { Revision = 1, ConfigurationRevision = 1 }));
        }
        byte[] provider = File.ReadAllBytes(directory.ProviderPath); File.WriteAllBytes(CorePath(directory), core);
        Assert.Throws<InvalidDataException>(() => DurableFlowSession.Open(directory.Core, directory.Provider));
        Assert.Equal(core, File.ReadAllBytes(CorePath(directory))); Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Capacity_NoOpPreservesOlderUnacknowledgedIntentBytes(bool committed)
    {
        using var directory = new DurableTestDirectory();
        using (var initial = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime,
            stage => { if (stage == (committed ? DurableCommitStage.AfterProvider : DurableCommitStage.AfterIntent)) throw new CapacityFailure(); }))
            Assert.Throws<CapacityFailure>(() => initial.SetAdmission(CheckpointFixture.Origin, false, false));
        byte[] core = File.ReadAllBytes(CorePath(directory)), provider = File.ReadAllBytes(directory.ProviderPath); int callbacks = 0;
        using var restored = DurableFlowSession.Open(directory.Core, directory.Provider, _ => callbacks++);
        PortCheckpoint port = restored.GetPortSnapshot(CheckpointFixture.Origin);
        restored.SetPortCapacity(CheckpointFixture.Origin, port.MaxCargoBatches, port.MaxReceipts);
        Assert.Equal(0, callbacks); Assert.NotNull(ReadCore(directory).Admission);
        Assert.Equal(!committed, port.AcceptDeposits); Assert.Equal(!committed, port.AcceptExtractions);
        Assert.Equal(core, File.ReadAllBytes(CorePath(directory))); Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
    }

    [Fact]
    public void Capacity_JournalIncreaseRetainsMissingUntilExplicitReconcileAndRetry()
    {
        using var directory = new DurableTestDirectory();
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime))
        {
        session.SetPortCapacity(CheckpointFixture.Destination, 5, 1);
        session.SetAdmission(CheckpointFixture.Destination, false, true);
        session.Execute(runtime => { Assert.True(runtime.TryReserve(CheckpointFixture.Parcel)); Assert.Equal(3, runtime.AdvanceTo(4)); });
        Assert.Throws<InvalidOperationException>(() => session.Execute(runtime => { Assert.True(runtime.RetryDelivery(CheckpointFixture.Parcel)); runtime.AdvanceTo(5); }));
        }
        using var reopened = DurableFlowSession.Open(directory.Core, directory.Provider);
        Assert.Equal(ParcelState.DeliveryUncertain, reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        PortTransfer pending = reopened.Runtime.GetParcel(CheckpointFixture.Parcel).PendingTransfer!;
        DurableProviderReceipt[] receipts = ReadProvider(directory).Receipts;
        reopened.SetPortCapacity(CheckpointFixture.Destination, 5, 2);
        Assert.Same(pending, reopened.Runtime.GetParcel(CheckpointFixture.Parcel).PendingTransfer);
        Assert.Equal(receipts, ReadProvider(directory).Receipts);
        Assert.Equal(ParcelState.DeliveryUncertain, reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        reopened.SetAdmission(CheckpointFixture.Destination, true, true);
        reopened.Execute(runtime => Assert.True(runtime.ReconcileTransfer(CheckpointFixture.Parcel)));
        Assert.Equal(ParcelState.DeliveryFaulted, reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(receipts, ReadProvider(directory).Receipts);
        reopened.Execute(runtime => { Assert.True(runtime.RetryDelivery(CheckpointFixture.Parcel)); Assert.Equal(1, runtime.AdvanceTo(6)); });
        Assert.Equal(ParcelState.Delivered, reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(new[] { (int)PortResult.Rejected, (int)PortResult.Applied }, reopened.GetPortSnapshot(CheckpointFixture.Destination).Receipts.Select(item => item.Result));
        Assert.Equal(CheckpointFixture.Cargo.Value, Assert.Single(reopened.GetPortSnapshot(CheckpointFixture.Destination).Inventory).CargoId);
    }

    private static void Rehash(byte[] bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes.AsSpan(0, 65)); hash.AppendData(bytes.AsSpan(97)); hash.GetHashAndReset().CopyTo(bytes, 65);
    }
    private static string CorePath(DurableTestDirectory directory) => Path.Combine(directory.Core, DurableFlowSession.CheckpointFileName);
    private static DurableCoreImage ReadCore(DurableTestDirectory directory) => DurableCoreCodec.Decode(File.ReadAllBytes(CorePath(directory)));
    private static DurableProviderImage ReadProvider(DurableTestDirectory directory) => DurableProviderCodec.Decode(File.ReadAllBytes(directory.ProviderPath));
    private sealed class CapacityFailure : Exception { }
}
