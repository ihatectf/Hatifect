using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.Flow.Application.Planning;
using Hatifect.Flow.Diagnostics;
using Hatifect.Flow.Domain.Checkpoints;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Network;
using Hatifect.Flow.Domain.Policies;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Scheduling;
using Hatifect.Flow.Domain.Shipments;
using static Hatifect.Flow.Domain.Checkpoints.CheckpointValues;

namespace Hatifect.Flow.Application.Dispatch;

internal sealed partial class FlowRuntime
{
    public static FlowRuntime RestoreCheckpoint(FlowCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        try
        {
            return new CheckpointReader(checkpoint).Read();
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or OverflowException)
        {
            throw new ArgumentException("Invalid Flow checkpoint: inconsistent references, capacity or timeline.",
                nameof(checkpoint), error);
        }
    }

    // Construction remains private until the last gate passes. No external Port
    // is bound and no inventory Apply operation is performed during restoration.
    private sealed class CheckpointReader
    {
        private const int MaximumCollection = 65536;
        private const int MaximumEntries = 262144;
        private readonly FlowCheckpoint _data;
        private readonly FlowRuntime _runtime;
        private readonly Dictionary<TransferKey, PortTransfer> _transfers = new();
        private readonly Dictionary<TransferKey, PortResult> _receipts = new();
        private readonly Dictionary<CargoId, (StationId Station, CargoManifest Manifest)> _physical = new();
        private int _entries;

        internal CheckpointReader(FlowCheckpoint data)
        {
            _data = data;
            LimitsCheckpoint limits = data.Limits;
            Require(limits is not null, "missing limits.");
            foreach (int limit in new[] { limits.MaxStations, limits.MaxLinks, limits.MaxParcels,
                limits.MaxRouteVisits, limits.MaxRoutePlans, limits.MaxEvents,
                limits.MaxOperationsPerAdvance, limits.MaxPendingOperations })
            {
                Require(limit > 0 && limit <= MaximumCollection, "unsupported resource limit.");
            }
            Require(limits.MaxDeliveryAttempts is > 0 and <= 64, "unsupported attempt limit.");
            var policy = new FlowLimits(limits.MaxStations, limits.MaxLinks, limits.MaxParcels,
                limits.MaxRouteVisits, limits.MaxRoutePlans, limits.MaxDeliveryAttempts, limits.MaxEvents,
                limits.MaxOperationsPerAdvance, limits.MaxCargoUnits, limits.MaxPendingOperations);
            Require(data.Now >= 0, "negative authoritative time.");
            _runtime = new FlowRuntime(new NetworkId(data.NetworkId), policy) { Now = data.Now };
        }

        internal FlowRuntime Read()
        {
            RestoreNetwork();
            RestoreShipments();
            RestoreParcels();
            RestoreTransfers();
            RestoreReceipts();
            RestoreCargo();
            ValidateExecutions();
            ValidateCargoLineage();
            ValidateConservation();
            RestoreEvents();
            return _runtime;
        }

        private T[] Items<T>(T[]? items, int maximum)
        {
            T[] result = CheckpointValues.Array(items, maximum);
            _entries = checked(_entries + result.Length);
            Require(_entries <= MaximumEntries, "checkpoint entry budget exceeded.");
            return result;
        }

        private CargoManifest ReadManifest(ManifestCheckpoint? value)
            => Manifest(value, _runtime._limits.MaxCargoUnits);

        private StationId Station(Guid value)
        {
            var id = new StationId(value);
            _runtime.RequireStation(id);
            return id;
        }

