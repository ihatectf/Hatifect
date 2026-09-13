using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.Flow.Domain.Checkpoints;

namespace Hatifect.Flow.Infrastructure.Persistence;

internal sealed partial class DurableCargoProvider
{
    private PortCapacityIntent? _armedCapacity;
    private long _capacityRevision;

    public bool ValidateCapacity(PortCapacityIntent intent)
    {
        RequireAvailable();
        ArgumentNullException.ThrowIfNull(intent);
        if (!_stations.TryGetValue(intent.StationId, out StationCheckpoint? station))
        {
            throw new ArgumentException("Capacity changes require an existing Station.", nameof(intent));
        }
        if (intent.MaxCargoBatches <= 0 || intent.MaxCargoBatches > 65536
            || intent.MaxReceipts <= 0 || intent.MaxReceipts > 65536)
        {
            throw new ArgumentOutOfRangeException(nameof(intent), "Port capacities must be within 1–65536.");
        }
        if (intent.MaxCargoBatches < station.Port.Inventory.Length || intent.MaxReceipts < station.Port.Receipts.Length)
        {
            throw new InvalidOperationException("Port capacity cannot fall below retained inventory or receipts.");
        }
        if (station.Port.MaxCargoBatches == intent.MaxCargoBatches && station.Port.MaxReceipts == intent.MaxReceipts)
        {
            return false;
        }
        if (_image.Revision >= 262144)
        {
            throw new InvalidOperationException("Provider commit budget is exhausted.");
        }
        _ = DurableProviderCodec.Encode(PortCapacityProjection.Provider(_image, intent));
        return true;
    }

    public void ArmCapacity(object owner, PortCapacityIntent intent, long expectedRevision)
    {
        RequireAvailable();
        RequireAdmissionOwner(owner);
        if (_armed is not null || _armedAdmission is not null || _armedRegistration is not null
            || _armedProvision is not null || _armedCapacity is not null || expectedRevision != _image.Revision)
        {
            throw new InvalidOperationException("Capacity changes require an idle provider at the expected revision.");
        }
        if (!ValidateCapacity(intent))
        {
            throw new InvalidOperationException("A no-op capacity change must not create a durable effect.");
        }
        _armedCapacity = intent;
        _capacityRevision = expectedRevision;
    }

    public void ApplyCapacity(object owner, PortCapacityIntent intent, long expectedRevision)
    {
        RequireAvailable();
        RequireAdmissionOwner(owner);
        ArgumentNullException.ThrowIfNull(intent);
        if (!ReferenceEquals(_armedCapacity, intent) || _image.Revision != expectedRevision
            || _capacityRevision != expectedRevision)
        {
            throw new InvalidOperationException("Capacity changes require their exact armed intent and revision.");
        }
        _armedCapacity = null;
        _operating = true;
        try
        {
            DurableProviderImage next = PortCapacityProjection.Provider(_image, intent);
            byte[] bytes = DurableProviderCodec.Encode(next);
            Dictionary<Guid, StationCheckpoint> stations = next.Stations.ToDictionary(station => station.Id);
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
}
