using System;
using System.IO;
using System.Text.Json;
using Hatifect.Flow.Domain.Checkpoints;

namespace Hatifect.Flow.Infrastructure.Persistence;

// .NET 6's generator warns on init-only records even in serialization-only mode.
// Write this internal version's fixed schema directly instead of adding mutable
// DTO setters, reflection fallback, packages, or warning suppressions.
internal static class CheckpointJsonWriter
{
    internal static void Write(Utf8JsonWriter writer, FlowCheckpoint value)
    {
        writer.WriteStartObject();
        writer.WriteString("networkId", value.NetworkId);
        writer.WriteNumber("now", value.Now);
        Object(writer, "limits", value.Limits, Limits);
        Array(writer, "stations", value.Stations, Station);
        Array(writer, "links", value.Links, Link);
        Array(writer, "shipments", value.Shipments, Shipment);
        Array(writer, "parcels", value.Parcels, Parcel);
        Array(writer, "cargo", value.Cargo, Cargo);
        Array(writer, "transfers", value.Transfers, Transfer);
        Array(writer, "events", value.Events, Event);
        writer.WriteEndObject();
    }

    private static void Limits(Utf8JsonWriter writer, LimitsCheckpoint value)
    {
        writer.WriteStartObject();
        writer.WriteNumber("maxStations", value.MaxStations);
        writer.WriteNumber("maxLinks", value.MaxLinks);
        writer.WriteNumber("maxParcels", value.MaxParcels);
        writer.WriteNumber("maxRouteVisits", value.MaxRouteVisits);
        writer.WriteNumber("maxRoutePlans", value.MaxRoutePlans);
        writer.WriteNumber("maxDeliveryAttempts", value.MaxDeliveryAttempts);
        writer.WriteNumber("maxEvents", value.MaxEvents);
        writer.WriteNumber("maxOperationsPerAdvance", value.MaxOperationsPerAdvance);
        writer.WriteNumber("maxCargoUnits", value.MaxCargoUnits);
        writer.WriteNumber("maxPendingOperations", value.MaxPendingOperations);
        writer.WriteEndObject();
    }