        private void RestoreNetwork()
        {
            foreach (StationCheckpoint entry in Items(_data.Stations, _runtime._limits.MaxStations))
            {
                var station = new StationId(entry.Id);
                PortCheckpoint port = entry.Port;
                Require(port is not null && port.MaxCargoBatches is > 0 and <= MaximumCollection
                    && port.MaxReceipts is > 0 and <= MaximumEntries, "unsupported Port limit.");
                var restored = new InMemoryCargoPort(port.MaxCargoBatches, port.MaxReceipts)
                {
                    AcceptDeposits = port.AcceptDeposits,
                    AcceptExtractions = port.AcceptExtractions
                };
                foreach (InventoryCheckpoint batch in Items(port.Inventory, port.MaxCargoBatches))
                {
                    var cargo = new CargoId(batch.CargoId);
                    CargoManifest manifest = ReadManifest(batch.Manifest);
                    Require(_physical.TryAdd(cargo, (station, manifest)), "duplicate physical cargo identity.");
                    restored.Seed(cargo, manifest);
                }
                _runtime.AddStation(station, restored);
            }
            foreach (LinkCheckpoint link in Items(_data.Links, _runtime._limits.MaxLinks))
            {
                var id = new LinkId(link.Id);
                _runtime.AddLink(id, Station(link.Origin), Station(link.Destination), link.Capacity, link.TransitTicks);
                if (!link.Active)
                {
                    _runtime.RemoveLink(id);
                }
            }
        }

        private void RestoreShipments()
        {
            foreach (ShipmentCheckpoint entry in Items(_data.Shipments, _runtime._limits.MaxParcels))
            {
                var id = new ShipmentId(entry.Id);
                Shipment shipment = _runtime.CreateShipment(id, Station(entry.Origin), Station(entry.Destination),
                    ReadManifest(entry.Manifest), Policy(entry.Policy));
                _runtime._shipments[id] = shipment with
                {
                    ParcelId = entry.ParcelId is Guid parcel ? new ParcelId(parcel) : null
                };
            }
        }

        private void RestoreParcels()
        {
            foreach (ParcelCheckpoint entry in Items(_data.Parcels, _runtime._limits.MaxParcels))
            {
                var id = new ParcelId(entry.Id);
                var shipmentId = new ShipmentId(entry.ShipmentId);
                Shipment shipment = _runtime._shipments[shipmentId];
                CargoManifest manifest = ReadManifest(entry.Manifest);
                Require(shipment.ParcelId == id && shipment.Manifest == manifest,
                    "Parcel does not match its Shipment.");
                long maximumVersion = 5L + 2L * _runtime._limits.MaxLinks + 3L * _runtime._limits.MaxDeliveryAttempts;
                Require(entry.Version >= 0 && entry.Version <= maximumVersion
                    && entry.DeliveryAttempts >= 0 && entry.DeliveryAttempts <= _runtime._limits.MaxDeliveryAttempts
                    && entry.TransferTick >= 0 && entry.TransferTick <= _data.Now, "invalid execution counters.");
                ScheduledOperation? operation = entry.PendingOperation is null ? null : new ScheduledOperation(id,
                    entry.PendingOperation.Sequence, EnumValue<OperationKind>(entry.PendingOperation.Kind),
                    entry.PendingOperation.DueTick);
                if (operation is not null)
                {
                    Require(operation.Sequence == entry.Version + 1 && operation.DueTick >= 0,
                        "invalid operation sequence or time.");
                    _runtime._operations.Add(operation);
                }
                var parcel = new Parcel(id, shipmentId, new CargoId(entry.CargoId), manifest, Policy(entry.Policy),
                    EnumValue<ParcelState>(entry.State), entry.Version, entry.DeliveryAttempts,
                    Station(entry.CurrentStation), operation, CargoDispatch: entry.CargoDispatch);
                var execution = new Execution(parcel) { Hop = entry.Hop, TransferTick = entry.TransferTick };
                if (entry.Plan is RouteCheckpoint route)
                {
                    execution.Plan = ReadRoute(route, shipment);
                    Require(entry.Hop >= 0 && entry.Hop < execution.Plan.Links.Count, "invalid route cursor.");
                }
                else
                {
                    Require(entry.Hop == 0, "cursor without route.");
                }
                _runtime._parcels.Add(id, execution);
            }
            foreach (Shipment shipment in _runtime._shipments.Values)
            {
                Require(shipment.ParcelId is null || (_runtime._parcels.TryGetValue(shipment.ParcelId.Value,
                    out Execution? parcel) && parcel.Snapshot.ShipmentId == shipment.Id), "orphan Shipment Parcel.");
            }
        }

