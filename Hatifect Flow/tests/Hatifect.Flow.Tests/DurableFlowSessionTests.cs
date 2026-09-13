using System;
using System.Collections.Generic;
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
public sealed class DurableFlowSessionTests
{
    public static IEnumerable<object[]> TransferFailures()
    {
        foreach (bool deposit in new[] { false, true })
        foreach (bool accepted in new[] { false, true })
        foreach (DurableCommitStage stage in Enum.GetValues<DurableCommitStage>())
        { yield return new object[] { deposit, accepted, (int)stage }; }
    }

    [Theory]
    [MemberData(nameof(TransferFailures))]
    public void DurableTransfer_FaultBoundaryRecoversSinglePhysicalOutcome(bool deposit, bool accepted, int failureStage)
    {
        using var directory = new DurableTestDirectory();
        var fixture = new CheckpointFixture();
        fixture.Source.Inner.AcceptExtractions = deposit || accepted;
        fixture.DestinationPort.Inner.AcceptDeposits = !deposit || accepted;
        bool armed = false;
        var failure = new CommitFailure();
        DurableCommitStage target = (DurableCommitStage)failureStage;
        using var session = DurableFlowSession.Create(directory.Core, directory.Provider, fixture.Runtime,
            stage => { if (armed && stage == target) { throw failure; } });
        session.Execute(runtime => Assert.True(runtime.TryReserve(CheckpointFixture.Parcel)));
        if (deposit) { session.Execute(runtime => Assert.Equal(2, runtime.AdvanceTo(4, 2))); }
        FlowRuntime old = session.Runtime;
        long providerBefore = session.ProviderRevision;
        armed = true;

        Assert.Same(failure, Assert.Throws<CommitFailure>(() => session.Execute(runtime => runtime.AdvanceTo(deposit ? 4 : 1))));

        Assert.Throws<ObjectDisposedException>(() => session.Execute(_ => { }));
        Assert.Throws<InvalidOperationException>(() => old.AdvanceTo(8));
        DurableProviderImage persisted = DurableProviderCodec.Decode(File.ReadAllBytes(directory.ProviderPath));
        bool providerCommitted = target is DurableCommitStage.AfterProvider or DurableCommitStage.BeforeAcknowledgement or DurableCommitStage.AfterAcknowledgement;
        Assert.Equal(providerBefore + (providerCommitted ? 1 : 0), persisted.Revision);
        Assert.Equal(persisted.Revision, persisted.Receipts.Length);
        int physicalCopies = persisted.Stations.Sum(station => station.Port.Inventory.Count(cargo => cargo.CargoId == CheckpointFixture.Cargo.Value));
        Assert.Equal(deposit ? (providerCommitted && accepted ? 1 : 0) : (providerCommitted && accepted ? 0 : 1), physicalCopies);
        if (providerCommitted)
        {
            DurableProviderReceipt receipt = persisted.Receipts.Last();
            Assert.Equal(accepted ? (int)PortResult.Applied : (int)PortResult.Rejected, receipt.Result);
            Assert.Equal(CheckpointFixture.Cargo.Value, receipt.CargoId);
            Assert.Equal(new ManifestCheckpoint("ore", 7), receipt.Manifest);
        }
        using (DurableFlowSession reopened = DurableFlowSession.Open(directory.Core, directory.Provider))
        {
            ParcelState expected = target == DurableCommitStage.BeforeIntent
                ? (deposit ? ParcelState.Arrived : ParcelState.Reserved)
                : target == DurableCommitStage.AfterAcknowledgement
                    ? Settled(deposit, providerCommitted, accepted)
                    : (deposit ? ParcelState.DeliveryUncertain : ParcelState.ExtractionUncertain);
            Assert.Equal(expected, reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
            if (expected is ParcelState.DeliveryUncertain or ParcelState.ExtractionUncertain)
            {
                byte[] providerBytes = File.ReadAllBytes(directory.ProviderPath);
                reopened.Execute(runtime => Assert.True(runtime.ReconcileTransfer(CheckpointFixture.Parcel)));
                Assert.Equal(Settled(deposit, providerCommitted, accepted), reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
                Assert.Equal(providerBytes, File.ReadAllBytes(directory.ProviderPath));
            }
            AssertCustody(reopened);
            Assert.Equal(providerBefore + (providerCommitted ? 1 : 0), reopened.ProviderRevision);
        }
        using DurableFlowSession secondOpen = DurableFlowSession.Open(directory.Core, directory.Provider);
        AssertCustody(secondOpen);
        Assert.Equal(providerBefore + (providerCommitted ? 1 : 0), secondOpen.ProviderRevision);
    }

    [Fact]
    public void DurableSession_AcknowledgedDeliveryPersistsExactCargoAndBothReceipts()
    {
        using var directory = new DurableTestDirectory();
        var fixture = new CheckpointFixture();
        using (DurableFlowSession session = DurableFlowSession.Create(directory.Core, directory.Provider, fixture.Runtime))
        {
            Assert.Throws<InvalidOperationException>(() => fixture.Runtime.TryReserve(CheckpointFixture.Parcel));
            session.Execute(runtime => { Assert.True(runtime.TryReserve(CheckpointFixture.Parcel)); Assert.Equal(3, runtime.AdvanceTo(4)); });
            Assert.Equal(ParcelState.Delivered, session.Runtime.GetParcel(CheckpointFixture.Parcel).State);
            Assert.Equal(2, session.ProviderRevision);
            AssertCustody(session);
        }
        using DurableFlowSession reopened = DurableFlowSession.Open(directory.Core, directory.Provider);
        Assert.Equal(ParcelState.Delivered, reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Null(reopened.Runtime.GetCargo(CheckpointFixture.Cargo).ClaimedBy);
        Assert.Equal(0, reopened.Runtime.ReservedUnits(CheckpointFixture.Link));
        Assert.Equal(0, reopened.Runtime.PendingOperationCount);
        Assert.Equal(2, reopened.ProviderRevision);
        Assert.Single(reopened.GetPortSnapshot(CheckpointFixture.Origin).Receipts);
        Assert.Single(reopened.GetPortSnapshot(CheckpointFixture.Destination).Receipts);
        AssertCustody(reopened);
    }

    [Fact]
    public void DurableSession_RejectsMutationsOutsideExecuteAndCompetingWriter()
    {
        using var directory = new DurableTestDirectory();
        using var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime);
        FlowRuntime runtime = session.Runtime;
        byte[] bytes = File.ReadAllBytes(directory.ProviderPath);
        Assert.Throws<InvalidOperationException>(() => runtime.TryReserve(CheckpointFixture.Parcel));
        Assert.Throws<InvalidOperationException>(() => runtime.Restart());
        Assert.Throws<IOException>(() => DurableFlowSession.Open(directory.Core, directory.Provider));
        Assert.Throws<IOException>(() => DurableCargoProvider.Open(directory.Provider));
        Assert.Equal(ParcelState.Created, runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(bytes, File.ReadAllBytes(directory.ProviderPath));
        session.Execute(active => Assert.True(active.TryReserve(CheckpointFixture.Parcel)));
        Assert.Equal(ParcelState.Reserved, runtime.GetParcel(CheckpointFixture.Parcel).State);
    }

    [Fact]
    public void DurableSession_CaughtProviderFaultCannotAcknowledgeOrContinueWithStaleMirror()
    {
        using var directory = new DurableTestDirectory();
        bool armed = false;
        using var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime,
            stage => { if (armed && stage == DurableCommitStage.AfterProvider) { throw new CommitFailure(); } });
        session.Execute(runtime => Assert.True(runtime.TryReserve(CheckpointFixture.Parcel)));
        armed = true;
        Assert.Throws<InvalidOperationException>(() => session.Execute(runtime =>
        {
            Assert.Throws<CommitFailure>(() => runtime.AdvanceTo(1));
            Assert.Throws<InvalidOperationException>(() => runtime.ReconcileTransfer(CheckpointFixture.Parcel));
        }));
        using DurableFlowSession reopened = DurableFlowSession.Open(directory.Core, directory.Provider);
        Assert.Equal(ParcelState.ExtractionUncertain, reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(1, reopened.ProviderRevision);
        reopened.Execute(runtime => Assert.True(runtime.ReconcileTransfer(CheckpointFixture.Parcel)));
        Assert.Equal(ParcelState.InTransit, reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        AssertCustody(reopened);
    }

    [Fact]
    public void DurableOpen_RejectsWrongPairWithoutRewritingEitherProvider()
    {
        using var first = new DurableTestDirectory();
        using var second = new DurableTestDirectory();
        using (var session = DurableFlowSession.Create(first.Core, first.Provider, new CheckpointFixture().Runtime)) { }
        using (var session = DurableFlowSession.Create(second.Core, second.Provider, new CheckpointFixture().Runtime)) { }
        byte[] a = File.ReadAllBytes(first.ProviderPath), b = File.ReadAllBytes(second.ProviderPath);
        Assert.Throws<InvalidDataException>(() => DurableFlowSession.Open(first.Core, second.Provider));
        Assert.Equal(a, File.ReadAllBytes(first.ProviderPath));
        Assert.Equal(b, File.ReadAllBytes(second.ProviderPath));
        using var valid = DurableFlowSession.Open(first.Core, first.Provider);
        Assert.Equal(ParcelState.Created, valid.Runtime.GetParcel(CheckpointFixture.Parcel).State);
    }

    [Fact]
    public void DurableOpen_RejectsProviderRollbackAfterAcknowledgedExtraction()
    {
        using var directory = new DurableTestDirectory();
        byte[] old;
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime))
        {
            old = File.ReadAllBytes(directory.ProviderPath);
            session.Execute(runtime => { Assert.True(runtime.TryReserve(CheckpointFixture.Parcel)); runtime.AdvanceTo(1); });
        }
        File.WriteAllBytes(directory.ProviderPath, old);
        Assert.Throws<InvalidDataException>(() => DurableFlowSession.Open(directory.Core, directory.Provider));
        Assert.Equal(old, File.ReadAllBytes(directory.ProviderPath));
    }

    [Theory]
    [InlineData("network")]
    [InlineData("admission")]
    [InlineData("inventory")]
    [InlineData("receipt-payload")]
    [InlineData("digest")]
    public void DurableOpen_RejectsIndependentProviderChangesWithoutRepairingThem(string corruption)
    {
        using var directory = new DurableTestDirectory();
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime))
        { session.Execute(runtime => { runtime.TryReserve(CheckpointFixture.Parcel); runtime.AdvanceTo(1); }); }
        DurableProviderImage data = DurableProviderCodec.Decode(File.ReadAllBytes(directory.ProviderPath));
        switch (corruption)
        {
            case "network": data = data with { NetworkId = CheckpointFixture.Id(999) }; break;
            case "admission": data.Stations[1] = data.Stations[1] with { Port = data.Stations[1].Port with { AcceptDeposits = false } }; break;
            case "inventory": data.Stations[1] = data.Stations[1] with { Port = data.Stations[1].Port with { Inventory = new[] { new InventoryCheckpoint(CheckpointFixture.Cargo.Value, new ManifestCheckpoint("ore", 7)) } } }; break;
            case "receipt-payload": data.Receipts[0] = data.Receipts[0] with { Manifest = new ManifestCheckpoint("other", 7) }; break;
        }
        byte[] bytes = DurableProviderCodec.Encode(data);
        if (corruption == "digest") { bytes[^1] ^= 1; }
        File.WriteAllBytes(directory.ProviderPath, bytes);
        Assert.Throws<InvalidDataException>(() => DurableFlowSession.Open(directory.Core, directory.Provider));
        Assert.Equal(bytes, File.ReadAllBytes(directory.ProviderPath));
    }

