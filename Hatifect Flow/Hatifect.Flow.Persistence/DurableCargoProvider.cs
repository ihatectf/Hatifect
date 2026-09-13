using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hatifect.Flow.Domain.Checkpoints;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Shipments;

namespace Hatifect.Flow.Infrastructure.Persistence;

// A complete fake inventory and retained receipt journal commit independently
// from Core. Only the coordinator can arm a request after its intent is durable.
internal sealed partial class DurableCargoProvider : IDisposable
{
    public const string ImageFileName = "provider.image";
    public const string LockFileName = "provider.lock";
    private readonly string _directory;
    private readonly FileStream _lease;
    private readonly Action<CheckpointPublishStage>? _publicationFault;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private Dictionary<Guid, ProviderPort> _ports = new();
    private DurableProviderImage _image;
    private Dictionary<TransferKey, DurableProviderReceipt> _receipts;
    private Dictionary<Guid, StationCheckpoint> _stations;
    private Dictionary<Guid, (Guid Station, ManifestCheckpoint Manifest)> _inventory;
    private PortTransfer? _armed;
    private AdmissionIntent? _armedAdmission;
    private StationRegistrationIntent? _armedRegistration;
    private long _registrationRevision;
    private long _admissionRevision;
    private object? _admissionOwner;
    private PortAuthority? _authority;
    private bool _operating;
    private bool _faulted;
    private bool _disposed;

    private DurableCargoProvider(string directory, FileStream lease, DurableProviderImage image,
        Action<CheckpointPublishStage>? publicationFault)
    {
        _directory = directory; _lease = lease; _image = image; _publicationFault = publicationFault;
        _receipts = image.Receipts.ToDictionary(r => r.Key);
        _stations = image.Stations.ToDictionary(s => s.Id);
        _inventory = BuildInventoryIndex(image);
        foreach (StationCheckpoint station in image.Stations) { _ports.Add(station.Id, new ProviderPort(this, station.Id)); }
    }

    public Guid PairId { get { RequireAvailable(); return _image.PairId; } }
    public Guid NetworkId { get { RequireAvailable(); return _image.NetworkId; } }
    public long Revision { get { RequireAvailable(); return _image.Revision; } }
    public long ConfigurationRevision { get { RequireAvailable(); return _image.ConfigurationRevision; } }

    public static DurableCargoProvider Create(string directory, Guid pairId, Guid networkId,
        StationCheckpoint[] initial, Action<CheckpointPublishStage>? publicationFault = null)
    {
        // Encode/decode detaches every array before it can become authoritative.
        var image = new DurableProviderImage(pairId, networkId, 0, initial, Array.Empty<DurableProviderReceipt>());
        byte[] bytes = DurableProviderCodec.Encode(image);
        image = DurableProviderCodec.Decode(bytes);
        string full = GetDirectory(directory);
        Directory.CreateDirectory(full);
        FileStream lease = CheckpointWriterLease.Acquire(Path.Combine(full, LockFileName));
        DurableCargoProvider? provider = null;
        try
        {
            if (File.Exists(Path.Combine(full, ImageFileName))) { throw new IOException("A durable provider already exists; open it explicitly."); }
            provider = new(full, lease, image, publicationFault);
            provider._operating = true;
            try { provider.Publish(bytes, overwrite: false); }
            finally { provider._operating = false; }
            return provider;
        }
        catch (Exception error)
        {
            CheckpointWriterLease.DisposeAfterFailure(provider is null ? lease : provider, error);
            throw;
        }
    }

