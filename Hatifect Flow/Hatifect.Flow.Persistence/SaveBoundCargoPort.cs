using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.Flow.Diagnostics;
using Hatifect.Flow.Domain.Checkpoints;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Shipments;

namespace Hatifect.Flow.Infrastructure.Persistence;

// Journal projection for inventories committed in the SAME save generation as Core.
// Inventory entries record last transport custody, not the mutable contents of a chest.
// Restoring this projection NEVER restores items at a station. Only the game adapter owns item payloads.
internal sealed class SaveBoundCargoPort : ICheckpointCargoPort
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Func<PortTransfer, PortResult> _apply;
    private readonly Dictionary<CargoId, CargoManifest> _inventory;
    private readonly Dictionary<TransferKey, PortResult> _receipts;
    private readonly int _maxCargo;
    private readonly int _maxReceipts;
    private readonly bool _acceptDeposits;
    private readonly bool _acceptExtractions;
    private PortAuthority? _authority;
    private StationId _station;
    private bool _applying;

    internal SaveBoundCargoPort(Func<PortTransfer, PortResult> apply, PortCheckpoint? checkpoint = null)
    {
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        checkpoint ??= new PortCheckpoint(1024, 4096, true, true, Array.Empty<InventoryCheckpoint>(), Array.Empty<ReceiptCheckpoint>());
        if (checkpoint.MaxCargoBatches is <= 0 or > 65536 || checkpoint.MaxReceipts is <= 0 or > 262144)
            throw new ArgumentException("Unsupported save-bound port limits.", nameof(checkpoint));
        _maxCargo = checkpoint.MaxCargoBatches;
        _maxReceipts = checkpoint.MaxReceipts;
        _acceptDeposits = checkpoint.AcceptDeposits;
        _acceptExtractions = checkpoint.AcceptExtractions;
        _inventory = CheckpointValues.Array(checkpoint.Inventory, _maxCargo).ToDictionary(
            item => new CargoId(item.CargoId), item => CheckpointValues.Manifest(item.Manifest, int.MaxValue));
        _receipts = CheckpointValues.Array(checkpoint.Receipts, _maxReceipts).ToDictionary(item => item.Key,
            item => item.Result is (int)PortResult.Applied or (int)PortResult.Rejected
                ? (PortResult)item.Result : throw new ArgumentException("Invalid retained receipt."));
    }

    public void Bind(PortAuthority authority, StationId station)
    {
        RequireIdle();
        ArgumentNullException.ThrowIfNull(authority);
        if (station.Value == Guid.Empty || _authority is not null && (!ReferenceEquals(_authority, authority) || _station != station))
            throw new InvalidOperationException("A save-bound port has one session and station owner.");
        _authority = authority;
        _station = station;
    }

    internal void Register(CargoId cargo, CargoManifest manifest)
    {
        RequireIdle();
        ArgumentNullException.ThrowIfNull(manifest);
        if (cargo.Value == Guid.Empty || _inventory.Count >= _maxCargo || _inventory.ContainsKey(cargo)
            || _authority?.IsCargoRegistered(cargo) == true)
            throw new InvalidOperationException("Cargo identity is unavailable or the batch limit is reached.");
        _inventory.Add(cargo, manifest);
    }

    public CargoManifest? ReadCargo(CargoId cargo)
    {
        RequireIdle();
        return _inventory.GetValueOrDefault(cargo);
    }

    public PortResult Apply(PortTransfer transfer)
    {
        RequireIdle();
        Validate(transfer);
        TransferKey key = CheckpointValues.Key(transfer.Id);
        if (_receipts.TryGetValue(key, out PortResult prior))
        {
            return prior;
        }
        if (!_authority!.IsActive(transfer) || _receipts.Count >= _maxReceipts)
        {
            throw new InvalidOperationException("Transfer is retired or the receipt budget is exhausted.");
        }
        _receipts.EnsureCapacity(_receipts.Count + 1);
        _inventory.EnsureCapacity(_inventory.Count + 1);
        bool admitted = CanAdmit(transfer);
        _applying = true;
        try
        {
            // Synchronous, settled Applied/Rejected only. An exception has NO settled receipt;
            // the game owner must persist a recovery fence and prohibit automatic replay/reconciliation.
            PortResult result = admitted ? _apply(transfer) : PortResult.Rejected;
            if (result is not (PortResult.Applied or PortResult.Rejected))
            {
                throw new InvalidOperationException("The game adapter did not settle its transfer.");
            }
            RecordSettlement(result);
            return result;
        }
        finally
        {
            _applying = false;
        }

        void RecordSettlement(PortResult result)
        {
            if (result == PortResult.Applied)
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
            _receipts.Add(key, result);
        }
    }

    private bool CanAdmit(PortTransfer transfer)
    {
        if (transfer.Kind == PortTransferKind.Extract)
        {
            return _acceptExtractions && _inventory.GetValueOrDefault(transfer.CargoId) == transfer.Manifest;
        }
        return _acceptDeposits && !_inventory.ContainsKey(transfer.CargoId) && _inventory.Count < _maxCargo;
    }

    public PortResult ReadResult(PortTransfer transfer)
    {
        RequireIdle();
        Validate(transfer);
        return _receipts.GetValueOrDefault(CheckpointValues.Key(transfer.Id), PortResult.Missing);
    }

    public PortCheckpoint CaptureCheckpoint()
    {
        RequireIdle();
        return new PortCheckpoint(_maxCargo, _maxReceipts, _acceptDeposits, _acceptExtractions,
            _inventory.OrderBy(item => item.Key.Value).Select(item => new InventoryCheckpoint(item.Key.Value,
                CheckpointValues.Capture(item.Value))).ToArray(),
            _receipts.OrderBy(item => item.Key.ParcelId).ThenBy(item => item.Key.Kind).ThenBy(item => item.Key.Attempt)
                .Select(item => new ReceiptCheckpoint(item.Key, (int)item.Value)).ToArray());
    }

    internal FlowPortResources ReadResources()
    {
        RequireIdle();
        return new FlowPortResources(new(_inventory.Count, _maxCargo), new(_receipts.Count, _maxReceipts));
    }

    private void Validate(PortTransfer transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        if (_authority is null || transfer.StationId != _station)
            throw new InvalidOperationException("Foreign save-bound transfer.");
        _authority.ValidateIssued(transfer);
    }

    private void RequireIdle()
    {
        if (_thread != Environment.CurrentManagedThreadId || _applying)
            throw new InvalidOperationException("Save-bound inventory access requires the idle owning thread.");
    }
}
