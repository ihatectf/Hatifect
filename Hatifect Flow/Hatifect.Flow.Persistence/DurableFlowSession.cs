using System;
using System.IO;
using System.Linq;
using Hatifect.Flow.Application.Dispatch;
using Hatifect.Flow.Domain.Checkpoints;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Shipments;

namespace Hatifect.Flow.Infrastructure.Persistence;

internal enum DurableCommitStage
{
    BeforeIntent, AfterIntent, BeforeProvider, AfterProvider,
    BeforeAcknowledgement, AfterAcknowledgement
}

// Owns the command boundary and two independent local stores. File IO is confined
// to this opt-in fake-provider coordinator; Core remains host-free and IO-free.
internal sealed class DurableFlowSession : IDisposable
{
    public const string CheckpointFileName = "durable-core.image";
    public const string LockFileName = "durable-core.lock";
    private readonly string _directory;
    private readonly FileStream _lease;
    private readonly DurableCargoProvider _provider;
    private readonly FlowRuntime _runtime;
    private readonly Action<DurableCommitStage>? _fault;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly Guid _pairId;
    private readonly object _owner = new();
    private long _revision;
    private bool _operating;
    private bool _faulted;
    private bool _closed;

    private DurableFlowSession(string directory, FileStream lease, DurableCargoProvider provider,
        FlowRuntime runtime, Guid pairId, long revision, Action<DurableCommitStage>? fault)
    {
        _directory = directory; _lease = lease; _provider = provider; _runtime = runtime;
        _pairId = pairId; _revision = revision; _fault = fault;
    }

    public FlowRuntime Runtime { get { RequireAvailable(); return _runtime; } }
    public Guid PairId { get { RequireAvailable(); return _pairId; } }
    public long Revision { get { RequireAvailable(); return _revision; } }
    public long ProviderRevision { get { RequireAvailable(); return _provider.Revision; } }
    public PortCheckpoint GetPortSnapshot(StationId station)
    { RequireAvailable(); return _provider.GetPort(station).CaptureCheckpoint(); }

    public static DurableFlowSession Create(string coreDirectory, string providerDirectory, FlowRuntime initial,
        Action<DurableCommitStage>? fault = null)
    {
        ArgumentNullException.ThrowIfNull(initial);
        FlowCheckpoint checkpoint = initial.CaptureCheckpoint();
        if (checkpoint.Transfers.Length != 0)
            throw new InvalidOperationException("A new durable pair requires a fixture with no issued transfers.");
        Guid pair = Guid.NewGuid();
        byte[] bytes = DurableCoreCodec.Encode(new DurableCoreImage(pair, 0, null, new CheckpointImage(0, checkpoint)));
        // Validate provider bounds before consuming the caller's fixture.
        _ = DurableProviderCodec.Encode(new DurableProviderImage(pair, checkpoint.NetworkId, 0,
            checkpoint.Stations, Array.Empty<DurableProviderReceipt>()));
        string directory = GetDirectory(coreDirectory);
        string providerPath = GetDirectory(providerDirectory);
        Directory.CreateDirectory(directory);
        FileStream lease = CheckpointWriterLease.Acquire(Path.Combine(directory, LockFileName));
        DurableCargoProvider? provider = null;
        DurableFlowSession? session = null;
        try
        {
            if (File.Exists(Path.Combine(directory, CheckpointFileName))
                || File.Exists(Path.Combine(providerPath, DurableCargoProvider.ImageFileName)))
                throw new IOException("A durable pair member already exists; open the complete pair explicitly.");
            // Consume the old fake authority before any persistent provider exists.
            object consumedOwner = new();
            initial.AttachCheckpointOwner(consumedOwner);
            initial.FenceCheckpointOwner(consumedOwner);
            FlowRuntime runtime = FlowRuntime.RestoreCheckpoint(checkpoint);
            provider = DurableCargoProvider.Create(providerPath, pair, checkpoint.NetworkId,
                checkpoint.Stations, stage => session?.ProviderFault(stage));
            session = new(directory, lease, provider, runtime, pair, 0, fault);
            session.Attach();
            session.Publish(bytes, overwrite: false, null, null);
            return session;
        }
        catch (Exception error)
        {
            if (session is not null) CheckpointWriterLease.DisposeAfterFailure(session, error);
            else
            {
                try { if (provider is not null) CheckpointWriterLease.DisposeAfterFailure(provider, error); }
                finally { CheckpointWriterLease.DisposeAfterFailure(lease, error); }
            }
            throw;
        }
    }

