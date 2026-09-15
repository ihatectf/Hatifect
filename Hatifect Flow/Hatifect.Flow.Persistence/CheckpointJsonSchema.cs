using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Hatifect.Flow.Infrastructure.Persistence;

// .NET 6 constructor binding supplies defaults for absent properties. Every
// field, including nullable fields and false-valued flags, must instead be
// present in this versioned format. This shape check precedes DTO construction;
// domain values and cross-record consistency remain Core's responsibility.
internal static class CheckpointJsonSchema
{
    private static readonly Shape Text = new(JsonValueKind.String);
    private static readonly Shape Number = new(JsonValueKind.Number);
    private static readonly Shape Boolean = new(JsonValueKind.True);
    private static readonly Shape NullableText = Nullable(Text);
    private static readonly Shape Manifest = Object(("itemKey", Text), ("quantity", Number));
    private static readonly Shape Policy = Object(("serviceClass", Number), ("deliveryGuarantee", Number));
    private static readonly Shape TransferKey = Object(("parcelId", Text), ("kind", Number), ("attempt", Number));
    private static readonly Shape Owner = Object(("stationId", NullableText), ("parcelId", NullableText),
        ("transfer", Nullable(TransferKey)));
    private static readonly Shape Inventory = Object(("cargoId", Text), ("manifest", Manifest));
    private static readonly Shape Receipt = Object(("key", TransferKey), ("result", Number));
    private static readonly Shape Port = Object(("maxCargoBatches", Number), ("maxReceipts", Number),
        ("acceptDeposits", Boolean), ("acceptExtractions", Boolean),
        ("inventory", Array(Inventory)), ("receipts", Array(Receipt)));
    private static readonly Shape Station = Object(("id", Text), ("port", Port));
    private static readonly Shape Link = Object(("id", Text), ("origin", Text), ("destination", Text),
        ("capacity", Number), ("transitTicks", Number), ("active", Boolean));
    private static readonly Shape Shipment = Object(("id", Text), ("origin", Text), ("destination", Text),
        ("manifest", Manifest), ("policy", Policy), ("parcelId", NullableText));
    private static readonly Shape Operation = Object(("sequence", Number), ("kind", Number), ("dueTick", Number));
    private static readonly Shape Dependency = Object(("stationId", Text), ("revision", Number));
    private static readonly Shape Route = Object(("links", Array(Text)), ("dependencies", Array(Dependency)));
    private static readonly Shape Parcel = Object(("id", Text), ("shipmentId", Text), ("cargoId", Text),
        ("manifest", Manifest), ("policy", Policy), ("state", Number), ("version", Number),
        ("deliveryAttempts", Number), ("currentStation", Text), ("pendingOperation", Nullable(Operation)),
        ("pendingTransfer", Nullable(TransferKey)), ("plan", Nullable(Route)), ("hop", Number),
        ("transferTick", Number), ("cargoDispatch", Number));
    private static readonly Shape Cargo = Object(("id", Text), ("manifest", Manifest),
        ("owner", Owner), ("claimedBy", NullableText), ("registrationStation", Text), ("dispatchCount", Number));
    private static readonly Shape Transfer = Object(("key", TransferKey), ("cargoId", Text),
        ("stationId", Text), ("manifest", Manifest), ("retired", Boolean));
    private static readonly Shape Event = Object(("parcelId", Text), ("sequence", Number),
        ("tick", Number), ("kind", Number), ("state", Number), ("owner", Owner));
    private static readonly Shape Limits = Object(("maxStations", Number), ("maxLinks", Number),
        ("maxParcels", Number), ("maxRouteVisits", Number), ("maxRoutePlans", Number),
        ("maxDeliveryAttempts", Number), ("maxEvents", Number), ("maxOperationsPerAdvance", Number),
        ("maxCargoUnits", Number), ("maxPendingOperations", Number));
    private static readonly Shape Root = Object(("networkId", Text), ("now", Number), ("limits", Limits),
        ("stations", Array(Station)), ("links", Array(Link)), ("shipments", Array(Shipment)),
        ("parcels", Array(Parcel)), ("cargo", Array(Cargo)), ("transfers", Array(Transfer)),
        ("events", Array(Event)));

    internal static void Validate(JsonElement element) => Validate(element, Root);

    private static void Validate(JsonElement element, Shape shape)
    {
        if (shape.NullableShape is not null)
        {
            if (element.ValueKind != JsonValueKind.Null)
            {
                Validate(element, shape.NullableShape);
            }
            return;
        }
        if (element.ValueKind != shape.Kind
            && !(shape.Kind == JsonValueKind.True && element.ValueKind == JsonValueKind.False))
        {
            throw new InvalidDataException("Checkpoint JSON contains a field with an invalid shape.");
        }
        if (shape.Fields is not null)
        {
            ValidateFields(element, shape.Fields);
        }
        else if (shape.Item is not null)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                Validate(item, shape.Item);
            }
        }
    }

    private static void ValidateFields(JsonElement element, Dictionary<string, Shape> fields)
    {
        int count = 0;
        foreach (JsonProperty field in element.EnumerateObject())
        {
            if (!fields.TryGetValue(field.Name, out Shape? child))
            {
                throw new InvalidDataException("Checkpoint JSON contains an unknown property.");
            }
            Validate(field.Value, child);
            count++;
        }
        if (count != fields.Count)
        {
            throw new InvalidDataException("Checkpoint JSON is missing a required property.");
        }
    }

    private static Shape Object(params (string Name, Shape Shape)[] fields)
    {
        var members = new Dictionary<string, Shape>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            members.Add(field.Name, field.Shape);
        }
        return new Shape(JsonValueKind.Object, Fields: members);
    }

    private static Shape Array(Shape item) => new(JsonValueKind.Array, Item: item);
    private static Shape Nullable(Shape shape) => new(JsonValueKind.Undefined, NullableShape: shape);

    private sealed record Shape(JsonValueKind Kind, Dictionary<string, Shape>? Fields = null,
        Shape? Item = null, Shape? NullableShape = null);
}
