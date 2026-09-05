using System;
using System.Collections.Generic;
using Hatifect.Flow.Application.Planning;
using Hatifect.Flow.Diagnostics;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Network;
using Hatifect.Flow.Domain.Policies;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Scheduling;
using Hatifect.Flow.Domain.Shipments;

namespace Hatifect.Flow.Application.Dispatch;

// One coordinator owns a retained session. Ports atomically mutate inventories
// and journal outcomes; only this owner resolves cargo claims and execution.
internal sealed partial class FlowRuntime
{
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly FlowLimits _limits;
    private readonly NetworkGraph _network;
    private readonly RoutePlanner _planner;
    private readonly CargoLedger _cargo;
    private readonly OperationQueue _operations;
    private readonly CapacityReservations _capacity = new();
    private readonly Dictionary<StationId, ICargoPort> _ports = new();
    private readonly Dictionary<ShipmentId, Shipment> _shipments = new();
    private readonly Dictionary<ParcelId, Execution> _parcels = new();
    private readonly Queue<TransportEvent> _events = new();
    private bool _mutating;
    private bool _retired;
    private object? _checkpointOwner;
    private Action<PortTransfer>? _intentSink;
    private PortTransfer? _preparingTransfer;
    private bool _ownedCommand;
    private readonly PortAuthority _authority;

    public FlowRuntime(NetworkId networkId, FlowLimits? limits = null)
    {
        _limits = limits ?? new FlowLimits();
        _authority = new PortAuthority(_limits);
        _network = new NetworkGraph(networkId, _limits);
        _planner = new RoutePlanner(_network, _limits);
        _cargo = new CargoLedger(_limits.MaxParcels);
        _operations = new OperationQueue(_limits.MaxPendingOperations);
    }

    private FlowRuntime(FlowRuntime previous)
    {
        _limits = previous._limits;
        _authority = previous._authority;
        _network = previous._network;
        _planner = previous._planner;
        _cargo = previous._cargo;
        _operations = previous._operations;
        _capacity = previous._capacity;
        _ports = previous._ports;
        _shipments = previous._shipments;
        _parcels = previous._parcels;
        _events = previous._events;
        Now = previous.Now;
        // O(P) only on coordinator replacement. Existing tickets confer no
        // authority on the replacement, including tickets held by callers.
        foreach (Execution execution in _parcels.Values)
        {
            if (execution.Snapshot.PendingOperation is ScheduledOperation old)
            {
                var replacement = old with { };
                _operations.Remove(old);
                _operations.Add(replacement);
                execution.Snapshot = execution.Snapshot with { PendingOperation = replacement };
            }
        }
    }

    public FlowRuntime Restart()
    {
        using MutationScope mutation = EnterMutation();
        if (_checkpointOwner is not null)
        {
            throw new InvalidOperationException("A checkpoint owner must close and reopen its durable session.");
        }
        var replacement = new FlowRuntime(this);
        _retired = true;
        return replacement;
    }

    public NetworkId NetworkId => _network.Id;
    public long Now { get; private set; }
    public int PendingOperationCount => _operations.Count;
    public long RouteSearchCount => _planner.SearchCount;
    public int RouteCacheCount => _planner.CacheCount;
    public IReadOnlyList<TransportEvent> Events => _events.ToArray();

    public Shipment GetShipment(ShipmentId id) => _shipments[id];
    public Parcel GetParcel(ParcelId id) => _parcels[id].Snapshot;
    public CargoBatch GetCargo(CargoId id) => _cargo.Get(id);
    public int ReservedUnits(LinkId id) => _capacity.ReservedUnits(id);
    public ScheduledOperation? PeekNextOperation() => _operations.Peek();

    public void AddStation(StationId station, ICargoPort port)
    {
        using MutationScope mutation = EnterMutation();
        if (_intentSink is not null) throw new InvalidOperationException("Durable Stations must be registered through their session.");
        AddStationValue(station, port);
    }

    private void RequireNewStation(StationId station)
    {
        IdentityValue.Require(station.Value);
        if (_network.Contains(station) || _ports.Count >= _limits.MaxStations)
        {
            throw new InvalidOperationException("Station identity exists or the Station limit is reached.");
        }
    }