    public static DurableFlowSession Open(string coreDirectory, string providerDirectory,
        Action<DurableCommitStage>? fault = null)
        => OpenCore(coreDirectory, providerDirectory, fault, null, null);

    internal static DurableFlowSession OpenBound(string coreDirectory, string providerDirectory,
        Guid pairId, Guid networkId)
    {
        if (pairId == Guid.Empty || networkId == Guid.Empty)
            throw new ArgumentException("Bound pair and network identities must be non-empty.");
        return OpenCore(coreDirectory, providerDirectory, null, pairId, networkId);
    }

    private static DurableFlowSession OpenCore(string coreDirectory, string providerDirectory,
        Action<DurableCommitStage>? fault, Guid? expectedPairId, Guid? expectedNetworkId)
    {
        string directory = GetDirectory(coreDirectory);
        FileStream lease = CheckpointWriterLease.Acquire(Path.Combine(directory, LockFileName));
        DurableCargoProvider? provider = null;
        DurableFlowSession? session = null;
        try
        {
            DurableCoreImage image = DurableCoreCodec.Decode(ReadImage(Path.Combine(directory, CheckpointFileName)));
            if ((expectedPairId.HasValue && image.PairId != expectedPairId.Value)
                || (expectedNetworkId.HasValue && image.Core.Checkpoint.NetworkId != expectedNetworkId.Value))
                throw new InvalidDataException("The durable Core does not match the save binding.");
            provider = DurableCargoProvider.Open(providerDirectory, stage => session?.ProviderFault(stage));
            FlowCheckpoint restored = DurableRecovery.Recover(image, provider.CaptureImage());
            FlowRuntime runtime = FlowRuntime.RestoreCheckpoint(restored);
            session = new(directory, lease, provider, runtime, image.PairId, image.Core.Revision, fault);
            session.Attach();
            return session;
        }
        catch (Exception error)
        {
            if (session is not null) CheckpointWriterLease.DisposeAfterFailure(session, error);
            else
            {
                try { if (provider is not null) CheckpointWriterLease.DisposeAfterFailure(provider, error); }
                finally { CheckpointWriterLease.DisposeAfterFailure(lease, error); }
            }
            throw;
        }
    }

    public void Execute(Action<FlowRuntime> action)
    {
        RequireAvailable();
        ArgumentNullException.ThrowIfNull(action);
        _operating = true;
        try
        {
            _runtime.ExecuteOwned(_owner, action);
            RequireHealthy();
            Acknowledge();
        }
        catch (Exception error)
        {
            _faulted = true;
            CheckpointWriterLease.DisposeAfterFailure(new FailureCleanup(this), error);
            throw;
        }
        finally { _operating = false; }
    }

    public void SetAdmission(StationId station, bool acceptDeposits, bool acceptExtractions)
    {
        RequireAvailable();
        var intent = new AdmissionIntent(station.Value, acceptDeposits, acceptExtractions);
        // Input, no-op and budget decisions precede any command/publication scope.
        if (!_provider.ValidateAdmission(intent))
        {
            return;
        }
        _operating = true;
        try
        {
            long providerRevision = _provider.Revision;
            long revision = checked(_revision + 1);
            var image = new DurableCoreImage(_pairId, providerRevision, null,
                new CheckpointImage(revision, CaptureOwned()), intent, _provider.ConfigurationRevision);
            Publish(DurableCoreCodec.Encode(image), true, DurableCommitStage.BeforeIntent, DurableCommitStage.AfterIntent);
            _revision = revision;
            _provider.ArmAdmission(_owner, intent, providerRevision);
            _provider.ApplyAdmission(_owner, intent, providerRevision);
            RequireHealthy();
            Acknowledge();
        }
        catch (Exception error)
        {
            _faulted = true;
            CheckpointWriterLease.DisposeAfterFailure(new FailureCleanup(this), error);
            throw;
        }
        finally { _operating = false; }
    }

