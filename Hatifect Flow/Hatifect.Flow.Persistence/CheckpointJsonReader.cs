using System;
using System.Text.Json;
using Hatifect.Flow.Domain.Checkpoints;

namespace Hatifect.Flow.Infrastructure.Persistence;

// After strict shape validation, construct the fixed version's immutable DTOs
// explicitly. This pairs with the explicit .NET 6 writer for init-only records.
// No reflection, CLR type name lookup, or mutable deserialization setters exist.
internal static class CheckpointJsonReader
{
    internal static FlowCheckpoint Read(JsonElement value) => new(
        GuidValue(value, "networkId"), Long(value, "now"), Limits(value.GetProperty("limits")),
        Array(value, "stations", Station), Array(value, "links", Link), Array(value, "shipments", Shipment),
        Array(value, "parcels", Parcel), Array(value, "cargo", Cargo), Array(value, "transfers", Transfer),
        Array(value, "events", Event));

    private static LimitsCheckpoint Limits(JsonElement value) => new(
        Int(value, "maxStations"), Int(value, "maxLinks"), Int(value, "maxParcels"),
        Int(value, "maxRouteVisits"), Int(value, "maxRoutePlans"), Int(value, "maxDeliveryAttempts"),
        Int(value, "maxEvents"), Int(value, "maxOperationsPerAdvance"), Int(value, "maxCargoUnits"),
        Int(value, "maxPendingOperations"));

    private static StationCheckpoint Station(JsonElement value) => new(GuidValue(value, "id"), Port(value.GetProperty("port")));

    private static PortCheckpoint Port(JsonElement value) => new(Int(value, "maxCargoBatches"), Int(value, "maxReceipts"),
        Bool(value, "acceptDeposits"), Bool(value, "acceptExtractions"), Array(value, "inventory", Inventory),
        Array(value, "receipts", Receipt));

    private static InventoryCheckpoint Inventory(JsonElement value) => new(GuidValue(value, "cargoId"), Manifest(value.GetProperty("manifest")));
    private static ReceiptCheckpoint Receipt(JsonElement value) => new(Key(value.GetProperty("key")), Int(value, "result"));

    private static LinkCheckpoint Link(JsonElement value) => new(GuidValue(value, "id"), GuidValue(value, "origin"),
        GuidValue(value, "destination"), Int(value, "capacity"), Long(value, "transitTicks"), Bool(value, "active"));

    private static ManifestCheckpoint Manifest(JsonElement value) => new(value.GetProperty("itemKey").GetString()!, Int(value, "quantity"));
    private static PolicyCheckpoint Policy(JsonElement value) => new(Int(value, "serviceClass"), Int(value, "deliveryGuarantee"));

    private static ShipmentCheckpoint Shipment(JsonElement value) => new(GuidValue(value, "id"), GuidValue(value, "origin"),
        GuidValue(value, "destination"), Manifest(value.GetProperty("manifest")), Policy(value.GetProperty("policy")),
        NullableGuid(value, "parcelId"));

    private static ParcelCheckpoint Parcel(JsonElement value) => new(GuidValue(value, "id"), GuidValue(value, "shipmentId"),
        GuidValue(value, "cargoId"), Manifest(value.GetProperty("manifest")), Policy(value.GetProperty("policy")),
        Int(value, "state"), Long(value, "version"), Int(value, "deliveryAttempts"), GuidValue(value, "currentStation"),
        NullableObject(value, "pendingOperation", Operation), NullableObject(value, "pendingTransfer", Key),
        NullableObject(value, "plan", Route), Int(value, "hop"), Long(value, "transferTick"), Int(value, "cargoDispatch"));

    private static OperationCheckpoint Operation(JsonElement value) => new(Long(value, "sequence"), Int(value, "kind"), Long(value, "dueTick"));
    private static RouteCheckpoint Route(JsonElement value) => new(Array(value, "links", static item => item.GetGuid()), Array(value, "dependencies", Dependency));
    private static DependencyCheckpoint Dependency(JsonElement value) => new(GuidValue(value, "stationId"), Long(value, "revision"));

    private static CargoCheckpoint Cargo(JsonElement value) => new(GuidValue(value, "id"), Manifest(value.GetProperty("manifest")),
        Owner(value.GetProperty("owner")), NullableGuid(value, "claimedBy"), GuidValue(value, "registrationStation"), Int(value, "dispatchCount"));

    private static OwnerCheckpoint Owner(JsonElement value) => new(NullableGuid(value, "stationId"), NullableGuid(value, "parcelId"),
        NullableObject(value, "transfer", Key));

    private static TransferKey Key(JsonElement value) => new(GuidValue(value, "parcelId"), Int(value, "kind"), Int(value, "attempt"));

    private static TransferCheckpoint Transfer(JsonElement value) => new(Key(value.GetProperty("key")), GuidValue(value, "cargoId"),
        GuidValue(value, "stationId"), Manifest(value.GetProperty("manifest")), Bool(value, "retired"));

    private static EventCheckpoint Event(JsonElement value) => new(GuidValue(value, "parcelId"), Long(value, "sequence"),
        Long(value, "tick"), Int(value, "kind"), Int(value, "state"), Owner(value.GetProperty("owner")));

    private static int Int(JsonElement value, string name) => value.GetProperty(name).GetInt32();
    private static long Long(JsonElement value, string name) => value.GetProperty(name).GetInt64();
    private static bool Bool(JsonElement value, string name) => value.GetProperty(name).GetBoolean();
    private static Guid GuidValue(JsonElement value, string name) => value.GetProperty(name).GetGuid();

    private static Guid? NullableGuid(JsonElement value, string name)
    {
        JsonElement field = value.GetProperty(name);
        return field.ValueKind == JsonValueKind.Null ? null : field.GetGuid();
    }

    private static T? NullableObject<T>(JsonElement value, string name, Func<JsonElement, T> read) where T : class
    {
        JsonElement field = value.GetProperty(name);
        return field.ValueKind == JsonValueKind.Null ? null : read(field);
    }

    private static T[] Array<T>(JsonElement value, string name, Func<JsonElement, T> read)
    {
        JsonElement field = value.GetProperty(name);
        var result = new T[field.GetArrayLength()];
        int index = 0;
        foreach (JsonElement item in field.EnumerateArray())
        {
            result[index++] = read(item);
        }
        return result;
    }
}
