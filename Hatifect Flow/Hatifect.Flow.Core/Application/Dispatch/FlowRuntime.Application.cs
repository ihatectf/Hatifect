using System;
using System.Linq;
using Hatifect.Flow.Application.Planning;
using Hatifect.Flow.Domain.Shipments;

namespace Hatifect.Flow.Application.Dispatch;

internal sealed partial class FlowRuntime
{
    internal int RetainedLinkCount => _network.History.Count();

    internal FlowSnapshot ReadApplicationSnapshot(Guid sessionId, long revision,
        FlowProviderMode providerMode = FlowProviderMode.DiagnosticFake, FlowParcelActions supported = FlowParcelActions.All)
    {
        if (_ownerThread != Environment.CurrentManagedThreadId || _mutating || _retired)
        {
            throw new InvalidOperationException("Application reads require an active idle runtime on its owning thread.");
        }
        var stations = _ports.Keys.OrderBy(id => id.Value).Select(id => new FlowStationSnapshot(id.Value)).ToArray();
        var links = _network.History.Where(link => _network.IsActive(link.Id)).OrderBy(link => link.Id.Value)
            .Select(link => new FlowLinkSnapshot(link.Id.Value, link.Origin.Value, link.Destination.Value,
                link.Capacity, link.TransitTicks)).ToArray();
        var parcels = _parcels.Values.Select(execution => execution.Snapshot).OrderBy(parcel => parcel.Id.Value)
            .Select(parcel =>
            {
                Shipment shipment = _shipments[parcel.ShipmentId];
                FlowActionAvailabilitySet availability = AvailableActions(parcel);
                if (supported != FlowParcelActions.All) availability = availability.Restrict(supported);
                return new FlowParcelSnapshot(parcel.Id.Value, parcel.ShipmentId.Value, parcel.CargoId.Value,
                    parcel.Manifest.ItemKey, parcel.Manifest.Quantity, shipment.Origin.Value, shipment.Destination.Value,
                    parcel.CurrentStation.Value, parcel.State, parcel.Version, parcel.DeliveryAttempts, availability.Actions, availability);
            }).ToArray();
        return new FlowSnapshot(sessionId, NetworkId.Value, revision, FlowApplicationState.Active, stations, links, parcels, providerMode, supported);
    }

    private FlowActionAvailabilitySet AvailableActions(Parcel parcel)
    {
        FlowRejectionCode reserve = ReservationRejection(parcel, out _);
        FlowRejectionCode cancel = parcel.State is ParcelState.Created or ParcelState.Reserved ? FlowRejectionCode.None : FlowRejectionCode.InvalidState;
        FlowRejectionCode retry = parcel.State is not (ParcelState.DeliveryRejected or ParcelState.DeliveryFaulted or ParcelState.ReturnRejected or ParcelState.ReturnFaulted)
            ? FlowRejectionCode.InvalidState : parcel.PendingOperation is not null ? FlowRejectionCode.OperationPending
            : parcel.DeliveryAttempts >= _limits.MaxDeliveryAttempts ? FlowRejectionCode.RetryLimit
            : !_operations.HasRoom ? FlowRejectionCode.WorkLimit : FlowRejectionCode.None;
        FlowRejectionCode returning = parcel.State is ParcelState.DeliveryRejected or ParcelState.DeliveryFaulted ? retry : FlowRejectionCode.InvalidState;
        FlowRejectionCode reconcile = parcel.State is ParcelState.DeliveryUncertain or ParcelState.ReturnUncertain ? FlowRejectionCode.None
            : parcel.State != ParcelState.ExtractionUncertain ? FlowRejectionCode.InvalidState
            : _operations.HasRoom ? FlowRejectionCode.None : FlowRejectionCode.WorkLimit;
        return FlowActionAvailabilitySet.FromCodes(reserve, cancel, retry, reconcile, returning);
    }

    private FlowRejectionCode ReservationRejection(Parcel parcel, out RoutePlan? plan)
    {
        plan = null;
        if (parcel.State != ParcelState.Created) return FlowRejectionCode.InvalidState;
        if (!_operations.HasRoom) return FlowRejectionCode.WorkLimit;
        Shipment shipment = _shipments[parcel.ShipmentId];
        plan = _planner.Plan(shipment.Origin, shipment.Destination);
        if (plan.Status != RouteStatus.Found) return plan.Status == RouteStatus.SearchLimitExceeded
            ? FlowRejectionCode.RouteSearchLimit : FlowRejectionCode.RouteUnavailable;
        foreach (var link in plan.Links)
            if (parcel.Manifest.Quantity > link.Capacity - _capacity.ReservedUnits(link.Id)) return FlowRejectionCode.CapacityUnavailable;
        return FlowRejectionCode.None;
    }
}
