using System;
using System.Diagnostics.CodeAnalysis;
using Hatifect.Flow.Domain.Policies;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Shipments;

namespace Hatifect.Flow.Domain.Checkpoints;

internal static class CheckpointValues
{
    internal static ManifestCheckpoint Capture(CargoManifest value) => new(value.ItemKey, value.Quantity);
    internal static PolicyCheckpoint Capture(ServicePolicy value) => new((int)value.ServiceClass, (int)value.Guarantee);
    internal static TransferKey Key(PortTransferId value) => new(value.ParcelId.Value, (int)value.Kind, value.Attempt);
    internal static OwnerCheckpoint Capture(CargoOwner value) => new(value.Station?.Value,
        value.Parcel?.Value, value.Transfer is null ? null : Key(value.Transfer));

    internal static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
        {
            throw new ArgumentException("Invalid Flow checkpoint: " + message);
        }
    }

    internal static T[] Array<T>(T[]? values, int maximum)
    {
        Require(values is not null && values.Length <= maximum, "missing or oversized collection.");
        foreach (T value in values!)
        {
            Require(value is not null, "null collection entry.");
        }
        return values;
    }

    internal static T EnumValue<T>(int value) where T : struct, Enum
    {
        Require(Enum.IsDefined(typeof(T), value), "unknown " + typeof(T).Name + ".");
        return (T)(object)value;
    }

    internal static CargoManifest Manifest(ManifestCheckpoint? value, int maximum)
    {
        Require(value is not null && value.Quantity <= maximum, "missing or oversized manifest.");
        return new CargoManifest(value!.ItemKey, value.Quantity);
    }

    internal static ServicePolicy Policy(PolicyCheckpoint? value)
    {
        Require(value is not null, "missing service policy.");
        return new ServicePolicy(EnumValue<ServiceClass>(value!.ServiceClass),
            EnumValue<DeliveryGuarantee>(value.DeliveryGuarantee));
    }
}