    private static void Station(Utf8JsonWriter writer, StationCheckpoint value)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        Object(writer, "port", value.Port, Port);
        writer.WriteEndObject();
    }

    private static void Port(Utf8JsonWriter writer, PortCheckpoint value)
    {
        writer.WriteStartObject();
        writer.WriteNumber("maxCargoBatches", value.MaxCargoBatches);
        writer.WriteNumber("maxReceipts", value.MaxReceipts);
        writer.WriteBoolean("acceptDeposits", value.AcceptDeposits);
        writer.WriteBoolean("acceptExtractions", value.AcceptExtractions);
        Array(writer, "inventory", value.Inventory, Inventory);
        Array(writer, "receipts", value.Receipts, Receipt);
        writer.WriteEndObject();
    }

    private static void Inventory(Utf8JsonWriter writer, InventoryCheckpoint value)
    {
        writer.WriteStartObject();
        writer.WriteString("cargoId", value.CargoId);
        Object(writer, "manifest", value.Manifest, Manifest);
        writer.WriteEndObject();
    }

    private static void Receipt(Utf8JsonWriter writer, ReceiptCheckpoint value)
    {
        writer.WriteStartObject();
        Object(writer, "key", value.Key, Key);
        writer.WriteNumber("result", value.Result);
        writer.WriteEndObject();
    }

    private static void Link(Utf8JsonWriter writer, LinkCheckpoint value)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WriteString("origin", value.Origin);
        writer.WriteString("destination", value.Destination);
        writer.WriteNumber("capacity", value.Capacity);
        writer.WriteNumber("transitTicks", value.TransitTicks);
        writer.WriteBoolean("active", value.Active);
        writer.WriteEndObject();
    }

    private static void Manifest(Utf8JsonWriter writer, ManifestCheckpoint value)
    {
        // Bound the only arbitrary string before Utf8JsonWriter can rent an
        // escaping buffer. This internal format matches Core's item-key limit.
        if (value.ItemKey is not null && value.ItemKey.Length > 256)
        {
            throw new InvalidDataException("Checkpoint item keys cannot exceed 256 characters.");
        }
        if (value.ItemKey is not null)
        {
            for (int index = 0; index < value.ItemKey.Length; index++)
            {
                char character = value.ItemKey[index];
                if (char.IsHighSurrogate(character))
                {
                    if (++index == value.ItemKey.Length || !char.IsLowSurrogate(value.ItemKey[index]))
                    {
                        throw new InvalidDataException("Checkpoint item keys must contain valid UTF-16.");
                    }
                }
                else if (char.IsLowSurrogate(character))
                {
                    throw new InvalidDataException("Checkpoint item keys must contain valid UTF-16.");
                }
            }
        }
        writer.WriteStartObject();
        writer.WriteString("itemKey", value.ItemKey);
        writer.WriteNumber("quantity", value.Quantity);
        writer.WriteEndObject();
    }

    private static void Policy(Utf8JsonWriter writer, PolicyCheckpoint value)
    {
        writer.WriteStartObject();
        writer.WriteNumber("serviceClass", value.ServiceClass);
        writer.WriteNumber("deliveryGuarantee", value.DeliveryGuarantee);
        writer.WriteEndObject();
    }

    private static void Shipment(Utf8JsonWriter writer, ShipmentCheckpoint value)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WriteString("origin", value.Origin);
        writer.WriteString("destination", value.Destination);
        Object(writer, "manifest", value.Manifest, Manifest);
        Object(writer, "policy", value.Policy, Policy);
        NullableGuid(writer, "parcelId", value.ParcelId);
        writer.WriteEndObject();
    }

    private static void Parcel(Utf8JsonWriter writer, ParcelCheckpoint value)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WriteString("shipmentId", value.ShipmentId);
        writer.WriteString("cargoId", value.CargoId);
        Object(writer, "manifest", value.Manifest, Manifest);
        Object(writer, "policy", value.Policy, Policy);
        writer.WriteNumber("state", value.State);
        writer.WriteNumber("version", value.Version);
        writer.WriteNumber("deliveryAttempts", value.DeliveryAttempts);
        writer.WriteString("currentStation", value.CurrentStation);
        Object(writer, "pendingOperation", value.PendingOperation, Operation);
        Object(writer, "pendingTransfer", value.PendingTransfer, Key);
        Object(writer, "plan", value.Plan, Route);
        writer.WriteNumber("hop", value.Hop);
        writer.WriteNumber("transferTick", value.TransferTick);
        writer.WriteNumber("cargoDispatch", value.CargoDispatch);
        writer.WriteEndObject();
    }

    private static void Operation(Utf8JsonWriter writer, OperationCheckpoint value)
    {
        writer.WriteStartObject();
        writer.WriteNumber("sequence", value.Sequence);
        writer.WriteNumber("kind", value.Kind);
        writer.WriteNumber("dueTick", value.DueTick);
        writer.WriteEndObject();
    }

    private static void Route(Utf8JsonWriter writer, RouteCheckpoint value)
    {
        writer.WriteStartObject();
        Array(writer, "links", value.Links, static (output, id) => output.WriteStringValue(id));
        Array(writer, "dependencies", value.Dependencies, Dependency);
        writer.WriteEndObject();
    }

    private static void Dependency(Utf8JsonWriter writer, DependencyCheckpoint value)
    {
        writer.WriteStartObject();
        writer.WriteString("stationId", value.StationId);
        writer.WriteNumber("revision", value.Revision);
        writer.WriteEndObject();
    }

    private static void Cargo(Utf8JsonWriter writer, CargoCheckpoint value)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        Object(writer, "manifest", value.Manifest, Manifest);
        Object(writer, "owner", value.Owner, Owner);
        NullableGuid(writer, "claimedBy", value.ClaimedBy);
        writer.WriteString("registrationStation", value.RegistrationStation);
        writer.WriteNumber("dispatchCount", value.DispatchCount);
        writer.WriteEndObject();
    }

    private static void Owner(Utf8JsonWriter writer, OwnerCheckpoint value)
    {
        writer.WriteStartObject();
        NullableGuid(writer, "stationId", value.StationId);
        NullableGuid(writer, "parcelId", value.ParcelId);
        Object(writer, "transfer", value.Transfer, Key);
        writer.WriteEndObject();
    }

    private static void Key(Utf8JsonWriter writer, TransferKey value)
    {
        writer.WriteStartObject();
        writer.WriteString("parcelId", value.ParcelId);
        writer.WriteNumber("kind", value.Kind);
        writer.WriteNumber("attempt", value.Attempt);
        writer.WriteEndObject();
    }

    private static void Transfer(Utf8JsonWriter writer, TransferCheckpoint value)
    {
        writer.WriteStartObject();
        Object(writer, "key", value.Key, Key);
        writer.WriteString("cargoId", value.CargoId);
        writer.WriteString("stationId", value.StationId);
        Object(writer, "manifest", value.Manifest, Manifest);
        writer.WriteBoolean("retired", value.Retired);
        writer.WriteEndObject();
    }

    private static void Event(Utf8JsonWriter writer, EventCheckpoint value)
    {
        writer.WriteStartObject();
        writer.WriteString("parcelId", value.ParcelId);
        writer.WriteNumber("sequence", value.Sequence);
        writer.WriteNumber("tick", value.Tick);
        writer.WriteNumber("kind", value.Kind);
        writer.WriteNumber("state", value.State);
        Object(writer, "owner", value.Owner, Owner);
        writer.WriteEndObject();
    }

    private static void NullableGuid(Utf8JsonWriter writer, string name, Guid? value)
    {
        if (value.HasValue) writer.WriteString(name, value.Value);
        else writer.WriteNull(name);
    }

    private static void Object<T>(Utf8JsonWriter writer, string name, T? value,
        Action<Utf8JsonWriter, T> write) where T : class
    {
        writer.WritePropertyName(name);
        if (value is null) writer.WriteNullValue();
        else write(writer, value);
    }

    private static void Array<T>(Utf8JsonWriter writer, string name, T[]? values, Action<Utf8JsonWriter, T> write)
    {
        writer.WritePropertyName(name);
        if (values is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartArray();
        foreach (T value in values)
        {
            if (value is null) writer.WriteNullValue();
            else write(writer, value);
        }
        writer.WriteEndArray();
    }
}