    public void RegisterStation(StationId station, int maxCargoBatches = 1024, int maxReceipts = 4096,
        bool acceptDeposits = true, bool acceptExtractions = true)
    {
        RequireAvailable();
        var intent = new StationRegistrationIntent(station.Value, maxCargoBatches, maxReceipts,
            acceptDeposits, acceptExtractions);
        _runtime.ExecuteOwned(_owner, runtime => runtime.ValidateDurableStation(_owner, station));
        _provider.ValidateRegistration(intent);
        long providerRevision = _provider.Revision;
        long revision = checked(_revision + 1);
        long acknowledgementRevision = checked(revision + 1);
        FlowCheckpoint checkpoint = CaptureOwned();
        var registered = new StationCheckpoint(station.Value,
            new PortCheckpoint(maxCargoBatches, maxReceipts, acceptDeposits, acceptExtractions,
                Array.Empty<InventoryCheckpoint>(), Array.Empty<ReceiptCheckpoint>()));
        // The post-effect Core image must fit too. A provider-only size preflight
        // could otherwise commit a Station that Core cannot acknowledge.
        _ = DurableCoreCodec.Encode(new DurableCoreImage(_pairId, checked(providerRevision + 1), null,
            new CheckpointImage(acknowledgementRevision,
                checkpoint with { Stations = checkpoint.Stations.Append(registered).ToArray() }),
            ProviderConfigurationRevision: checked(_provider.ConfigurationRevision + 1)));
        _operating = true;
        try
        {
            var image = new DurableCoreImage(_pairId, providerRevision, null,
                new CheckpointImage(revision, checkpoint),
                ProviderConfigurationRevision: _provider.ConfigurationRevision, Registration: intent);
            Publish(DurableCoreCodec.Encode(image), true, DurableCommitStage.BeforeIntent, DurableCommitStage.AfterIntent);
            _revision = revision;
            _provider.ArmRegistration(_owner, intent, providerRevision);
            _provider.ApplyRegistration(_owner, intent, providerRevision);
            _runtime.ExecuteOwned(_owner, runtime => runtime.AddDurableStation(_owner, station, _provider.GetPort(station)));
            RequireHealthy();
            Acknowledge();
        }
        catch (Exception error)
        {
            _faulted = true;
            CheckpointWriterLease.DisposeAfterFailure(new FailureCleanup(this), error);
            throw;
        }
        finally { _operating = false; }
    }