    private void AddStationValue(StationId station, ICargoPort port)
    {
        ArgumentNullException.ThrowIfNull(port);
        RequireNewStation(station);
        foreach (ICargoPort existing in _ports.Values)
        {
            if (ReferenceEquals(existing, port))
            {
                throw new InvalidOperationException("A Port can bind to only one Station.");
            }
        }
        port.Bind(_authority, station);
        _network.AddStation(station);
        _ports.Add(station, port);
    }

    public void AddLink(LinkId link, StationId origin, StationId destination,
        int capacity, long transitTicks)
    {
        using MutationScope mutation = EnterMutation();
        _network.AddLink(new Link(link, origin, destination, capacity, transitTicks));
    }

    public bool RemoveLink(LinkId link)
    {
        using MutationScope mutation = EnterMutation();
        return _network.RemoveLink(link);
    }

    public RoutePlan PlanRoute(StationId origin, StationId destination)
    {
        using MutationScope mutation = EnterMutation();
        return _planner.Plan(origin, destination);
    }

    public bool IsPlanCurrent(RoutePlan plan)
    {
        using MutationScope mutation = EnterMutation();
        ArgumentNullException.ThrowIfNull(plan);
        return plan.IsCurrent(_network);
    }

    public void RegisterCargo(CargoId cargo, StationId station, CargoManifest manifest)
    {
        using MutationScope mutation = EnterMutation();
        if (_intentSink is not null) throw new InvalidOperationException("Durable cargo must be provisioned through its session.");
        RegisterCargoValue(cargo, station, manifest);
    }

    private void RequireNewCargo(CargoId cargo, StationId station, CargoManifest manifest)
    {
        IdentityValue.Require(cargo.Value);
        RequireStation(station);
        RequireManifest(manifest);
        _cargo.ValidateSeed(cargo);
    }

    private void RegisterCargoValue(CargoId cargo, StationId station, CargoManifest manifest)
    {
        RequireNewCargo(cargo, station, manifest);
        if (_ports[station].ReadCargo(cargo) != manifest)
        {
            throw new InvalidOperationException("The source inventory does not contain the registered batch.");
        }
        foreach (KeyValuePair<StationId, ICargoPort> entry in _ports)
        {
            if (entry.Key != station && entry.Value.ReadCargo(cargo) is not null)
            {
                throw new InvalidOperationException("Cargo identity exists in more than one Port.");
            }
        }
        _cargo.Seed(cargo, station, manifest);
        _authority.RegisterCargo(cargo);
    }

    public Shipment CreateShipment(ShipmentId id, StationId origin, StationId destination,
        CargoManifest manifest, ServicePolicy policy)
    {
        using MutationScope mutation = EnterMutation();
        IdentityValue.Require(id.Value);
        RequireStation(origin);
        RequireStation(destination);
        RequireManifest(manifest);
        ArgumentNullException.ThrowIfNull(policy);
        if (origin == destination)
        {
            throw new ArgumentException("Shipment endpoints must differ.");
        }
        if (_shipments.ContainsKey(id) || _shipments.Count >= _limits.MaxParcels)
        {
            throw new InvalidOperationException("Shipment identity exists or the session Shipment limit is reached.");
        }
        var shipment = new Shipment(id, origin, destination, manifest, policy);
        _shipments.Add(id, shipment);
        return shipment;
    }

    public Parcel SplitShipment(ShipmentId id, ParcelId parcelId, CargoId cargoId)
    {
        using MutationScope mutation = EnterMutation();
        IdentityValue.Require(parcelId.Value);
        Shipment shipment = _shipments[id];
        if (shipment.ParcelId is not null || _parcels.ContainsKey(parcelId)
            || _parcels.Count >= _limits.MaxParcels)
        {
            throw new InvalidOperationException("This batch is already assigned or the Parcel limit is reached.");
        }
        IdentityValue.Require(cargoId.Value);
        _cargo.Claim(cargoId, parcelId, shipment.Origin, shipment.Manifest);
        var parcel = new Parcel(parcelId, id, cargoId, shipment.Manifest, shipment.Policy,
            ParcelState.Created, 0, 0, shipment.Origin, null, CargoDispatch: _cargo.Get(cargoId).DispatchCount);
        _parcels.Add(parcelId, new Execution(parcel));
        _shipments[id] = shipment with { ParcelId = parcelId };
        return parcel;
    }