        private RoutePlan ReadRoute(RouteCheckpoint route, Shipment shipment)
        {
            Guid[] ids = Items(route.Links, _runtime._limits.MaxRouteVisits);
            Require(ids.Length > 0, "empty admitted route.");
            var links = new Link[ids.Length];
            var visited = new HashSet<StationId> { shipment.Origin };
            StationId previous = shipment.Origin;
            var dependencies = new Dictionary<StationId, long>();
            foreach (DependencyCheckpoint dependency in Items(route.Dependencies, _runtime._limits.MaxRouteVisits))
            {
                StationId station = Station(dependency.StationId);
                Require(dependency.Revision >= 0 && dependency.Revision <= _runtime._network.Revision(station),
                    "invalid topology dependency revision.");
                dependencies.Add(station, dependency.Revision);
            }
            for (int index = 0; index < ids.Length; index++)
            {
                Link link = _runtime._network.HistoricalLink(new LinkId(ids[index]));
                Require(link.Capacity >= shipment.Manifest.Quantity && link.Origin == previous && visited.Add(link.Destination)
                    && dependencies.ContainsKey(link.Origin), "disconnected or cyclic admitted route.");
                links[index] = link;
                previous = link.Destination;
            }
            Require(previous == shipment.Destination, "route destination mismatch.");
            RoutePlan plan = RoutePlan.Restore(_runtime._network, links, dependencies);
            Require(!plan.IsCurrent(_runtime._network) || links.All(link => _runtime._network.IsActive(link.Id)),
                "current route uses a closed Link.");
            return plan;
        }

        private void RestoreTransfers()
        {
            int maximum = (int)Math.Min(MaximumEntries,
                (long)_runtime._limits.MaxParcels * (1L + _runtime._limits.MaxDeliveryAttempts));
            foreach (TransferCheckpoint entry in Items(_data.Transfers, maximum))
            {
                Require(entry.Key is not null, "missing transfer key.");
                Parcel parcel = _runtime._parcels[new ParcelId(entry.Key.ParcelId)].Snapshot;
                Shipment shipment = _runtime._shipments[parcel.ShipmentId];
                PortTransferKind kind = EnumValue<PortTransferKind>(entry.Key.Kind);
                StationId station = Station(entry.StationId);
                Require(entry.CargoId == parcel.CargoId.Value && ReadManifest(entry.Manifest) == parcel.Manifest
                    && (kind == PortTransferKind.Extract ? station == shipment.Origin : station == shipment.Destination || station == shipment.Origin),
                    "transfer payload does not match its execution.");
                Require(entry.Key.Attempt >= 1 && entry.Key.Attempt <= (kind == PortTransferKind.Extract
                    ? 1 : parcel.DeliveryAttempts), "invalid transfer attempt.");
                PortTransfer transfer = _runtime._authority.Issue(parcel, station, kind, entry.Key.Attempt);
                _transfers.Add(entry.Key, transfer);
                if (entry.Retired)
                {
                    _runtime._authority.Retire(transfer);
                }
            }
            foreach (ParcelCheckpoint entry in _data.Parcels)
            {
                if (entry.PendingTransfer is TransferKey key)
                {
                    Execution execution = _runtime._parcels[new ParcelId(entry.Id)];
                    PortTransfer transfer = _transfers[key];
                    Require(transfer.Id.ParcelId == execution.Snapshot.Id, "pending transfer belongs to another Parcel.");
                    execution.Snapshot = execution.Snapshot with { PendingTransfer = transfer };
                }
            }
        }

