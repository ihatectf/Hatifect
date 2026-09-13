using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.Flow.Domain.Checkpoints;
using Hatifect.Flow.Domain.Shipments;

namespace Hatifect.Flow.Infrastructure.Persistence;

internal sealed partial class DurableCargoProvider
{
    private CargoProvisionIntent? _armedProvision;
    private long _provisionRevision;

    public void ValidateProvision(CargoProvisionIntent intent)
    {
        RequireAvailable();
        RequireProvisionInput(intent);
        _ = DurableProviderCodec.Encode(CargoProvisionProjection.Provider(_image, intent));
    }

    public void ArmProvision(object owner, CargoProvisionIntent intent, long expectedRevision)
    {
        RequireAvailable();
        RequireAdmissionOwner(owner);
        if (_armed is not null || _armedAdmission is not null || _armedRegistration is not null
            || _armedProvision is not null || _armedCapacity is not null || expectedRevision != _image.Revision)
        {
            throw new InvalidOperationException("Provisioning requires an idle provider at the expected revision.");
        }
        RequireProvisionInput(intent);
        _armedProvision = intent;
        _provisionRevision = expectedRevision;
    }

    public void ApplyProvision(object owner, CargoProvisionIntent intent, long expectedRevision)
    {
        RequireAvailable();
        RequireAdmissionOwner(owner);
        ArgumentNullException.ThrowIfNull(intent);
        if (!ReferenceEquals(_armedProvision, intent) || _image.Revision != expectedRevision
            || _provisionRevision != expectedRevision)
        {
            throw new InvalidOperationException("Provisioning requires its exact armed intent and revision.");
        }
        _armedProvision = null;
        _operating = true;
        try
        {
            DurableProviderImage next = CargoProvisionProjection.Provider(_image, intent);
            byte[] bytes = DurableProviderCodec.Encode(next);
            Dictionary<Guid, StationCheckpoint> stations = next.Stations.ToDictionary(s => s.Id);
            Dictionary<Guid, (Guid Station, ManifestCheckpoint Manifest)> inventory = BuildInventoryIndex(next);
            Publish(bytes, overwrite: true);
            _image = next;
            _stations = stations;
            _inventory = inventory;
        }
        catch
        {
            _faulted = true;
            throw;
        }
        finally { _operating = false; }
    }

    private void RequireProvisionInput(CargoProvisionIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(intent.Manifest);
        if (intent.CargoId == Guid.Empty || !_stations.TryGetValue(intent.StationId, out StationCheckpoint? station))
        {
            throw new ArgumentException("Provisioning requires a new Cargo identity and an existing Station.", nameof(intent));
        }
        _ = new CargoManifest(intent.Manifest.ItemKey, intent.Manifest.Quantity);
        if (_inventory.ContainsKey(intent.CargoId) || _image.Receipts.Any(receipt => receipt.CargoId == intent.CargoId))
        {
            throw new InvalidOperationException("A provisioned Cargo identity must be new to the provider.");
        }
        if (station.Port.Inventory.Length >= station.Port.MaxCargoBatches || _image.Revision >= 262144)
        {
            throw new InvalidOperationException("Provider inventory or commit budget is exhausted.");
        }
    }
}
