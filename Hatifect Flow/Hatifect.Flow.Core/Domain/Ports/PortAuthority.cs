using System;
using System.Collections.Generic;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Policies;
using Hatifect.Flow.Domain.Shipments;

namespace Hatifect.Flow.Domain.Ports;

// Session-scoped in-memory capabilities. Retention is bounded by admitted
// Parcels and their fixed extraction/delivery attempt limits; no eviction.
internal sealed class PortAuthority
{
    private readonly Dictionary<PortTransferId, PortTransfer> _issued = new();
    private readonly HashSet<PortTransferId> _retired = new();
    private readonly HashSet<CargoId> _registeredCargo = new();
    private readonly long _limit;
    private readonly object _session = new();
    private bool _sealed;

    internal PortAuthority(FlowLimits limits)
        => _limit = (long)limits.MaxParcels * (1L + limits.MaxDeliveryAttempts);

    internal bool IsCargoRegistered(CargoId cargo) => _registeredCargo.Contains(cargo);
    internal void RegisterCargo(CargoId cargo) => _registeredCargo.Add(cargo);
    internal IEnumerable<PortTransfer> Issued => _issued.Values;
    internal int IssuedCount => _issued.Count;
    internal int RetiredCount => _retired.Count;
    internal long Limit => _limit;
    internal bool IsRetired(PortTransfer transfer) => _retired.Contains(transfer.Id);
    internal void Seal() => _sealed = true;

    internal PortTransfer Issue(Parcel parcel, StationId station, PortTransferKind kind, int attempt)
    {
        if (_sealed || _issued.Count >= _limit)
        {
            throw new InvalidOperationException("The session transfer capability limit is reached.");
        }
        var id = new PortTransferId(_session, parcel.Id, kind, attempt);
        var transfer = new PortTransfer(id, parcel.CargoId, station, parcel.Manifest);
        _issued.Add(id, transfer);
        return transfer;
    }

    internal void ValidateIssued(PortTransfer transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        if (!_issued.TryGetValue(transfer.Id, out PortTransfer? issued)
            || !ReferenceEquals(issued, transfer))
        {
            throw new InvalidOperationException("The transfer is not the issued capability for this session.");
        }
    }

    internal bool IsActive(PortTransfer transfer)
    {
        ValidateIssued(transfer);
        return !_sealed && !_retired.Contains(transfer.Id);
    }

    internal void Retire(PortTransfer transfer)
    {
        ValidateIssued(transfer);
        _retired.Add(transfer.Id);
    }
}
