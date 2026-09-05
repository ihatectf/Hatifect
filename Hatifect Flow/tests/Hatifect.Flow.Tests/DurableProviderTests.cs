using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Hatifect.Flow.Domain.Checkpoints;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Policies;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Infrastructure.Persistence;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class DurableProviderTests
{
    [Fact]
    public void ProviderCreate_DetachesSetupAndCaptureArraysAndRetainsExclusiveLease()
    {
        using var directory = new DurableTestDirectory();
        var data = Initial();
        using (DurableCargoProvider provider = DurableCargoProvider.Create(directory.Provider, data.PairId, data.NetworkId, data.Stations))
        {
            data.Stations[0].Port.Inventory[0] = new InventoryCheckpoint(CheckpointFixture.Id(777), new ManifestCheckpoint("changed", 1));
            DurableProviderImage capture = provider.CaptureImage();
            capture.Stations[0].Port.Inventory[0] = new InventoryCheckpoint(CheckpointFixture.Id(888), new ManifestCheckpoint("changed", 2));
            Assert.Equal(CheckpointFixture.Manifest, provider.GetPort(CheckpointFixture.Origin).ReadCargo(CheckpointFixture.Cargo));
            Assert.Throws<IOException>(() => DurableCargoProvider.Open(directory.Provider));
            Assert.Equal(0, provider.Revision);
        }
        using DurableCargoProvider reopened = DurableCargoProvider.Open(directory.Provider);
        Assert.Equal(CheckpointFixture.Manifest, reopened.GetPort(CheckpointFixture.Origin).ReadCargo(CheckpointFixture.Cargo));
        Assert.Empty(reopened.CaptureImage().Receipts);
    }

    [Theory]
    [InlineData("pair")]
    [InlineData("network")]
    [InlineData("revision")]
    [InlineData("duplicate-station")]
    [InlineData("duplicate-cargo")]
    [InlineData("quantity")]
    [InlineData("unicode")]
    [InlineData("batch-limit")]
    [InlineData("receipt-limit")]
    [InlineData("missing-receipt")]
    [InlineData("wrong-station")]
    [InlineData("result")]
    [InlineData("kind")]
    [InlineData("attempt")]
    public void ProviderCodec_RejectsContradictoryIdentityInventoryOrJournal(string corruption)
    {
        DurableProviderImage data = Initial();
        switch (corruption)
        {
            case "pair": data = data with { PairId = Guid.Empty }; break;
            case "network": data = data with { NetworkId = Guid.Empty }; break;
            case "revision": data = data with { Revision = 1 }; break;
            case "duplicate-station": data.Stations[1] = data.Stations[0]; break;
            case "duplicate-cargo": data.Stations[1] = data.Stations[1] with { Port = data.Stations[1].Port with { Inventory = data.Stations[0].Port.Inventory } }; break;
            case "quantity": data.Stations[0].Port.Inventory[0] = data.Stations[0].Port.Inventory[0] with { Manifest = new ManifestCheckpoint("ore", 0) }; break;
            case "unicode": data.Stations[0].Port.Inventory[0] = data.Stations[0].Port.Inventory[0] with { Manifest = new ManifestCheckpoint("\uD800", 7) }; break;
            case "batch-limit": data.Stations[0] = data.Stations[0] with { Port = data.Stations[0].Port with { MaxCargoBatches = 0 } }; break;
            case "receipt-limit": data.Stations[0] = data.Stations[0] with { Port = data.Stations[0].Port with { MaxReceipts = 65537 } }; break;
            default:
                var key = new TransferKey(CheckpointFixture.Parcel.Value, corruption == "kind" ? 999 : (int)PortTransferKind.Extract, corruption == "attempt" ? 2 : 1);
                int result = corruption == "result" ? (int)PortResult.Missing : (int)PortResult.Rejected;
                var receipt = new DurableProviderReceipt(key, CheckpointFixture.Cargo.Value,
                    corruption == "wrong-station" ? CheckpointFixture.Id(999) : CheckpointFixture.Origin.Value,
                    new ManifestCheckpoint("ore", 7), result);
                data = data with { Revision = 1, Receipts = new[] { receipt } };
                if (corruption != "missing-receipt")
                { data.Stations[0] = data.Stations[0] with { Port = data.Stations[0].Port with { Receipts = new[] { new ReceiptCheckpoint(key, result) } } }; }
                break;
        }
        Assert.Throws<InvalidDataException>(() => DurableProviderCodec.Encode(data));
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("truncated")]
    [InlineData("count")]
    [InlineData("boolean")]
    public void ProviderCodec_RejectsCorruptEnvelopeAndChecksPayloadBoundsAfterIntegrity(string corruption)
    {
        byte[] bytes = DurableProviderCodec.Encode(Initial());
        switch (corruption)
        {
            case "magic": bytes[0] ^= 1; break;
            case "version": bytes[8] = 3; break;
            case "hash": bytes[24] ^= 1; break;
            case "truncated": bytes = bytes[..^1]; break;
            case "count": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(DurableProviderCodec.HeaderLength + 32), int.MaxValue); Rehash(bytes); break;
            case "boolean": bytes[DurableProviderCodec.HeaderLength + 36 + 16 + 8] = 2; Rehash(bytes); break;
            default: throw new ArgumentException(nameof(corruption));
        }
        Assert.Throws<InvalidDataException>(() => DurableProviderCodec.Decode(bytes));
    }

    [Fact]
    public void ProviderOpen_CorruptionPreservesBytesAndReleasesLeaseForExplicitRepair()
    {
        using var directory = new DurableTestDirectory();
        DurableProviderImage data = Initial();
        using (DurableCargoProvider provider = DurableCargoProvider.Create(directory.Provider, data.PairId, data.NetworkId, data.Stations)) { }
        byte[] valid = File.ReadAllBytes(directory.ProviderPath);
        byte[] corrupt = valid.ToArray();
        corrupt[^1] ^= 1;
        File.WriteAllBytes(directory.ProviderPath, corrupt);
        Assert.Throws<InvalidDataException>(() => DurableCargoProvider.Open(directory.Provider));
        Assert.Equal(corrupt, File.ReadAllBytes(directory.ProviderPath));
        File.WriteAllBytes(directory.ProviderPath, valid);
        using DurableCargoProvider reopened = DurableCargoProvider.Open(directory.Provider);
        Assert.Equal(CheckpointFixture.Manifest, reopened.GetPort(CheckpointFixture.Origin).ReadCargo(CheckpointFixture.Cargo));
    }

    [Theory]
    [InlineData("unarmed")]
    [InlineData("clone")]
    [InlineData("foreign")]
    [InlineData("retired")]
    [InlineData("sealed")]
    public void ProviderApply_UnarmedOrInvalidAuthorityCannotProduceFirstPhysicalEffect(string invalid)
    {
        using var directory = new DurableTestDirectory();
        DurableProviderImage data = Initial();
        using var provider = DurableCargoProvider.Create(directory.Provider, data.PairId, data.NetworkId, data.Stations);
        var authority = new PortAuthority(new FlowLimits());
        ICheckpointCargoPort port = provider.GetPort(CheckpointFixture.Origin);
        port.Bind(authority, CheckpointFixture.Origin);
        PortTransfer valid = Issue(authority, 1, PortTransferKind.Extract);
        PortTransfer request = valid;
        switch (invalid)
        {
            case "clone": request = valid with { }; break;
            case "foreign": request = Issue(new PortAuthority(new FlowLimits()), 1, PortTransferKind.Extract); break;
            case "retired": authority.Retire(valid); break;
            case "sealed": authority.Seal(); break;
        }
        byte[] before = File.ReadAllBytes(directory.ProviderPath);
        Assert.Throws<InvalidOperationException>(() => port.Apply(request));
        if (invalid != "unarmed") { Assert.Throws<InvalidOperationException>(() => provider.ArmIntent(request)); }
        Assert.Equal(before, File.ReadAllBytes(directory.ProviderPath));
        Assert.Equal(CheckpointFixture.Manifest, port.ReadCargo(CheckpointFixture.Cargo));
        Assert.Equal(0, provider.Revision);
        Assert.Empty(provider.CaptureImage().Receipts);
        if (invalid == "unarmed")
        {
            provider.ArmIntent(valid);
            Assert.Equal(PortResult.Applied, port.Apply(valid));
            Assert.Null(port.ReadCargo(CheckpointFixture.Cargo));
            Assert.Equal(1, provider.Revision);
        }
    }

    [Fact]
    public void ProviderReplay_RetainsPayloadBoundReceiptAcrossFreshScopeAndCargoRedispatch()
    {
        using var directory = new DurableTestDirectory();
        DurableProviderImage data = Initial();
        PortTransfer old;
        ICheckpointCargoPort oldPort;
        using (var provider = DurableCargoProvider.Create(directory.Provider, data.PairId, data.NetworkId, data.Stations))
        {
            var authority = new PortAuthority(new FlowLimits());
            oldPort = provider.GetPort(CheckpointFixture.Origin);
            oldPort.Bind(authority, CheckpointFixture.Origin);
            old = Issue(authority, 1, PortTransferKind.Extract);
            provider.ArmIntent(old);
            Assert.Equal(PortResult.Applied, oldPort.Apply(old));
            authority.Retire(old);
            PortTransfer returned = Issue(authority, 1, PortTransferKind.Deposit);
            provider.ArmIntent(returned);
            Assert.Equal(PortResult.Applied, oldPort.Apply(returned));
            byte[] beforeReplay = File.ReadAllBytes(directory.ProviderPath);
            Assert.Equal(PortResult.Applied, oldPort.Apply(old));
            Assert.Equal(beforeReplay, File.ReadAllBytes(directory.ProviderPath));
            Assert.Equal(CheckpointFixture.Manifest, oldPort.ReadCargo(CheckpointFixture.Cargo));
        }
        Assert.Throws<ObjectDisposedException>(() => oldPort.Apply(old));
        using var reopened = DurableCargoProvider.Open(directory.Provider);
        var freshAuthority = new PortAuthority(new FlowLimits());
        ICheckpointCargoPort fresh = reopened.GetPort(CheckpointFixture.Origin);
        fresh.Bind(freshAuthority, CheckpointFixture.Origin);
        byte[] bytes = File.ReadAllBytes(directory.ProviderPath);
        Assert.Throws<InvalidOperationException>(() => fresh.Apply(old));
        PortTransfer changed = Issue(freshAuthority, 1, PortTransferKind.Extract, new CargoManifest("wrong", 7));
        Assert.Throws<InvalidOperationException>(() => fresh.ReadResult(changed));
        Assert.Throws<InvalidOperationException>(() => fresh.Apply(changed));
        Assert.Equal(bytes, File.ReadAllBytes(directory.ProviderPath));
        PortTransfer next = Issue(freshAuthority, 2, PortTransferKind.Extract);
        reopened.ArmIntent(next);
        Assert.Equal(PortResult.Applied, fresh.Apply(next));
        Assert.Null(fresh.ReadCargo(CheckpointFixture.Cargo));
        Assert.Equal(3, reopened.Revision);
        Assert.Equal(3, reopened.CaptureImage().Receipts.Length);
    }

    private static PortTransfer Issue(PortAuthority authority, int execution, PortTransferKind kind, CargoManifest? manifest = null)
    {
        var parcel = new Parcel(new ParcelId(CheckpointFixture.Id(execution)), new ShipmentId(CheckpointFixture.Id(execution)),
            CheckpointFixture.Cargo, manifest ?? CheckpointFixture.Manifest, CheckpointFixture.Policy,
            ParcelState.Created, 0, 0, CheckpointFixture.Origin, null);
        return authority.Issue(parcel, CheckpointFixture.Origin, kind, 1);
    }

    private static DurableProviderImage Initial() => new(CheckpointFixture.Id(456), CheckpointFixture.Id(100), 0,
        new CheckpointFixture().Runtime.CaptureCheckpoint().Stations, Array.Empty<DurableProviderReceipt>());
    private static void Rehash(byte[] bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes.AsSpan(0, 24));
        hash.AppendData(bytes.AsSpan(DurableProviderCodec.HeaderLength));
        hash.GetHashAndReset().CopyTo(bytes, 24);
    }
}

internal sealed class DurableTestDirectory : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "hatifect-durable-test-" + Guid.NewGuid().ToString("N"));
    internal string Core => Path.Combine(Root, "core");
    internal string Provider => Path.Combine(Root, "provider");
    internal string ProviderPath => Path.Combine(Provider, DurableCargoProvider.ImageFileName);
    internal DurableTestDirectory() { Directory.CreateDirectory(Core); Directory.CreateDirectory(Provider); }
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