    public static DurableCargoProvider Open(string directory, Action<CheckpointPublishStage>? publicationFault = null)
    {
        string full = GetDirectory(directory);
        FileStream lease = CheckpointWriterLease.Acquire(Path.Combine(full, LockFileName));
        try
        {
            using var input = new FileStream(Path.Combine(full, ImageFileName), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length < DurableProviderCodec.HeaderLength || input.Length > DurableProviderCodec.MaxImageBytes)
            { throw new InvalidDataException("Provider image is outside its size bounds."); }
            byte[] bytes = new byte[(int)input.Length];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int count = input.Read(bytes, offset, bytes.Length - offset);
                if (count == 0) { throw new InvalidDataException("Provider image is truncated."); }
                offset += count;
            }
            if (input.ReadByte() != -1) { throw new InvalidDataException("Provider image changed while reading."); }
            return new(full, lease, DurableProviderCodec.Decode(bytes), publicationFault);
        }
        catch (Exception error)
        { CheckpointWriterLease.DisposeAfterFailure(lease, error); throw; }
    }

    public DurableProviderImage CaptureImage()
    {
        RequireAvailable();
        return _image with { Stations = CaptureStationsCore(), Receipts = _image.Receipts.ToArray() };
    }
    public StationCheckpoint[] CaptureStations() { RequireAvailable(); return CaptureStationsCore(); }
    public DurableProviderReceipt? GetReceipt(TransferKey key)
    { RequireAvailable(); ArgumentNullException.ThrowIfNull(key); return _receipts.TryGetValue(key, out DurableProviderReceipt? receipt) ? receipt : null; }
    public ICheckpointCargoPort GetPort(StationId station)
    { RequireAvailable(); return _ports[station.Value]; }

    public void ArmIntent(PortTransfer transfer)
    {
        RequireAvailable();
        ArgumentNullException.ThrowIfNull(transfer);
        if (_armed is not null || _armedAdmission is not null || _armedRegistration is not null || _armedProvision is not null || _armedCapacity is not null) { throw new InvalidOperationException("A provider intent is already armed."); }
        ProviderPort port = _ports[transfer.StationId.Value];
        PortAuthority authority = port.ValidateScope(transfer);
        if (!authority.IsActive(transfer)) { throw new InvalidOperationException("Only an active transfer can be armed."); }
        // A known receipt is immutable, but payload comparison still precedes replay.
        if (_receipts.TryGetValue(CheckpointValues.Key(transfer.Id), out DurableProviderReceipt? known))
        { RequirePayload(known, transfer); }
        _armed = transfer;
    }

    public void BindAdmissionOwner(object owner)
    {
        RequireAvailable();
        ArgumentNullException.ThrowIfNull(owner);
        if (_admissionOwner is not null && !ReferenceEquals(_admissionOwner, owner))
        {
            throw new InvalidOperationException("Provider admission already has a lifecycle owner.");
        }
        _admissionOwner = owner;
    }

    public bool ValidateAdmission(AdmissionIntent intent)
    {
        RequireAvailable();
        ArgumentNullException.ThrowIfNull(intent);
        if (!_stations.TryGetValue(intent.StationId, out StationCheckpoint? station))
        {
            throw new ArgumentException("Admission requires an existing Station.", nameof(intent));
        }
        if (station.Port.AcceptDeposits == intent.AcceptDeposits
            && station.Port.AcceptExtractions == intent.AcceptExtractions)
        {
            return false;
        }
        if (_image.Revision >= 262144)
        {
            throw new InvalidOperationException("Provider commit budget is exhausted.");
        }
        return true;
    }

    public void ArmAdmission(object owner, AdmissionIntent intent, long expectedRevision)
    {
        RequireAvailable();
        RequireAdmissionOwner(owner);
        if (_armed is not null || _armedAdmission is not null || _armedRegistration is not null || _armedProvision is not null || _armedCapacity is not null || expectedRevision != _image.Revision)
        {
            throw new InvalidOperationException("Admission requires an idle provider at the expected revision.");
        }
        if (!ValidateAdmission(intent))
        {
            throw new InvalidOperationException("A no-op admission must not create a durable effect.");
        }
        _armedAdmission = intent;
        _admissionRevision = expectedRevision;
    }

    public void ApplyAdmission(object owner, AdmissionIntent intent, long expectedRevision)
    {
        RequireAvailable();
        RequireAdmissionOwner(owner);
        ArgumentNullException.ThrowIfNull(intent);
        if (!ReferenceEquals(_armedAdmission, intent) || _image.Revision != expectedRevision
            || _admissionRevision != expectedRevision)
        {
            throw new InvalidOperationException("Admission requires its exact armed intent and revision.");
        }
        _armedAdmission = null;
        _operating = true;
        try
        {
            DurableProviderImage next = _image with
            {
                Revision = checked(_image.Revision + 1),
                ConfigurationRevision = checked(_image.ConfigurationRevision + 1),
                Stations = _image.Stations.Select(s => s.Id == intent.StationId
                    ? s with { Port = s.Port with { AcceptDeposits = intent.AcceptDeposits,
                        AcceptExtractions = intent.AcceptExtractions } } : s).ToArray()
            };
            byte[] bytes = DurableProviderCodec.Encode(next);
            Dictionary<Guid, StationCheckpoint> stations = next.Stations.ToDictionary(s => s.Id);
            Publish(bytes, overwrite: true);
            _image = next;
            _stations = stations;
        }
        catch
        {
            _faulted = true;
            throw;
        }
        finally { _operating = false; }
    }

    public void ValidateRegistration(StationRegistrationIntent intent)
    {
        RequireAvailable();
        RequireRegistrationInput(intent);
        // Cold-path preflight proves aggregate entry and byte budgets before WAL.
        _ = DurableProviderCodec.Encode(BuildRegistrationImage(intent));
    }

    public void ArmRegistration(object owner, StationRegistrationIntent intent, long expectedRevision)
    {
        RequireAvailable();
        RequireAdmissionOwner(owner);
        if (_armed is not null || _armedAdmission is not null || _armedRegistration is not null || _armedProvision is not null || _armedCapacity is not null
            || expectedRevision != _image.Revision)
            throw new InvalidOperationException("Registration requires an idle provider at the expected revision.");
        RequireRegistrationInput(intent);
        _armedRegistration = intent;
        _registrationRevision = expectedRevision;
    }

    public void ApplyRegistration(object owner, StationRegistrationIntent intent, long expectedRevision)
    {
        RequireAvailable();
        RequireAdmissionOwner(owner);
        ArgumentNullException.ThrowIfNull(intent);
        if (!ReferenceEquals(_armedRegistration, intent) || _image.Revision != expectedRevision
            || _registrationRevision != expectedRevision)
            throw new InvalidOperationException("Registration requires its exact armed intent and revision.");
        _armedRegistration = null;
        _operating = true;
        try
        {
            DurableProviderImage next = BuildRegistrationImage(intent);
            byte[] bytes = DurableProviderCodec.Encode(next);
            Dictionary<Guid, StationCheckpoint> stations = next.Stations.ToDictionary(s => s.Id);
            var ports = new Dictionary<Guid, ProviderPort>(_ports)
            {
                [intent.StationId] = new ProviderPort(this, intent.StationId)
            };
            Publish(bytes, overwrite: true);
            _image = next;
            _stations = stations;
            _ports = ports;
        }
        catch { _faulted = true; throw; }
        finally { _operating = false; }
    }

    private void RequireRegistrationInput(StationRegistrationIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.StationId == Guid.Empty)
            throw new ArgumentException("A Station identity is required.", nameof(intent));
        if (intent.MaxCargoBatches <= 0 || intent.MaxCargoBatches > 65536
            || intent.MaxReceipts <= 0 || intent.MaxReceipts > 65536)
            throw new ArgumentOutOfRangeException(nameof(intent), "Port capacities must be within 1–65536.");
        if (_stations.ContainsKey(intent.StationId) || _stations.Count >= 65536)
            throw new InvalidOperationException("Station identity exists or the provider Station limit is reached.");
        if (_image.Revision >= 262144)
            throw new InvalidOperationException("Provider commit budget is exhausted.");
    }

    private DurableProviderImage BuildRegistrationImage(StationRegistrationIntent intent) => _image with
    {
        Revision = checked(_image.Revision + 1),
        ConfigurationRevision = checked(_image.ConfigurationRevision + 1),
        Stations = _image.Stations.Append(new StationCheckpoint(intent.StationId,
            new PortCheckpoint(intent.MaxCargoBatches, intent.MaxReceipts, intent.AcceptDeposits,
                intent.AcceptExtractions, Array.Empty<InventoryCheckpoint>(), Array.Empty<ReceiptCheckpoint>()))).ToArray()
    };

    private void RequireAdmissionOwner(object owner)
    {
        if (owner is null || !ReferenceEquals(owner, _admissionOwner))
        {
            throw new InvalidOperationException("The provider admission owner is required.");
        }
    }

    public void Dispose()
    {
        RequireThreadAndIdle();
        if (!_disposed)
        {
            _disposed = true;
            _armed = null;
            _armedAdmission = null;
            _armedRegistration = null;
            _armedProvision = null; _armedCapacity = null;
        }
        _lease.Dispose();
    }

    private PortResult Apply(ProviderPort port, PortTransfer transfer)
    {
        RequireAvailable();
        PortAuthority authority = port.ValidateScope(transfer);
        TransferKey key = CheckpointValues.Key(transfer.Id);
        if (_receipts.TryGetValue(key, out DurableProviderReceipt? known))
        {
            RequirePayload(known, transfer);
            if (ReferenceEquals(_armed, transfer)) { _armed = null; }
            return (PortResult)known.Result;
        }
        if (!authority.IsActive(transfer) || !ReferenceEquals(_armed, transfer))
        { throw new InvalidOperationException("An unseen provider command requires its exact durable intent permission."); }
        _armed = null;
        _operating = true;
        try
        {
            StationCheckpoint station = _stations[port.Station];
            PortCheckpoint state = station.Port;
            if (state.Receipts.Length >= state.MaxReceipts) { throw new InvalidOperationException("Provider receipt capacity is exhausted."); }
            ManifestCheckpoint manifest = CheckpointValues.Capture(transfer.Manifest);
            InventoryCheckpoint? existing = state.Inventory.SingleOrDefault(c => c.CargoId == transfer.CargoId.Value);
            bool accepted = transfer.Kind switch
            {
                PortTransferKind.Extract => state.AcceptExtractions && existing?.Manifest == manifest,
                PortTransferKind.Deposit => state.AcceptDeposits && existing is null && state.Inventory.Length < state.MaxCargoBatches
                    && !_inventory.ContainsKey(transfer.CargoId.Value),
                _ => throw new InvalidOperationException("Unknown provider transfer kind.")
            };
            PortResult result = accepted ? PortResult.Applied : PortResult.Rejected;
            InventoryCheckpoint[] inventory = state.Inventory;
            if (accepted)
            {
                inventory = transfer.Kind == PortTransferKind.Extract
                    ? inventory.Where(c => c.CargoId != transfer.CargoId.Value).ToArray()
                    : inventory.Append(new InventoryCheckpoint(transfer.CargoId.Value, manifest)).OrderBy(c => c.CargoId).ToArray();
            }
            var receipt = new DurableProviderReceipt(key, transfer.CargoId.Value, port.Station, manifest, (int)result);
            PortCheckpoint nextPort = state with { Inventory = inventory, Receipts = state.Receipts.Append(new ReceiptCheckpoint(key, (int)result)).ToArray() };
            DurableProviderImage next = _image with
            {
                Revision = checked(_image.Revision + 1),
                Stations = _image.Stations.Select(s => s.Id == port.Station ? s with { Port = nextPort } : s).ToArray(),
                Receipts = _image.Receipts.Append(receipt).ToArray()
            };
            byte[] bytes = DurableProviderCodec.Encode(next);
            // Reserve the index allocation before publication. No allocation or
            // callback is needed between a successful publish and state adoption.
            Dictionary<TransferKey, DurableProviderReceipt> index = next.Receipts.ToDictionary(r => r.Key);
            Dictionary<Guid, StationCheckpoint> stations = next.Stations.ToDictionary(s => s.Id);
            Dictionary<Guid, (Guid Station, ManifestCheckpoint Manifest)> inventoryIndex = BuildInventoryIndex(next);
            Publish(bytes, overwrite: true);
            _image = next; _receipts = index; _stations = stations; _inventory = inventoryIndex;
            return result;
        }
        catch
        {
            _faulted = true; _armed = null; _armedAdmission = null; _armedRegistration = null; _armedProvision = null; _armedCapacity = null;
            throw;
        }
        finally { _operating = false; }
    }

    private PortResult ReadResult(ProviderPort port, PortTransfer transfer)
    {
        RequireAvailable(); port.ValidateScope(transfer);
        if (!_receipts.TryGetValue(CheckpointValues.Key(transfer.Id), out DurableProviderReceipt? known)) { return PortResult.Missing; }
        RequirePayload(known, transfer);
        return (PortResult)known.Result;
    }

    private static Dictionary<Guid, (Guid Station, ManifestCheckpoint Manifest)> BuildInventoryIndex(DurableProviderImage image)
    {
        var result = new Dictionary<Guid, (Guid Station, ManifestCheckpoint Manifest)>();
        foreach (StationCheckpoint station in image.Stations)
        {
            foreach (InventoryCheckpoint cargo in station.Port.Inventory)
            { result.Add(cargo.CargoId, (station.Id, cargo.Manifest)); }
        }
        return result;
    }

    private StationCheckpoint[] CaptureStationsCore() => _image.Stations.Select(s => s with
    { Port = s.Port with { Inventory = s.Port.Inventory.ToArray(), Receipts = s.Port.Receipts.ToArray() } }).ToArray();

    private void Publish(byte[] bytes, bool overwrite)
    {
        string candidate = Path.Combine(_directory, ".provider-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool own = false;
        try
        {
            using (var output = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { own = true; output.Write(bytes); output.Flush(flushToDisk: true); }
            _publicationFault?.Invoke(CheckpointPublishStage.BeforePublish);
            File.Move(candidate, Path.Combine(_directory, ImageFileName), overwrite);
            own = false;
            _publicationFault?.Invoke(CheckpointPublishStage.AfterPublish);
        }
        catch (Exception error)
        {
            // Keep the lease until the coordinating session fences its runtime.
            // Even read/reconciliation is forbidden until a fresh Open resolves
            // whether the atomic rename actually committed.
            _faulted = true; _armed = null; _armedAdmission = null; _armedRegistration = null; _armedProvision = null; _armedCapacity = null;
            if (own)
            {
                try { File.Delete(candidate); }
                catch (Exception cleanup) { throw new AggregateException("Provider publication and candidate cleanup failed.", error, cleanup); }
            }
            throw;
        }
    }

    private static void RequirePayload(DurableProviderReceipt receipt, PortTransfer transfer)
    {
        if (receipt.CargoId != transfer.CargoId.Value || receipt.StationId != transfer.StationId.Value
            || receipt.Manifest != CheckpointValues.Capture(transfer.Manifest))
        { throw new InvalidOperationException("A stable provider transfer key cannot change its payload."); }
    }
    private void RequireAvailable()
    {
        RequireThreadAndIdle();
        if (_disposed) { throw new ObjectDisposedException(nameof(DurableCargoProvider)); }
        if (_faulted) { throw new InvalidOperationException("Provider publication outcome is uncertain; reopen the provider."); }
    }
    private void RequireThreadAndIdle()
    {
        if (_ownerThread != Environment.CurrentManagedThreadId || _operating)
        { throw new InvalidOperationException("Provider access requires its owning thread outside an active operation."); }
    }
    private static string GetDirectory(string directory)
    { if (string.IsNullOrWhiteSpace(directory)) { throw new ArgumentException("A provider directory is required.", nameof(directory)); } return Path.GetFullPath(directory); }

    private sealed class ProviderPort : ICheckpointCargoPort
    {
        private readonly DurableCargoProvider _provider;
        private PortAuthority? _authority;
        internal Guid Station { get; }
        internal ProviderPort(DurableCargoProvider provider, Guid station) { _provider = provider; Station = station; }
        public void Bind(PortAuthority authority, StationId station)
        {
            _provider.RequireAvailable(); ArgumentNullException.ThrowIfNull(authority);
            if (station.Value != Station || (_authority is not null && !ReferenceEquals(_authority, authority))
                || (_provider._authority is not null && !ReferenceEquals(_provider._authority, authority)))
            { throw new InvalidOperationException("Provider Port belongs to exactly one Station and current session."); }
            _authority = authority;
            _provider._authority = authority;
        }
        public CargoManifest? ReadCargo(CargoId cargo)
        {
            _provider.RequireAvailable();
            if (cargo.Value == Guid.Empty) { throw new ArgumentException("A Cargo identity is required.", nameof(cargo)); }
            return _provider._inventory.TryGetValue(cargo.Value, out var item) && item.Station == Station
                ? new CargoManifest(item.Manifest.ItemKey, item.Manifest.Quantity) : null;
        }
        public PortResult Apply(PortTransfer transfer) => _provider.Apply(this, transfer);
        public PortResult ReadResult(PortTransfer transfer) => _provider.ReadResult(this, transfer);
        public PortCheckpoint CaptureCheckpoint()
        {
            _provider.RequireAvailable();
            PortCheckpoint state = _provider._stations[Station].Port;
            return state with { Inventory = state.Inventory.ToArray(), Receipts = state.Receipts.ToArray() };
        }
        internal PortAuthority ValidateScope(PortTransfer transfer)
        {
            ArgumentNullException.ThrowIfNull(transfer);
            if (_authority is null || transfer.Id is null || transfer.StationId.Value != Station)
            { throw new InvalidOperationException("Transfer does not belong to the bound provider Port."); }
            _authority.ValidateIssued(transfer);
            return _authority;
        }
    }
}