    public void ChangePolicy(ShipmentId id, ServicePolicy policy)
    {
        using MutationScope mutation = EnterMutation();
        ArgumentNullException.ThrowIfNull(policy);
        _shipments[id] = _shipments[id] with { Policy = policy };
    }

    public bool TryReserve(ParcelId id)
    {
        using MutationScope mutation = EnterMutation();
        Execution execution = _parcels[id];
        Parcel parcel = execution.Snapshot;
        if (parcel.State != ParcelState.Created || !_operations.HasRoom)
        {
            return false;
        }
        Shipment shipment = _shipments[parcel.ShipmentId];
        RoutePlan plan = _planner.Plan(shipment.Origin, shipment.Destination);
        if (plan.Status != RouteStatus.Found)
        {
            return false;
        }
        // Validate the entire timeline before reserving anything. Later hops use
        // these admitted snapshots, so no arithmetic failure can strand cargo.
        long departure = checked(Now + 1);
        long arrival = departure;
        foreach (Link link in plan.Links)
        {
            arrival = checked(arrival + link.TransitTicks);
        }
        if (!_capacity.TryReserve(plan.Links, parcel.Manifest.Quantity))
        {
            return false;
        }
        execution.Plan = plan;
        Commit(execution, ParcelState.Reserved, parcel.CurrentStation, OperationKind.Reservation, Now);
        Schedule(execution, OperationKind.Departure, departure);
        return true;
    }

    public bool Cancel(ParcelId id)
    {
        using MutationScope mutation = EnterMutation();
        Execution execution = _parcels[id];
        if (execution.Snapshot.State is not (ParcelState.Created or ParcelState.Reserved))
        {
            return false;
        }
        CancelBeforeDeparture(execution, OperationKind.Cancellation, Now);
        return true;
    }

    public bool RetryDelivery(ParcelId id)
    {
        using MutationScope mutation = EnterMutation();
        Execution execution = _parcels[id];
        Parcel parcel = execution.Snapshot;
        if (parcel.State is not (ParcelState.DeliveryRejected or ParcelState.DeliveryFaulted or ParcelState.ReturnRejected or ParcelState.ReturnFaulted)
            || parcel.PendingOperation is not null
            || parcel.DeliveryAttempts >= _limits.MaxDeliveryAttempts || !_operations.HasRoom)
        {
            return false;
        }
        long dueTick = checked(Now + 1);
        Commit(execution, parcel.State, parcel.CurrentStation, OperationKind.DeliveryRetry, Now);
        Schedule(execution, parcel.State is ParcelState.ReturnRejected or ParcelState.ReturnFaulted ? OperationKind.ReturnDelivery : OperationKind.Delivery, dueTick);
        return true;
    }

    // Explicit custody refund after failed delivery. This is not a new network journey:
    // only the retained batch may be deposited back into its source, under the same receipt budget.
    public bool ReturnToSource(ParcelId id)
    {
        using MutationScope mutation = EnterMutation();
        Execution execution = _parcels[id];
        Parcel parcel = execution.Snapshot;
        if (parcel.State is not (ParcelState.DeliveryRejected or ParcelState.DeliveryFaulted)
            || parcel.PendingOperation is not null || parcel.DeliveryAttempts >= _limits.MaxDeliveryAttempts || !_operations.HasRoom)
            return false;
        long due = checked(Now + 1);
        Commit(execution, ParcelState.ReturnRequested, parcel.CurrentStation, OperationKind.ReturnRequested, Now);
        Schedule(execution, OperationKind.ReturnDelivery, due);
        return true;
    }

