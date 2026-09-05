using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Hatifect.Flow.Application.Dispatch;
using Hatifect.Flow.Domain.Checkpoints;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Infrastructure.Persistence;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class CheckpointPersistenceTests
{
    [Fact]
    public void PersistentSession_ReopenRestoresOnlyCommittedCompleteState()
    {
        using var directory = new OwnedDirectory();
        var fixture = new CheckpointFixture();
        using (CheckpointSession session = CheckpointSession.Create(directory.Path, fixture.Runtime))
        {
            Assert.Equal(0, session.Revision);
            Assert.True(session.Runtime.TryReserve(CheckpointFixture.Parcel));
            session.Commit(0);
            Assert.Equal(1, session.Revision);
            Assert.Equal(3, session.Runtime.AdvanceTo(4));
            Assert.Equal(ParcelState.Delivered, session.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        }

        Assert.Throws<InvalidOperationException>(() => fixture.Runtime.AdvanceTo(5));
        using CheckpointSession reopened = CheckpointSession.Open(directory.Path);
        Assert.Equal(1, reopened.Revision);
        Assert.Equal(ParcelState.Reserved, reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(CheckpointFixture.Manifest, reopened.Runtime.GetCheckpointPort(CheckpointFixture.Origin).ReadCargo(CheckpointFixture.Cargo));
        Assert.Empty(reopened.Runtime.GetCheckpointPort(CheckpointFixture.Destination).Inventory);
        Assert.Equal(3, reopened.Runtime.AdvanceTo(4));
        reopened.Commit(1);
        CheckpointImage image = CheckpointCodec.Decode(File.ReadAllBytes(directory.CheckpointPath));
        Assert.Equal(2, image.Revision);
        Assert.Equal((int)ParcelState.Delivered, Assert.Single(image.Checkpoint.Parcels).State);
        Assert.Single(image.Checkpoint.Stations[0].Port.Receipts);
        Assert.Single(image.Checkpoint.Stations[1].Port.Receipts);
        Assert.Equal(CheckpointFixture.Cargo.Value, Assert.Single(image.Checkpoint.Stations[1].Port.Inventory).CargoId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersistentCommit_FaultBoundaryLeavesOneCompleteCommittedGeneration(bool afterPublish)
    {
        using var directory = new OwnedDirectory();
        var fixture = new CheckpointFixture();
        bool armed = false;
        var failure = new PublicationFailure();
        using CheckpointSession session = CheckpointSession.Create(directory.Path, fixture.Runtime, stage =>
        {
            if (armed && stage == (afterPublish ? CheckpointPublishStage.AfterPublish : CheckpointPublishStage.BeforePublish))
            {
                throw failure;
            }
        });
        Assert.True(fixture.Runtime.TryReserve(CheckpointFixture.Parcel));
        Assert.Equal(3, fixture.Runtime.AdvanceTo(4));
        armed = true;

        Assert.Same(failure, Assert.Throws<PublicationFailure>(() => session.Commit(0)));

        Assert.Throws<ObjectDisposedException>(() => session.Commit(0));
        Assert.Throws<InvalidOperationException>(() => fixture.Runtime.CaptureCheckpoint());
        Assert.Empty(Directory.GetFiles(directory.Path, ".flow-checkpoint-*.tmp"));
        using CheckpointSession reopened = CheckpointSession.Open(directory.Path);
        Assert.Equal(afterPublish ? 1 : 0, reopened.Revision);
        Assert.Equal(afterPublish ? ParcelState.Delivered : ParcelState.Created,
            reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        InMemoryCargoPort source = reopened.Runtime.GetCheckpointPort(CheckpointFixture.Origin);
        InMemoryCargoPort destination = reopened.Runtime.GetCheckpointPort(CheckpointFixture.Destination);
        Assert.Equal(afterPublish ? 0 : 1, source.Inventory.Count);
        Assert.Equal(afterPublish ? 1 : 0, destination.Inventory.Count);
        FlowCheckpoint data = reopened.Runtime.CaptureCheckpoint();
        Assert.Equal(afterPublish ? 2 : 0, data.Transfers.Length);
        Assert.Equal(afterPublish ? 2 : 0, data.Stations.Sum(station => station.Port.Receipts.Length));
    }

    [Fact]
    public void PersistentSession_RejectsConcurrentWriterAndStaleRevisionWithoutClosingOwner()
    {
        using var directory = new OwnedDirectory();
        var fixture = new CheckpointFixture();
        using CheckpointSession session = CheckpointSession.Create(directory.Path, fixture.Runtime);
        byte[] before = File.ReadAllBytes(directory.CheckpointPath);

        Assert.Throws<IOException>(() => CheckpointSession.Open(directory.Path));
        Assert.Throws<IOException>(() => new FileStream(System.IO.Path.Combine(directory.Path, CheckpointSession.LockFileName),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        Assert.Throws<InvalidOperationException>(() => session.Commit(-1));
        Assert.Throws<InvalidOperationException>(() => session.Commit(1));

        Assert.Equal(before, File.ReadAllBytes(directory.CheckpointPath));
        Assert.Equal(0, session.Revision);
        Assert.True(session.Runtime.TryReserve(CheckpointFixture.Parcel));
        session.Commit(0);
        Assert.Equal(1, session.Revision);
        Assert.Throws<InvalidOperationException>(() => session.Runtime.Restart());
        Assert.Throws<IOException>(() => CheckpointSession.Create(directory.Path, new CheckpointFixture().Runtime));
    }

    [Fact]
    public void Dispose_FencesUnseenPortRequestBeforeLeaseCanBeReacquired()
    {
        using var directory = new OwnedDirectory();
        var fixture = new CheckpointFixture();
        fixture.Source.Throw = true;
        Assert.True(fixture.Runtime.TryReserve(CheckpointFixture.Parcel));
        Assert.Throws<CheckpointFixture.PortFailure>(() => fixture.Runtime.AdvanceTo(1));
        PortTransfer oldRequest = fixture.Runtime.GetParcel(CheckpointFixture.Parcel).PendingTransfer!;
        using (CheckpointSession session = CheckpointSession.Create(directory.Path, fixture.Runtime))
        {
            Assert.Equal(PortResult.Missing, fixture.Source.ReadResult(oldRequest));
        }

        Assert.Throws<InvalidOperationException>(() => fixture.Source.Inner.Apply(oldRequest));
        Assert.Equal(CheckpointFixture.Manifest, fixture.Source.ReadCargo(CheckpointFixture.Cargo));
        using CheckpointSession reopened = CheckpointSession.Open(directory.Path);
        PortTransfer fresh = reopened.Runtime.GetParcel(CheckpointFixture.Parcel).PendingTransfer!;
        Assert.NotSame(oldRequest.Id.Session, fresh.Id.Session);
        Assert.Equal(PortResult.Missing, reopened.Runtime.GetCheckpointPort(fresh.StationId).ReadResult(fresh));
        Assert.True(reopened.Runtime.ReconcileTransfer(CheckpointFixture.Parcel));
        Assert.Equal(ParcelState.Cancelled, reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Null(reopened.Runtime.GetCargo(CheckpointFixture.Cargo).ClaimedBy);
        Assert.Throws<InvalidOperationException>(() => reopened.Runtime.GetCheckpointPort(fresh.StationId).Apply(oldRequest));
        Assert.Equal(CheckpointFixture.Manifest, reopened.Runtime.GetCheckpointPort(fresh.StationId).ReadCargo(CheckpointFixture.Cargo));
    }

    [Fact]
    public void Session_WrongThreadAndReentrantDisposeRetainExclusiveLiveOwner()
    {
        using var directory = new OwnedDirectory();
        var fixture = new CheckpointFixture();
        CheckpointSession? active = null;
        bool observedReentrancy = false;
        using CheckpointSession session = CheckpointSession.Create(directory.Path, fixture.Runtime, _ =>
        {
            if (active is not null)
            {
                Assert.Throws<InvalidOperationException>(() => active.Dispose());
                Assert.Throws<InvalidOperationException>(() => active.Commit(0));
                observedReentrancy = true;
            }
        });
        active = session;
        Exception? failure = null;
        var thread = new Thread(() => failure = Record.Exception(() => session.Dispose()));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Throws<IOException>(() => CheckpointSession.Open(directory.Path));

        Assert.True(session.Runtime.TryReserve(CheckpointFixture.Parcel));
        session.Commit(0);
        Assert.True(observedReentrancy);
        Assert.Equal(1, session.Revision);
        Assert.Equal(3, session.Runtime.AdvanceTo(4));
        Assert.Equal(ParcelState.Delivered, session.Runtime.GetParcel(CheckpointFixture.Parcel).State);
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("version")]
    [InlineData("revision")]
    [InlineData("length")]
    [InlineData("hash")]
    [InlineData("payload")]
    [InlineData("truncated")]
    [InlineData("trailing")]
    [InlineData("oversized")]
    public void CheckpointCodec_RejectsCorruptedEnvelope(string corruption)
    {
        byte[] bytes = CheckpointCodec.Encode(new CheckpointImage(10, new CheckpointFixture().Runtime.CaptureCheckpoint()));
        switch (corruption)
        {
            case "magic": bytes[0] ^= 1; break;
            case "version": bytes[8] ^= 1; break;
            case "revision": bytes[12] ^= 1; break;
            case "length": bytes[20] ^= 1; break;
            case "hash": bytes[24] ^= 1; break;
            case "payload": bytes[CheckpointCodec.HeaderLength + 1] ^= 1; break;
            case "truncated": bytes = bytes.Take(CheckpointCodec.HeaderLength - 1).ToArray(); break;
            case "trailing": bytes = bytes.Concat(new byte[] { 0 }).ToArray(); break;
            case "oversized": bytes = new byte[CheckpointCodec.MaxImageBytes + 1]; break;
            default: throw new ArgumentException(nameof(corruption));
        }

        Assert.Throws<InvalidDataException>(() => CheckpointCodec.Decode(bytes));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("malformed")]
    [InlineData("null")]
    [InlineData("unpaired-high")]
    [InlineData("unpaired-low")]
    public void CheckpointCodec_RejectsInvalidSchemaEvenWithValidIntegrityDigest(string corruption)
    {
        byte[] valid = CheckpointCodec.Encode(new CheckpointImage(0, new CheckpointFixture().Runtime.CaptureCheckpoint()));
        string json = Encoding.UTF8.GetString(valid, CheckpointCodec.HeaderLength, valid.Length - CheckpointCodec.HeaderLength);
        string changed = corruption switch
        {
            "missing" => json.Replace("\"now\":0,", "", StringComparison.Ordinal),
            "unknown" => "{\"unknown\":0," + json[1..],
            "duplicate" => "{\"now\":0," + json[1..],
            "malformed" => "{broken",
            "null" => "null",
            "unpaired-high" => json.Replace("\"ore\"", "\"ore\\uD800\"", StringComparison.Ordinal),
            "unpaired-low" => json.Replace("\"ore\"", "\"ore\\uDC00\"", StringComparison.Ordinal),
            _ => throw new ArgumentException(nameof(corruption))
        };
        Assert.NotEqual(json, changed);
        byte[] bytes = Envelope(valid, changed);

        Assert.Throws<InvalidDataException>(() => CheckpointCodec.Decode(bytes));
    }

    [Fact]
    public void Open_InvalidSemanticImageReleasesLeaseAndPreservesCommittedBytes()
    {
        using var directory = new OwnedDirectory();
        FlowCheckpoint invalid = new CheckpointFixture().Runtime.CaptureCheckpoint() with { Now = -1 };
        byte[] bytes = CheckpointCodec.Encode(new CheckpointImage(0, invalid));
        File.WriteAllBytes(directory.CheckpointPath, bytes);

        Assert.Throws<ArgumentException>(() => CheckpointSession.Open(directory.Path));
        Assert.Equal(bytes, File.ReadAllBytes(directory.CheckpointPath));
        using (var probe = new FileStream(System.IO.Path.Combine(directory.Path, CheckpointSession.LockFileName),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(probe.CanWrite);
        }
        File.WriteAllBytes(directory.CheckpointPath,
            CheckpointCodec.Encode(new CheckpointImage(0, new CheckpointFixture().Runtime.CaptureCheckpoint())));
        using CheckpointSession reopened = CheckpointSession.Open(directory.Path);
        Assert.Equal(ParcelState.Created, reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
    }

    [Fact]
    public void Create_RejectsExistingCommittedWorldWithoutTakingRuntimeOwnership()
    {
        using var directory = new OwnedDirectory();
        using (CheckpointSession original = CheckpointSession.Create(directory.Path, new CheckpointFixture().Runtime))
        {
            Assert.Equal(0, original.Revision);
        }
        byte[] bytes = File.ReadAllBytes(directory.CheckpointPath);
        var replacement = new CheckpointFixture();

        Assert.Throws<IOException>(() => CheckpointSession.Create(directory.Path, replacement.Runtime));

        Assert.Equal(bytes, File.ReadAllBytes(directory.CheckpointPath));
        Assert.True(replacement.Runtime.TryReserve(CheckpointFixture.Parcel));
        using CheckpointSession reopened = CheckpointSession.Open(directory.Path);
        Assert.Equal(ParcelState.Created, reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
    }

    [Fact]
    public void CheckpointBuffer_RejectsOversizedReservationBeforeGrowthAndPreservesLastAvailableByte()
    {
        var buffer = new CheckpointBuffer(64);
        Memory<byte> initial = buffer.GetMemory(63);
        Assert.InRange(initial.Length, 63, 64);
        initial.Span[..63].Fill(4);
        buffer.Advance(63);

        Assert.Throws<InvalidDataException>(() => buffer.GetMemory(2));
        Assert.Throws<InvalidDataException>(() => buffer.GetSpan(2));
        Assert.Equal(Enumerable.Repeat((byte)4, 63).ToArray(), buffer.WrittenSpan.ToArray());
        Assert.Equal(1, buffer.GetMemory(1).Length);
        buffer.GetSpan(1)[0] = 7;
        buffer.Advance(1);

        Assert.Equal(64, buffer.WrittenSpan.Length);
        Assert.Equal(7, buffer.WrittenSpan[63]);
        Assert.Throws<InvalidDataException>(() => buffer.GetMemory());
        Assert.Throws<InvalidDataException>(() => buffer.GetSpan());
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Advance(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.GetMemory(-1));
        Assert.Equal(64, buffer.WrittenSpan.Length);
    }

    [Theory]
    [InlineData("high")]
    [InlineData("low")]
    [InlineData("length")]
    public void CheckpointCodec_RejectsItemKeyThatWouldBeRewrittenOrExceedStringBound(string invalid)
    {
        string key = invalid switch
        {
            "high" => "\uD800",
            "low" => "\uDC00",
            "length" => new string('"', 257),
            _ => throw new ArgumentException(nameof(invalid))
        };
        FlowCheckpoint data = WithItemKey(new CheckpointFixture().Runtime.CaptureCheckpoint(), key);

        Assert.Throws<InvalidDataException>(() => CheckpointCodec.Encode(new CheckpointImage(0, data)));
    }

    [Fact]
    public void CheckpointCodec_PreservesEscapedMaximumLengthKeyAndValidSurrogatePair()
    {
        string key = new string('"', 254) + "\uD83D\uDE80";
        Assert.Equal(256, key.Length);
        FlowCheckpoint data = WithItemKey(new CheckpointFixture().Runtime.CaptureCheckpoint(), key);

        byte[] bytes = CheckpointCodec.Encode(new CheckpointImage(0, data));
        FlowRuntime restored = FlowRuntime.RestoreCheckpoint(CheckpointCodec.Decode(bytes).Checkpoint);

        Assert.Equal(key, restored.GetCargo(CheckpointFixture.Cargo).Manifest.ItemKey);
        Assert.Equal(key, restored.GetCheckpointPort(CheckpointFixture.Origin).ReadCargo(CheckpointFixture.Cargo)!.ItemKey);
        Assert.True(restored.TryReserve(CheckpointFixture.Parcel));
        Assert.Equal(3, restored.AdvanceTo(4));
        Assert.Equal(key, restored.GetCheckpointPort(CheckpointFixture.Destination).ReadCargo(CheckpointFixture.Cargo)!.ItemKey);
    }

    private static FlowCheckpoint WithItemKey(FlowCheckpoint data, string key)
    {
        var manifest = new ManifestCheckpoint(key, CheckpointFixture.Manifest.Quantity);
        data.Cargo[0] = data.Cargo[0] with { Manifest = manifest };
        data.Parcels[0] = data.Parcels[0] with { Manifest = manifest };
        data.Shipments[0] = data.Shipments[0] with { Manifest = manifest };
        data.Stations[0].Port.Inventory[0] = data.Stations[0].Port.Inventory[0] with { Manifest = manifest };
        return data;
    }

    private static byte[] Envelope(byte[] template, string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] image = new byte[CheckpointCodec.HeaderLength + payload.Length];
        template.AsSpan(0, CheckpointCodec.HeaderLength).CopyTo(image);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(20), payload.Length);
        payload.CopyTo(image, CheckpointCodec.HeaderLength);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(image.AsSpan(0, 24));
        hash.AppendData(payload);
        hash.GetHashAndReset().CopyTo(image, 24);
        return image;
    }

    private sealed class PublicationFailure : Exception { }
    private sealed class OwnedDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hatifect-checkpoint-test-" + Guid.NewGuid().ToString("N"));
        internal string CheckpointPath => System.IO.Path.Combine(Path, CheckpointSession.CheckpointFileName);
        internal OwnedDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
