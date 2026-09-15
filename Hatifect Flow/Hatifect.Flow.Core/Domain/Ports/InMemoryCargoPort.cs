using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Hatifect.Flow.Domain.Checkpoints;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Shipments;

namespace Hatifect.Flow.Domain.Ports;

// Fixture adapter for physical whole-batch inventory. Every operation, including
// reads, belongs to the constructor thread. No callback can observe an inventory
// mutation between its preflight and the matching receipt commit.
internal sealed class InMemoryCargoPort : ICheckpointCargoPort
{
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly int _maxCargoBatches;
    private readonly int _maxReceipts;
    private readonly Dictionary<CargoId, CargoManifest> _inventory = new();
    private readonly Dictionary<PortTransferId, PortResult> _receipts = new();
    private PortAuthority? _authority;
    private StationId _station;
    private bool _acceptDeposits = true;
    private bool _acceptExtractions = true;

    public InMemoryCargoPort(int maxCargoBatches = 1024, int maxReceipts = 4096)
    {
        if (maxCargoBatches <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCargoBatches));
        }
        if (maxReceipts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxReceipts));
        }
        _maxCargoBatches = maxCargoBatches;
        _maxReceipts = maxReceipts;
    }

    public bool AcceptDeposits
    {
        get
        {
            EnsureOwnerThread();
            return _acceptDeposits;
        }
        set
        {
            EnsureOwnerThread();
            _acceptDeposits = value;
        }
    }

    public bool AcceptExtractions
    {
        get
        {
            EnsureOwnerThread();
            return _acceptExtractions;
        }
        set
        {
            EnsureOwnerThread();
            _acceptExtractions = value;
        }
    }

    // Snapshots do not expose the mutable backing dictionary; CargoManifest is
    // immutable, so a captured inventory remains stable after later transfers.
    public IReadOnlyDictionary<CargoId, CargoManifest> Inventory
    {
        get
        {
            EnsureOwnerThread();
            return new ReadOnlyDictionary<CargoId, CargoManifest>(
                new Dictionary<CargoId, CargoManifest>(_inventory));
        }
    }

    public void Bind(PortAuthority authority, StationId station)
    {
        EnsureOwnerThread();
        ArgumentNullException.ThrowIfNull(authority);
        if (station.Value == Guid.Empty)
        {
            throw new ArgumentException("A valid Station identity is required.", nameof(station));
        }
        if (_authority is not null)
        {
            if (!ReferenceEquals(_authority, authority) || _station != station)
            {
                throw new InvalidOperationException("A cargo port belongs to exactly one session and Station.");
            }
            return;
        }
        foreach (CargoId cargo in _inventory.Keys)
        {
            if (authority.IsCargoRegistered(cargo))
            {
                throw new InvalidOperationException("A new Port cannot introduce an already registered cargo identity.");
            }
        }
        _authority = authority;
        _station = station;
    }

    // Trusted fixture provisioning only, never runtime adapter mutation. Seeding
    // after binding is allowed for controlled tests. Runtime admission
    // independently validates the physical batch before claiming its custody.
    public void Seed(CargoId cargo, CargoManifest manifest)
    {
        EnsureOwnerThread();
        RequireCargoIdentity(cargo);
        ArgumentNullException.ThrowIfNull(manifest);
        if (_inventory.ContainsKey(cargo) || _authority?.IsCargoRegistered(cargo) == true)
        {
            throw new InvalidOperationException("Cargo identity already exists in this port.");
        }
        if (_inventory.Count >= _maxCargoBatches)
        {
            throw new InvalidOperationException("The cargo port batch limit is reached.");
        }
        _inventory.Add(cargo, manifest);
    }

    public CargoManifest? ReadCargo(CargoId cargo)
    {
        EnsureOwnerThread();
        RequireCargoIdentity(cargo);
        return _inventory.TryGetValue(cargo, out CargoManifest? manifest) ? manifest : null;
    }

    public PortResult Apply(PortTransfer transfer)
    {
        EnsureOwnerThread();
        PortAuthority authority = ValidateScope(transfer);
        if (_receipts.TryGetValue(transfer.Id, out PortResult receipt))
        {
            // Completed capabilities may be retired or their cargo dispatched
            // again. A replay only observes the original result.
            return receipt;
        }
        if (!authority.IsActive(transfer))
        {
            throw new InvalidOperationException("A retired transfer cannot change physical inventory.");
        }
        if (_receipts.Count >= _maxReceipts)
        {
            throw new InvalidOperationException("The cargo port receipt limit is reached.");
        }

        bool accepted = IsTransferAccepted(transfer);
        ReserveCapacityForOutcome(transfer, accepted);
        if (accepted)
        {
            ApplyAcceptedTransfer(transfer);
        }
        PortResult result = accepted ? PortResult.Applied : PortResult.Rejected;
        _receipts.Add(transfer.Id, result);
        return result;
    }

    private bool IsTransferAccepted(PortTransfer transfer) => transfer.Kind switch
    {
        PortTransferKind.Extract => _acceptExtractions
            && _inventory.TryGetValue(transfer.CargoId, out CargoManifest? batch)
            && batch == transfer.Manifest,
        PortTransferKind.Deposit => _acceptDeposits
            && !_inventory.ContainsKey(transfer.CargoId)
            && _inventory.Count < _maxCargoBatches,
        _ => false
    };

    // Reserve all dictionary storage before changing custody. Rejection is
    // also recorded, so replay cannot later turn a refused command into an
    // accepted one merely because capacity or physical inventory changed.
    private void ReserveCapacityForOutcome(PortTransfer transfer, bool accepted)
    {
        _receipts.EnsureCapacity(_receipts.Count + 1);
        if (accepted && transfer.Kind == PortTransferKind.Deposit)
        {
            _inventory.EnsureCapacity(_inventory.Count + 1);
        }
    }

    private void ApplyAcceptedTransfer(PortTransfer transfer)
    {
        if (transfer.Kind == PortTransferKind.Extract)
        {
            _inventory.Remove(transfer.CargoId);
        }
        else
        {
            _inventory.Add(transfer.CargoId, transfer.Manifest);
        }
    }

    public PortResult ReadResult(PortTransfer transfer)
    {
        EnsureOwnerThread();
        ValidateScope(transfer);
        return _receipts.TryGetValue(transfer.Id, out PortResult result) ? result : PortResult.Missing;
    }

    public PortCheckpoint CaptureCheckpoint()
    {
        EnsureOwnerThread();
        return new PortCheckpoint(_maxCargoBatches, _maxReceipts, _acceptDeposits, _acceptExtractions,
            _inventory.OrderBy(pair => pair.Key.Value).Select(pair => new InventoryCheckpoint(
                pair.Key.Value, CheckpointValues.Capture(pair.Value))).ToArray(),
            _receipts.OrderBy(pair => pair.Key.ParcelId.Value).ThenBy(pair => pair.Key.Kind)
                .ThenBy(pair => pair.Key.Attempt).Select(pair => new ReceiptCheckpoint(
                    CheckpointValues.Key(pair.Key), (int)pair.Value)).ToArray());
    }

    internal void RestoreReceipt(PortTransfer transfer, PortResult result)
    {
        EnsureOwnerThread();
        ValidateScope(transfer);
        if (result is not (PortResult.Applied or PortResult.Rejected) || _receipts.Count >= _maxReceipts)
        {
            throw new ArgumentException("Invalid restored Port receipt.");
        }
        _receipts.Add(transfer.Id, result);
    }

    private PortAuthority ValidateScope(PortTransfer transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        if (_authority is null || transfer.Id is null || transfer.StationId != _station)
        {
            throw new InvalidOperationException("The transfer does not belong to this cargo port binding.");
        }
        _authority.ValidateIssued(transfer);
        return _authority;
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
        {
            throw new InvalidOperationException("Cargo port access must stay on its owning thread.");
        }
    }

    private static void RequireCargoIdentity(CargoId cargo)
    {
        if (cargo.Value == Guid.Empty)
        {
            throw new ArgumentException("A valid Cargo identity is required.", nameof(cargo));
        }
    }
}