    public void ProvisionCargo(CargoId cargo, StationId station, CargoManifest manifest)
    {
        RequireAvailable();
        ArgumentNullException.ThrowIfNull(manifest);
        _runtime.ExecuteOwned(_owner, runtime => runtime.ValidateDurableCargo(_owner, cargo, station, manifest));
        var intent = new CargoProvisionIntent(cargo.Value, station.Value, CheckpointValues.Capture(manifest));
        _provider.ValidateProvision(intent);
        long providerRevision = _provider.Revision;
        long revision = checked(_revision + 1);
        long acknowledgementRevision = checked(revision + 1);
        FlowCheckpoint checkpoint = CaptureOwned();
        FlowCheckpoint projected = CargoProvisionProjection.Core(checkpoint, intent);
        _ = FlowRuntime.RestoreCheckpoint(projected);
        _ = DurableCoreCodec.Encode(new DurableCoreImage(_pairId, checked(providerRevision + 1), null,
            new CheckpointImage(acknowledgementRevision, projected),
            ProviderConfigurationRevision: checked(_provider.ConfigurationRevision + 1)));
        var image = new DurableCoreImage(_pairId, providerRevision, null,
            new CheckpointImage(revision, checkpoint),
            ProviderConfigurationRevision: _provider.ConfigurationRevision, Provision: intent);
        byte[] intentBytes = DurableCoreCodec.Encode(image);
        _operating = true;
        try
        {
            Publish(intentBytes, true, DurableCommitStage.BeforeIntent, DurableCommitStage.AfterIntent);
            _revision = revision;
            _provider.ArmProvision(_owner, intent, providerRevision);
            _provider.ApplyProvision(_owner, intent, providerRevision);
            _runtime.ExecuteOwned(_owner, runtime => runtime.AddDurableCargo(_owner, cargo, station, manifest));
            RequireHealthy();
            Acknowledge();
        }
        catch (Exception error)
        {
            _faulted = true;
            CheckpointWriterLease.DisposeAfterFailure(new FailureCleanup(this), error);
            throw;
        }
        finally { _operating = false; }
    }

    public void SetPortCapacity(StationId station, int maxCargoBatches, int maxReceipts)
    {
        RequireAvailable();
        var intent = new PortCapacityIntent(station.Value, maxCargoBatches, maxReceipts);
        // No-op preserves even an older pending intent and consumes no revisions.
        if (!_provider.ValidateCapacity(intent))
        {
            return;
        }
        long providerRevision = _provider.Revision;
        long revision = checked(_revision + 1);
        long acknowledgementRevision = checked(revision + 1);
        FlowCheckpoint checkpoint = CaptureOwned();
        FlowCheckpoint projected = PortCapacityProjection.Core(checkpoint, intent);
        _ = FlowRuntime.RestoreCheckpoint(projected);
        _ = DurableCoreCodec.Encode(new DurableCoreImage(_pairId, checked(providerRevision + 1), null,
            new CheckpointImage(acknowledgementRevision, projected),
            ProviderConfigurationRevision: checked(_provider.ConfigurationRevision + 1)));
        var image = new DurableCoreImage(_pairId, providerRevision, null,
            new CheckpointImage(revision, checkpoint),
            ProviderConfigurationRevision: _provider.ConfigurationRevision, Capacity: intent);
        byte[] intentBytes = DurableCoreCodec.Encode(image);
        _operating = true;
        try
        {
            Publish(intentBytes, true, DurableCommitStage.BeforeIntent, DurableCommitStage.AfterIntent);
            _revision = revision;
            _provider.ArmCapacity(_owner, intent, providerRevision);
            _provider.ApplyCapacity(_owner, intent, providerRevision);
            // Existing bound Ports capture current provider metadata. No Core
            // owner, capability, cargo claim or route needs to be replaced.
            RequireHealthy();
            Acknowledge();
        }
        catch (Exception error)
        {
            _faulted = true;
            CheckpointWriterLease.DisposeAfterFailure(new FailureCleanup(this), error);
            throw;
        }
        finally { _operating = false; }
    }

    public void Dispose() { RequireThreadAndIdle(); FenceAndClose(); }

    private FlowCheckpoint CaptureOwned()
    {
        FlowCheckpoint? checkpoint = null;
        _runtime.ExecuteOwned(_owner, runtime => checkpoint = runtime.CaptureCheckpoint());
        return checkpoint!;
    }

    private void Acknowledge()
    {
        FlowCheckpoint checkpoint = CaptureOwned();
        long revision = checked(_revision + 1);
        var image = new DurableCoreImage(_pairId, _provider.Revision, null,
            new CheckpointImage(revision, checkpoint), ProviderConfigurationRevision: _provider.ConfigurationRevision);
        // Publication callbacks run after the mutation permit has closed.
        Publish(DurableCoreCodec.Encode(image), true,
            DurableCommitStage.BeforeAcknowledgement, DurableCommitStage.AfterAcknowledgement);
        _revision = revision;
    }

