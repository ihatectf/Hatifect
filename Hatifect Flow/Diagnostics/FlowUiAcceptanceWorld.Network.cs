using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Application.Planning;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Policies;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Shipments;

namespace Hatifect.Flow.Diagnostics;

// One isolated source stack and one lifetime shipment. This façade exercises the existing
// NetworkExperience/FlowSendCommand contract; it is never registered as a game inventory provider.
internal sealed partial class FlowUiAcceptanceWorld : IFlowNetworkApplication
{
    private InMemoryCargoPort? _networkSource, _networkDestination;
    private bool _networkPrepared, _networkCreated, _creating;
    private IReadOnlyList<FlowInventorySlot> _networkInventory = Array.Empty<FlowInventorySlot>();
    internal Guid SourceStation => Id(2);
    internal Guid DestinationStation => Id(3);
    internal int CreateEffects { get; private set; }

    internal void PrepareNetwork(bool connected = true)
    {
        FlowSnapshot snapshot = _owner.ReadSnapshot();
        if (_disposed || _seeded || snapshot.State != FlowApplicationState.Active)
            throw new InvalidOperationException("A fake network can be prepared only once while active.");
        _networkSource = new InMemoryCargoPort();
        _networkDestination = new InMemoryCargoPort();
        _runtime.AddStation(new StationId(SourceStation), _networkSource);
        _runtime.AddStation(new StationId(DestinationStation), _networkDestination);
        if (connected) _runtime.AddLink(new LinkId(Id(7)), new StationId(SourceStation), new StationId(DestinationStation), 20, 8);
        _networkInventory = Array.AsReadOnly(new[] { new FlowInventorySlot(0, "(O)378", Quantity,
            new string(World == "A" ? 'a' : 'b', 64), "0") });
        _networkPrepared = _seeded = true;
        _owner.Refresh();
    }

    public FlowNetworkSnapshot ReadNetwork()
    {
        FlowSnapshot snapshot = ReadSnapshot();
        return new(snapshot, snapshot.Stations.Select(station => new FlowStationDetails(station.Id,
            StationName(station.Id), "Isolated diagnostic " + World, snapshot.State == FlowApplicationState.Active)).ToArray(),
            Guid.Empty, string.Empty);
    }

    public IReadOnlyList<FlowInventorySlot> ReadInventory(Guid station)
    {
        FlowSnapshot snapshot = ReadSnapshot();
        // A claimed stack is retained by its shipment and is not offered for a second create.
        return _networkPrepared && !_networkCreated && station == SourceStation
            && snapshot.State == FlowApplicationState.Active ? _networkInventory : Array.Empty<FlowInventorySlot>();
    }

    public FlowRoutePreview PreviewRoute(Guid source, Guid destination)
    {
        FlowSnapshot snapshot = ReadSnapshot();
        if (!_networkPrepared || snapshot.State != FlowApplicationState.Active
            || source != SourceStation || destination != DestinationStation)
            return new(false, 0, 0, 0);
        RoutePlan plan = _runtime.PlanRoute(new StationId(source), new StationId(destination));
        return plan.Status == RouteStatus.Found
            ? new(true, plan.Links.Count, plan.Links.Sum(link => link.TransitTicks),
                plan.Links.Min(link => link.Capacity - _runtime.ReservedUnits(link.Id)))
            : new(false, 0, 0, 0);
    }

    public FlowCommandResult Execute(FlowSendCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _owner.ReadSnapshot();
        if (_creating) return NetworkResult(FlowCommandStatus.Rejected, FlowRejectionCode.OperationPending);
        FlowCommandResult? invalid = _owner.ValidateExternalCommand(command.SessionId, command.ExpectedRevision);
        if (invalid is not null) return invalid;
        if (!_networkPrepared || command.Source != SourceStation || command.Destination != DestinationStation
            || command.Slot != 0 || command.Quantity is <= 0 || command.Quantity > Quantity)
            return NetworkResult(FlowCommandStatus.InvalidCommand, FlowRejectionCode.InvalidCommand);
        if (_networkCreated) return NetworkResult(FlowCommandStatus.Rejected, FlowRejectionCode.WorkLimit);
        if (!string.Equals(command.Fingerprint, _networkInventory[0].Fingerprint, StringComparison.Ordinal))
            return NetworkResult(FlowCommandStatus.Conflict, FlowRejectionCode.StateChanged);
        if (!PreviewRoute(command.Source, command.Destination).Found)
            return NetworkResult(FlowCommandStatus.Rejected, FlowRejectionCode.RouteUnavailable);
        _creating = true;
        try
        {
            var cargo = new CargoId(Id(4));
            var shipment = new ShipmentId(Id(5));
            var source = new StationId(SourceStation);
            var destination = new StationId(DestinationStation);
            var manifest = new CargoManifest("(O)378", command.Quantity ?? Quantity);
            _networkSource!.Seed(cargo, manifest);
            _runtime.RegisterCargo(cargo, source, manifest);
            _runtime.CreateShipment(shipment, source, destination, manifest,
                new ServicePolicy(ServiceClass.Standard, DeliveryGuarantee.Reserved));
            _runtime.SplitShipment(shipment, new ParcelId(Parcel), cargo);
            // Production shipping also creates first, then attempts reservation. No automatic
            // dispatch is promised if reservation is unavailable; the parcel remains inspectable.
            _runtime.TryReserve(new ParcelId(Parcel));
            _networkCreated = true;
            CreateEffects++;
            _owner.Refresh();
            return NetworkResult(FlowCommandStatus.Applied);
        }
        finally { _creating = false; }
    }

    internal void AcceptNetworkDelivery(bool accept)
    {
        FlowSnapshot snapshot = _owner.ReadSnapshot();
        if (!_networkPrepared || _disposed || _creating || snapshot.State != FlowApplicationState.Active)
            throw new InvalidOperationException("The isolated network is unavailable.");
        _networkDestination!.AcceptDeposits = accept;
    }

    internal int AdvanceNetworkTo(long tick)
    {
        FlowSnapshot snapshot = _owner.ReadSnapshot();
        if (!_networkPrepared || _disposed || _creating || snapshot.State != FlowApplicationState.Active)
            throw new InvalidOperationException("The isolated network cannot advance.");
        int transitions = _runtime.AdvanceTo(tick);
        _owner.Refresh();
        return transitions;
    }

    public IReadOnlyList<FlowRecoveryIssue> ReadRecovery() { _owner.ReadSnapshot(); return Array.Empty<FlowRecoveryIssue>(); }
    public FlowCommandResult Execute(FlowNetworkCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _owner.ValidateExternalCommand(command.SessionId, command.ExpectedRevision)
            ?? NetworkResult(FlowCommandStatus.Rejected, FlowRejectionCode.UnsupportedAction);
    }
    public FlowCommandResult Execute(FlowRecoveryCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _owner.ValidateExternalCommand(command.SessionId, command.ExpectedRevision, recovery: true)
            ?? NetworkResult(FlowCommandStatus.Rejected, FlowRejectionCode.UnsupportedAction);
    }
    private FlowCommandResult NetworkResult(FlowCommandStatus status, FlowRejectionCode code = FlowRejectionCode.None)
        => new(status, _owner.ReadSnapshot().Revision, code);
}
