using System;
using System.Linq;
using Hatifect.Flow.Domain.Checkpoints;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Ports;

namespace Hatifect.Flow.Application.Dispatch;

internal sealed partial class FlowRuntime
{
    public void AttachCheckpointOwner(object owner)
    {
        using MutationScope mutation = EnterMutation();
        ArgumentNullException.ThrowIfNull(owner);
        if (_checkpointOwner is not null)
        {
            throw new InvalidOperationException("Runtime already belongs to a checkpoint owner.");
        }
        _checkpointOwner = owner;
    }

    public void FenceCheckpointOwner(object owner)
    {
        if (_ownerThread != Environment.CurrentManagedThreadId || _mutating
            || owner is null || !ReferenceEquals(owner, _checkpointOwner))
        {
            throw new InvalidOperationException("Only the checkpoint owner may fence this runtime.");
        }
        _retired = true;
        _authority.Seal();
    }

    public InMemoryCargoPort GetCheckpointPort(StationId station)
    {
        using MutationScope mutation = EnterMutation();
        return _ports[station] as InMemoryCargoPort
            ?? throw new InvalidOperationException("Station does not use a concrete checkpoint fake Port.");
    }

    public FlowCheckpoint CaptureCheckpoint()
    {
        using MutationScope mutation = EnterMutation();
        FlowCheckpoint snapshot = CaptureCheckpointValue();
        _ = RestoreCheckpoint(snapshot);
        return snapshot;
    }

    private FlowCheckpoint CaptureCheckpointValue()
    {
        var limits = new LimitsCheckpoint(_limits.MaxStations, _limits.MaxLinks, _limits.MaxParcels,
            _limits.MaxRouteVisits, _limits.MaxRoutePlans, _limits.MaxDeliveryAttempts, _limits.MaxEvents,
            _limits.MaxOperationsPerAdvance, _limits.MaxCargoUnits, _limits.MaxPendingOperations);
        StationCheckpoint[] stations = _ports.OrderBy(pair => pair.Key.Value).Select(pair =>
            new StationCheckpoint(pair.Key.Value, pair.Value is ICheckpointCargoPort port
                ? port.CaptureCheckpoint()
                : throw new InvalidOperationException("A Port cannot capture its complete fake inventory and journal."))).ToArray();
        Guid networkId = NetworkId.Value;
        long tick = Now;
        LinkCheckpoint[] links = _network.History.OrderBy(link => link.Id.Value)
            .Select(link => new LinkCheckpoint(link.Id.Value,
                link.Origin.Value, link.Destination.Value, link.Capacity, link.TransitTicks,
                _network.IsActive(link.Id))).ToArray();
        ShipmentCheckpoint[] shipments = _shipments.Values.OrderBy(shipment => shipment.Id.Value)
            .Select(shipment => new ShipmentCheckpoint(
                shipment.Id.Value, shipment.Origin.Value, shipment.Destination.Value,
                CheckpointValues.Capture(shipment.Manifest), CheckpointValues.Capture(shipment.Policy),
                shipment.ParcelId?.Value)).ToArray();
        ParcelCheckpoint[] parcels = _parcels.Values.OrderBy(execution => execution.Snapshot.Id.Value)
            .Select(CaptureExecution).ToArray();
        CargoCheckpoint[] cargo = _cargo.Batches.OrderBy(batch => batch.Id.Value)
            .Select(batch => new CargoCheckpoint(batch.Id.Value,
                CheckpointValues.Capture(batch.Manifest), CheckpointValues.Capture(batch.Owner),
                batch.ClaimedBy?.Value, batch.RegistrationStation.Value, batch.DispatchCount)).ToArray();
        TransferCheckpoint[] transfers = _authority.Issued.OrderBy(transfer => transfer.Id.ParcelId.Value)
            .ThenBy(transfer => transfer.Kind).ThenBy(transfer => transfer.Id.Attempt)
            .Select(transfer => new TransferCheckpoint(
                CheckpointValues.Key(transfer.Id), transfer.CargoId.Value, transfer.StationId.Value,
                CheckpointValues.Capture(transfer.Manifest), _authority.IsRetired(transfer))).ToArray();
        EventCheckpoint[] events = _events.Select(entry => new EventCheckpoint(entry.ParcelId.Value, entry.Sequence, entry.Tick,
            (int)entry.Kind, (int)entry.State, CheckpointValues.Capture(entry.Owner))).ToArray();
        return new FlowCheckpoint(networkId, tick, limits, stations, links, shipments, parcels, cargo, transfers, events);
    }

    private static ParcelCheckpoint CaptureExecution(Execution execution)
    {
        var parcel = execution.Snapshot;
        var plan = execution.Plan;
        return new ParcelCheckpoint(parcel.Id.Value, parcel.ShipmentId.Value, parcel.CargoId.Value,
            CheckpointValues.Capture(parcel.Manifest), CheckpointValues.Capture(parcel.PolicySnapshot),
            (int)parcel.State, parcel.Version, parcel.DeliveryAttempts, parcel.CurrentStation.Value,
            parcel.PendingOperation is null ? null : new OperationCheckpoint(parcel.PendingOperation.Sequence,
                (int)parcel.PendingOperation.Kind, parcel.PendingOperation.DueTick),
            parcel.PendingTransfer is null ? null : CheckpointValues.Key(parcel.PendingTransfer.Id),
            plan is null ? null : new RouteCheckpoint(plan.Links.Select(link => link.Id.Value).ToArray(),
                plan.Dependencies.OrderBy(pair => pair.Key.Value).Select(pair => new DependencyCheckpoint(
                    pair.Key.Value, pair.Value)).ToArray()), execution.Hop, execution.TransferTick, parcel.CargoDispatch);
    }
}
