using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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
public sealed class DurableProvisionTests
{
    private static readonly CargoId NewCargo = new(CheckpointFixture.Id(9044));
    private static readonly CargoManifest NewManifest = new("wood", 5);

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Provision_FaultBoundaryRecoversExactlyOnePhysicalAndCanonicalBatch(int boundary)
    {
        using var directory = new DurableTestDirectory();
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime,
            stage => { if ((int)stage == boundary) throw new ProvisionFailure(); }))
        {
            Assert.Throws<ProvisionFailure>(() => session.ProvisionCargo(NewCargo, CheckpointFixture.Origin, NewManifest));
            Assert.Throws<ObjectDisposedException>(() => session.ProvisionCargo(NewCargo, CheckpointFixture.Origin, NewManifest));
        }
        byte[] core = File.ReadAllBytes(CorePath(directory)), provider = File.ReadAllBytes(directory.ProviderPath);
        bool committed = boundary >= (int)DurableCommitStage.AfterProvider;
        for (int iteration = 0; iteration < 2; iteration++)
        {
            using var restored = DurableFlowSession.Open(directory.Core, directory.Provider);
            Assert.Equal(committed ? 1 : 0, restored.ProviderRevision);
            Assert.Equal(committed ? 1 : 0, ReadProvider(directory).ConfigurationRevision);
            Assert.Empty(ReadProvider(directory).Receipts);
            Assert.Equal(committed ? 2 : 1, restored.GetPortSnapshot(CheckpointFixture.Origin).Inventory.Length);
            Assert.Equal(ParcelState.Created, restored.Runtime.GetParcel(CheckpointFixture.Parcel).State);
            Assert.Equal(CheckpointFixture.Parcel, restored.Runtime.GetCargo(CheckpointFixture.Cargo).ClaimedBy);
            if (committed) AssertNewBatch(restored, CheckpointFixture.Origin);
            else Assert.Throws<KeyNotFoundException>(() => restored.Runtime.GetCargo(NewCargo));
            Assert.Equal(core, File.ReadAllBytes(CorePath(directory)));
            Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
        }
    }

    [Fact]
    public void Provision_TrustedSeedPreservesRuntimeAndClaimsThenDeliversReusableCargo()
    {
        using var directory = new DurableTestDirectory();
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime))
        {
            FlowRuntime held = session.Runtime;
            CargoBatch old = held.GetCargo(CheckpointFixture.Cargo);
            session.SetAdmission(CheckpointFixture.Origin, false, false);
            session.ProvisionCargo(NewCargo, CheckpointFixture.Origin, NewManifest);
            Assert.Same(held, session.Runtime);
            Assert.Equal(old, held.GetCargo(CheckpointFixture.Cargo));
            AssertNewBatch(session, CheckpointFixture.Origin);
            Assert.False(session.GetPortSnapshot(CheckpointFixture.Origin).AcceptDeposits);
            Assert.False(session.GetPortSnapshot(CheckpointFixture.Origin).AcceptExtractions);
            Assert.Empty(ReadProvider(directory).Receipts);
            DurableCoreImage ack = ReadCore(directory);
            Assert.Single(ack.Core.Checkpoint.Shipments); Assert.Single(ack.Core.Checkpoint.Parcels);
            Assert.Null(ack.Provision); Assert.Equal(2, ack.ProviderConfigurationRevision);
            session.SetAdmission(CheckpointFixture.Origin, true, true);
            session.Execute(runtime => Deliver(runtime, NewCargo, 44, CheckpointFixture.Origin, CheckpointFixture.Destination, 4));
        }
        using (var reopened = DurableFlowSession.Open(directory.Core, directory.Provider))
        {
            Assert.Equal(1, reopened.Runtime.GetCargo(NewCargo).DispatchCount);
            Assert.Null(reopened.Runtime.GetCargo(NewCargo).ClaimedBy);
            Assert.Equal(CheckpointFixture.Destination, reopened.Runtime.GetCargo(NewCargo).Owner.Station);
            reopened.Execute(runtime =>
            {
                runtime.AddLink(new LinkId(CheckpointFixture.Id(44)), CheckpointFixture.Destination, CheckpointFixture.Origin, 20, 3);
                Deliver(runtime, NewCargo, 45, CheckpointFixture.Destination, CheckpointFixture.Origin, 8);
            });
            Assert.Equal(2, reopened.Runtime.GetCargo(NewCargo).DispatchCount);
            Assert.Null(reopened.Runtime.GetCargo(NewCargo).ClaimedBy);
            Assert.Equal(CheckpointFixture.Origin, reopened.Runtime.GetCargo(NewCargo).RegistrationStation);
            Assert.Equal(CheckpointFixture.Origin, reopened.Runtime.GetCargo(NewCargo).Owner.Station);
            Assert.Equal(CheckpointFixture.Parcel, reopened.Runtime.GetCargo(CheckpointFixture.Cargo).ClaimedBy);
            Assert.Equal(4, ReadProvider(directory).Receipts.Length);
            Assert.Equal(7, reopened.ProviderRevision);
            Assert.Equal(3, ReadProvider(directory).ConfigurationRevision);
        }
        using var final = DurableFlowSession.Open(directory.Core, directory.Provider);
        Assert.Equal(2, final.GetPortSnapshot(CheckpointFixture.Origin).Inventory.Length);
        Assert.Empty(final.GetPortSnapshot(CheckpointFixture.Destination).Inventory);
        Assert.Equal(2, final.Runtime.GetCargo(NewCargo).DispatchCount);
    }

    [Theory]
    [InlineData("empty-cargo")] [InlineData("empty-station")] [InlineData("unknown-station")]
    [InlineData("null-manifest")] [InlineData("invalid-unicode")] [InlineData("quantity")]
    [InlineData("duplicate")] [InlineData("conflict")]
    public void Provision_InvalidRequestPreservesPairAndSession(string invalid)
    {
        using var directory = new DurableTestDirectory();
        using var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime);
        byte[] core = File.ReadAllBytes(CorePath(directory)), provider = File.ReadAllBytes(directory.ProviderPath);
        switch (invalid)
        {
            case "empty-cargo": Assert.Throws<ArgumentException>(() => session.ProvisionCargo(default, CheckpointFixture.Origin, NewManifest)); break;
            case "empty-station": Assert.Throws<ArgumentException>(() => session.ProvisionCargo(NewCargo, default, NewManifest)); break;
            case "unknown-station": Assert.Throws<ArgumentException>(() => session.ProvisionCargo(NewCargo, new StationId(CheckpointFixture.Id(999)), NewManifest)); break;
            case "null-manifest": Assert.Throws<ArgumentNullException>(() => session.ProvisionCargo(NewCargo, CheckpointFixture.Origin, null!)); break;
            case "invalid-unicode": Assert.Throws<InvalidDataException>(() => session.ProvisionCargo(NewCargo, CheckpointFixture.Origin, new CargoManifest("\ud800", 5))); break;
            case "quantity": Assert.Throws<ArgumentOutOfRangeException>(() => session.ProvisionCargo(NewCargo, CheckpointFixture.Origin, new CargoManifest("wood", 1000001))); break;
            case "duplicate": Assert.Throws<InvalidOperationException>(() => session.ProvisionCargo(CheckpointFixture.Cargo, CheckpointFixture.Origin, CheckpointFixture.Manifest)); break;
            case "conflict": Assert.Throws<InvalidOperationException>(() => session.ProvisionCargo(CheckpointFixture.Cargo, CheckpointFixture.Destination, NewManifest)); break;
        }
        Assert.Equal(core, File.ReadAllBytes(CorePath(directory)));
        Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
        Assert.Equal(0, session.ProviderRevision);
        session.ProvisionCargo(NewCargo, CheckpointFixture.Origin, NewManifest);
        AssertNewBatch(session, CheckpointFixture.Origin);
    }

    [Fact]
    public void Provision_RejectsInTransitIdentityEvenWhenNoPortHasBatch()
    {
        using var directory = new DurableTestDirectory();
        using var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime);
        session.Execute(runtime => { Assert.True(runtime.TryReserve(CheckpointFixture.Parcel)); Assert.Equal(1, runtime.AdvanceTo(1)); });
        Assert.All(ReadProvider(directory).Stations, station => Assert.Empty(station.Port.Inventory));
        CargoBatch before = session.Runtime.GetCargo(CheckpointFixture.Cargo);
        byte[] core = File.ReadAllBytes(CorePath(directory)), provider = File.ReadAllBytes(directory.ProviderPath);
        Assert.Throws<InvalidOperationException>(() => session.ProvisionCargo(CheckpointFixture.Cargo, CheckpointFixture.Destination, CheckpointFixture.Manifest));
        Assert.Equal(core, File.ReadAllBytes(CorePath(directory))); Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
        Assert.Equal(before, session.Runtime.GetCargo(CheckpointFixture.Cargo));
        session.ProvisionCargo(NewCargo, CheckpointFixture.Destination, NewManifest);
        AssertNewBatch(session, CheckpointFixture.Destination);
        session.Execute(runtime => Assert.Equal(2, runtime.AdvanceTo(4)));
        Assert.Equal(2, session.GetPortSnapshot(CheckpointFixture.Destination).Inventory.Length);
        Assert.Equal(ParcelState.Delivered, session.Runtime.GetParcel(CheckpointFixture.Parcel).State);
    }

    [Theory]
    [InlineData("port")] [InlineData("core")] [InlineData("generation")]
    public void Provision_CapacityRejectionHappensBeforeIntent(string capacity)
    {
        using var directory = new DurableTestDirectory();
        FlowCheckpoint setup = new CheckpointFixture().Runtime.CaptureCheckpoint();
        if (capacity == "core") setup = setup with { Limits = setup.Limits with { MaxParcels = 1 } };
        if (capacity == "port") setup.Stations[0] = setup.Stations[0] with { Port = setup.Stations[0].Port with { MaxCargoBatches = 1 } };
        using (var initial = DurableFlowSession.Create(directory.Core, directory.Provider, FlowRuntime.RestoreCheckpoint(setup))) { }
        if (capacity == "generation")
        {
            File.WriteAllBytes(directory.ProviderPath, DurableProviderCodec.Encode(ReadProvider(directory) with { Revision = 262144, ConfigurationRevision = 262144 }));
            File.WriteAllBytes(CorePath(directory), DurableCoreCodec.Encode(ReadCore(directory) with { ProviderRevision = 262144, ProviderConfigurationRevision = 262144 }));
        }
        int callbacks = 0;
        using var session = DurableFlowSession.Open(directory.Core, directory.Provider, _ => callbacks++);
        byte[] core = File.ReadAllBytes(CorePath(directory)), provider = File.ReadAllBytes(directory.ProviderPath);
        Assert.Throws<InvalidOperationException>(() => session.ProvisionCargo(NewCargo, CheckpointFixture.Origin, NewManifest));
        Assert.Equal(0, callbacks); Assert.Equal(core, File.ReadAllBytes(CorePath(directory))); Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
        Assert.Throws<KeyNotFoundException>(() => session.Runtime.GetCargo(NewCargo));
        Assert.Equal(CheckpointFixture.Parcel, session.Runtime.GetCargo(CheckpointFixture.Cargo).ClaimedBy);
        Assert.Equal(capacity == "generation" ? 262144 : 0, session.ProviderRevision);
    }

    [Theory]
    [InlineData(long.MaxValue)] [InlineData(long.MaxValue - 1)]
    public void Provision_BothCoreRevisionsMustFitBeforePublication(long revision)
    {
        using var directory = new DurableTestDirectory();
        using (var initial = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime)) { }
        DurableCoreImage image = ReadCore(directory); image = image with { Core = image.Core with { Revision = revision } };
        byte[] core = DurableCoreCodec.Encode(image), provider = File.ReadAllBytes(directory.ProviderPath);
        File.WriteAllBytes(CorePath(directory), core);
        int callbacks = 0;
        using var session = DurableFlowSession.Open(directory.Core, directory.Provider, _ => callbacks++);
        Assert.Throws<OverflowException>(() => session.ProvisionCargo(NewCargo, CheckpointFixture.Origin, NewManifest));
        Assert.Equal(0, callbacks); Assert.Equal(revision, session.Revision);
        Assert.Equal(core, File.ReadAllBytes(CorePath(directory))); Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
        Assert.Equal(0, session.ProviderRevision); Assert.Throws<KeyNotFoundException>(() => session.Runtime.GetCargo(NewCargo));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Provision_CallbackCannotMutateOrReleaseLease(int boundary)
    {
        using var directory = new DurableTestDirectory();
        DurableFlowSession? active = null; FlowRuntime? held = null; bool observed = false;
        using var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime, stage =>
        {
            if ((int)stage != boundary) return;
            Assert.Throws<InvalidOperationException>(() => active!.ProvisionCargo(NewCargo, CheckpointFixture.Origin, NewManifest));
            Assert.Throws<InvalidOperationException>(() => active!.RegisterStation(new StationId(CheckpointFixture.Id(55))));
            Assert.Throws<InvalidOperationException>(() => active!.SetAdmission(CheckpointFixture.Origin, false, false));
            Assert.Throws<InvalidOperationException>(() => active!.Execute(_ => { }));
            Assert.Throws<InvalidOperationException>(() => active!.Dispose());
            Assert.Throws<InvalidOperationException>(() => held!.Cancel(CheckpointFixture.Parcel));
            Assert.Throws<IOException>(() => DurableFlowSession.Open(directory.Core, directory.Provider));
            observed = true;
        });
        active = session; held = session.Runtime;
        session.ProvisionCargo(NewCargo, CheckpointFixture.Origin, NewManifest);
        Assert.True(observed); Assert.Same(held, session.Runtime); AssertNewBatch(session, CheckpointFixture.Origin);
        Assert.Equal(ParcelState.Created, held.GetParcel(CheckpointFixture.Parcel).State); Assert.Equal(1, session.ProviderRevision);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Provision_CoexistsWithUnreconciledTransfer(bool committed)
    {
        using var directory = new DurableTestDirectory(); bool armed = false;
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime,
            stage => { if (armed && stage == (committed ? DurableCommitStage.AfterProvider : DurableCommitStage.AfterIntent)) throw new ProvisionFailure(); }))
        {
            session.Execute(runtime => Assert.True(runtime.TryReserve(CheckpointFixture.Parcel))); armed = true;
            Assert.Throws<ProvisionFailure>(() => session.Execute(runtime => runtime.AdvanceTo(1)));
        }
        using (var restored = DurableFlowSession.Open(directory.Core, directory.Provider))
        {
            DurableProviderReceipt[] receipts = ReadProvider(directory).Receipts;
            PortTransfer held = restored.Runtime.GetParcel(CheckpointFixture.Parcel).PendingTransfer!;
            restored.ProvisionCargo(NewCargo, CheckpointFixture.Origin, NewManifest);
            Assert.Same(held, restored.Runtime.GetParcel(CheckpointFixture.Parcel).PendingTransfer);
            Assert.Equal(receipts, ReadProvider(directory).Receipts);
            Assert.Equal(ParcelState.ExtractionUncertain, restored.Runtime.GetParcel(CheckpointFixture.Parcel).State);
            AssertNewBatch(restored, CheckpointFixture.Origin);
        }
        using var reopened = DurableFlowSession.Open(directory.Core, directory.Provider);
        byte[] provider = File.ReadAllBytes(directory.ProviderPath);
        reopened.Execute(runtime => Assert.True(runtime.ReconcileTransfer(CheckpointFixture.Parcel)));
        Assert.Equal(committed ? ParcelState.InTransit : ParcelState.Cancelled, reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(committed ? 1 : 2, reopened.GetPortSnapshot(CheckpointFixture.Origin).Inventory.Length);
        Assert.Equal(committed ? 2 : 1, reopened.ProviderRevision);
        Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath)); AssertNewBatch(reopened, CheckpointFixture.Origin);
    }

    [Fact]
    public void Provision_NestedWrongThreadAndForgedCoreOwnerRemainDenied()
    {
        using var directory = new DurableTestDirectory();
        using var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime);
        Exception? observed = null;
        var thread = new System.Threading.Thread(() => observed = Record.Exception(() => session.ProvisionCargo(NewCargo, CheckpointFixture.Origin, NewManifest)));
        thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(5))); Assert.IsType<InvalidOperationException>(observed);
        session.Execute(runtime =>
        {
            Assert.Throws<InvalidOperationException>(() => session.ProvisionCargo(NewCargo, CheckpointFixture.Origin, NewManifest));
            Assert.Throws<InvalidOperationException>(() => runtime.RegisterCargo(NewCargo, CheckpointFixture.Origin, NewManifest));
            Assert.Throws<InvalidOperationException>(() => runtime.ValidateDurableCargo(session, NewCargo, CheckpointFixture.Origin, NewManifest));
            Assert.Throws<InvalidOperationException>(() => runtime.AddDurableCargo(session, NewCargo, CheckpointFixture.Origin, NewManifest));
        });
        Assert.Throws<InvalidOperationException>(() => session.Runtime.ValidateDurableCargo(session, NewCargo, CheckpointFixture.Origin, NewManifest));
        Assert.Throws<InvalidOperationException>(() => session.Runtime.AddDurableCargo(session, NewCargo, CheckpointFixture.Origin, NewManifest));
        Assert.Equal(0, session.ProviderRevision);
        Assert.Throws<KeyNotFoundException>(() => session.Runtime.GetCargo(NewCargo));
        session.ProvisionCargo(NewCargo, CheckpointFixture.Origin, NewManifest);
        AssertNewBatch(session, CheckpointFixture.Origin);
    }

    [Theory]
    [InlineData("unarmed")] [InlineData("clone")] [InlineData("owner")] [InlineData("revision")]
    public void ProvisionProvider_RequiresExactArmedPermission(string denial)
    {
        using var directory = new DurableTestDirectory();
        FlowCheckpoint setup = new CheckpointFixture().Runtime.CaptureCheckpoint();
        using var provider = DurableCargoProvider.Create(directory.Provider, CheckpointFixture.Id(999), setup.NetworkId, setup.Stations);
        object owner = new(); provider.BindAdmissionOwner(owner);
        var intent = new CargoProvisionIntent(NewCargo.Value, CheckpointFixture.Origin.Value, new ManifestCheckpoint("wood", 5));
        byte[] before = File.ReadAllBytes(directory.ProviderPath);
        if (denial != "unarmed") provider.ArmProvision(owner, intent, 0);
        Assert.Throws<InvalidOperationException>(() => provider.ApplyProvision(denial == "owner" ? new object() : owner,
            denial == "clone" ? intent with { } : intent, denial == "revision" ? 1 : 0));
        Assert.Equal(before, File.ReadAllBytes(directory.ProviderPath)); Assert.Null(provider.GetPort(CheckpointFixture.Origin).ReadCargo(NewCargo));
        if (denial == "unarmed") provider.ArmProvision(owner, intent, 0);
        provider.ApplyProvision(owner, intent, 0);
        Assert.Equal(1, provider.Revision); Assert.Equal(1, provider.ConfigurationRevision);
        Assert.Equal(NewManifest, provider.GetPort(CheckpointFixture.Origin).ReadCargo(NewCargo));
        Assert.Empty(provider.CaptureImage().Receipts);
        Assert.Throws<InvalidOperationException>(() => provider.ApplyProvision(owner, intent, 0));
        Assert.Equal(1, provider.Revision);
    }

    [Theory]
    [InlineData("admission", false)] [InlineData("admission", true)]
    [InlineData("registration", false)] [InlineData("registration", true)]
    [InlineData("transfer", false)] [InlineData("transfer", true)]
    public void ProvisionProvider_AllOtherIntentPermissionsAreExclusive(string other, bool provisionFirst)
    {
        using var directory = new DurableTestDirectory();
        FlowCheckpoint setup = new CheckpointFixture().Runtime.CaptureCheckpoint();
        using var provider = DurableCargoProvider.Create(directory.Provider, CheckpointFixture.Id(999), setup.NetworkId, setup.Stations);
        object owner = new(); provider.BindAdmissionOwner(owner);
        var provision = new CargoProvisionIntent(NewCargo.Value, CheckpointFixture.Origin.Value, new ManifestCheckpoint("wood", 5));
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
            else provider.ArmIntent(transfer);
        };
        if (provisionFirst)
        {
            provider.ArmProvision(owner, provision, 0); Assert.Throws<InvalidOperationException>(armOther);
            provider.ApplyProvision(owner, provision, 0);
            Assert.Equal(NewManifest, port.ReadCargo(NewCargo)); Assert.Equal(CheckpointFixture.Manifest, port.ReadCargo(CheckpointFixture.Cargo));
            Assert.True(port.CaptureCheckpoint().AcceptExtractions); Assert.Equal(2, provider.CaptureStations().Length);
            Assert.Empty(provider.CaptureImage().Receipts);
        }
        else
        {
            armOther(); Assert.Throws<InvalidOperationException>(() => provider.ArmProvision(owner, provision, 0));
            if (other == "admission") provider.ApplyAdmission(owner, admission, 0);
            else if (other == "registration") provider.ApplyRegistration(owner, registration, 0);
            else Assert.Equal(PortResult.Applied, port.Apply(transfer));
            Assert.Null(port.ReadCargo(NewCargo));
            Assert.Equal(other == "transfer" ? 1 : 0, provider.CaptureImage().Receipts.Length);
        }
        Assert.Equal(1, provider.Revision);
        Assert.Equal(!provisionFirst && other == "transfer" ? 0 : 1, provider.ConfigurationRevision);
    }

    [Theory]
    [InlineData("new-manifest")] [InlineData("missing-batch")] [InlineData("new-station")]
    [InlineData("old-manifest")] [InlineData("flags")] [InlineData("limit")] [InlineData("extra-batch")]
    public void ProvisionOpen_RejectsEveryUnjustifiedAheadState(string corruption)
    {
        using var directory = new DurableTestDirectory();
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime,
            stage => { if (stage == DurableCommitStage.AfterProvider) throw new ProvisionFailure(); }))
            Assert.Throws<ProvisionFailure>(() => session.ProvisionCargo(NewCargo, CheckpointFixture.Origin, NewManifest));
        DurableProviderImage image = ReadProvider(directory);
        int index = Array.FindIndex(image.Stations, station => station.Id == CheckpointFixture.Origin.Value);
        StationCheckpoint station = image.Stations[index]; PortCheckpoint port = station.Port;
        switch (corruption)
        {
            case "new-manifest": port = port with { Inventory = port.Inventory.Select(item => item.CargoId == NewCargo.Value ? item with { Manifest = new ManifestCheckpoint("wood", 6) } : item).ToArray() }; break;
            case "missing-batch": port = port with { Inventory = port.Inventory.Where(item => item.CargoId != NewCargo.Value).ToArray() }; break;
            case "new-station":
                InventoryCheckpoint cargo = port.Inventory.Single(item => item.CargoId == NewCargo.Value);
                port = port with { Inventory = port.Inventory.Where(item => item.CargoId != NewCargo.Value).ToArray() };
                int destination = Array.FindIndex(image.Stations, item => item.Id == CheckpointFixture.Destination.Value);
                image.Stations[destination] = image.Stations[destination] with { Port = image.Stations[destination].Port with { Inventory = new[] { cargo } } }; break;
            case "old-manifest": port = port with { Inventory = port.Inventory.Select(item => item.CargoId == CheckpointFixture.Cargo.Value ? item with { Manifest = new ManifestCheckpoint("ore", 8) } : item).ToArray() }; break;
            case "flags": port = port with { AcceptExtractions = false }; break;
            case "limit": port = port with { MaxCargoBatches = 99 }; break;
            case "extra-batch": port = port with { Inventory = port.Inventory.Append(new InventoryCheckpoint(CheckpointFixture.Id(9999), new ManifestCheckpoint("stone", 2))).ToArray() }; break;
        }
        image.Stations[index] = station with { Port = port };
        byte[] bytes = DurableProviderCodec.Encode(image), core = File.ReadAllBytes(CorePath(directory));
        File.WriteAllBytes(directory.ProviderPath, bytes);
        Assert.Throws<InvalidDataException>(() => DurableFlowSession.Open(directory.Core, directory.Provider));
        Assert.Equal(bytes, File.ReadAllBytes(directory.ProviderPath)); Assert.Equal(core, File.ReadAllBytes(CorePath(directory)));
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("unknown-station")] [InlineData("core-limit")] [InlineData("quantity")]
    public void ProvisionOpen_RejectsInvalidIntentEvenAtEqualGeneration(string invalid)
    {
        using var directory = new DurableTestDirectory();
        FlowCheckpoint setup = new CheckpointFixture().Runtime.CaptureCheckpoint();
        if (invalid == "core-limit") setup = setup with { Limits = setup.Limits with { MaxParcels = 1 } };
        using (var initial = DurableFlowSession.Create(directory.Core, directory.Provider, FlowRuntime.RestoreCheckpoint(setup))) { }
        var intent = new CargoProvisionIntent(invalid == "duplicate" ? CheckpointFixture.Cargo.Value : NewCargo.Value,
            invalid == "unknown-station" ? CheckpointFixture.Id(999) : CheckpointFixture.Origin.Value,
            new ManifestCheckpoint("wood", invalid == "quantity" ? 1000001 : 5));
        byte[] bytes = DurableCoreCodec.Encode(ReadCore(directory) with { Provision = intent });
        byte[] provider = File.ReadAllBytes(directory.ProviderPath); File.WriteAllBytes(CorePath(directory), bytes);
        Assert.Throws<InvalidDataException>(() => DurableFlowSession.Open(directory.Core, directory.Provider));
        Assert.Equal(bytes, File.ReadAllBytes(CorePath(directory))); Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
    }

    [Theory]
    [InlineData("discriminator")] [InlineData("reserved")]
    [InlineData("length-negative")] [InlineData("length-short")] [InlineData("length-high")]
    [InlineData("empty-cargo")] [InlineData("empty-station")] [InlineData("zero-quantity")]
    [InlineData("invalid-utf8")] [InlineData("blank-key")]
    [InlineData("with-transfer")] [InlineData("with-admission")] [InlineData("with-registration")]
    [InlineData("truncated")] [InlineData("trailing")]
    public void ProvisionCodec_RejectsMalformedV4AfterValidIntegrity(string corruption)
    {
        byte[] bytes = DurableCoreCodec.Encode(new DurableCoreImage(CheckpointFixture.Id(999), 0, null,
            new CheckpointImage(0, new CheckpointFixture().Runtime.CaptureCheckpoint()),
            Provision: new CargoProvisionIntent(NewCargo.Value, CheckpointFixture.Origin.Value, new ManifestCheckpoint("wood", 5))));
        bytes = bytes[..156].Concat(bytes[DurableCoreCodec.HeaderLength..]).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 4);
        switch (corruption)
        {
            case "discriminator": bytes[151] = 2; break;
            case "reserved": bytes[151] = 0; break;
            case "length-negative": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(152), -1); break;
            case "length-short": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(152), 36); break;
            case "length-high": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(152), 1061); break;
            case "empty-cargo": Array.Clear(bytes, 156, 16); break;
            case "empty-station": Array.Clear(bytes, 172, 16); break;
            case "zero-quantity": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(188), 0); break;
            case "invalid-utf8": bytes[192] = 255; break;
            case "blank-key": bytes.AsSpan(192, 4).Fill(32); break;
            case "with-admission": bytes[105] = 1; CheckpointFixture.Origin.Value.TryWriteBytes(bytes.AsSpan(106, 16)); break;
            case "with-registration": bytes[124] = 1; CheckpointFixture.Id(44).TryWriteBytes(bytes.AsSpan(125, 16));
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(141), 17); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(145), 23); break;
            case "with-transfer": bytes[36] = 1; CheckpointFixture.Parcel.Value.TryWriteBytes(bytes.AsSpan(37, 16));
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(53), (int)PortTransferKind.Extract); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(57), 1); break;
            case "truncated": bytes = bytes[..^1]; break;
            case "trailing": bytes = bytes.Concat(new byte[] { 0 }).ToArray(); break;
        }
        Rehash(bytes); Assert.Throws<InvalidDataException>(() => DurableCoreCodec.Decode(bytes));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Provision_LegacyPairReadsWithoutRewriteAndRecoversMixedIntent(int version)
    {
        using var directory = new DurableTestDirectory();
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime)) { }
        byte[] current = File.ReadAllBytes(CorePath(directory));
        int oldHeader = version == 1 ? 97 : version == 2 ? 124 : 151;
        byte[] legacy = current[..oldHeader].Concat(current[DurableCoreCodec.HeaderLength..]).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(legacy.AsSpan(8), version); Rehash(legacy);
        File.WriteAllBytes(CorePath(directory), legacy);
        byte[] provider = File.ReadAllBytes(directory.ProviderPath);
        using (var old = DurableFlowSession.Open(directory.Core, directory.Provider,
            stage => { if (stage == DurableCommitStage.AfterIntent) throw new ProvisionFailure(); }))
        {
            Assert.Equal(legacy, File.ReadAllBytes(CorePath(directory))); Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
            Assert.Throws<ProvisionFailure>(() => old.ProvisionCargo(NewCargo, CheckpointFixture.Origin, NewManifest));
        }
        byte[] intent = File.ReadAllBytes(CorePath(directory)); Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(intent.AsSpan(8)));
        using (var mixed = DurableFlowSession.Open(directory.Core, directory.Provider))
        {
            Assert.Throws<KeyNotFoundException>(() => mixed.Runtime.GetCargo(NewCargo));
            Assert.Equal(intent, File.ReadAllBytes(CorePath(directory))); Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
            mixed.ProvisionCargo(NewCargo, CheckpointFixture.Origin, NewManifest);
        }
        using var final = DurableFlowSession.Open(directory.Core, directory.Provider);
        AssertNewBatch(final, CheckpointFixture.Origin); Assert.Equal(1, final.ProviderRevision);
    }

    [Fact]
    public void Provision_UnicodeManifestFillsLastAllowedSlotThenSurvivesOpenAndDelivery()
    {
        using var directory = new DurableTestDirectory();
        string key = "древесина🪵" + new string('木', 245);
        Assert.Equal(256, key.Length);
        var manifest = new CargoManifest(key, 7);
        FlowCheckpoint setup = new CheckpointFixture().Runtime.CaptureCheckpoint();
        setup = setup with { Limits = setup.Limits with { MaxParcels = 2, MaxCargoUnits = 7 } };
        setup.Stations[0] = setup.Stations[0] with { Port = setup.Stations[0].Port with { MaxCargoBatches = 2 } };
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, FlowRuntime.RestoreCheckpoint(setup)))
        {
            session.ProvisionCargo(NewCargo, CheckpointFixture.Origin, manifest);
            Assert.Equal(2, session.GetPortSnapshot(CheckpointFixture.Origin).Inventory.Length);
            byte[] core = File.ReadAllBytes(CorePath(directory)), provider = File.ReadAllBytes(directory.ProviderPath);
            Assert.Throws<InvalidOperationException>(() => session.ProvisionCargo(new CargoId(CheckpointFixture.Id(9999)), CheckpointFixture.Origin, manifest));
            Assert.Equal(core, File.ReadAllBytes(CorePath(directory))); Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
        }
        using var reopened = DurableFlowSession.Open(directory.Core, directory.Provider);
        Assert.Equal(manifest, reopened.Runtime.GetCargo(NewCargo).Manifest);
        Assert.Equal(manifest.ItemKey, Assert.Single(reopened.GetPortSnapshot(CheckpointFixture.Origin).Inventory, item => item.CargoId == NewCargo.Value).Manifest.ItemKey);
        reopened.Execute(runtime =>
        {
            var shipment = new ShipmentId(CheckpointFixture.Id(44)); var parcel = new ParcelId(CheckpointFixture.Id(44));
            runtime.CreateShipment(shipment, CheckpointFixture.Origin, CheckpointFixture.Destination, manifest, CheckpointFixture.Policy);
            runtime.SplitShipment(shipment, parcel, NewCargo); Assert.True(runtime.TryReserve(parcel));
            Assert.Equal(3, runtime.AdvanceTo(4)); Assert.Equal(ParcelState.Delivered, runtime.GetParcel(parcel).State);
        });
        InventoryCheckpoint delivered = Assert.Single(reopened.GetPortSnapshot(CheckpointFixture.Destination).Inventory);
        Assert.Equal(NewCargo.Value, delivered.CargoId); Assert.Equal(new ManifestCheckpoint(key, 7), delivered.Manifest);
        Assert.Null(reopened.Runtime.GetCargo(NewCargo).ClaimedBy); Assert.Equal(1, reopened.Runtime.GetCargo(NewCargo).DispatchCount);
    }

    [Fact]
    public void ProvisionOpen_IntentCannotRepairMissingBaselineCargoLedger()
    {
        using var directory = new DurableTestDirectory();
        using (var initial = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime)) { }
        DurableCoreImage image = ReadCore(directory);
        FlowCheckpoint baseline = image.Core.Checkpoint with { Cargo = Array.Empty<CargoCheckpoint>() };
        image = image with { Core = image.Core with { Checkpoint = baseline },
            Provision = new CargoProvisionIntent(NewCargo.Value, CheckpointFixture.Origin.Value, new ManifestCheckpoint("wood", 5)) };
        byte[] core = DurableCoreCodec.Encode(image), provider = File.ReadAllBytes(directory.ProviderPath);
        File.WriteAllBytes(CorePath(directory), core);
        Assert.Throws<InvalidDataException>(() => DurableFlowSession.Open(directory.Core, directory.Provider));
        Assert.Equal(core, File.ReadAllBytes(CorePath(directory))); Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
    }

    [Fact]
    public void ProvisionProvider_ReceiptIdentityCannotBeSeededAgainAfterExtraction()
    {
        using var directory = new DurableTestDirectory();
        FlowCheckpoint setup = new CheckpointFixture().Runtime.CaptureCheckpoint();
        using var provider = DurableCargoProvider.Create(directory.Provider, CheckpointFixture.Id(999), setup.NetworkId, setup.Stations);
        object owner = new(); provider.BindAdmissionOwner(owner);
        var authority = new PortAuthority(new FlowLimits());
        ICheckpointCargoPort port = provider.GetPort(CheckpointFixture.Origin); port.Bind(authority, CheckpointFixture.Origin);
        var parcel = new Parcel(CheckpointFixture.Parcel, CheckpointFixture.Shipment, CheckpointFixture.Cargo,
            CheckpointFixture.Manifest, CheckpointFixture.Policy, ParcelState.Created, 0, 0, CheckpointFixture.Origin, null);
        PortTransfer transfer = authority.Issue(parcel, CheckpointFixture.Origin, PortTransferKind.Extract, 1);
        provider.ArmIntent(transfer); Assert.Equal(PortResult.Applied, port.Apply(transfer));
        Assert.Null(port.ReadCargo(CheckpointFixture.Cargo));
        byte[] before = File.ReadAllBytes(directory.ProviderPath);
        var duplicate = new CargoProvisionIntent(CheckpointFixture.Cargo.Value, CheckpointFixture.Destination.Value, new ManifestCheckpoint("wood", 5));
        Assert.Throws<InvalidOperationException>(() => provider.ValidateProvision(duplicate));
        Assert.Throws<InvalidOperationException>(() => provider.ArmProvision(owner, duplicate, 1));
        Assert.Equal(before, File.ReadAllBytes(directory.ProviderPath));
        Assert.Equal(PortResult.Applied, port.ReadResult(transfer)); Assert.Single(provider.CaptureImage().Receipts);
        Assert.Equal(1, provider.Revision); Assert.Equal(0, provider.ConfigurationRevision);
    }

    private static void Rehash(byte[] bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes.AsSpan(0, 65)); hash.AppendData(bytes.AsSpan(97)); hash.GetHashAndReset().CopyTo(bytes, 65);
    }

    private static void Deliver(FlowRuntime runtime, CargoId cargo, int execution, StationId origin, StationId destination, long tick)
    {
        var shipment = new ShipmentId(CheckpointFixture.Id(execution)); var parcel = new ParcelId(CheckpointFixture.Id(execution));
        runtime.CreateShipment(shipment, origin, destination, NewManifest, CheckpointFixture.Policy);
        runtime.SplitShipment(shipment, parcel, cargo); Assert.True(runtime.TryReserve(parcel));
        Assert.Equal(3, runtime.AdvanceTo(tick)); Assert.Equal(ParcelState.Delivered, runtime.GetParcel(parcel).State);
    }

    private static void AssertNewBatch(DurableFlowSession session, StationId station)
    {
        CargoBatch batch = session.Runtime.GetCargo(NewCargo);
        Assert.Equal(NewManifest, batch.Manifest); Assert.Equal(CargoOwner.AtStation(station), batch.Owner);
        Assert.Equal(station, batch.RegistrationStation); Assert.Null(batch.ClaimedBy); Assert.Equal(0, batch.DispatchCount);
        InventoryCheckpoint physical = Assert.Single(session.GetPortSnapshot(station).Inventory, item => item.CargoId == NewCargo.Value);
        Assert.Equal(new ManifestCheckpoint("wood", 5), physical.Manifest);
    }

    private static string CorePath(DurableTestDirectory directory) => Path.Combine(directory.Core, DurableFlowSession.CheckpointFileName);
    private static DurableCoreImage ReadCore(DurableTestDirectory directory) => DurableCoreCodec.Decode(File.ReadAllBytes(CorePath(directory)));
    private static DurableProviderImage ReadProvider(DurableTestDirectory directory) => DurableProviderCodec.Decode(File.ReadAllBytes(directory.ProviderPath));
    private sealed class ProvisionFailure : Exception { }
}
