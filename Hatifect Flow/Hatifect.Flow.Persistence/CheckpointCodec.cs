using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hatifect.Flow.Domain.Checkpoints;

namespace Hatifect.Flow.Infrastructure.Persistence;

// The fixed little-endian envelope is independent of CLR type names. Its hash
// detects corruption; it provides neither authentication nor rollback protection.
internal static class CheckpointCodec
{
    public const int MaxImageBytes = 64 * 1024 * 1024;
    public const int HeaderLength = 56;
    public const int FormatVersion = 1;
    private const int MaxJsonTokens = 1000000;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("HATFLOWC");

    public static byte[] Encode(CheckpointImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(image.Checkpoint);
        if (image.Revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(image), "Checkpoint revision cannot be negative.");
        }
        var payload = new CheckpointBuffer(MaxImageBytes - HeaderLength);
        using (var writer = new Utf8JsonWriter(payload))
        {
            CheckpointJsonWriter.Write(writer, image.Checkpoint);
        }
        ReadOnlySpan<byte> json = payload.WrittenSpan;
        using JsonDocument validated = ValidateJson(json);
        byte[] bytes = new byte[HeaderLength + json.Length];
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), FormatVersion);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12), image.Revision);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), json.Length);
        json.CopyTo(bytes.AsSpan(HeaderLength));
        HashImage(bytes).CopyTo(bytes, 24);
        return bytes;
    }

    public static CheckpointImage Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderLength || bytes.Length > MaxImageBytes)
        {
            throw new InvalidDataException("Checkpoint image length is outside the supported bounds.");
        }
        if (!bytes.Slice(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Checkpoint image has an unknown signature.");
        }
        if (BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(8)) != FormatVersion)
        {
            throw new InvalidDataException("Checkpoint format version is unsupported.");
        }
        long revision = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(12));
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(20));
        if (revision < 0 || payloadLength <= 0 || payloadLength != bytes.Length - HeaderLength)
        {
            throw new InvalidDataException("Checkpoint revision or declared payload length is invalid.");
        }
        ReadOnlySpan<byte> payload = bytes.Slice(HeaderLength);
        if (!CryptographicOperations.FixedTimeEquals(bytes.Slice(24, 32), HashImage(bytes)))
        {
            throw new InvalidDataException("Checkpoint image integrity check failed.");
        }
        using JsonDocument validated = ValidateJson(payload);
        try
        {
            FlowCheckpoint checkpoint = CheckpointJsonReader.Read(validated.RootElement);
            return new CheckpointImage(revision, checkpoint);
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException)
        {
            throw new InvalidDataException("Checkpoint payload is not a valid checkpoint document.", error);
        }
    }

    // Revision participates in the integrity boundary because it governs commits.
    // Only the digest slot itself is excluded from the digest.
    private static byte[] HashImage(ReadOnlySpan<byte> bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes.Slice(0, 24));
        hash.AppendData(bytes.Slice(HeaderLength));
        return hash.GetHashAndReset();
    }

    private static JsonDocument ValidateJson(ReadOnlySpan<byte> json)
    {
        try
        {
            ValidateTokens(json);
            var documentReader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 64 });
            JsonDocument document = JsonDocument.ParseValue(ref documentReader);
            try
            {
                CheckpointJsonSchema.Validate(document.RootElement);
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Checkpoint JSON is malformed or exceeds the depth limit.", error);
        }
        catch (InvalidOperationException error)
        {
            // JsonElement/Utf8JsonReader decode malformed Unicode escapes with
            // InvalidOperationException on .NET 6, even after JSON tokenization.
            throw new InvalidDataException("Checkpoint JSON contains invalid text encoding.", error);
        }

        static void ValidateTokens(ReadOnlySpan<byte> json)
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 64 });
            var properties = new Stack<HashSet<string>>();
            int tokens = 0;
            while (reader.Read())
            {
                if (++tokens > MaxJsonTokens)
                {
                    throw new InvalidDataException("Checkpoint JSON exceeds the token limit.");
                }
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    properties.Push(new HashSet<string>(StringComparer.Ordinal));
                }
                else if (reader.TokenType == JsonTokenType.EndObject)
                {
                    properties.Pop();
                }
                else if (reader.TokenType == JsonTokenType.PropertyName
                    && !properties.Peek().Add(reader.GetString()!))
                {
                    throw new InvalidDataException("Checkpoint JSON contains duplicate property names.");
                }
            }
        }
    }
}