    private void Attach()
    {
        _runtime.AttachCheckpointOwner(_owner);
        _provider.BindAdmissionOwner(_owner);
        _runtime.BindDurableOwner(_owner, _provider.GetPort, WriteIntent);
    }

    private void WriteIntent(PortTransfer transfer)
    {
        try
        {
            RequireHealthy();
            FlowCheckpoint checkpoint = _runtime.CaptureTransferIntent(_owner, transfer);
            long revision = checked(_revision + 1);
            var image = new DurableCoreImage(_pairId, _provider.Revision, CheckpointValues.Key(transfer.Id),
                new CheckpointImage(revision, checkpoint), ProviderConfigurationRevision: _provider.ConfigurationRevision);
            Publish(DurableCoreCodec.Encode(image), true, DurableCommitStage.BeforeIntent, DurableCommitStage.AfterIntent);
            _revision = revision;
            _provider.ArmIntent(transfer);
        }
        catch { _faulted = true; throw; }
    }

    private void ProviderFault(CheckpointPublishStage stage)
    {
        try
        {
            if (_faulted || _closed) throw new InvalidOperationException("Durable session is faulted.");
            _fault?.Invoke(stage == CheckpointPublishStage.BeforePublish
                ? DurableCommitStage.BeforeProvider : DurableCommitStage.AfterProvider);
        }
        catch { _faulted = true; throw; }
    }

    private void Publish(byte[] bytes, bool overwrite, DurableCommitStage? before, DurableCommitStage? after)
    {
        string candidate = Path.Combine(_directory, ".durable-core-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool own = false;
        try
        {
            using (var output = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { own = true; output.Write(bytes); output.Flush(flushToDisk: true); }
            if (before.HasValue) _fault?.Invoke(before.Value);
            File.Move(candidate, Path.Combine(_directory, CheckpointFileName), overwrite);
            own = false;
            if (after.HasValue) _fault?.Invoke(after.Value);
        }
        catch (Exception error)
        {
            _faulted = true;
            if (own)
            {
                try { File.Delete(candidate); }
                catch (Exception cleanup) { throw new AggregateException("Core publication and cleanup failed.", error, cleanup); }
            }
            throw;
        }
    }

    private void RequireHealthy()
    {
        if (_closed) throw new ObjectDisposedException(nameof(DurableFlowSession));
        if (_faulted) throw new InvalidOperationException("Durable command outcome is uncertain; reopen the pair.");
        _ = _provider.Revision;
    }
    private void FenceAndClose()
    {
        if (!_closed)
        {
            _runtime.FenceCheckpointOwner(_owner);
            _closed = true;
        }
        try { _provider.Dispose(); }
        finally { _lease.Dispose(); }
    }
    private void RequireAvailable() { RequireThreadAndIdle(); RequireHealthy(); }
    private void RequireThreadAndIdle()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread || _operating)
            throw new InvalidOperationException("Durable session requires its owning thread outside an active command.");
    }
    private sealed class FailureCleanup : IDisposable
    {
        private readonly DurableFlowSession _session;
        public FailureCleanup(DurableFlowSession session) { _session = session; }
        public void Dispose() => _session.FenceAndClose();
    }
    private static string GetDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A durable directory is required.", nameof(directory));
        return Path.GetFullPath(directory);
    }
    private static byte[] ReadImage(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length < DurableCoreCodec.MinimumHeaderLength || input.Length > DurableCoreCodec.MaxImageBytes)
            throw new InvalidDataException("Durable Core image exceeds its bounds.");
        byte[] bytes = new byte[(int)input.Length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int count = input.Read(bytes, offset, bytes.Length - offset);
            if (count == 0) throw new InvalidDataException("Durable Core image is truncated.");
            offset += count;
        }
        if (input.ReadByte() != -1) throw new InvalidDataException("Durable Core image changed during reading.");
        return bytes;
    }
}
