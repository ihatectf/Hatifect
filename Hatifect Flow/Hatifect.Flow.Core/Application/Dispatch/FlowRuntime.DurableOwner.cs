using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.Flow.Domain.Checkpoints;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Scheduling;
using Hatifect.Flow.Domain.Shipments;

namespace Hatifect.Flow.Application.Dispatch;

internal sealed partial class FlowRuntime
{
    public void ValidateDurableCargo(object owner, CargoId cargo, StationId station, CargoManifest manifest)
    {
        using MutationScope mutation = EnterMutation();
        RequireCheckpointOwner(owner);
        if (!_ownedCommand || _intentSink is null)
        {
            throw new InvalidOperationException("Cargo provisioning requires a durable owned command.");
        }
        RequireNewCargo(cargo, station, manifest);
    }

    public void AddDurableCargo(object owner, CargoId cargo, StationId station, CargoManifest manifest)
    {
        using MutationScope mutation = EnterMutation();
        RequireCheckpointOwner(owner);
        if (!_ownedCommand || _intentSink is null)
        {
            throw new InvalidOperationException("Cargo provisioning requires a durable owned command.");
        }
        RegisterCargoValue(cargo, station, manifest);
    }

    public void ValidateDurableStation(object owner, StationId station)
    {
        using MutationScope mutation = EnterMutation();
        RequireCheckpointOwner(owner);
        if (!_ownedCommand || _intentSink is null)
            throw new InvalidOperationException("Station registration requires a durable owned command.");
        RequireNewStation(station);
    }

    public void AddDurableStation(object owner, StationId station, ICheckpointCargoPort port)
    {
        using MutationScope mutation = EnterMutation();
        RequireCheckpointOwner(owner);
        if (!_ownedCommand || _intentSink is null)
            throw new InvalidOperationException("Station registration requires a durable owned command.");
        ArgumentNullException.ThrowIfNull(port);
        PortCheckpoint checkpoint = port.CaptureCheckpoint();
        if (checkpoint.Inventory.Length != 0 || checkpoint.Receipts.Length != 0)
            throw new InvalidOperationException("A newly registered durable Station must be empty.");
        AddStationValue(station, port);
    }

    public void BindDurableOwner(object owner, Func<StationId, ICheckpointCargoPort> ports,
        Action<PortTransfer> intentSink)
    {
        using MutationScope mutation = EnterMutation();
        RequireCheckpointOwner(owner);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(intentSink);
        if (_intentSink is not null) throw new InvalidOperationException("A durable owner is already bound.");
        var replacements = new Dictionary<StationId, ICargoPort>();
        foreach (StationId station in _ports.Keys)
        {
            ICheckpointCargoPort port = ports(station);
            port.Bind(_authority, station);
            replacements.Add(station, port);
        }
        _ports.Clear();
        foreach (var port in replacements) _ports.Add(port.Key, port.Value);
        _intentSink = intentSink;
    }

    public void ExecuteOwned(object owner, Action<FlowRuntime> action)
    {
        RequireCheckpointOwner(owner);
        ArgumentNullException.ThrowIfNull(action);
        if (_retired || _mutating || _ownedCommand || _intentSink is null)
            throw new InvalidOperationException("Durable command scope is not available.");
        _ownedCommand = true;
        try { action(this); }
        finally { _ownedCommand = false; }
    }

    public FlowCheckpoint CaptureTransferIntent(object owner, PortTransfer transfer)
    {
        RequireCheckpointOwner(owner);
        if (!_ownedCommand || !_mutating || !ReferenceEquals(_preparingTransfer, transfer))
            throw new InvalidOperationException("Only the preparing transfer can capture a durable intent.");
        FlowCheckpoint snapshot = CaptureCheckpointValue();
        int index = System.Array.FindIndex(snapshot.Parcels, parcel => parcel.Id == transfer.Id.ParcelId.Value);
        ParcelCheckpoint parcel = snapshot.Parcels[index];
        int state = (int)(transfer.Kind == PortTransferKind.Extract
            ? ParcelState.ExtractionUncertain : transfer.StationId == _shipments[new ShipmentId(parcel.ShipmentId)].Origin
                ? ParcelState.ReturnUncertain : ParcelState.DeliveryUncertain);
        snapshot.Parcels[index] = parcel with
        {
            State = state, Version = checked(parcel.Version + 1), PendingOperation = null
        };
        var history = snapshot.Events.ToList();
        if (history.Count == _limits.MaxEvents) history.RemoveAt(0);
        history.Add(new EventCheckpoint(parcel.Id, parcel.Version + 1, parcel.TransferTick,
            (int)OperationKind.PortUncertain, state, new OwnerCheckpoint(null, null, CheckpointValues.Key(transfer.Id))));
        snapshot = snapshot with { Events = history.ToArray() };
        _ = RestoreCheckpoint(snapshot);
        return snapshot;
    }

    private void RequireCheckpointOwner(object owner)
    {
        if (_ownerThread != Environment.CurrentManagedThreadId || owner is null
            || !ReferenceEquals(owner, _checkpointOwner))
            throw new InvalidOperationException("The checkpoint owner is required on its owning thread.");
    }
}