        private void RestoreReceipts()
        {
            foreach (StationCheckpoint station in _data.Stations)
            {
                var port = (InMemoryCargoPort)_runtime._ports[new StationId(station.Id)];
                foreach (ReceiptCheckpoint receipt in Items(station.Port.Receipts, station.Port.MaxReceipts))
                {
                    Require(receipt.Key is not null, "missing receipt key.");
                    PortTransfer transfer = _transfers[receipt.Key];
                    Require(transfer.StationId.Value == station.Id, "receipt belongs to another Station.");
                    PortResult result = EnumValue<PortResult>(receipt.Result);
                    Require(result != PortResult.Missing, "Missing is not a retained receipt.");
                    _receipts.Add(receipt.Key, result);
                    port.RestoreReceipt(transfer, result);
                }
            }
        }

        private CargoOwner ReadOwner(OwnerCheckpoint? entry)
        {
            Require(entry is not null, "missing cargo owner.");
            Require((entry.StationId is null ? 0 : 1) + (entry.ParcelId is null ? 0 : 1)
                + (entry.Transfer is null ? 0 : 1) == 1, "cargo must have exactly one owner.");
            if (entry.StationId is Guid station)
            {
                return CargoOwner.AtStation(Station(station));
            }
            if (entry.ParcelId is Guid parcel)
            {
                var id = new ParcelId(parcel);
                Require(_runtime._parcels.ContainsKey(id), "unknown Parcel owner.");
                return CargoOwner.InParcel(id);
            }
            return CargoOwner.InTransfer(_transfers[entry.Transfer!].Id);
        }

        private void RestoreCargo()
        {
            foreach (CargoCheckpoint entry in Items(_data.Cargo, _runtime._limits.MaxParcels))
            {
                var id = new CargoId(entry.Id);
                Require(entry.DispatchCount >= 0 && entry.DispatchCount <= _runtime._limits.MaxParcels,
                    "invalid cargo dispatch count.");
                var batch = new CargoBatch(id, ReadManifest(entry.Manifest), ReadOwner(entry.Owner),
                    Station(entry.RegistrationStation), entry.ClaimedBy is Guid parcel ? new ParcelId(parcel) : null,
                    entry.DispatchCount);
                _runtime._cargo.Restore(batch);
                _runtime._authority.RegisterCargo(id);
                if (batch.ClaimedBy is ParcelId claimant)
                {
                    Parcel execution = _runtime._parcels[claimant].Snapshot;
                    Require(execution.CargoId == id && execution.Manifest == batch.Manifest
                        && execution.State is not (ParcelState.Cancelled or ParcelState.Delivered or ParcelState.Returned), "invalid cargo claim.");
                }
            }
        }

