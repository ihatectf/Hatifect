using System;

namespace Hatifect.Flow.Domain.Checkpoints;

// Detached values only. These internal records are untrusted input on restore;
// no CLR reference, session token, live Port or execution ticket is persisted.
internal sealed record FlowCheckpoint(Guid NetworkId, long Now, LimitsCheckpoint Limits,
    StationCheckpoint[] Stations, LinkCheckpoint[] Links, ShipmentCheckpoint[] Shipments,
    ParcelCheckpoint[] Parcels, CargoCheckpoint[] Cargo, TransferCheckpoint[] Transfers,
    EventCheckpoint[] Events);

internal sealed record LimitsCheckpoint(int MaxStations, int MaxLinks, int MaxParcels,
    int MaxRouteVisits, int MaxRoutePlans, int MaxDeliveryAttempts, int MaxEvents,
    int MaxOperationsPerAdvance, int MaxCargoUnits, int MaxPendingOperations);
internal sealed record StationCheckpoint(Guid Id, PortCheckpoint Port);
internal sealed record PortCheckpoint(int MaxCargoBatches, int MaxReceipts,
    bool AcceptDeposits, bool AcceptExtractions, InventoryCheckpoint[] Inventory,
    ReceiptCheckpoint[] Receipts);
internal sealed record InventoryCheckpoint(Guid CargoId, ManifestCheckpoint Manifest);
internal sealed record ReceiptCheckpoint(TransferKey Key, int Result);
internal sealed record LinkCheckpoint(Guid Id, Guid Origin, Guid Destination,
    int Capacity, long TransitTicks, bool Active);
internal sealed record ManifestCheckpoint(string ItemKey, int Quantity);
internal sealed record PolicyCheckpoint(int ServiceClass, int DeliveryGuarantee);
internal sealed record ShipmentCheckpoint(Guid Id, Guid Origin, Guid Destination,
    ManifestCheckpoint Manifest, PolicyCheckpoint Policy, Guid? ParcelId);
internal sealed record ParcelCheckpoint(Guid Id, Guid ShipmentId, Guid CargoId,
    ManifestCheckpoint Manifest, PolicyCheckpoint Policy, int State, long Version,
    int DeliveryAttempts, Guid CurrentStation, OperationCheckpoint? PendingOperation,
    TransferKey? PendingTransfer, RouteCheckpoint? Plan, int Hop, long TransferTick, int CargoDispatch);
internal sealed record OperationCheckpoint(long Sequence, int Kind, long DueTick);
internal sealed record RouteCheckpoint(Guid[] Links, DependencyCheckpoint[] Dependencies);
internal sealed record DependencyCheckpoint(Guid StationId, long Revision);
internal sealed record CargoCheckpoint(Guid Id, ManifestCheckpoint Manifest,
    OwnerCheckpoint Owner, Guid? ClaimedBy, Guid RegistrationStation, int DispatchCount);
internal sealed record OwnerCheckpoint(Guid? StationId, Guid? ParcelId, TransferKey? Transfer);
internal sealed record TransferKey(Guid ParcelId, int Kind, int Attempt);
internal sealed record TransferCheckpoint(TransferKey Key, Guid CargoId, Guid StationId,
    ManifestCheckpoint Manifest, bool Retired);
internal sealed record EventCheckpoint(Guid ParcelId, long Sequence, long Tick,
    int Kind, int State, OwnerCheckpoint Owner);
