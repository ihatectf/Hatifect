using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Hatifect.Flow.Domain.Checkpoints;
using Hatifect.Flow.Domain.Ports;

namespace Hatifect.Flow.Infrastructure.Persistence;

// Independent provider format. Fixed-width counts are checked before allocation;
// UTF-8 is strict, and the digest includes all metadata except its own slot.
internal static class DurableProviderCodec
{
    public const int HeaderLength = 56;
    public const int MaxImageBytes = 64 * 1024 * 1024;
    private const int MaxEntries = 262144;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("HATFLOWP");
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] Encode(DurableProviderImage image)
    {
        int length = Validate(image);
        byte[] bytes = new byte[length];
        using var stream = new MemoryStream(bytes, writable: true);
        stream.Position = HeaderLength;
        using var writer = new BinaryWriter(stream, Utf8, leaveOpen: true);
        writer.Write(image.PairId.ToByteArray());
        writer.Write(image.NetworkId.ToByteArray());
        writer.Write(image.Stations.Length);
        foreach (StationCheckpoint station in image.Stations)
        {
            WriteStation(writer, station);
        }
        writer.Write(image.Receipts.Length);
        foreach (DurableProviderReceipt receipt in image.Receipts)
        {
            WriteReceipt(writer, receipt);
        }
        writer.Write(image.ConfigurationRevision);
        if (stream.Position != length) { throw new InvalidDataException("Provider image sizing disagrees with its format."); }
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 2);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12), image.Revision);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), length - HeaderLength);
        Hash(bytes).CopyTo(bytes, 24);
        return bytes;
    }

    public static DurableProviderImage Decode(ReadOnlySpan<byte> bytes)
    {
        Require(bytes.Length >= HeaderLength && bytes.Length <= MaxImageBytes, "image size");
        Require(bytes[..8].SequenceEqual(Magic), "signature");
        int version = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        Require(version is 1 or 2, "version");
        long revision = BinaryPrimitives.ReadInt64LittleEndian(bytes[12..]);
        Require(revision >= 0 && BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]) == bytes.Length - HeaderLength, "metadata");
        Require(CryptographicOperations.FixedTimeEquals(bytes.Slice(24, 32), Hash(bytes)), "integrity");
        try
        {
            using var stream = new MemoryStream(bytes[HeaderLength..].ToArray(), writable: false);
            using var reader = new BinaryReader(stream, Utf8);
            Guid pair = ReadGuid(reader);
            Guid network = ReadGuid(reader);
            int budget = MaxEntries;
            StationCheckpoint[] stations = ReadStations(reader, ref budget);
            DurableProviderReceipt[] journal = ReadJournal(reader, ref budget);
            long configurationRevision = version == 2 ? reader.ReadInt64() : 0;
            Require(stream.Position == stream.Length, "trailing bytes");
            AttachJournalReceipts(stations, journal);
            var image = new DurableProviderImage(pair, network, revision, stations, journal, configurationRevision);
            Validate(image, version);
            return image;
        }
        catch (Exception error) when (error is EndOfStreamException or DecoderFallbackException or OverflowException)
        {
            throw new InvalidDataException("Provider image is malformed.", error);
        }
    }

    private static void WriteStation(BinaryWriter writer, StationCheckpoint station)
    {
        writer.Write(station.Id.ToByteArray());
        PortCheckpoint port = station.Port;
        writer.Write(port.MaxCargoBatches);
        writer.Write(port.MaxReceipts);
        writer.Write(port.AcceptDeposits);
        writer.Write(port.AcceptExtractions);
        writer.Write(port.Inventory.Length);
        foreach (InventoryCheckpoint cargo in port.Inventory)
        {
            writer.Write(cargo.CargoId.ToByteArray());
            WriteManifest(writer, cargo.Manifest);
        }
    }

    private static void WriteReceipt(BinaryWriter writer, DurableProviderReceipt receipt)
    {
        writer.Write(receipt.Key.ParcelId.ToByteArray());
        writer.Write(receipt.Key.Kind);
        writer.Write(receipt.Key.Attempt);
        writer.Write(receipt.CargoId.ToByteArray());
        writer.Write(receipt.StationId.ToByteArray());
        WriteManifest(writer, receipt.Manifest);
        writer.Write(receipt.Result);
    }

    private static StationCheckpoint[] ReadStations(BinaryReader reader, ref int budget)
    {
        var stations = new StationCheckpoint[Count(reader, ref budget, 65536)];
        for (int index = 0; index < stations.Length; index++)
        {
            stations[index] = ReadStation(reader, ref budget);
        }
        return stations;
    }

    private static StationCheckpoint ReadStation(BinaryReader reader, ref int budget)
    {
        Guid id = ReadGuid(reader);
        int maxCargoBatches = reader.ReadInt32();
        int maxReceipts = reader.ReadInt32();
        bool acceptDeposits = ReadBool(reader);
        bool acceptExtractions = ReadBool(reader);
        var inventory = new InventoryCheckpoint[Count(reader, ref budget, 65536)];
        for (int index = 0; index < inventory.Length; index++)
        {
            inventory[index] = new InventoryCheckpoint(ReadGuid(reader), ReadManifest(reader));
        }
        return new StationCheckpoint(id, new PortCheckpoint(maxCargoBatches, maxReceipts,
            acceptDeposits, acceptExtractions, inventory, Array.Empty<ReceiptCheckpoint>()));
    }

    private static DurableProviderReceipt[] ReadJournal(BinaryReader reader, ref int budget)
    {
        var journal = new DurableProviderReceipt[Count(reader, ref budget, MaxEntries)];
        for (int index = 0; index < journal.Length; index++)
        {
            var key = new TransferKey(ReadGuid(reader), reader.ReadInt32(), reader.ReadInt32());
            journal[index] = new DurableProviderReceipt(key, ReadGuid(reader), ReadGuid(reader),
                ReadManifest(reader), reader.ReadInt32());
        }
        return journal;
    }

    private static void AttachJournalReceipts(StationCheckpoint[] stations, DurableProviderReceipt[] journal)
    {
        var receiptsByStation = journal.ToLookup(receipt => receipt.StationId);
        for (int index = 0; index < stations.Length; index++)
        {
            StationCheckpoint station = stations[index];
            ReceiptCheckpoint[] receipts = receiptsByStation[station.Id]
                .Select(receipt => new ReceiptCheckpoint(receipt.Key, receipt.Result))
                .ToArray();
            stations[index] = station with { Port = station.Port with { Receipts = receipts } };
        }
    }

    private static int Validate(DurableProviderImage image, int version = 2)
    {
        Require(image is not null && image.PairId != Guid.Empty && image.NetworkId != Guid.Empty, "identity");
        Require(image!.Stations is not null && image.Stations.Length <= 65536 && image.Receipts is not null && image.Receipts.Length <= MaxEntries, "collections");
        Require(image.Revision is >= 0 and <= MaxEntries
            && image.ConfigurationRevision >= 0 && image.ConfigurationRevision <= image.Revision
            && image.Revision - image.ConfigurationRevision == image.Receipts!.Length, "journal revision");
        long length = HeaderLength + 40 + (version == 2 ? 8 : 0), entries = image.Stations!.Length + (long)image.Receipts.Length;
        var stations = new Dictionary<Guid, StationCheckpoint>();
        var cargoIds = new HashSet<Guid>();
        foreach (StationCheckpoint station in image.Stations)
        {
            Require(station is not null && station.Id != Guid.Empty && stations.TryAdd(station.Id, station), "station identity");
            PortCheckpoint port = station!.Port;
            Require(port is not null && port.MaxCargoBatches is > 0 and <= 65536 && port.MaxReceipts is > 0 and <= 65536, "port limits");
            Require(port!.Inventory is not null && port.Inventory.Length <= port.MaxCargoBatches && port.Receipts is not null && port.Receipts.Length <= port.MaxReceipts, "port collections");
            entries += port.Inventory!.Length;
            Require(entries <= MaxEntries, "resource budget");
            length += 30;
            foreach (InventoryCheckpoint cargo in port.Inventory)
            {
                Require(cargo is not null && cargo.CargoId != Guid.Empty && cargoIds.Add(cargo.CargoId), "inventory identity");
                length += 16 + ManifestSize(cargo!.Manifest);
            }
        }
        var keys = new HashSet<TransferKey>();
        var expected = new Dictionary<Guid, Dictionary<TransferKey, int>>();
        foreach (DurableProviderReceipt receipt in image.Receipts)
        {
            Require(receipt is not null && receipt.Key is not null && receipt.Key.ParcelId != Guid.Empty && keys.Add(receipt.Key), "receipt identity");
            TransferKey key = receipt!.Key;
            Require((key.Kind == (int)PortTransferKind.Extract && key.Attempt == 1)
                || (key.Kind == (int)PortTransferKind.Deposit && key.Attempt is > 0 and <= 64), "transfer key");
            Require(receipt.CargoId != Guid.Empty && stations.ContainsKey(receipt.StationId)
                && receipt.Result is (int)PortResult.Applied or (int)PortResult.Rejected, "receipt payload");
            if (!expected.TryGetValue(receipt.StationId, out Dictionary<TransferKey, int>? records))
            { records = new(); expected.Add(receipt.StationId, records); }
            records.Add(key, receipt.Result);
            length += 60 + ManifestSize(receipt.Manifest);
        }
        foreach (StationCheckpoint station in image.Stations)
        {
            expected.TryGetValue(station.Id, out Dictionary<TransferKey, int>? records);
            Require(station.Port.Receipts.Length == (records?.Count ?? 0), "station receipt count");
            var local = new HashSet<TransferKey>();
            foreach (ReceiptCheckpoint receipt in station.Port.Receipts)
            {
                Require(receipt is not null && receipt.Key is not null && local.Add(receipt.Key)
                    && records!.TryGetValue(receipt.Key, out int result) && result == receipt.Result, "station receipt mismatch");
            }
        }
        Require(entries <= MaxEntries && length <= MaxImageBytes, "resource budget");
        return checked((int)length);
    }

    private static int ManifestSize(ManifestCheckpoint value)
    {
        Require(value is not null && !string.IsNullOrWhiteSpace(value.ItemKey) && value.ItemKey.Length <= 256 && value.Quantity > 0, "manifest");
        try { return 8 + Utf8.GetByteCount(value!.ItemKey); }
        catch (EncoderFallbackException error) { throw new InvalidDataException("Provider item key has invalid Unicode.", error); }
    }
    private static void WriteManifest(BinaryWriter writer, ManifestCheckpoint value)
    { byte[] text = Utf8.GetBytes(value.ItemKey); writer.Write(text.Length); writer.Write(text); writer.Write(value.Quantity); }
    private static ManifestCheckpoint ReadManifest(BinaryReader reader)
    {
        int count = reader.ReadInt32(); Require(count is > 0 and <= 1024, "item key size");
        byte[] bytes = reader.ReadBytes(count); Require(bytes.Length == count, "item key truncation");
        return new(Utf8.GetString(bytes), reader.ReadInt32());
    }
    private static Guid ReadGuid(BinaryReader reader)
    { byte[] bytes = reader.ReadBytes(16); Require(bytes.Length == 16, "identity truncation"); return new Guid(bytes); }
    private static bool ReadBool(BinaryReader reader)
    { byte value = reader.ReadByte(); Require(value <= 1, "boolean"); return value == 1; }
    private static int Count(BinaryReader reader, ref int budget, int maximum)
    { int count = reader.ReadInt32(); Require(count >= 0 && count <= maximum && count <= budget, "collection count"); budget -= count; return count; }
    private static byte[] Hash(ReadOnlySpan<byte> bytes)
    { using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(bytes[..24]); hash.AppendData(bytes[HeaderLength..]); return hash.GetHashAndReset(); }
    private static void Require([DoesNotReturnIf(false)] bool condition, string field)
    { if (!condition) { throw new InvalidDataException("Invalid durable provider " + field + "."); } }
}