        private void ValidateExecutions()
        {
            foreach (Execution execution in _runtime._parcels.Values)
            {
                Parcel parcel = execution.Snapshot;
                Shipment shipment = _runtime._shipments[parcel.ShipmentId];
                CargoBatch batch = _runtime._cargo.Get(parcel.CargoId);
                Require(batch.Manifest == parcel.Manifest, "Parcel cargo manifest mismatch.");
                bool terminal = parcel.State is ParcelState.Cancelled or ParcelState.Delivered or ParcelState.Returned;
                Require(terminal || batch.ClaimedBy == parcel.Id, "live Parcel has no exclusive cargo claim.");
                Require(parcel.State == ParcelState.Created ? parcel.Version == 0 : parcel.Version > 0,
                    "state and sequence disagree.");
                Require(parcel.State != ParcelState.Reserved || parcel.Version == 1,
                    "reserved sequence is not its first transition.");
                Require(parcel.State != ParcelState.ExtractionUncertain || parcel.Version == 2,
                    "uncertain extraction sequence mismatch.");
                bool beforeDeparture = parcel.State is ParcelState.Created or ParcelState.Reserved
                    or ParcelState.Cancelled or ParcelState.ExtractionUncertain;
                Require(!beforeDeparture || (execution.Hop == 0 && parcel.CurrentStation == shipment.Origin),
                    "pre-departure cursor moved.");
                Require(parcel.State != ParcelState.Created || execution.Plan is null, "Created Parcel has an admitted route.");
                Require(parcel.State is not (ParcelState.Created or ParcelState.Reserved)
                    || execution.TransferTick == 0, "transfer time precedes first departure.");
                Require(execution.Plan is not null || execution.TransferTick == 0, "transfer without an admitted route.");
                Require(parcel.State is ParcelState.Created or ParcelState.Reserved or ParcelState.Cancelled
                    || execution.TransferTick >= 1, "execution started before its first departure.");
                if (parcel.State is not (ParcelState.Created or ParcelState.Cancelled))
                {
                    Require(execution.Plan is not null, "executing Parcel has no admitted route.");
                }
                bool uncertain = parcel.State is ParcelState.ExtractionUncertain or ParcelState.DeliveryUncertain or ParcelState.ReturnUncertain;
                Require(uncertain == (parcel.PendingTransfer is not null), "state and pending transfer disagree.");
                ValidateHistory(parcel);
                ValidateSchedule(execution);
                if (terminal)
                {
                    continue; // Historical CargoId may already be dispatched by a newer Parcel.
                }
                CargoOwner expected = parcel.State switch
                {
                    ParcelState.Created or ParcelState.Reserved => CargoOwner.AtStation(shipment.Origin),
                    ParcelState.ExtractionUncertain or ParcelState.DeliveryUncertain or ParcelState.ReturnUncertain => CargoOwner.InTransfer(parcel.PendingTransfer!.Id),
                    _ => CargoOwner.InParcel(parcel.Id)
                };
                Require(batch.Owner == expected, "execution state and authoritative owner disagree.");
                RestoreCapacity(execution);
            }
        }

