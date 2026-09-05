using System;
using System.Linq;
using Hatifect.Flow.Domain.Shipments;

namespace Hatifect.Flow.Application.Dispatch;

internal sealed partial class FlowRuntime
{
    internal int RetainedLinkCount => _network.History.Count();

    internal FlowSnapshot ReadApplicationSnapshot(Guid sessionId, long revision)
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
                return new FlowParcelSnapshot(parcel.Id.Value, parcel.ShipmentId.Value, parcel.CargoId.Value,
                    parcel.Manifest.ItemKey, parcel.Manifest.Quantity, shipment.Origin.Value, shipment.Destination.Value,
                    parcel.CurrentStation.Value, parcel.State, parcel.Version, parcel.DeliveryAttempts, AvailableActions(parcel));
            }).ToArray();
        return new FlowSnapshot(sessionId, NetworkId.Value, revision, FlowApplicationState.Active, stations, links, parcels);
    }

    private FlowParcelActions AvailableActions(Parcel parcel)
    {
        FlowParcelActions actions = FlowParcelActions.None;
        if (parcel.State == ParcelState.Created && _operations.HasRoom)
        {
            // Route/capacity admission is still checked by the command; reading a view does not search the graph.
            actions |= FlowParcelActions.Reserve;
        }
        if (parcel.State is ParcelState.Created or ParcelState.Reserved)
        {
            actions |= FlowParcelActions.Cancel;
        }
        if (parcel.State is ParcelState.DeliveryRejected or ParcelState.DeliveryFaulted or ParcelState.ReturnRejected or ParcelState.ReturnFaulted
            && parcel.PendingOperation is null && parcel.DeliveryAttempts < _limits.MaxDeliveryAttempts && _operations.HasRoom)
        {
            actions |= FlowParcelActions.RetryDelivery;
            if (parcel.State is ParcelState.DeliveryRejected or ParcelState.DeliveryFaulted) actions |= FlowParcelActions.ReturnToSource;
        }
        if (parcel.State is ParcelState.DeliveryUncertain or ParcelState.ReturnUncertain || parcel.State == ParcelState.ExtractionUncertain && _operations.HasRoom)
        {
            actions |= FlowParcelActions.ReconcileTransfer;
        }
        return actions;
    }
}
