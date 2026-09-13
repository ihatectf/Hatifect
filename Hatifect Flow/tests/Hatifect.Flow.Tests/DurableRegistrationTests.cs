using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.IO;
using System.Linq;
using Hatifect.Flow.Application.Dispatch;
using Hatifect.Flow.Application.Planning;
using Hatifect.Flow.Domain.Checkpoints;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Policies;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Infrastructure.Persistence;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class DurableRegistrationTests
{
    private static readonly StationId NewStation = new(CheckpointFixture.Id(44));

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Registration_FaultBoundaryRecoversOneEmptyStation(int boundary)
    {
        using var directory = new DurableTestDirectory();
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime,
            stage => { if ((int)stage == boundary) throw new RegistrationFailure(); }))
        {
            Assert.Throws<RegistrationFailure>(() => session.RegisterStation(NewStation, 17, 23, false, true));
            Assert.Throws<ObjectDisposedException>(() => session.RegisterStation(NewStation));
        }
        byte[] core = File.ReadAllBytes(CorePath(directory)), provider = File.ReadAllBytes(directory.ProviderPath);
        bool committed = boundary >= (int)DurableCommitStage.AfterProvider;
        for (int iteration = 0; iteration < 2; iteration++)
        {
            using var restored = DurableFlowSession.Open(directory.Core, directory.Provider);
            DurableProviderImage image = ReadProvider(directory);
            Assert.Equal(committed ? 3 : 2, image.Stations.Length);
            Assert.Equal(committed ? 1 : 0, image.ConfigurationRevision);
            Assert.Equal(committed ? 1 : 0, restored.ProviderRevision);
            Assert.Empty(image.Receipts);
            Assert.Equal(ParcelState.Created, restored.Runtime.GetParcel(CheckpointFixture.Parcel).State);
            if (committed)
            {
                PortCheckpoint port = restored.GetPortSnapshot(NewStation);
                Assert.Equal(17, port.MaxCargoBatches); Assert.Equal(23, port.MaxReceipts);
                Assert.False(port.AcceptDeposits); Assert.True(port.AcceptExtractions);
                Assert.Empty(port.Inventory); Assert.Empty(port.Receipts);
            }
            else Assert.DoesNotContain(image.Stations, station => station.Id == NewStation.Value);
            Assert.Equal(core, File.ReadAllBytes(CorePath(directory)));
            Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
        }
    }

    [Fact]
    public void Registration_NewStationAcceptsExplicitLinkAndExistingCargo()
    {
        using var directory = new DurableTestDirectory();
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime))
        {
            FlowRuntime held = session.Runtime;
            RoutePlan? plan = null;
            session.Execute(runtime => plan = runtime.PlanRoute(CheckpointFixture.Origin, CheckpointFixture.Destination));
            long searches = held.RouteSearchCount;
            session.RegisterStation(NewStation);
            Assert.Same(held, session.Runtime);
            Assert.Equal(1024, session.GetPortSnapshot(NewStation).MaxCargoBatches);
            Assert.Equal(4096, session.GetPortSnapshot(NewStation).MaxReceipts);
            Assert.Empty(session.GetPortSnapshot(NewStation).Inventory);
            session.Execute(runtime =>
            {
                Assert.True(runtime.IsPlanCurrent(plan!));
                Assert.Equal(RouteStatus.Found, runtime.PlanRoute(CheckpointFixture.Origin, CheckpointFixture.Destination).Status);
                Assert.Equal(searches, runtime.RouteSearchCount);
                Assert.Equal(RouteStatus.NoRoute, runtime.PlanRoute(CheckpointFixture.Origin, NewStation).Status);
                Assert.True(runtime.Cancel(CheckpointFixture.Parcel));
                runtime.AddLink(new LinkId(CheckpointFixture.Id(44)), CheckpointFixture.Origin, NewStation, 20, 3);
                var shipment = new ShipmentId(CheckpointFixture.Id(2));
                var parcel = new ParcelId(CheckpointFixture.Id(2));
                runtime.CreateShipment(shipment, CheckpointFixture.Origin, NewStation, CheckpointFixture.Manifest, CheckpointFixture.Policy);
                runtime.SplitShipment(shipment, parcel, CheckpointFixture.Cargo);
                Assert.True(runtime.TryReserve(parcel));
                Assert.Equal(3, runtime.AdvanceTo(4));
                Assert.Equal(ParcelState.Delivered, runtime.GetParcel(parcel).State);
            });
            InventoryCheckpoint cargo = Assert.Single(session.GetPortSnapshot(NewStation).Inventory);
            Assert.Equal(CheckpointFixture.Cargo.Value, cargo.CargoId);
            Assert.Equal(new ManifestCheckpoint("ore", 7), cargo.Manifest);
            Assert.Empty(session.GetPortSnapshot(CheckpointFixture.Origin).Inventory);
        }
        using var restored = DurableFlowSession.Open(directory.Core, directory.Provider);
        Assert.Equal(3, restored.ProviderRevision);
        Assert.Equal(1, ReadProvider(directory).ConfigurationRevision);
        Assert.Single(restored.GetPortSnapshot(NewStation).Inventory);
        Assert.Null(restored.Runtime.GetCargo(CheckpointFixture.Cargo).ClaimedBy);
        Assert.Equal(NewStation, restored.Runtime.GetCargo(CheckpointFixture.Cargo).Owner.Station);
    }

    [Theory]
    [InlineData("empty")] [InlineData("duplicate")]
    [InlineData("batches-zero")] [InlineData("batches-negative")] [InlineData("batches-high")]
    [InlineData("receipts-zero")] [InlineData("receipts-negative")] [InlineData("receipts-high")]
    public void Registration_InvalidRequestsLeavePairAndSessionUnchanged(string invalid)
    {
        using var directory = new DurableTestDirectory();
        using var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime);
        byte[] core = File.ReadAllBytes(CorePath(directory)), provider = File.ReadAllBytes(directory.ProviderPath);
        if (invalid == "empty") Assert.Throws<ArgumentException>(() => session.RegisterStation(default));
        else if (invalid == "duplicate") Assert.Throws<InvalidOperationException>(() => session.RegisterStation(CheckpointFixture.Origin));
        else
        {
            int limit = invalid.EndsWith("zero", StringComparison.Ordinal) ? 0 : invalid.EndsWith("negative", StringComparison.Ordinal) ? -1 : 65537;
            Assert.Throws<ArgumentOutOfRangeException>(() => session.RegisterStation(NewStation,
                invalid.StartsWith("batches", StringComparison.Ordinal) ? limit : 17,
                invalid.StartsWith("receipts", StringComparison.Ordinal) ? limit : 23));
        }
        Assert.Equal(core, File.ReadAllBytes(CorePath(directory)));
        Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
        Assert.Equal(0, session.ProviderRevision);
        session.RegisterStation(NewStation, 1, 65536);
        Assert.Equal(1, session.GetPortSnapshot(NewStation).MaxCargoBatches);
        Assert.Equal(65536, session.GetPortSnapshot(NewStation).MaxReceipts);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Registration_RejectsCoreAndGenerationCapacityBeforeIntent(bool generation)
    {
        using var directory = new DurableTestDirectory();
        FlowCheckpoint checkpoint = new CheckpointFixture().Runtime.CaptureCheckpoint();
        if (!generation) checkpoint = checkpoint with { Limits = checkpoint.Limits with { MaxStations = 2 } };
        using (var created = DurableFlowSession.Create(directory.Core, directory.Provider, FlowRuntime.RestoreCheckpoint(checkpoint))) { }
        if (generation)
        {
            DurableProviderImage provider = ReadProvider(directory) with { Revision = 262144, ConfigurationRevision = 262144 };
            DurableCoreImage core = DurableCoreCodec.Decode(File.ReadAllBytes(CorePath(directory))) with { ProviderRevision = 262144, ProviderConfigurationRevision = 262144 };
            File.WriteAllBytes(directory.ProviderPath, DurableProviderCodec.Encode(provider));
            File.WriteAllBytes(CorePath(directory), DurableCoreCodec.Encode(core));
        }
        using var session = DurableFlowSession.Open(directory.Core, directory.Provider);
        byte[] beforeCore = File.ReadAllBytes(CorePath(directory)), beforeProvider = File.ReadAllBytes(directory.ProviderPath);
        Assert.Throws<InvalidOperationException>(() => session.RegisterStation(NewStation));
        Assert.Equal(beforeCore, File.ReadAllBytes(CorePath(directory)));
        Assert.Equal(beforeProvider, File.ReadAllBytes(directory.ProviderPath));
        Assert.Equal(ParcelState.Created, session.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(generation ? 262144 : 0, session.ProviderRevision);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Registration_CallbackCannotMutateOrReleaseLease(int boundary)
    {
        using var directory = new DurableTestDirectory();
        DurableFlowSession? active = null; FlowRuntime? held = null; bool observed = false;
        using var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime, stage =>
        {
            if ((int)stage != boundary) return;
            Assert.Throws<InvalidOperationException>(() => active!.RegisterStation(new StationId(CheckpointFixture.Id(55))));
            Assert.Throws<InvalidOperationException>(() => active!.SetAdmission(CheckpointFixture.Origin, false, false));
            Assert.Throws<InvalidOperationException>(() => active!.Execute(_ => { }));
            Assert.Throws<InvalidOperationException>(() => active!.Dispose());
            Assert.Throws<InvalidOperationException>(() => held!.Cancel(CheckpointFixture.Parcel));
            Assert.Throws<IOException>(() => DurableFlowSession.Open(directory.Core, directory.Provider));
            observed = true;
        });
        active = session; held = session.Runtime;
        session.RegisterStation(NewStation);
        Assert.True(observed); Assert.Same(held, session.Runtime);
        Assert.Equal(3, ReadProvider(directory).Stations.Length);
        Assert.Equal(ParcelState.Created, held.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(1, session.ProviderRevision);
    }

    [Fact]
    public void Registration_NestedWrongThreadAndDirectCoreMutationRemainDenied()
    {
        using var directory = new DurableTestDirectory();
        using var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime);
        Exception? observed = null;
        var thread = new System.Threading.Thread(() => observed = Record.Exception(() => session.RegisterStation(NewStation)));
        thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(observed);
        session.Execute(runtime =>
        {
            Assert.Throws<InvalidOperationException>(() => session.RegisterStation(NewStation));
            Assert.Throws<InvalidOperationException>(() => runtime.AddStation(NewStation, new InMemoryCargoPort()));
            Assert.Throws<InvalidOperationException>(() => runtime.ValidateDurableStation(session, NewStation));
            Assert.Throws<InvalidOperationException>(() => runtime.AddDurableStation(session, NewStation, new InMemoryCargoPort()));
        });
        Assert.Throws<InvalidOperationException>(() => session.Runtime.ValidateDurableStation(session, NewStation));
        Assert.Throws<InvalidOperationException>(() => session.Runtime.AddDurableStation(session, NewStation, new InMemoryCargoPort()));
        Assert.Equal(2, ReadProvider(directory).Stations.Length);
        session.RegisterStation(NewStation);
        Assert.Equal(3, ReadProvider(directory).Stations.Length);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Registration_CoexistsWithUnreconciledTransfer(bool committed)
    {
        using var directory = new DurableTestDirectory();
        bool armed = false;
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime,
            stage => { if (armed && stage == (committed ? DurableCommitStage.AfterProvider : DurableCommitStage.AfterIntent)) throw new RegistrationFailure(); }))
        {
            session.Execute(runtime => Assert.True(runtime.TryReserve(CheckpointFixture.Parcel))); armed = true;
            Assert.Throws<RegistrationFailure>(() => session.Execute(runtime => runtime.AdvanceTo(1)));
        }
        using (var restored = DurableFlowSession.Open(directory.Core, directory.Provider))
        {
            DurableProviderReceipt[] receipts = ReadProvider(directory).Receipts;
            restored.RegisterStation(NewStation);
            Assert.Equal(receipts, ReadProvider(directory).Receipts);
            Assert.Equal(ParcelState.ExtractionUncertain, restored.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        }
        using var reopened = DurableFlowSession.Open(directory.Core, directory.Provider);
        byte[] provider = File.ReadAllBytes(directory.ProviderPath);
        reopened.Execute(runtime => Assert.True(runtime.ReconcileTransfer(CheckpointFixture.Parcel)));
        Assert.Equal(committed ? ParcelState.InTransit : ParcelState.Cancelled, reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(committed ? 0 : 1, reopened.GetPortSnapshot(CheckpointFixture.Origin).Inventory.Length);
        Assert.Empty(reopened.GetPortSnapshot(NewStation).Inventory);
        Assert.Equal(committed ? 2 : 1, reopened.ProviderRevision);
        Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
    }

    [Theory]
    [InlineData("unarmed")] [InlineData("clone")] [InlineData("owner")] [InlineData("revision")]
    public void RegistrationProvider_RequiresExactArmedPermission(string denial)
    {
        using var directory = new DurableTestDirectory();
        FlowCheckpoint setup = new CheckpointFixture().Runtime.CaptureCheckpoint();
        using var provider = DurableCargoProvider.Create(directory.Provider, CheckpointFixture.Id(999), setup.NetworkId, setup.Stations);
        object owner = new(); provider.BindAdmissionOwner(owner);
        var intent = new StationRegistrationIntent(NewStation.Value, 17, 23, true, false);
        byte[] bytes = File.ReadAllBytes(directory.ProviderPath);
        if (denial != "unarmed") provider.ArmRegistration(owner, intent, 0);
        Assert.Throws<InvalidOperationException>(() => provider.ApplyRegistration(denial == "owner" ? new object() : owner,
            denial == "clone" ? intent with { } : intent, denial == "revision" ? 1 : 0));
        Assert.Equal(bytes, File.ReadAllBytes(directory.ProviderPath)); Assert.Equal(2, provider.CaptureStations().Length);
        if (denial == "unarmed") provider.ArmRegistration(owner, intent, 0);
        provider.ApplyRegistration(owner, intent, 0);
        Assert.Equal(1, provider.Revision); Assert.Equal(1, provider.ConfigurationRevision);
        Assert.Empty(provider.CaptureImage().Receipts);
        Assert.Equal(3, provider.CaptureStations().Length);
        Assert.Empty(provider.GetPort(NewStation).CaptureCheckpoint().Inventory);
        Assert.Throws<InvalidOperationException>(() => provider.ApplyRegistration(owner, intent, 0));
        Assert.Equal(1, provider.Revision);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RegistrationProvider_AdmissionAndRegistrationCannotBeArmedTogether(bool registrationFirst)
    {
        using var directory = new DurableTestDirectory();
        FlowCheckpoint setup = new CheckpointFixture().Runtime.CaptureCheckpoint();
        using var provider = DurableCargoProvider.Create(directory.Provider, CheckpointFixture.Id(999), setup.NetworkId, setup.Stations);
        object owner = new(); provider.BindAdmissionOwner(owner);
        var registration = new StationRegistrationIntent(NewStation.Value, 17, 23, true, false);
        var admission = new AdmissionIntent(CheckpointFixture.Origin.Value, false, false);
        if (registrationFirst)
        {
            provider.ArmRegistration(owner, registration, 0);
            Assert.Throws<InvalidOperationException>(() => provider.ArmAdmission(owner, admission, 0));
            provider.ApplyRegistration(owner, registration, 0);
            Assert.True(provider.GetPort(CheckpointFixture.Origin).CaptureCheckpoint().AcceptExtractions);
        }
        else
        {
            provider.ArmAdmission(owner, admission, 0);
            Assert.Throws<InvalidOperationException>(() => provider.ArmRegistration(owner, registration, 0));
            provider.ApplyAdmission(owner, admission, 0);
            Assert.Equal(2, provider.CaptureStations().Length);
        }
        Assert.Equal(1, provider.Revision); Assert.Empty(provider.CaptureImage().Receipts);
    }

    [Theory]
    [InlineData("flags")] [InlineData("limit")] [InlineData("inventory")]
    [InlineData("extra-station")] [InlineData("new-flags")] [InlineData("new-inventory")]
    public void RegistrationOpen_RejectsUnexpectedAheadState(string corruption)
    {
        using var directory = new DurableTestDirectory();
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime,
            stage => { if (stage == DurableCommitStage.AfterProvider) throw new RegistrationFailure(); }))
            Assert.Throws<RegistrationFailure>(() => session.RegisterStation(NewStation));
        DurableProviderImage image = ReadProvider(directory);
        int index = Array.FindIndex(image.Stations, station => station.Id == (corruption.StartsWith("new-", StringComparison.Ordinal) ? NewStation.Value : CheckpointFixture.Origin.Value));
        StationCheckpoint station = image.Stations[index]; PortCheckpoint port = station.Port;
        switch (corruption)
        {
            case "flags": case "new-flags": port = port with { AcceptExtractions = false }; break;
            case "limit": port = port with { MaxCargoBatches = port.MaxCargoBatches - 1 }; break;
            case "inventory": port = port with { Inventory = new[] { new InventoryCheckpoint(CheckpointFixture.Cargo.Value, new ManifestCheckpoint("ore", 8)) } }; break;
            case "new-inventory": port = port with { Inventory = new[] { new InventoryCheckpoint(CheckpointFixture.Id(9999), new ManifestCheckpoint("ore", 7)) } }; break;
            case "extra-station": image = image with { Stations = image.Stations.Append(new StationCheckpoint(CheckpointFixture.Id(55),
                new PortCheckpoint(17, 23, true, true, Array.Empty<InventoryCheckpoint>(), Array.Empty<ReceiptCheckpoint>()))).ToArray() }; break;
        }
        image.Stations[index] = station with { Port = port };
        byte[] bytes = DurableProviderCodec.Encode(image), core = File.ReadAllBytes(CorePath(directory));
        File.WriteAllBytes(directory.ProviderPath, bytes);
        Assert.Throws<InvalidDataException>(() => DurableFlowSession.Open(directory.Core, directory.Provider));
        Assert.Equal(bytes, File.ReadAllBytes(directory.ProviderPath)); Assert.Equal(core, File.ReadAllBytes(CorePath(directory)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RegistrationOpen_RejectsExistingOrOverLimitIntentAtEqualRevision(bool overLimit)
    {
        using var directory = new DurableTestDirectory();
        FlowCheckpoint setup = new CheckpointFixture().Runtime.CaptureCheckpoint();
        if (overLimit) setup = setup with { Limits = setup.Limits with { MaxStations = 2 } };
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, FlowRuntime.RestoreCheckpoint(setup))) { }
        DurableCoreImage core = DurableCoreCodec.Decode(File.ReadAllBytes(CorePath(directory))) with
        { Registration = new StationRegistrationIntent(overLimit ? NewStation.Value : CheckpointFixture.Origin.Value, 17, 23, true, false) };
        byte[] bytes = DurableCoreCodec.Encode(core); File.WriteAllBytes(CorePath(directory), bytes);
        Assert.Throws<InvalidDataException>(() => DurableFlowSession.Open(directory.Core, directory.Provider));
        Assert.Equal(bytes, File.ReadAllBytes(CorePath(directory)));
    }

    [Theory]
    [InlineData("discriminator")] [InlineData("reserved")] [InlineData("deposit-bool")] [InlineData("extract-bool")]
    [InlineData("empty-id")] [InlineData("batches-zero")] [InlineData("batches-high")]
    [InlineData("receipts-zero")] [InlineData("receipts-high")] [InlineData("with-admission")] [InlineData("with-transfer")]
    [InlineData("truncated")] [InlineData("trailing")]
    public void RegistrationCodec_RejectsMalformedV3ExtensionAfterValidIntegrity(string corruption)
    {
        byte[] bytes = DurableCoreCodec.Encode(new DurableCoreImage(CheckpointFixture.Id(999), 0, null,
            new CheckpointImage(0, new CheckpointFixture().Runtime.CaptureCheckpoint()),
            Registration: new StationRegistrationIntent(NewStation.Value, 17, 23, true, false)));
        switch (corruption)
        {
            case "discriminator": bytes[124] = 2; break;
            case "reserved": bytes[124] = 0; break;
            case "deposit-bool": bytes[149] = 2; break;
            case "extract-bool": bytes[150] = 2; break;
            case "empty-id": Array.Clear(bytes, 125, 16); break;
            case "batches-zero": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(141), 0); break;
            case "batches-high": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(141), 65537); break;
            case "receipts-zero": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(145), 0); break;
            case "receipts-high": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(145), 65537); break;
            case "with-admission": bytes[105] = 1; CheckpointFixture.Origin.Value.TryWriteBytes(bytes.AsSpan(106, 16)); break;
            case "with-transfer": bytes[36] = 1; CheckpointFixture.Parcel.Value.TryWriteBytes(bytes.AsSpan(37, 16));
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(53), (int)PortTransferKind.Extract);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(57), 1); break;
            case "truncated": bytes = bytes[..^1]; break;
            case "trailing": bytes = bytes.Concat(new byte[] { 0 }).ToArray(); break;
        }
        Rehash(bytes);
        Assert.Throws<InvalidDataException>(() => DurableCoreCodec.Decode(bytes));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Registration_LegacyV2PairRecoversMixedIntentWithoutReadRewrite(bool withConfiguration)
    {
        using var directory = new DurableTestDirectory();
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime))
            if (withConfiguration) session.SetAdmission(CheckpointFixture.Destination, false, true);
        byte[] v3 = File.ReadAllBytes(CorePath(directory));
        byte[] v2 = v3[..124].Concat(v3[DurableCoreCodec.HeaderLength..]).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(v2.AsSpan(8), 2); Rehash(v2);
        File.WriteAllBytes(CorePath(directory), v2);
        byte[] provider = File.ReadAllBytes(directory.ProviderPath);
        using (var legacy = DurableFlowSession.Open(directory.Core, directory.Provider, stage =>
            { if (stage == DurableCommitStage.AfterIntent) throw new RegistrationFailure(); }))
        {
            Assert.Equal(v2, File.ReadAllBytes(CorePath(directory)));
            Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
            Assert.Throws<RegistrationFailure>(() => legacy.RegisterStation(NewStation));
        }
        byte[] mixed = File.ReadAllBytes(CorePath(directory));
        using (var reopened = DurableFlowSession.Open(directory.Core, directory.Provider))
        {
            Assert.Equal(2, ReadProvider(directory).Stations.Length);
            Assert.Equal(mixed, File.ReadAllBytes(CorePath(directory)));
            Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
            reopened.RegisterStation(NewStation);
        }
        using var final = DurableFlowSession.Open(directory.Core, directory.Provider);
        Assert.Equal(3, ReadProvider(directory).Stations.Length);
        Assert.Equal(withConfiguration ? 2 : 1, final.ProviderRevision);
        Assert.Equal(!withConfiguration, final.GetPortSnapshot(CheckpointFixture.Destination).AcceptDeposits);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RegistrationProvider_TransferAndRegistrationPermissionsAreExclusive(bool registrationFirst)
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
        var registration = new StationRegistrationIntent(NewStation.Value, 17, 23, true, true);
        if (registrationFirst)
        {
            provider.ArmRegistration(owner, registration, 0);
            Assert.Throws<InvalidOperationException>(() => provider.ArmIntent(transfer));
            provider.ApplyRegistration(owner, registration, 0);
            Assert.Equal(CheckpointFixture.Manifest, port.ReadCargo(CheckpointFixture.Cargo));
            Assert.Empty(provider.CaptureImage().Receipts);
        }
        else
        {
            provider.ArmIntent(transfer);
            Assert.Throws<InvalidOperationException>(() => provider.ArmRegistration(owner, registration, 0));
            Assert.Equal(PortResult.Applied, port.Apply(transfer));
            Assert.Null(port.ReadCargo(CheckpointFixture.Cargo));
            Assert.Equal(2, provider.CaptureStations().Length);
        }
        Assert.Equal(1, provider.Revision);
        Assert.Equal(registrationFirst ? 1 : 0, provider.ConfigurationRevision);
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MaxValue - 1)]
    public void Registration_CoreRevisionExhaustionRejectsBeforePublication(long revision)
    {
        using var directory = new DurableTestDirectory();
        using (var initial = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime)) { }
        DurableCoreImage image = DurableCoreCodec.Decode(File.ReadAllBytes(CorePath(directory)));
        image = image with { Core = image.Core with { Revision = revision } };
        byte[] core = DurableCoreCodec.Encode(image), provider = File.ReadAllBytes(directory.ProviderPath);
        File.WriteAllBytes(CorePath(directory), core);
        int callbacks = 0;
        using var session = DurableFlowSession.Open(directory.Core, directory.Provider, _ => callbacks++);
        Assert.Throws<OverflowException>(() => session.RegisterStation(NewStation));
        Assert.Equal(0, callbacks);
        Assert.Equal(core, File.ReadAllBytes(CorePath(directory)));
        Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
        Assert.Equal(revision, session.Revision);
        Assert.Equal(0, session.ProviderRevision);
        Assert.Equal(ParcelState.Created, session.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(2, ReadProvider(directory).Stations.Length);
    }

    private static void Rehash(byte[] bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes.AsSpan(0, 65)); hash.AppendData(bytes.AsSpan(97));
        hash.GetHashAndReset().CopyTo(bytes, 65);
    }

    private static string CorePath(DurableTestDirectory directory) => Path.Combine(directory.Core, DurableFlowSession.CheckpointFileName);
    private static DurableProviderImage ReadProvider(DurableTestDirectory directory) => DurableProviderCodec.Decode(File.ReadAllBytes(directory.ProviderPath));
    private sealed class RegistrationFailure : Exception { }
}