    public bool ReconcileTransfer(ParcelId id)
    {
        using MutationScope mutation = EnterMutation();
        Execution execution = _parcels[id];
        Parcel parcel = execution.Snapshot;
        if (parcel.State is not (ParcelState.ExtractionUncertain or ParcelState.DeliveryUncertain or ParcelState.ReturnUncertain))
        {
            return false;
        }
        PortTransfer transfer = parcel.PendingTransfer!;
        // Applied extraction needs one arrival ticket. Do not settle custody
        // and then discover that the queue cannot represent its continuation.
        if (transfer.Kind == PortTransferKind.Extract && !_operations.HasRoom)
        {
            return false;
        }
        PortResult result = _ports[transfer.StationId].ReadResult(transfer);
        if (result is not (PortResult.Missing or PortResult.Applied or PortResult.Rejected))
        {
            throw new InvalidOperationException("Port returned an invalid retained outcome.");
        }
        SettleTransfer(execution, result);
        return true;
    }

    // A host may defer physical work while acquiring exclusive inventory access. False leaves
    // the exact pending ticket, reservation, custody and attempt count intact, including on save.
    public int AdvanceTo(long tick, int? budget = null, Func<StationId, bool>? canAccessPort = null)
    {
        using MutationScope mutation = EnterMutation();
        if (tick < Now)
        {
            throw new ArgumentOutOfRangeException(nameof(tick), "Authoritative time cannot move backwards.");
        }
        int limit = budget ?? _limits.MaxOperationsPerAdvance;
        if (limit <= 0 || limit > _limits.MaxOperationsPerAdvance)
        {
            throw new ArgumentOutOfRangeException(nameof(budget));
        }
        Now = tick;
        int processed = 0;
        while (processed < limit && _operations.Peek() is ScheduledOperation next && next.DueTick <= Now)
        {
            if (canAccessPort is not null && PhysicalPort(next) is StationId station && !canAccessPort(station))
                break;
            if (!Execute(next))
            {
                throw new InvalidOperationException("The authoritative operation queue contains an invalid transition.");
            }
            processed++;
        }
        return processed;
    }

    private StationId? PhysicalPort(ScheduledOperation operation)
    {
        Execution execution = _parcels[operation.ParcelId];
        return operation.Kind switch
        {
            // Invalidated reservations are cancelled without touching a physical inventory.
            OperationKind.Departure when execution.Plan!.IsCurrent(_network) => execution.Snapshot.CurrentStation,
            OperationKind.Delivery => execution.Snapshot.CurrentStation,
            OperationKind.ReturnDelivery => _shipments[execution.Snapshot.ShipmentId].Origin,
            _ => null
        };
    }

    public bool ApplyOperation(ScheduledOperation operation)
    {
        using MutationScope mutation = EnterMutation();
        ArgumentNullException.ThrowIfNull(operation);
        return Execute(operation);
    }

    private bool Execute(ScheduledOperation operation)
    {
        // Tickets belong to the issuing runtime. Equal values from a different
        // Network/session (or a fabricated clone) grant no mutation authority.
        if (operation.DueTick > Now || !ReferenceEquals(_operations.Peek(), operation)
            || !_parcels.TryGetValue(operation.ParcelId, out Execution? execution))
        {
            return false;
        }
        Parcel parcel = execution.Snapshot;
        if (!ReferenceEquals(parcel.PendingOperation, operation) || operation.Sequence != parcel.Version + 1)
        {
            return false;
        }
        switch (operation.Kind)
        {
            case OperationKind.Departure when parcel.State == ParcelState.Reserved:
                Depart(execution, operation.DueTick);
                return true;
            case OperationKind.Arrival when parcel.State == ParcelState.InTransit:
                Arrive(execution, operation.DueTick);
                return true;
            case OperationKind.Transfer when parcel.State == ParcelState.Arrived:
                Transfer(execution, operation.DueTick);
                return true;
            case OperationKind.Delivery when parcel.State is ParcelState.Arrived or ParcelState.DeliveryRejected or ParcelState.DeliveryFaulted:
                Deliver(execution, operation.DueTick);
                return true;
            case OperationKind.ReturnDelivery when parcel.State is ParcelState.ReturnRequested or ParcelState.ReturnRejected or ParcelState.ReturnFaulted:
                BeginTransfer(execution, PortTransferKind.Deposit, operation.DueTick, _shipments[parcel.ShipmentId].Origin);
                return true;
            default:
                return false;
        }
    }

