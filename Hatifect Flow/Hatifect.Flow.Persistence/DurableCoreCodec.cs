using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Hatifect.Flow.Domain.Checkpoints;
using Hatifect.Flow.Domain.Ports;

namespace Hatifect.Flow.Infrastructure.Persistence;

// This envelope pairs a Core checkpoint with an independently durable provider.
// Intent is explicit: older uncertain Parcels may coexist with the next request.
internal static class DurableCoreCodec
{
    public const int MinimumHeaderLength = 97;
    private const int AdmissionHeaderLength = 124;
    private const int RegistrationHeaderLength = 151;
    private const int ProvisionHeaderLength = 156;
    public const int HeaderLength = 181;
    private const int ProvisionPrefixLength = 36;
    private const int MaxProvisionBytes = ProvisionPrefixLength + 1024;
    public const int MaxImageBytes = CheckpointCodec.MaxImageBytes + HeaderLength + MaxProvisionBytes;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("HATFLOWD");

    public static byte[] Encode(DurableCoreImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Validate(image.PairId, image.ProviderRevision, image.Intent, image.Admission, image.ProviderConfigurationRevision, image.Registration, image.Provision, image.Capacity);
        byte[] core = CheckpointCodec.Encode(image.Core);
        byte[] provisionKey = image.Provision is null ? Array.Empty<byte>() : Utf8.GetBytes(image.Provision.Manifest.ItemKey);
        int provisionLength = image.Provision is null ? 0 : ProvisionPrefixLength + provisionKey.Length;
        byte[] bytes = new byte[HeaderLength + provisionLength + core.Length];
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 5);
        image.PairId.TryWriteBytes(bytes.AsSpan(12, 16));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(28), image.ProviderRevision);
        bytes[36] = image.Intent is null ? (byte)0 : (byte)1;
        if (image.Intent is not null)
        {
            image.Intent.ParcelId.TryWriteBytes(bytes.AsSpan(37, 16));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(53), image.Intent.Kind);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(57), image.Intent.Attempt);
        }
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(61), core.Length);
        WriteVersionedIntents(bytes, image, provisionKey, provisionLength);
        core.CopyTo(bytes, HeaderLength + provisionLength);
        Hash(bytes).CopyTo(bytes, 65);
        return bytes;

        static void WriteVersionedIntents(byte[] bytes, DurableCoreImage image,
            byte[] provisionKey, int provisionLength)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(97), image.ProviderConfigurationRevision);
            bytes[105] = image.Admission is null ? (byte)0 : (byte)1;
            if (image.Admission is not null)
            {
                image.Admission.StationId.TryWriteBytes(bytes.AsSpan(106, 16));
                bytes[122] = image.Admission.AcceptDeposits ? (byte)1 : (byte)0;
                bytes[123] = image.Admission.AcceptExtractions ? (byte)1 : (byte)0;
            }
            bytes[124] = image.Registration is null ? (byte)0 : (byte)1;
            if (image.Registration is not null)
            {
                image.Registration.StationId.TryWriteBytes(bytes.AsSpan(125, 16));
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(141), image.Registration.MaxCargoBatches);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(145), image.Registration.MaxReceipts);
                bytes[149] = image.Registration.AcceptDeposits ? (byte)1 : (byte)0;
                bytes[150] = image.Registration.AcceptExtractions ? (byte)1 : (byte)0;
            }
            bytes[151] = image.Provision is null ? (byte)0 : (byte)1;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(152), provisionLength);
            if (image.Provision is not null)
            {
                image.Provision.CargoId.TryWriteBytes(bytes.AsSpan(HeaderLength, 16));
                image.Provision.StationId.TryWriteBytes(bytes.AsSpan(HeaderLength + 16, 16));
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(HeaderLength + 32), image.Provision.Manifest.Quantity);
                provisionKey.CopyTo(bytes, HeaderLength + ProvisionPrefixLength);
            }
            bytes[156] = image.Capacity is null ? (byte)0 : (byte)1;
            if (image.Capacity is not null)
            {
                image.Capacity.StationId.TryWriteBytes(bytes.AsSpan(157, 16));
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(173), image.Capacity.MaxCargoBatches);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(177), image.Capacity.MaxReceipts);
            }
        }
    }

    public static DurableCoreImage Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < MinimumHeaderLength + CheckpointCodec.HeaderLength || bytes.Length > MaxImageBytes
            || !bytes[..8].SequenceEqual(Magic))
        { throw new InvalidDataException("Durable Core envelope size or signature is invalid."); }
        int version = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        int headerLength = version switch
        {
            1 => MinimumHeaderLength,
            2 => AdmissionHeaderLength,
            3 => RegistrationHeaderLength,
            4 => ProvisionHeaderLength,
            5 => HeaderLength,
            _ => throw new InvalidDataException("Durable Core envelope version is invalid.")
        };
        if (bytes.Length < headerLength + CheckpointCodec.HeaderLength)
        { throw new InvalidDataException("Durable Core envelope is truncated."); }
        int provisionLength = version >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(bytes[152..]) : 0;
        if (provisionLength < 0 || provisionLength > MaxProvisionBytes
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[61..]) != bytes.Length - headerLength - provisionLength
            || bytes.Length - headerLength - provisionLength < CheckpointCodec.HeaderLength
            || !CryptographicOperations.FixedTimeEquals(bytes.Slice(65, 32), Hash(bytes)))
        { throw new InvalidDataException("Durable Core envelope length or integrity is invalid."); }
        Guid pairId = new(bytes.Slice(12, 16));
        long revision = BinaryPrimitives.ReadInt64LittleEndian(bytes[28..]);
        TransferKey? intent = ReadTransferIntent(bytes);
        AdmissionIntent? admission = null;
        long configurationRevision = 0;
        if (version >= 2)
        {
            configurationRevision = BinaryPrimitives.ReadInt64LittleEndian(bytes[97..]);
            admission = ReadAdmissionIntent(bytes);
        }
        StationRegistrationIntent? registration = version >= 3 ? ReadRegistrationIntent(bytes) : null;
        CargoProvisionIntent? provision = version >= 4
            ? ReadProvisionIntent(bytes, headerLength, provisionLength)
            : null;
        PortCapacityIntent? capacity = version >= 5 ? ReadCapacityIntent(bytes) : null;
        Validate(pairId, revision, intent, admission, configurationRevision, registration, provision, capacity);
        return new DurableCoreImage(pairId, revision, intent, CheckpointCodec.Decode(bytes[(headerLength + provisionLength)..]),
            admission, configurationRevision, registration, provision, capacity);

        static TransferKey? ReadTransferIntent(ReadOnlySpan<byte> bytes)
        {
            if (bytes[36] == 1)
            {
                return new TransferKey(new Guid(bytes.Slice(37, 16)),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes[53..]),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes[57..]));
            }
            if (bytes[36] == 0 && bytes.Slice(37, 24).SequenceEqual(new byte[24])) return null;
            throw new InvalidDataException("Durable Core intent discriminator or reserved bytes are invalid.");
        }

        static AdmissionIntent? ReadAdmissionIntent(ReadOnlySpan<byte> bytes)
        {
            if (bytes[105] == 1 && bytes[122] <= 1 && bytes[123] <= 1)
                return new AdmissionIntent(new Guid(bytes.Slice(106, 16)), bytes[122] == 1, bytes[123] == 1);
            if (bytes[105] == 0 && bytes.Slice(106, 18).SequenceEqual(new byte[18])) return null;
            throw new InvalidDataException("Durable Core admission discriminator or reserved bytes are invalid.");
        }

        static StationRegistrationIntent? ReadRegistrationIntent(ReadOnlySpan<byte> bytes)
        {
            if (bytes[124] == 1 && bytes[149] <= 1 && bytes[150] <= 1)
            {
                return new StationRegistrationIntent(new Guid(bytes.Slice(125, 16)),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes[141..]),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes[145..]), bytes[149] == 1, bytes[150] == 1);
            }
            if (bytes[124] == 0 && bytes.Slice(125, 26).SequenceEqual(new byte[26])) return null;
            throw new InvalidDataException("Durable Core registration discriminator or reserved bytes are invalid.");
        }

        static CargoProvisionIntent? ReadProvisionIntent(ReadOnlySpan<byte> bytes,
            int headerLength, int provisionLength)
        {
            if (bytes[151] == 1 && provisionLength > ProvisionPrefixLength)
            {
                string itemKey;
                try { itemKey = Utf8.GetString(bytes.Slice(headerLength + ProvisionPrefixLength, provisionLength - ProvisionPrefixLength)); }
                catch (DecoderFallbackException error)
                { throw new InvalidDataException("Durable Core provision item key has invalid UTF8.", error); }
                return new CargoProvisionIntent(new Guid(bytes.Slice(headerLength, 16)),
                    new Guid(bytes.Slice(headerLength + 16, 16)),
                    new ManifestCheckpoint(itemKey, BinaryPrimitives.ReadInt32LittleEndian(bytes[(headerLength + 32)..])));
            }
            if (bytes[151] == 0 && provisionLength == 0) return null;
            throw new InvalidDataException("Durable Core provision discriminator or reserved length is invalid.");
        }

        static PortCapacityIntent? ReadCapacityIntent(ReadOnlySpan<byte> bytes)
        {
            if (bytes[156] == 1)
            {
                return new PortCapacityIntent(new Guid(bytes.Slice(157, 16)),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes[173..]),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes[177..]));
            }
            if (bytes[156] == 0 && bytes.Slice(157, 24).SequenceEqual(new byte[24])) return null;
            throw new InvalidDataException("Durable Core capacity discriminator or reserved bytes are invalid.");
        }
    }

    private static void Validate(Guid pairId, long revision, TransferKey? intent,
        AdmissionIntent? admission, long configurationRevision, StationRegistrationIntent? registration,
        CargoProvisionIntent? provision, PortCapacityIntent? capacity)
    {
        if (pairId == Guid.Empty || revision < 0 || revision > 262144
            || configurationRevision < 0 || configurationRevision > revision)
        { throw new InvalidDataException("Durable Core pair identity or provider revision is invalid."); }
        if (admission is not null && (admission.StationId == Guid.Empty || intent is not null))
        { throw new InvalidDataException("Durable Core admission identity or mutually exclusive intent is invalid."); }
        if (registration is not null && (registration.StationId == Guid.Empty
            || registration.MaxCargoBatches is < 1 or > 65536 || registration.MaxReceipts is < 1 or > 65536
            || intent is not null || admission is not null))
        { throw new InvalidDataException("Durable Core registration identity, limits or mutually exclusive intent is invalid."); }
        if (provision is not null)
        {
            if (provision.CargoId == Guid.Empty || provision.StationId == Guid.Empty
                || provision.Manifest is null || string.IsNullOrWhiteSpace(provision.Manifest.ItemKey)
                || provision.Manifest.ItemKey.Length > 256 || provision.Manifest.Quantity <= 0
                || intent is not null || admission is not null || registration is not null)
            { throw new InvalidDataException("Durable Core provision identity, manifest or mutually exclusive intent is invalid."); }
            try { _ = Utf8.GetByteCount(provision.Manifest.ItemKey); }
            catch (EncoderFallbackException error)
            { throw new InvalidDataException("Durable Core provision item key has invalid Unicode.", error); }
        }
        if (capacity is not null && (capacity.StationId == Guid.Empty
            || capacity.MaxCargoBatches is < 1 or > 65536 || capacity.MaxReceipts is < 1 or > 65536
            || intent is not null || admission is not null || registration is not null || provision is not null))
        { throw new InvalidDataException("Durable Core capacity identity, limits or mutually exclusive intent is invalid."); }
        if (intent is not null && (intent.ParcelId == Guid.Empty
            || !((intent.Kind == (int)PortTransferKind.Extract && intent.Attempt == 1)
                || (intent.Kind == (int)PortTransferKind.Deposit && intent.Attempt is > 0 and <= 64))))
        { throw new InvalidDataException("Durable Core intent key is invalid."); }
    }

    private static byte[] Hash(ReadOnlySpan<byte> bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes[..65]); hash.AppendData(bytes[MinimumHeaderLength..]);
        return hash.GetHashAndReset();
    }
}