        private void ValidateHistory(Parcel parcel)
        {
            var extractKey = new TransferKey(parcel.Id.Value, (int)PortTransferKind.Extract, 1);
            _transfers.TryGetValue(extractKey, out PortTransfer? extract);
            bool before = parcel.State is ParcelState.Created or ParcelState.Reserved;
            if (before)
            {
                Require(extract is null && parcel.DeliveryAttempts == 0, "pre-departure transfer history.");
            }
            else if (parcel.State == ParcelState.ExtractionUncertain)
            {
                Require(extract is not null && parcel.PendingTransfer == extract && parcel.DeliveryAttempts == 0,
                    "uncertain extraction history mismatch.");
            }
            else if (parcel.State == ParcelState.Cancelled)
            {
                Require(parcel.DeliveryAttempts == 0 && (extract is null || Result(extract) != PortResult.Applied),
                    "cancelled Parcel was extracted.");
                Execution cancelled = _runtime._parcels[parcel.Id];
                Require(extract is null ? cancelled.TransferTick == 0
                    : cancelled.TransferTick >= 1 && cancelled.Plan is not null,
                    "cancelled extraction has no matching admission history.");
            }
            else
            {
                Require(extract is not null && Result(extract) == PortResult.Applied, "missing applied extraction.");
            }
            if (extract is not null)
            {
                Require(_runtime._authority.IsActive(extract) == (parcel.PendingTransfer == extract),
                    "extraction capability retirement mismatch.");
            }
            bool deliveryState = parcel.State is ParcelState.DeliveryRejected or ParcelState.DeliveryFaulted
                or ParcelState.Delivered or ParcelState.DeliveryUncertain or ParcelState.ReturnRequested
                or ParcelState.Returned or ParcelState.ReturnRejected or ParcelState.ReturnFaulted or ParcelState.ReturnUncertain;
            Require(deliveryState ? parcel.DeliveryAttempts > 0 : parcel.DeliveryAttempts == 0,
                "state and delivery attempts disagree.");
            bool returning = false;
            for (int attempt = 1; attempt <= parcel.DeliveryAttempts; attempt++)
            {
                PortTransfer deposit = _transfers[new TransferKey(parcel.Id.Value, (int)PortTransferKind.Deposit, attempt)];
                bool toSource = deposit.StationId == _runtime._shipments[parcel.ShipmentId].Origin;
                Require(!toSource || attempt > 1, "return precedes a failed delivery.");
                Require(!returning || toSource, "delivery resumed after returning cargo.");
                returning |= toSource;
                Require(_runtime._authority.IsActive(deposit) == (parcel.PendingTransfer == deposit),
                    "deposit capability retirement mismatch.");
                PortResult result = Result(deposit);
                if (attempt < parcel.DeliveryAttempts)
                {
                    Require(result != PortResult.Applied, "a completed deposit was retried.");
                }
                else
                {
                    Require(parcel.State switch
                    {
                        ParcelState.Delivered => !returning && result == PortResult.Applied,
                        ParcelState.DeliveryRejected => !returning && result == PortResult.Rejected,
                        ParcelState.DeliveryFaulted => !returning && result == PortResult.Missing,
                        ParcelState.DeliveryUncertain => !returning && parcel.PendingTransfer == deposit,
                        ParcelState.ReturnRequested => !returning && result != PortResult.Applied,
                        ParcelState.Returned => returning && result == PortResult.Applied,
                        ParcelState.ReturnRejected => returning && result == PortResult.Rejected,
                        ParcelState.ReturnFaulted => returning && result == PortResult.Missing,
                        ParcelState.ReturnUncertain => returning && parcel.PendingTransfer == deposit,
                        _ => false
                    }, "latest deposit and execution state disagree.");
                }
            }
        }

        private PortResult Result(PortTransfer transfer) => _receipts.GetValueOrDefault(Key(transfer.Id), PortResult.Missing);

        private void ValidateSchedule(Execution execution)
        {
            Parcel parcel = execution.Snapshot;
            ScheduledOperation? pending = parcel.PendingOperation;
            IReadOnlyList<Link>? route = execution.Plan?.Links;
            bool final = route is not null && execution.Hop == route.Count - 1;
            OperationKind? expected = parcel.State switch
            {
                ParcelState.Reserved => OperationKind.Departure,
                ParcelState.InTransit => OperationKind.Arrival,
                ParcelState.Arrived => final ? OperationKind.Delivery : OperationKind.Transfer,
                ParcelState.DeliveryRejected or ParcelState.DeliveryFaulted when pending is not null => OperationKind.Delivery,
                ParcelState.ReturnRequested => OperationKind.ReturnDelivery,
                ParcelState.ReturnRejected or ParcelState.ReturnFaulted when pending is not null => OperationKind.ReturnDelivery,
                _ => null
            };
            Require(expected is null ? pending is null : pending?.Kind == expected,
                "missing or incompatible execution ticket.");
            Require(parcel.State != ParcelState.Reserved || pending!.DueTick >= 1,
                "departure precedes its admission boundary.");
            if (parcel.State == ParcelState.InTransit)
            {
                Require(parcel.CurrentStation == route![execution.Hop].Origin, "transit cursor mismatch.");
            }
            else if (parcel.State == ParcelState.Arrived)
            {
                Require(parcel.CurrentStation == route![execution.Hop].Destination, "arrival cursor mismatch.");
            }
            else if (parcel.State is ParcelState.Delivered or ParcelState.DeliveryRejected
                or ParcelState.DeliveryFaulted or ParcelState.DeliveryUncertain or ParcelState.ReturnRequested
                or ParcelState.Returned or ParcelState.ReturnRejected or ParcelState.ReturnFaulted or ParcelState.ReturnUncertain)
            {
                StationId expectedStation = parcel.State == ParcelState.Returned ? _runtime._shipments[parcel.ShipmentId].Origin : route![execution.Hop].Destination;
                Require(final && parcel.CurrentStation == expectedStation,
                    "delivery did not reach the route destination.");
                Require(pending is null || parcel.DeliveryAttempts < _runtime._limits.MaxDeliveryAttempts,
                    "retry exceeds attempt budget.");
                Require(pending is null || pending.DueTick > execution.TransferTick,
                    "retry must occur after the previous delivery attempt.");
            }
            if (route is not null && (pending is not null || parcel.State == ParcelState.ExtractionUncertain))
            {
                long time = pending?.DueTick ?? execution.TransferTick;
                Require(time >= execution.TransferTick, "ticket precedes admitted transfer.");
                int first = parcel.State is ParcelState.Reserved or ParcelState.ExtractionUncertain
                    ? 0 : execution.Hop + 1;
                for (int index = first; index < route.Count; index++)
                {
                    time = checked(time + route[index].TransitTicks);
                }
                if (parcel.State is ParcelState.InTransit or ParcelState.Arrived)
                {
                    long arrival = execution.TransferTick;
                    for (int index = 0; index <= execution.Hop; index++)
                    {
                        arrival = checked(arrival + route[index].TransitTicks);
                        if (index < execution.Hop || parcel.State == ParcelState.Arrived)
                        {
                            Require(arrival <= _data.Now, "route cursor passed an arrival that has not occurred.");
                        }
                    }
                    Require(pending!.DueTick == arrival, "arrival changed its original admitted timeline.");
                }
            }
        }