    [Fact]
    public void DurableSession_StationSetupCannotChangeAfterProviderAdoption()
    {
        using var directory = new DurableTestDirectory();
        using var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime);
        Assert.Throws<InvalidOperationException>(() => session.Execute(runtime => runtime.AddStation(new StationId(CheckpointFixture.Id(555)), new InMemoryCargoPort())));
        using var reopened = DurableFlowSession.Open(directory.Core, directory.Provider);
        Assert.Equal(ParcelState.Created, reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(0, reopened.ProviderRevision);
        AssertCustody(reopened);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DurableOpen_MissingPairMemberFailsWithoutRecreatingOrOverwriting(bool missingProvider)
    {
        using var directory = new DurableTestDirectory();
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime)) { }
        string corePath = Path.Combine(directory.Core, DurableFlowSession.CheckpointFileName);
        string missing = missingProvider ? directory.ProviderPath : corePath;
        string retained = missingProvider ? corePath : directory.ProviderPath;
        byte[] bytes = File.ReadAllBytes(retained);
        File.Delete(missing);
        Assert.Throws<FileNotFoundException>(() => DurableFlowSession.Open(directory.Core, directory.Provider));
        Assert.False(File.Exists(missing));
        Assert.Equal(bytes, File.ReadAllBytes(retained));
        var replacement = new CheckpointFixture();
        Assert.Throws<IOException>(() => DurableFlowSession.Create(directory.Core, directory.Provider, replacement.Runtime));
        Assert.True(replacement.Runtime.TryReserve(CheckpointFixture.Parcel));
        Assert.Equal(bytes, File.ReadAllBytes(retained));
    }

    [Fact]
    public void DurableSession_ReentrantAndWrongThreadCommandsDoNotReleaseWriter()
    {
        using var directory = new DurableTestDirectory();
        using var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime);
        Exception? observed = null;
        var thread = new System.Threading.Thread(() => observed = Record.Exception(() => session.Dispose()));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(observed);
        session.Execute(runtime =>
        {
            Assert.Throws<InvalidOperationException>(() => session.Execute(_ => { }));
            Assert.Throws<InvalidOperationException>(() => session.Dispose());
            Assert.True(runtime.TryReserve(CheckpointFixture.Parcel));
        });
        Assert.Throws<IOException>(() => DurableFlowSession.Open(directory.Core, directory.Provider));
        Assert.Equal(ParcelState.Reserved, session.Runtime.GetParcel(CheckpointFixture.Parcel).State);
    }

    [Fact]
    public void DurableProvider_ReceiptLimitRetainsFirstCargoAndRejectsUnjournaledSecondExtraction()
    {
        using var directory = new DurableTestDirectory();
        var fixture = new CheckpointFixture();
        fixture.AddBatch(2);
        FlowCheckpoint data = fixture.Runtime.CaptureCheckpoint();
        data.Stations[0] = data.Stations[0] with { Port = data.Stations[0].Port with { MaxReceipts = 1 } };
        using var session = DurableFlowSession.Create(directory.Core, directory.Provider, FlowRuntime.RestoreCheckpoint(data));
        var second = new ParcelId(CheckpointFixture.Id(2));
        var secondCargo = new CargoId(CheckpointFixture.Id(9002));
        session.Execute(runtime =>
        {
            Assert.True(runtime.TryReserve(CheckpointFixture.Parcel));
            Assert.True(runtime.TryReserve(second));
            Assert.Equal(1, runtime.AdvanceTo(1, 1));
        });
        byte[] provider = File.ReadAllBytes(directory.ProviderPath);
        Assert.Throws<InvalidOperationException>(() => session.Execute(runtime => runtime.AdvanceTo(1, 1)));
        Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
        using var reopened = DurableFlowSession.Open(directory.Core, directory.Provider);
        Assert.Equal(ParcelState.InTransit, reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(ParcelState.ExtractionUncertain, reopened.Runtime.GetParcel(second).State);
        reopened.Execute(runtime => Assert.True(runtime.ReconcileTransfer(second)));
        Assert.Equal(ParcelState.Cancelled, reopened.Runtime.GetParcel(second).State);
        Assert.Null(reopened.Runtime.GetCargo(secondCargo).ClaimedBy);
        Assert.Equal(CheckpointFixture.Origin, reopened.Runtime.GetCargo(secondCargo).Owner.Station);
        Assert.Equal(secondCargo.Value, Assert.Single(reopened.GetPortSnapshot(CheckpointFixture.Origin).Inventory).CargoId);
        Assert.Single(reopened.GetPortSnapshot(CheckpointFixture.Origin).Receipts);
        Assert.Equal(1, reopened.ProviderRevision);
        Assert.Equal(7, reopened.Runtime.ReservedUnits(CheckpointFixture.Link));
        Assert.Equal(provider, File.ReadAllBytes(directory.ProviderPath));
        AssertCustody(reopened);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DurableAcknowledgement_CallbackCannotMutateAfterSnapshotCapture(bool afterPublish)
    {
        using var directory = new DurableTestDirectory();
        FlowRuntime? held = null;
        bool observed = false;
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime, stage =>
        {
            if (stage == (afterPublish ? DurableCommitStage.AfterAcknowledgement : DurableCommitStage.BeforeAcknowledgement))
            {
                Assert.Throws<InvalidOperationException>(() => held!.Cancel(CheckpointFixture.Parcel));
                observed = true;
            }
        }))
        {
            held = session.Runtime;
            session.Execute(runtime => Assert.True(runtime.TryReserve(CheckpointFixture.Parcel)));
            Assert.True(observed);
            Assert.Equal(ParcelState.Reserved, held.GetParcel(CheckpointFixture.Parcel).State);
        }
        using var reopened = DurableFlowSession.Open(directory.Core, directory.Provider);
        Assert.Equal(ParcelState.Reserved, reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(7, reopened.Runtime.ReservedUnits(CheckpointFixture.Link));
    }

    [Fact]
    public void DurableSession_PublicSessionObjectCannotForgeCoreMutationPermit()
    {
        using var directory = new DurableTestDirectory();
        using var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime);
        FlowRuntime held = session.Runtime;
        Assert.Throws<InvalidOperationException>(() => held.ExecuteOwned(session, runtime => runtime.TryReserve(CheckpointFixture.Parcel)));
        Assert.Equal(ParcelState.Created, held.GetParcel(CheckpointFixture.Parcel).State);
        session.Execute(runtime => Assert.True(runtime.TryReserve(CheckpointFixture.Parcel)));
        Assert.Equal(ParcelState.Reserved, held.GetParcel(CheckpointFixture.Parcel).State);
    }

    [Fact]
    public void DurableOpen_RejectsInvalidIntentEvenWhenProviderRevisionMatches()
    {
        using var directory = new DurableTestDirectory();
        using (var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime)) { }
        string path = Path.Combine(directory.Core, DurableFlowSession.CheckpointFileName);
        DurableCoreImage image = DurableCoreCodec.Decode(File.ReadAllBytes(path));
        image = image with { Intent = new TransferKey(CheckpointFixture.Parcel.Value, (int)PortTransferKind.Extract, 1) };
        byte[] bytes = DurableCoreCodec.Encode(image);
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => DurableFlowSession.Open(directory.Core, directory.Provider));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    private static ParcelState Settled(bool deposit, bool committed, bool accepted) => deposit
        ? (!committed ? ParcelState.DeliveryFaulted : accepted ? ParcelState.Delivered : ParcelState.DeliveryRejected)
        : (committed && accepted ? ParcelState.InTransit : ParcelState.Cancelled);
    private static void AssertCustody(DurableFlowSession session)
    {
        CargoBatch cargo = session.Runtime.GetCargo(CheckpointFixture.Cargo);
        InventoryCheckpoint[] physical = new[] { CheckpointFixture.Origin, CheckpointFixture.Destination }
            .SelectMany(station => session.GetPortSnapshot(station).Inventory).Where(batch => batch.CargoId == cargo.Id.Value).ToArray();
        bool escrow = cargo.Owner.Parcel is not null;
        if (cargo.Owner.Transfer is not null)
        {
            PortTransfer transfer = session.Runtime.GetParcel(CheckpointFixture.Parcel).PendingTransfer!;
            ReceiptCheckpoint? receipt = session.GetPortSnapshot(transfer.StationId).Receipts.SingleOrDefault(r => r.Key == CheckpointValues.Key(transfer.Id));
            bool applied = receipt?.Result == (int)PortResult.Applied;
            escrow = transfer.Kind == PortTransferKind.Extract ? applied : !applied;
        }
        Assert.Equal(7, physical.Sum(batch => batch.Manifest.Quantity) + (escrow ? cargo.Manifest.Quantity : 0));
        Assert.Equal(1, (cargo.Owner.Station is null ? 0 : 1) + (cargo.Owner.Parcel is null ? 0 : 1) + (cargo.Owner.Transfer is null ? 0 : 1));
        Assert.Equal(CheckpointFixture.Manifest, cargo.Manifest);
        Assert.All(physical, batch => Assert.Equal(new ManifestCheckpoint("ore", 7), batch.Manifest));
    }
    private sealed class CommitFailure : Exception { }
}
