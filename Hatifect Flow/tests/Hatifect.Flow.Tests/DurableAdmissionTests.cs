using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.IO;
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
public sealed class DurableAdmissionTests
{
    public static IEnumerable<object[]> Boundaries()
    {
        foreach (DurableCommitStage stage in Enum.GetValues<DurableCommitStage>())
            foreach (bool enabling in new[] { false, true })
                yield return new object[] { (int)stage, enabling };
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void Admission_FaultBoundaryRecoversOneCompleteConfiguration(int boundary, bool enabling)
    {
        using var directory = new DurableTestDirectory();
        var fixture = new CheckpointFixture();
        fixture.DestinationPort.Inner.AcceptDeposits = !enabling;
        fixture.DestinationPort.Inner.AcceptExtractions = !enabling;
        var stage = (DurableCommitStage)boundary;
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, fixture.Runtime,
            reached => { if (reached == stage) throw new AdmissionFailure(); }))
        {
            Assert.Throws<AdmissionFailure>(() => session.SetAdmission(CheckpointFixture.Destination, enabling, enabling));
            Assert.Throws<ObjectDisposedException>(() => session.SetAdmission(CheckpointFixture.Destination, !enabling, !enabling));
        }
        byte[] core = File.ReadAllBytes(CorePath(directory));
        byte[] provider = File.ReadAllBytes(directory.ProviderPath);
        bool committed = stage >= DurableCommitStage.AfterProvider;
        for (int reopen = 0; reopen < 2; reopen++)
        {
            using var session = DurableFlowSession.Open(directory.Core, directory.Provider);
            PortCheckpoint destination = session.GetPortSnapshot(CheckpointFixture.Destination);
            Assert.Equal(committed ? enabling : !enabling, destination.AcceptDeposits);
            Assert.Equal(committed ? enabling : !enabling, destination.AcceptExtractions);
            Assert.Equal(committed ? 1 : 0, session.ProviderRevision);
            Assert.Empty(destination.Inventory);
            Assert.Empty(destination.Receipts);
            Assert.Equal(CheckpointFixture.Cargo.Value, Assert.Single(session.GetPortSnapshot(CheckpointFixture.Origin).Inventory).CargoId);
            Assert.Equal(ParcelState.Created, session.Runtime.GetParcel(CheckpointFixture.Parcel).State);
            Assert.Equal(core, File.ReadAllBytes(CorePath(directory)));
            Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
        }
    }

    [Fact]
    public void Admission_ChangesOnlyFlagsAndRequiresExplicitDeliveryRetry()
    {
        using var directory = new DurableTestDirectory();
        var fixture = new CheckpointFixture();
        fixture.DestinationPort.Inner.AcceptDeposits = false;
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, fixture.Runtime))
        {
            session.Execute(runtime => { Assert.True(runtime.TryReserve(CheckpointFixture.Parcel)); Assert.Equal(3, runtime.AdvanceTo(4)); });
            Assert.Equal(ParcelState.DeliveryRejected, session.Runtime.GetParcel(CheckpointFixture.Parcel).State);
            DurableProviderImage before = ReadProvider(directory);
            session.SetAdmission(CheckpointFixture.Destination, true, false);
            DurableProviderImage after = ReadProvider(directory);
            Assert.Equal(before.Revision + 1, after.Revision);
            Assert.Equal(before.Receipts, after.Receipts);
            Assert.Equal(before.Stations.SelectMany(s => s.Port.Inventory), after.Stations.SelectMany(s => s.Port.Inventory));
            Assert.Equal(ParcelState.DeliveryRejected, session.Runtime.GetParcel(CheckpointFixture.Parcel).State);
            Assert.Equal(0, session.Runtime.PendingOperationCount);
            Assert.Empty(session.GetPortSnapshot(CheckpointFixture.Destination).Inventory);
            Assert.Equal((int)PortResult.Rejected, Assert.Single(session.GetPortSnapshot(CheckpointFixture.Destination).Receipts).Result);
        }
        using var reopened = DurableFlowSession.Open(directory.Core, directory.Provider);
        Assert.True(reopened.GetPortSnapshot(CheckpointFixture.Destination).AcceptDeposits);
        Assert.False(reopened.GetPortSnapshot(CheckpointFixture.Destination).AcceptExtractions);
        Assert.Equal(ParcelState.DeliveryRejected, reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        reopened.Execute(runtime => { Assert.True(runtime.RetryDelivery(CheckpointFixture.Parcel)); Assert.Equal(1, runtime.AdvanceTo(runtime.Now + 1)); });
        Assert.Equal(ParcelState.Delivered, reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(4, reopened.ProviderRevision);
        ReceiptCheckpoint[] receipts = reopened.GetPortSnapshot(CheckpointFixture.Destination).Receipts;
        Assert.Equal(new[] { (int)PortResult.Rejected, (int)PortResult.Applied }, receipts.Select(r => r.Result));
        InventoryCheckpoint cargo = Assert.Single(reopened.GetPortSnapshot(CheckpointFixture.Destination).Inventory);
        Assert.Equal(CheckpointFixture.Cargo.Value, cargo.CargoId);
        Assert.Equal(new ManifestCheckpoint("ore", 7), cargo.Manifest);
        Assert.Empty(reopened.GetPortSnapshot(CheckpointFixture.Origin).Inventory);
        Assert.Null(reopened.Runtime.GetCargo(CheckpointFixture.Cargo).ClaimedBy);
    }

    [Fact]
    public void Admission_NoOpLeavesBothFilesAndRevisionsUnchanged()
    {
        using var directory = new DurableTestDirectory();
        int callbacks = 0;
        using var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime, _ => callbacks++);
        byte[] core = File.ReadAllBytes(CorePath(directory)), provider = File.ReadAllBytes(directory.ProviderPath);
        long revision = session.Revision;
        session.SetAdmission(CheckpointFixture.Destination, true, true);
        Assert.Equal(0, callbacks);
        Assert.Equal(revision, session.Revision);
        Assert.Equal(0, session.ProviderRevision);
        Assert.Equal(core, File.ReadAllBytes(CorePath(directory)));
        Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Admission_CallbackAndNestedCommandsCannotMutateOrReleaseLease(int boundary)
    {
        using var directory = new DurableTestDirectory();
        DurableFlowSession? active = null;
        FlowRuntime? held = null;
        bool observed = false;
        using var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime, stage =>
        {
            if ((int)stage != boundary) return;
            Assert.Throws<InvalidOperationException>(() => active!.SetAdmission(CheckpointFixture.Origin, false, false));
            Assert.Throws<InvalidOperationException>(() => active!.Execute(_ => { }));
            Assert.Throws<InvalidOperationException>(() => active!.Dispose());
            Assert.Throws<InvalidOperationException>(() => held!.Cancel(CheckpointFixture.Parcel));
            Assert.Throws<IOException>(() => DurableFlowSession.Open(directory.Core, directory.Provider));
            observed = true;
        });
        active = session; held = session.Runtime;
        session.SetAdmission(CheckpointFixture.Destination, false, true);
        Assert.True(observed);
        Assert.Equal(ParcelState.Created, held.GetParcel(CheckpointFixture.Parcel).State);
        Assert.True(session.GetPortSnapshot(CheckpointFixture.Origin).AcceptDeposits);
        Assert.False(session.GetPortSnapshot(CheckpointFixture.Destination).AcceptDeposits);
        Assert.Equal(1, session.ProviderRevision);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Admission_CoexistsWithUnreconciledTransferWithoutReapply(bool accepted)
    {
        using var directory = new DurableTestDirectory();
        var fixture = new CheckpointFixture();
        fixture.DestinationPort.Inner.AcceptDeposits = accepted;
        bool armed = false;
        using (var first = DurableFlowSession.Create(directory.Core, directory.Provider, fixture.Runtime, stage =>
            { if (armed && stage == DurableCommitStage.AfterProvider) throw new AdmissionFailure(); }))
        {
            first.Execute(runtime => { Assert.True(runtime.TryReserve(CheckpointFixture.Parcel)); Assert.Equal(2, runtime.AdvanceTo(4, 2)); });
            armed = true;
            Assert.Throws<AdmissionFailure>(() => first.Execute(runtime => runtime.AdvanceTo(4)));
        }
        using (var second = DurableFlowSession.Open(directory.Core, directory.Provider))
        {
            Assert.Equal(ParcelState.DeliveryUncertain, second.Runtime.GetParcel(CheckpointFixture.Parcel).State);
            DurableProviderReceipt[] receipts = ReadProvider(directory).Receipts;
            second.SetAdmission(CheckpointFixture.Destination, !accepted, false);
            Assert.Equal(receipts, ReadProvider(directory).Receipts);
            Assert.Equal(ParcelState.DeliveryUncertain, second.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        }
        using var third = DurableFlowSession.Open(directory.Core, directory.Provider);
        byte[] provider = File.ReadAllBytes(directory.ProviderPath);
        third.Execute(runtime => Assert.True(runtime.ReconcileTransfer(CheckpointFixture.Parcel)));
        Assert.Equal(accepted ? ParcelState.Delivered : ParcelState.DeliveryRejected, third.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(accepted ? 1 : 0, third.GetPortSnapshot(CheckpointFixture.Destination).Inventory.Length);
        Assert.Equal(3, third.ProviderRevision);
        Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
    }

    [Fact]
    public void Admission_InvalidStationNestedAndWrongThreadLeaveSessionUsable()
    {
        using var directory = new DurableTestDirectory();
        var fixture = new CheckpointFixture();
        using var session = DurableFlowSession.Create(directory.Core, directory.Provider, fixture.Runtime);
        byte[] core = File.ReadAllBytes(CorePath(directory)), provider = File.ReadAllBytes(directory.ProviderPath);
        Assert.Throws<ArgumentException>(() => session.SetAdmission(default, false, false));
        Assert.Throws<ArgumentException>(() => session.SetAdmission(new StationId(CheckpointFixture.Id(777)), false, false));
        Exception? observed = null;
        var thread = new System.Threading.Thread(() => observed = Record.Exception(() => session.SetAdmission(CheckpointFixture.Origin, false, false)));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(observed);
        Assert.Equal(core, File.ReadAllBytes(CorePath(directory)));
        Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
        session.Execute(runtime =>
        {
            Assert.Throws<InvalidOperationException>(() => session.SetAdmission(CheckpointFixture.Destination, false, false));
            Assert.True(runtime.TryReserve(CheckpointFixture.Parcel));
        });
        fixture.DestinationPort.Inner.AcceptDeposits = false;
        session.Execute(runtime => Assert.Equal(3, runtime.AdvanceTo(4)));
        Assert.Equal(ParcelState.Delivered, session.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Single(session.GetPortSnapshot(CheckpointFixture.Destination).Inventory);
    }

    [Theory]
    [InlineData("unarmed")] [InlineData("clone")] [InlineData("owner")] [InlineData("revision")]
    public void AdmissionProvider_RequiresExactArmedPermission(string denial)
    {
        using var directory = new DurableTestDirectory();
        FlowCheckpoint setup = new CheckpointFixture().Runtime.CaptureCheckpoint();
        using var provider = DurableCargoProvider.Create(directory.Provider, CheckpointFixture.Id(999), setup.NetworkId, setup.Stations);
        object owner = new();
        provider.BindAdmissionOwner(owner);
        var intent = new AdmissionIntent(CheckpointFixture.Destination.Value, false, false);
        byte[] bytes = File.ReadAllBytes(directory.ProviderPath);
        if (denial != "unarmed") provider.ArmAdmission(owner, intent, 0);
        Assert.Throws<InvalidOperationException>(() => provider.ApplyAdmission(denial == "owner" ? new object() : owner,
            denial == "clone" ? intent with { } : intent, denial == "revision" ? 1 : 0));
        Assert.Equal(bytes, File.ReadAllBytes(directory.ProviderPath));
        Assert.Equal(0, provider.Revision);
        if (denial == "unarmed") provider.ArmAdmission(owner, intent, 0);
        provider.ApplyAdmission(owner, intent, 0);
        Assert.Equal(1, provider.ConfigurationRevision);
        Assert.Empty(provider.CaptureImage().Receipts);
        Assert.Throws<InvalidOperationException>(() => provider.ApplyAdmission(owner, intent, 0));
        Assert.False(provider.CaptureStations().Single(s => s.Id == intent.StationId).Port.AcceptDeposits);
        Assert.Equal(1, provider.Revision);
    }

    [Theory]
    [InlineData(-1, 0)] [InlineData(1, 0)] [InlineData(0, 1)] [InlineData(262145, 262145)]
    public void AdmissionCodec_RejectsInvalidConfigurationRevision(long configuration, long revision)
    {
        FlowCheckpoint setup = new CheckpointFixture().Runtime.CaptureCheckpoint();
        var image = new DurableProviderImage(CheckpointFixture.Id(999), setup.NetworkId, revision,
            setup.Stations, Array.Empty<DurableProviderReceipt>(), configuration);
        Assert.Throws<InvalidDataException>(() => DurableProviderCodec.Encode(image));
    }

    [Fact]
    public void Admission_NoOpAtGenerationLimitDoesNotWriteButChangeIsRejected()
    {
        using var directory = new DurableTestDirectory();
        using (var initial = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime)) { }
        DurableProviderImage provider = ReadProvider(directory) with { Revision = 262144, ConfigurationRevision = 262144 };
        DurableCoreImage core = DurableCoreCodec.Decode(File.ReadAllBytes(CorePath(directory))) with
            { ProviderRevision = 262144, ProviderConfigurationRevision = 262144 };
        byte[] providerBytes = DurableProviderCodec.Encode(provider), coreBytes = DurableCoreCodec.Encode(core);
        File.WriteAllBytes(directory.ProviderPath, providerBytes); File.WriteAllBytes(CorePath(directory), coreBytes);
        using var session = DurableFlowSession.Open(directory.Core, directory.Provider);
        session.SetAdmission(CheckpointFixture.Destination, true, true);
        Assert.Throws<InvalidOperationException>(() => session.SetAdmission(CheckpointFixture.Destination, false, true));
        Assert.Equal(262144, session.ProviderRevision);
        Assert.Equal(coreBytes, File.ReadAllBytes(CorePath(directory)));
        Assert.Equal(providerBytes, File.ReadAllBytes(directory.ProviderPath));
    }

    [Theory]
    [InlineData("station")] [InlineData("no-op")] [InlineData("both-intents")]
    public void AdmissionOpen_RejectsInvalidIntentAtEqualRevision(string corruption)
    {
        using var directory = new DurableTestDirectory();
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime)) { }
        DurableCoreImage core = DurableCoreCodec.Decode(File.ReadAllBytes(CorePath(directory)));
        core = core with { Admission = new AdmissionIntent(corruption == "station" ? CheckpointFixture.Id(999) : CheckpointFixture.Destination.Value,
            corruption == "no-op", true) };
        if (corruption == "both-intents") core = core with { Intent = new TransferKey(CheckpointFixture.Parcel.Value, (int)PortTransferKind.Extract, 1) };
        if (corruption == "both-intents")
        {
            Assert.Throws<InvalidDataException>(() => DurableCoreCodec.Encode(core));
            return;
        }
        byte[] bytes = DurableCoreCodec.Encode(core);
        File.WriteAllBytes(CorePath(directory), bytes);
        Assert.Throws<InvalidDataException>(() => DurableFlowSession.Open(directory.Core, directory.Provider));
        Assert.Equal(bytes, File.ReadAllBytes(CorePath(directory)));
    }

    [Theory]
    [InlineData("other-flags")] [InlineData("limit")] [InlineData("inventory")] [InlineData("configuration-counter")]
    public void AdmissionOpen_RejectsUnexpectedAheadChangesWithoutRewrite(string corruption)
    {
        using var directory = new DurableTestDirectory();
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime,
            stage => { if (stage == DurableCommitStage.AfterProvider) throw new AdmissionFailure(); }))
            Assert.Throws<AdmissionFailure>(() => session.SetAdmission(CheckpointFixture.Destination, false, false));
        DurableProviderImage provider = ReadProvider(directory);
        int origin = Array.FindIndex(provider.Stations, s => s.Id == CheckpointFixture.Origin.Value);
        PortCheckpoint port = provider.Stations[origin].Port;
        switch (corruption)
        {
            case "other-flags": port = port with { AcceptExtractions = false }; break;
            case "limit": port = port with { MaxReceipts = port.MaxReceipts - 1 }; break;
            case "inventory": port = port with { Inventory = new[] { new InventoryCheckpoint(CheckpointFixture.Cargo.Value, new ManifestCheckpoint("ore", 8)) } }; break;
            case "configuration-counter":
                DurableCoreImage core = DurableCoreCodec.Decode(File.ReadAllBytes(CorePath(directory))) with { ProviderConfigurationRevision = 1 };
                Assert.Throws<InvalidDataException>(() => DurableCoreCodec.Encode(core));
                return;
        }
        provider.Stations[origin] = provider.Stations[origin] with { Port = port };
        byte[] bytes = DurableProviderCodec.Encode(provider), oldCore = File.ReadAllBytes(CorePath(directory));
        File.WriteAllBytes(directory.ProviderPath, bytes);
        Assert.Throws<InvalidDataException>(() => DurableFlowSession.Open(directory.Core, directory.Provider));
        Assert.Equal(bytes, File.ReadAllBytes(directory.ProviderPath));
        Assert.Equal(oldCore, File.ReadAllBytes(CorePath(directory)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Admission_LegacyPairOpensReadOnlyAndRecoversMixedVersionIntent(bool receiptPresent)
    {
        using var directory = new DurableTestDirectory();
        using (var initial = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime))
        {
            if (receiptPresent) initial.Execute(runtime => { Assert.True(runtime.TryReserve(CheckpointFixture.Parcel)); Assert.Equal(1, runtime.AdvanceTo(1)); });
        }
        byte[] providerV2 = File.ReadAllBytes(directory.ProviderPath);
        byte[] providerV1 = providerV2[..^8];
        BinaryPrimitives.WriteInt32LittleEndian(providerV1.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(providerV1.AsSpan(20), providerV1.Length - 56);
        WriteHash(providerV1, 24, 56);
        byte[] coreV2 = File.ReadAllBytes(CorePath(directory));
        byte[] coreV1 = coreV2[..97].Concat(coreV2[DurableCoreCodec.HeaderLength..]).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(coreV1.AsSpan(8), 1);
        WriteHash(coreV1, 65, 97);
        File.WriteAllBytes(CorePath(directory), coreV1); File.WriteAllBytes(directory.ProviderPath, providerV1);
        using (var legacy = DurableFlowSession.Open(directory.Core, directory.Provider, stage =>
            { if (stage == DurableCommitStage.AfterIntent) throw new AdmissionFailure(); }))
        {
            Assert.Equal(coreV1, File.ReadAllBytes(CorePath(directory)));
            Assert.Equal(providerV1, File.ReadAllBytes(directory.ProviderPath));
            Assert.Equal(receiptPresent ? ParcelState.InTransit : ParcelState.Created, legacy.Runtime.GetParcel(CheckpointFixture.Parcel).State);
            Assert.Throws<AdmissionFailure>(() => legacy.SetAdmission(CheckpointFixture.Destination, false, true));
        }
        byte[] mixedCore = File.ReadAllBytes(CorePath(directory));
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(mixedCore.AsSpan(8)));
        using (var mixed = DurableFlowSession.Open(directory.Core, directory.Provider))
        {
            Assert.True(mixed.GetPortSnapshot(CheckpointFixture.Destination).AcceptDeposits);
            Assert.Equal(mixedCore, File.ReadAllBytes(CorePath(directory)));
            Assert.Equal(providerV1, File.ReadAllBytes(directory.ProviderPath));
            mixed.SetAdmission(CheckpointFixture.Destination, false, true);
        }
        using var upgraded = DurableFlowSession.Open(directory.Core, directory.Provider);
        Assert.False(upgraded.GetPortSnapshot(CheckpointFixture.Destination).AcceptDeposits);
        Assert.Equal(receiptPresent ? 2 : 1, upgraded.ProviderRevision);
        Assert.Equal(receiptPresent ? 1 : 0, ReadProvider(directory).Receipts.Length);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Admission_ExtractionUsesNewFlagsAndFaultRecoveryRetainsConfiguration(bool enabled)
    {
        using var directory = new DurableTestDirectory();
        bool armed = false;
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime, stage =>
            { if (armed && stage == DurableCommitStage.AfterProvider) throw new AdmissionFailure(); }))
        {
            session.SetAdmission(CheckpointFixture.Origin, false, enabled);
            session.Execute(runtime => Assert.True(runtime.TryReserve(CheckpointFixture.Parcel)));
            armed = true;
            Assert.Throws<AdmissionFailure>(() => session.Execute(runtime => runtime.AdvanceTo(1)));
        }
        using var recovered = DurableFlowSession.Open(directory.Core, directory.Provider);
        Assert.False(recovered.GetPortSnapshot(CheckpointFixture.Origin).AcceptDeposits);
        Assert.Equal(enabled, recovered.GetPortSnapshot(CheckpointFixture.Origin).AcceptExtractions);
        Assert.Equal(1, ReadProvider(directory).ConfigurationRevision);
        Assert.Equal(2, recovered.ProviderRevision);
        byte[] bytes = File.ReadAllBytes(directory.ProviderPath);
        recovered.Execute(runtime => Assert.True(runtime.ReconcileTransfer(CheckpointFixture.Parcel)));
        Assert.Equal(enabled ? ParcelState.InTransit : ParcelState.Cancelled, recovered.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(enabled ? 0 : 1, recovered.GetPortSnapshot(CheckpointFixture.Origin).Inventory.Length);
        Assert.Equal(bytes, File.ReadAllBytes(directory.ProviderPath));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AdmissionOpen_RejectsValidlyEncodedConfigurationCounterMismatch(bool pendingAdmission)
    {
        using var directory = new DurableTestDirectory();
        bool armed = false;
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime, stage =>
            { if (armed && stage == DurableCommitStage.AfterProvider) throw new AdmissionFailure(); }))
        {
            session.Execute(runtime => { Assert.True(runtime.TryReserve(CheckpointFixture.Parcel)); runtime.AdvanceTo(1); });
            if (pendingAdmission)
            {
                armed = true;
                Assert.Throws<AdmissionFailure>(() => session.SetAdmission(CheckpointFixture.Destination, false, true));
            }
        }
        DurableCoreImage core = DurableCoreCodec.Decode(File.ReadAllBytes(CorePath(directory))) with { ProviderConfigurationRevision = 1 };
        byte[] bytes = DurableCoreCodec.Encode(core), provider = File.ReadAllBytes(directory.ProviderPath);
        File.WriteAllBytes(CorePath(directory), bytes);
        Assert.Throws<InvalidDataException>(() => DurableFlowSession.Open(directory.Core, directory.Provider));
        Assert.Equal(bytes, File.ReadAllBytes(CorePath(directory)));
        Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
    }

    [Theory]
    [InlineData("discriminator")] [InlineData("reserved")] [InlineData("deposit-boolean")]
    [InlineData("extraction-boolean")] [InlineData("empty-station")] [InlineData("negative-counter")]
    [InlineData("ahead-counter")] [InlineData("truncated")] [InlineData("trailing")]
    public void AdmissionCoreCodec_RejectsMalformedExtensionAfterValidIntegrity(string corruption)
    {
        var core = new DurableCoreImage(CheckpointFixture.Id(999), 0, null,
            new CheckpointImage(0, new CheckpointFixture().Runtime.CaptureCheckpoint()),
            new AdmissionIntent(CheckpointFixture.Destination.Value, false, true));
        byte[] bytes = DurableCoreCodec.Encode(core);
        switch (corruption)
        {
            case "discriminator": bytes[105] = 2; break;
            case "reserved": bytes[105] = 0; break;
            case "deposit-boolean": bytes[122] = 2; break;
            case "extraction-boolean": bytes[123] = 2; break;
            case "empty-station": Array.Clear(bytes, 106, 16); break;
            case "negative-counter": BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(97), -1); break;
            case "ahead-counter": BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(97), 1); break;
            case "truncated": bytes = bytes[..^1]; break;
            case "trailing": bytes = bytes.Concat(new byte[] { 0 }).ToArray(); break;
        }
        WriteHash(bytes, 65, 97);
        Assert.Throws<InvalidDataException>(() => DurableCoreCodec.Decode(bytes));
    }

    [Theory]
    [InlineData("negative-counter")] [InlineData("ahead-counter")]
    [InlineData("truncated-counter")] [InlineData("trailing")]
    public void AdmissionProviderCodec_RejectsMalformedExtensionAfterValidIntegrity(string corruption)
    {
        FlowCheckpoint setup = new CheckpointFixture().Runtime.CaptureCheckpoint();
        byte[] bytes = DurableProviderCodec.Encode(new DurableProviderImage(CheckpointFixture.Id(999), setup.NetworkId, 0,
            setup.Stations, Array.Empty<DurableProviderReceipt>()));
        switch (corruption)
        {
            case "negative-counter": BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(bytes.Length - 8), -1); break;
            case "ahead-counter": BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(bytes.Length - 8), 1); break;
            case "truncated-counter": bytes = bytes[..^1]; break;
            case "trailing": bytes = bytes.Concat(new byte[] { 0 }).ToArray(); break;
        }
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), bytes.Length - 56);
        WriteHash(bytes, 24, 56);
        Assert.Throws<InvalidDataException>(() => DurableProviderCodec.Decode(bytes));
    }

    private static void WriteHash(byte[] bytes, int digest, int remainder)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes.AsSpan(0, digest)); hash.AppendData(bytes.AsSpan(remainder));
        hash.GetHashAndReset().CopyTo(bytes, digest);
    }

    private static DurableProviderImage ReadProvider(DurableTestDirectory directory) => DurableProviderCodec.Decode(File.ReadAllBytes(directory.ProviderPath));
    private static string CorePath(DurableTestDirectory directory) => Path.Combine(directory.Core, DurableFlowSession.CheckpointFileName);
    private sealed class AdmissionFailure : Exception { }
}