        private void RestoreCapacity(Execution execution)
        {
            Parcel parcel = execution.Snapshot;
            int first = parcel.State switch
            {
                ParcelState.Reserved or ParcelState.ExtractionUncertain => 0,
                ParcelState.InTransit => execution.Hop,
                ParcelState.Arrived => execution.Hop + 1,
                _ => -1
            };
            if (first < 0)
            {
                return;
            }
            Link[] remaining = execution.Plan!.Links.Skip(first).ToArray();
            Require(_runtime._capacity.TryReserve(remaining, parcel.Manifest.Quantity), "restored Link capacity exceeded.");
        }

        private void ValidateConservation()
        {
            foreach (CargoBatch batch in _runtime._cargo.Batches)
            {
                StationId? physicalStation = batch.Owner.Station;
                if (batch.Owner.Transfer is PortTransferId id)
                {
                    PortTransfer transfer = _transfers[Key(id)];
                    Require(transfer.CargoId == batch.Id && batch.ClaimedBy == transfer.Id.ParcelId
                        && _runtime._authority.IsActive(transfer), "transfer custody has no live claim.");
                    bool applied = Result(transfer) == PortResult.Applied;
                    physicalStation = transfer.Kind == PortTransferKind.Extract
                        ? (applied ? null : transfer.StationId)
                        : (applied ? transfer.StationId : null);
                }
                else if (batch.Owner.Parcel is ParcelId parcel)
                {
                    Require(batch.ClaimedBy == parcel, "Parcel custody has no live claim.");
                }
                bool present = _physical.TryGetValue(batch.Id, out var physical);
                Require(physicalStation is StationId station
                    ? present && physical.Station == station && physical.Manifest == batch.Manifest
                    : !present, "physical inventory and authoritative custody disagree.");
            }
        }

