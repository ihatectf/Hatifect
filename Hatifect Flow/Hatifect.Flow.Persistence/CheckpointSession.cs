using System;
using System.Collections.Generic;
using System.IO;
using Hatifect.Flow.Application.Dispatch;

namespace Hatifect.Flow.Infrastructure.Persistence;

internal enum CheckpointPublishStage
{
    BeforePublish,
    AfterPublish
}

// One cooperative local writer owns one complete fake world. Changes in Runtime
// are provisional until Commit publishes Core, inventories and receipts together.
// No real game save or independently committed inventory participates here.
internal sealed class CheckpointSession : IDisposable
{
    public const string CheckpointFileName = "flow.checkpoint";
    public const string LockFileName = "flow.checkpoint.lock";
    private readonly string _directory;
    private readonly FileStream _lease;
    private readonly FlowRuntime _runtime;
    private readonly Action<CheckpointPublishStage>? _publicationFault;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private long _revision;
    private bool _closed;
    private bool _operating;

    private CheckpointSession(string directory, FileStream lease, FlowRuntime runtime,
        long revision, Action<CheckpointPublishStage>? publicationFault)
    {
        _directory = directory;
        _lease = lease;
        _runtime = runtime;
        _revision = revision;
        _publicationFault = publicationFault;
    }

    public FlowRuntime Runtime
    {
        get { RequireAvailable(); return _runtime; }
    }

    public long Revision
    {
        get { RequireAvailable(); return _revision; }
    }

    public static CheckpointSession Create(string directory, FlowRuntime runtime,
        Action<CheckpointPublishStage>? publicationFault = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        string fullDirectory = GetDirectory(directory);
        Directory.CreateDirectory(fullDirectory);
        FileStream lease = CheckpointWriterLease.Acquire(Path.Combine(fullDirectory, LockFileName));
        CheckpointSession? session = null;
        bool attached = false;
        try
        {
            if (File.Exists(Path.Combine(fullDirectory, CheckpointFileName)))
            {
                throw new IOException("A committed checkpoint already exists; open it explicitly.");
            }
            // Validation and encoding precede attachment. An unsupported Port or
            // malformed fake world cannot take ownership of the supplied runtime.
            byte[] bytes = CheckpointCodec.Encode(new CheckpointImage(0, runtime.CaptureCheckpoint()));
            session = new CheckpointSession(fullDirectory, lease, runtime, 0, publicationFault);
            runtime.AttachCheckpointOwner(session);
            attached = true;
            session._operating = true;
            try
            {
                session.Publish(bytes, overwrite: false);
            }
            finally
            {
                session._operating = false;
            }
            return session;
        }
        catch (Exception error)
        {
            // Publish already fences on failure. Other initialization failures
            // release only a lease that has no active attached runtime.
            if (attached)
            {
                CheckpointWriterLease.DisposeAfterFailure(session!, error);
            }
            else
            {
                CheckpointWriterLease.DisposeAfterFailure(lease, error);
            }
            throw;
        }
    }

    public static CheckpointSession Open(string directory,
        Action<CheckpointPublishStage>? publicationFault = null)
    {
        string fullDirectory = GetDirectory(directory);
        FileStream lease = CheckpointWriterLease.Acquire(Path.Combine(fullDirectory, LockFileName));
        try
        {
            CheckpointImage image = CheckpointCodec.Decode(ReadImage(Path.Combine(fullDirectory, CheckpointFileName)));
            FlowRuntime runtime = FlowRuntime.RestoreCheckpoint(image.Checkpoint);
            var session = new CheckpointSession(fullDirectory, lease, runtime, image.Revision, publicationFault);
            runtime.AttachCheckpointOwner(session);
            return session;
        }
        catch (Exception error)
        {
            CheckpointWriterLease.DisposeAfterFailure(lease, error);
            throw;
        }
    }

    public void Commit(long expectedRevision)
    {
        RequireAvailable();
        if (expectedRevision != _revision)
        {
            throw new InvalidOperationException("Checkpoint revision has changed; this commit is stale.");
        }
        long nextRevision = checked(_revision + 1);
        _operating = true;
        try
        {
            // A capture callback cannot recursively commit/dispose this session.
            // Capture must unwind Core's mutation guard before publication starts.
            byte[] bytes = CheckpointCodec.Encode(new CheckpointImage(nextRevision, _runtime.CaptureCheckpoint()));
            Publish(bytes, overwrite: true);
            _revision = nextRevision;
        }
        finally
        {
            _operating = false;
        }
    }

    public void Dispose()
    {
        RequireThreadAndIdle();
        FenceAndClose();
    }

    private void Publish(byte[] bytes, bool overwrite)
    {
        string candidate = Path.Combine(_directory, ".flow-checkpoint-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool ownCandidate = false;
        try
        {
            using (var output = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownCandidate = true;
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            // Test seam is outside the inventory/receipt mutation boundary and
            // outside the file write. It models an unknown publication outcome.
            _publicationFault?.Invoke(CheckpointPublishStage.BeforePublish);
            File.Move(candidate, Path.Combine(_directory, CheckpointFileName), overwrite);
            ownCandidate = false;
            _publicationFault?.Invoke(CheckpointPublishStage.AfterPublish);
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            try
            {
                FenceAndClose();
            }
            catch (Exception closing)
            {
                failures.Add(closing);
            }
            if (ownCandidate)
            {
                try
                {
                    File.Delete(candidate);
                }
                catch (Exception cleanup)
                {
                    failures.Add(cleanup);
                }
            }
            if (failures.Count > 1)
            {
                throw new AggregateException("Checkpoint publication and cleanup failed.", failures);
            }
            throw;
        }
    }

    private void FenceAndClose()
    {
        if (!_closed)
        {
            // If fencing fails, retain the lease. Releasing it while the old
            // runtime remains active would permit two writers to own one world.
            _runtime.FenceCheckpointOwner(this);
            _closed = true;
        }
        _lease.Dispose();
    }

    private void RequireAvailable()
    {
        RequireThreadAndIdle();
        if (_closed)
        {
            throw new ObjectDisposedException(nameof(CheckpointSession));
        }
    }

    private void RequireThreadAndIdle()
    {
        if (_ownerThread != Environment.CurrentManagedThreadId || _operating)
        {
            throw new InvalidOperationException("Checkpoint session requires its owning thread outside an active operation.");
        }
    }

    private static string GetDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("A checkpoint directory is required.", nameof(directory));
        }
        return Path.GetFullPath(directory);
    }

    private static byte[] ReadImage(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length < CheckpointCodec.HeaderLength || input.Length > CheckpointCodec.MaxImageBytes)
        {
            throw new InvalidDataException("Checkpoint file length is outside the supported bounds.");
        }
        byte[] bytes = new byte[checked((int)input.Length)];
        int read = 0;
        while (read < bytes.Length)
        {
            int count = input.Read(bytes, read, bytes.Length - read);
            if (count == 0)
            {
                throw new InvalidDataException("Checkpoint file ended before its declared length.");
            }
            read += count;
        }
        if (input.ReadByte() != -1)
        {
            throw new InvalidDataException("Checkpoint file changed while being read.");
        }
        return bytes;
    }
}