    private void Depart(Execution execution, long tick)
    {
        if (!execution.Plan!.IsCurrent(_network))
        {
            CancelBeforeDeparture(execution, OperationKind.ReservationInvalidated, tick);
            return;
        }
        BeginTransfer(execution, PortTransferKind.Extract, tick);
    }

    private void Arrive(Execution execution, long tick)
    {
        Link link = execution.Plan!.Links[execution.Hop];
        _capacity.Release(link.Id, execution.Snapshot.Manifest.Quantity);
        Commit(execution, ParcelState.Arrived, link.Destination, OperationKind.Arrival, tick);
        bool final = execution.Hop + 1 == execution.Plan.Links.Count;
        Schedule(execution, final ? OperationKind.Delivery : OperationKind.Transfer, tick);
    }

    private void Transfer(Execution execution, long tick)
    {
        execution.Hop++;
        Link next = execution.Plan!.Links[execution.Hop];
        Commit(execution, ParcelState.InTransit, next.Origin, OperationKind.Transfer, tick);
        Schedule(execution, OperationKind.Arrival, tick + next.TransitTicks);
    }

    private void Deliver(Execution execution, long tick)
    {
        BeginTransfer(execution, PortTransferKind.Deposit, tick);
    }

    private void BeginTransfer(Execution execution, PortTransferKind kind, long tick, StationId? target = null)
    {
        Parcel parcel = execution.Snapshot;
        int attempt = kind == PortTransferKind.Extract ? 1 : parcel.DeliveryAttempts + 1;
        PortTransfer transfer = _authority.Issue(parcel, target ?? parcel.CurrentStation, kind, attempt);
        CargoOwner previous = kind == PortTransferKind.Extract
            ? CargoOwner.AtStation(parcel.CurrentStation) : CargoOwner.InParcel(parcel.Id);
        _cargo.Transfer(parcel.CargoId, previous, CargoOwner.InTransfer(transfer.Id));
        execution.TransferTick = tick;
        execution.Snapshot = parcel with
        {
            PendingTransfer = transfer,
            DeliveryAttempts = kind == PortTransferKind.Deposit ? attempt : parcel.DeliveryAttempts
        };
        PortResult result;
        try
        {
            _preparingTransfer = transfer;
            try
            {
                _intentSink?.Invoke(transfer);
            }
            finally
            {
                _preparingTransfer = null;
            }
            result = _ports[transfer.StationId].Apply(transfer);
            if (result is not (PortResult.Applied or PortResult.Rejected))
            {
                throw new InvalidOperationException("Apply must report Applied or Rejected; its result is uncertain.");
            }
        }
        catch
        {
            Commit(execution, kind == PortTransferKind.Extract
                ? ParcelState.ExtractionUncertain : target.HasValue ? ParcelState.ReturnUncertain : ParcelState.DeliveryUncertain,
                parcel.CurrentStation, OperationKind.PortUncertain, tick);
            throw;
        }
        SettleTransfer(execution, result);
    }