        private void ValidateCargoLineage()
        {
            var history = new Dictionary<CargoId, SortedDictionary<int, Parcel>>();
            foreach (Execution execution in _runtime._parcels.Values)
            {
                Parcel parcel = execution.Snapshot;
                if (!history.TryGetValue(parcel.CargoId, out SortedDictionary<int, Parcel>? dispatches))
                {
                    dispatches = new SortedDictionary<int, Parcel>();
                    history.Add(parcel.CargoId, dispatches);
                }
                dispatches.Add(parcel.CargoDispatch, parcel);
            }
            foreach (CargoBatch batch in _runtime._cargo.Batches)
            {
                history.TryGetValue(batch.Id, out SortedDictionary<int, Parcel>? dispatches);
                Require((dispatches?.Count ?? 0) == batch.DispatchCount, "cargo dispatch history has gaps.");
                StationId settled = batch.RegistrationStation;
                long settledAfter = 0;
                int expected = 1;
                bool live = false;
                if (dispatches is not null)
                {
                    foreach (KeyValuePair<int, Parcel> entry in dispatches)
                    {
                        Parcel parcel = entry.Value;
                        Shipment shipment = _runtime._shipments[parcel.ShipmentId];
                        Require(entry.Key == expected && shipment.Origin == settled && !live,
                            "cargo dispatch history is discontinuous or overlaps.");
                        Execution execution = _runtime._parcels[parcel.Id];
                        if (parcel.State == ParcelState.Reserved)
                        {
                            Require(parcel.PendingOperation!.DueTick > settledAfter,
                                "redispatch departure precedes the previous settlement.");
                        }
                        else if (execution.TransferTick > 0)
                        {
                            long earliest = checked(settledAfter + 1);
                            if (parcel.State is ParcelState.Delivered or ParcelState.DeliveryRejected
                                or ParcelState.DeliveryFaulted or ParcelState.DeliveryUncertain or ParcelState.ReturnRequested
                                or ParcelState.Returned or ParcelState.ReturnRejected or ParcelState.ReturnFaulted or ParcelState.ReturnUncertain)
                            {
                                foreach (Link link in execution.Plan!.Links)
                                {
                                    earliest = checked(earliest + link.TransitTicks);
                                }
                            }
                            Require(execution.TransferTick >= earliest,
                                "cargo execution predates its custody or admitted transit.");
                        }
                        expected++;
                        if (parcel.State is ParcelState.Delivered or ParcelState.Returned)
                        {
                            settled = parcel.State == ParcelState.Returned ? shipment.Origin : shipment.Destination;
                            settledAfter = execution.TransferTick;
                        }
                        else if (parcel.State == ParcelState.Cancelled)
                        {
                            // A pre-extraction cancellation has no transfer tick.
                            // Keep the earlier known bound without inventing one.
                            settledAfter = Math.Max(settledAfter, execution.TransferTick);
                        }
                        else
                        {
                            live = true;
                        }
                    }
                }
                Require(live || (batch.ClaimedBy is null && batch.Owner == CargoOwner.AtStation(settled)),
                    "settled cargo location is not explained by its dispatch history.");
            }
        }

        private void RestoreEvents()
        {
            var sequences = new Dictionary<ParcelId, long>();
            foreach (EventCheckpoint entry in Items(_data.Events, _runtime._limits.MaxEvents))
            {
                var id = new ParcelId(entry.ParcelId);
                Parcel parcel = _runtime._parcels[id].Snapshot;
                Require(entry.Sequence > 0 && entry.Sequence <= parcel.Version && entry.Tick >= 0
                    && entry.Tick <= _data.Now && entry.Sequence > sequences.GetValueOrDefault(id),
                    "invalid retained diagnostic event.");
                sequences[id] = entry.Sequence;
                var owner = ReadOwner(entry.Owner);
                Require(owner.Parcel is null || owner.Parcel == id, "event custody belongs to another Parcel.");
                Require(owner.Transfer is null || owner.Transfer.ParcelId == id, "event transfer belongs to another Parcel.");
                _runtime._events.Enqueue(new TransportEvent(id, entry.Sequence, entry.Tick,
                    EnumValue<OperationKind>(entry.Kind), EnumValue<ParcelState>(entry.State), owner));
            }
        }
    }
}
