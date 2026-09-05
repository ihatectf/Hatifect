using System;
using System.IO;

namespace Hatifect.Flow.Infrastructure.Persistence;

internal static class CheckpointWriterLease
{
    internal static FileStream Acquire(string path)
    {
        int expectedSharingError = OperatingSystem.IsWindows() ? unchecked((int)0x80070020)
            : OperatingSystem.IsMacOS() ? 35
            : OperatingSystem.IsLinux() ? 11
            : throw new PlatformNotSupportedException("Checkpoint writer leases require a verified file-lock platform.");
        string? environment = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING");
        if ((AppContext.TryGetSwitch("System.IO.DisableFileLocking", out bool disabled) && disabled)
            || string.Equals(environment, "true", StringComparison.OrdinalIgnoreCase) || environment == "1")
        {
            throw new PlatformNotSupportedException("Checkpoint writer leases require enabled file locking.");
        }

        // Keep this inode stable across sessions. Deleting a lock file would let
        // another process acquire a different inode while this lease still lives.
        var lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            // .NET 6 on Unix can ignore unsupported locking errors. A second
            // independent open must fail specifically with sharing contention;
            // permissions or arbitrary IO failures are not proof of exclusivity.
            bool exclusive = false;
            try
            {
                using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when (error.HResult == expectedSharingError)
            {
                exclusive = true;
            }
            if (!exclusive)
            {
                throw new PlatformNotSupportedException("The checkpoint directory does not provide an exclusive writer lease.");
            }
            return lease;
        }
        catch (Exception error)
        {
            DisposeAfterFailure(lease, error);
            throw;
        }
    }

    internal static void DisposeAfterFailure(IDisposable resource, Exception original)
    {
        try
        {
            resource.Dispose();
        }
        catch (Exception cleanup)
        {
            throw new AggregateException("Checkpoint operation and resource cleanup both failed.", original, cleanup);
        }
    }
}