    private void SettleTransfer(Execution execution, PortResult result)
    {
        Parcel parcel = execution.Snapshot;
        PortTransfer transfer = parcel.PendingTransfer!;
        bool extracted = transfer.Kind == PortTransferKind.Extract;
        CargoOwner destination = result == PortResult.Applied
            ? (extracted ? CargoOwner.InParcel(parcel.Id) : CargoOwner.AtStation(transfer.StationId))
            : (extracted ? CargoOwner.AtStation(transfer.StationId) : CargoOwner.InParcel(parcel.Id));
        // Revoke before releasing any claim, particularly when Missing has no
        // receipt. A retained old request cannot mutate redispatched cargo.
        _authority.Retire(transfer);
        _cargo.Transfer(parcel.CargoId, CargoOwner.InTransfer(transfer.Id), destination);
        execution.Snapshot = parcel with { PendingTransfer = null };
        long tick = execution.TransferTick;
        if (extracted)
        {
            if (result == PortResult.Applied)
            {
                Link link = execution.Plan!.Links[0];
                Commit(execution, ParcelState.InTransit, link.Origin, OperationKind.Departure, tick);
                Schedule(execution, OperationKind.Arrival, tick + link.TransitTicks);
            }
            else
            {
                CancelBeforeDeparture(execution, OperationKind.ExtractionRejected, tick);
            }
        }
        else
        {
            bool returning = transfer.StationId == _shipments[parcel.ShipmentId].Origin;
            ParcelState state = returning
                ? result == PortResult.Applied ? ParcelState.Returned : result == PortResult.Rejected ? ParcelState.ReturnRejected : ParcelState.ReturnFaulted
                : result == PortResult.Applied ? ParcelState.Delivered : result == PortResult.Rejected ? ParcelState.DeliveryRejected : ParcelState.DeliveryFaulted;
            Commit(execution, state, state == ParcelState.Returned ? transfer.StationId : parcel.CurrentStation,
                returning ? OperationKind.ReturnDelivery : OperationKind.Delivery, tick);
            if (state is ParcelState.Delivered or ParcelState.Returned)
            {
                _cargo.ReleaseClaim(parcel.CargoId, parcel.Id);
            }
        }
    }

    private void CancelBeforeDeparture(Execution execution, OperationKind kind, long tick)
    {
        if (execution.Plan is not null)
        {
            foreach (Link link in execution.Plan.Links)
            {
                _capacity.Release(link.Id, execution.Snapshot.Manifest.Quantity);
            }
        }
        Commit(execution, ParcelState.Cancelled, execution.Snapshot.CurrentStation, kind, tick);
        _cargo.ReleaseClaim(execution.Snapshot.CargoId, execution.Snapshot.Id);
    }

    private void Commit(Execution execution, ParcelState state, StationId station,
        OperationKind kind, long tick)
    {
        Parcel parcel = execution.Snapshot;
        if (parcel.PendingOperation is ScheduledOperation pending)
        {
            _operations.Remove(pending);
        }
        execution.Snapshot = parcel with
        {
            State = state, CurrentStation = station, Version = parcel.Version + 1, PendingOperation = null
        };
        if (_events.Count == _limits.MaxEvents)
        {
            _events.Dequeue();
        }
        _events.Enqueue(new TransportEvent(parcel.Id, execution.Snapshot.Version, tick,
            kind, state, _cargo.Get(parcel.CargoId).Owner));
    }

    private void Schedule(Execution execution, OperationKind kind, long tick)
    {
        Parcel parcel = execution.Snapshot;
        var operation = new ScheduledOperation(parcel.Id, parcel.Version + 1, kind, tick);
        _operations.Add(operation);
        execution.Snapshot = parcel with { PendingOperation = operation };
    }

    private void RequireStation(StationId id)
    {
        IdentityValue.Require(id.Value);
        if (!_network.Contains(id))
        {
            throw new ArgumentException("Station does not belong to this Network.", nameof(id));
        }
    }

    private void RequireManifest(CargoManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Quantity > _limits.MaxCargoUnits)
        {
            throw new ArgumentOutOfRangeException(nameof(manifest), "Cargo exceeds the bounded Parcel size.");
        }
    }

    private MutationScope EnterMutation()
    {
        if (_retired || _ownerThread != Environment.CurrentManagedThreadId || _mutating
            || (_intentSink is not null && !_ownedCommand))
        {
            throw new InvalidOperationException("Flow mutations require the owning thread and cannot be reentrant.");
        }
        _mutating = true;
        return new MutationScope(this);
    }

    private readonly struct MutationScope : IDisposable
    {
        private readonly FlowRuntime _runtime;
        internal MutationScope(FlowRuntime runtime) => _runtime = runtime;
        public void Dispose() => _runtime._mutating = false;
    }

    private sealed class Execution
    {
        internal Execution(Parcel snapshot) => Snapshot = snapshot;
        internal Parcel Snapshot { get; set; }
        internal RoutePlan? Plan { get; set; }
        internal int Hop { get; set; }
        internal long TransferTick { get; set; }
    }
}
