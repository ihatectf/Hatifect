using System;
using System.Collections.Generic;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Shipments;

namespace Hatifect.Flow.Domain.Ports;

internal sealed class CargoLedger
{
    private readonly Dictionary<CargoId, CargoBatch> _batches = new();
    private readonly int _limit;

    internal CargoLedger(int limit) => _limit = limit;
    internal CargoBatch Get(CargoId id) => _batches[id];
    internal IEnumerable<CargoBatch> Batches => _batches.Values;
    internal void Restore(CargoBatch batch)
    {
        if (_batches.Count >= _limit)
        {
            throw new InvalidOperationException("The restored cargo limit is reached.");
        }
        _batches.Add(batch.Id, batch);
    }

    internal void ValidateSeed(CargoId id)
    {
        if (_batches.ContainsKey(id) || _batches.Count >= _limit)
        {
            throw new InvalidOperationException("Cargo identity already exists or the session cargo limit is reached.");
        }
    }

    internal void Seed(CargoId id, StationId station, CargoManifest manifest)
    {
        ValidateSeed(id);
        _batches.Add(id, new CargoBatch(id, manifest, CargoOwner.AtStation(station), station));
    }

    internal void Claim(CargoId id, ParcelId parcel, StationId origin, CargoManifest manifest)
    {
        CargoBatch batch = _batches[id];
        if (batch.ClaimedBy is not null || batch.Owner != CargoOwner.AtStation(origin)
            || batch.Manifest != manifest)
        {
            throw new InvalidOperationException("Cargo is claimed or does not satisfy this Shipment.");
        }
        _batches[id] = batch with { ClaimedBy = parcel, DispatchCount = checked(batch.DispatchCount + 1) };
    }

    internal void ReleaseClaim(CargoId id, ParcelId parcel)
    {
        CargoBatch batch = _batches[id];
        if (batch.ClaimedBy != parcel || batch.Owner.Station is null)
        {
            throw new InvalidOperationException("Only settled Station cargo can release its execution claim.");
        }
        _batches[id] = batch with { ClaimedBy = null };
    }

    internal void Transfer(CargoId id, CargoOwner expected, CargoOwner destination)
    {
        CargoBatch batch = _batches[id];
        if (batch.Owner != expected)
        {
            throw new InvalidOperationException("Cargo custody does not match the authoritative transition.");
        }
        _batches[id] = batch with { Owner = destination };
    }
}
