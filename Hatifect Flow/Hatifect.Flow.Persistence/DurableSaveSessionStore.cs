using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Hatifect.Flow.Application.Dispatch;

namespace Hatifect.Flow.Infrastructure.Persistence;

// Cold lifecycle boundary: derives both pair paths and binds their identities.
// Binding is immutable. Incomplete creation requires explicit investigation,
// never adoption of an unbound pair or automatic replacement of existing state.
internal sealed class DurableSaveSessionStore
{
    public const string BindingFileName = "save.binding";
    public const string LockFileName = "save.lock";
    private const int BindingLength = 80;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("HTFLSAV1");
    private readonly string _root;

    public DurableSaveSessionStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("A save store root is required.", nameof(root));
        _root = Path.GetFullPath(root);
        RejectLinks(_root);
    }

    public string GetSaveDirectory(FlowSaveIdentity save)
    {
        if (save.Value == 0) throw new ArgumentOutOfRangeException(nameof(save));
        string directory = Path.Combine(_root, "save-" + save);
        ValidatePaths(directory);
        return directory;
    }

    public DurableFlowSession Create(FlowSaveIdentity save, FlowRuntime initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        string directory = GetSaveDirectory(save);
        Directory.CreateDirectory(directory);
        using FileStream bindingLease = CheckpointWriterLease.Acquire(Path.Combine(directory, LockFileName));
        ValidatePaths(directory);
        // Reject unknown or incomplete content before consuming the caller's
        // runtime. A stable lock inode is the only permitted prior entry.
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            if (!string.Equals(Path.GetFileName(entry), LockFileName, StringComparison.Ordinal))
                throw new IOException("Save state already exists; only an explicitly bound pair can be opened.");

        DurableFlowSession session = DurableFlowSession.Create(
            Path.Combine(directory, "core"), Path.Combine(directory, "provider"), initial);
        try
        {
            byte[] bytes = EncodeBinding(save, session);
            PublishBinding(directory, bytes);
            return session;
        }
        catch (Exception error)
        {
            CheckpointWriterLease.DisposeAfterFailure(session, error);
            throw;
        }
    }

    public DurableFlowSession Open(FlowSaveIdentity save)
    {
        string directory = GetSaveDirectory(save);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("No durable state exists for this save.");
        using FileStream bindingLease = CheckpointWriterLease.Acquire(Path.Combine(directory, LockFileName));
        ValidatePaths(directory);
        byte[] bytes = ReadBinding(Path.Combine(directory, BindingFileName));
        (Guid pair, Guid network) = DecodeBinding(bytes, save);
        return DurableFlowSession.OpenBound(Path.Combine(directory, "core"), Path.Combine(directory, "provider"), pair, network);
    }

    private static byte[] EncodeBinding(FlowSaveIdentity save, DurableFlowSession session)
    {
        byte[] bytes = new byte[BindingLength];
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8, 8), save.Value);
        session.PairId.TryWriteBytes(bytes.AsSpan(16, 16));
        session.Runtime.NetworkId.Value.TryWriteBytes(bytes.AsSpan(32, 16));
        SHA256.HashData(bytes.AsSpan(0, 48)).CopyTo(bytes, 48);
        return bytes;
    }

    private static (Guid Pair, Guid Network) DecodeBinding(byte[] bytes, FlowSaveIdentity save)
    {
        if (!bytes.AsSpan(0, 8).SequenceEqual(Magic)
            || BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8, 8)) != save.Value
            || !CryptographicOperations.FixedTimeEquals(bytes.AsSpan(48, 32), SHA256.HashData(bytes.AsSpan(0, 48))))
            throw new InvalidDataException("The durable binding does not identify this save or has invalid integrity.");
        Guid pair = new(bytes.AsSpan(16, 16));
        Guid network = new(bytes.AsSpan(32, 16));
        if (pair == Guid.Empty || network == Guid.Empty)
            throw new InvalidDataException("The durable binding contains an empty pair or network identity.");
        return (pair, network);
    }

    private static byte[] ReadBinding(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != BindingLength) throw new InvalidDataException("Invalid save binding length.");
        byte[] bytes = new byte[BindingLength];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int count = input.Read(bytes, offset, bytes.Length - offset);
            if (count == 0) throw new InvalidDataException("Truncated save binding.");
            offset += count;
        }
        if (input.ReadByte() != -1) throw new InvalidDataException("Save binding changed while reading.");
        return bytes;
    }

    private static void PublishBinding(string directory, byte[] bytes)
    {
        string temporary = Path.Combine(directory, ".save-binding-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool owned = false;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { owned = true; output.Write(bytes); output.Flush(true); }
            File.Move(temporary, Path.Combine(directory, BindingFileName), false);
            owned = false;
        }
        catch (Exception error)
        {
            if (owned)
            {
                try { File.Delete(temporary); }
                catch (Exception cleanup) { throw new AggregateException("Save binding publication and cleanup failed.", error, cleanup); }
            }
            throw;
        }
    }

    private static void ValidatePaths(string directory)
    {
        RejectLinks(directory);
        RejectLinks(Path.Combine(directory, BindingFileName));
        RejectLinks(Path.Combine(directory, LockFileName));
        RejectLinks(Path.Combine(directory, "core", DurableFlowSession.CheckpointFileName));
        RejectLinks(Path.Combine(directory, "core", DurableFlowSession.LockFileName));
        RejectLinks(Path.Combine(directory, "provider", DurableCargoProvider.ImageFileName));
        RejectLinks(Path.Combine(directory, "provider", DurableCargoProvider.LockFileName));
    }

    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new IOException("Durable save paths must not traverse symbolic links.");
        }
    }
}
