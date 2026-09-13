using System;
using Hatifect.Flow.Application;
using Hatifect.Flow.Diagnostics;
using Hatifect.Flow.Infrastructure.Persistence;
using Hatifect.Flow.Inventory;

namespace Hatifect.Flow.Sessions;

internal sealed partial class FlowGameSession
{
    // Current-load counters only. A refusal does not change the persisted aggregate or UI revision.
    private readonly long[] _admissionRejections = new long[3];

    internal FlowGameResources ReadResources()
    {
        RequireOwnerIdle();
        if (_closed) throw new InvalidOperationException("The game session is closed.");
        FlowSnapshot snapshot = Application.ReadSnapshot();
        var ports = new FlowStationResources[_ports.Count];
        int index = 0;
        foreach (var entry in _ports)
            ports[index++] = new FlowStationResources(entry.Key, entry.Value.ReadResources());
        Array.Sort(ports, static (left, right) => left.StationId.CompareTo(right.StationId));
        long characters = 0;
        foreach (CargoPayload payload in _payloads.Values) characters += payload.Xml.Length;
        int maximumAttempts = 0, exhaustedParcels = 0;
        FlowRuntimeResources runtime = _runtime.ReadResources();
        foreach (FlowParcelSnapshot parcel in snapshot.Parcels)
        {
            maximumAttempts = Math.Max(maximumAttempts, parcel.DeliveryAttempts);
            if (parcel.DeliveryAttempts == runtime.MaxDeliveryAttempts) exhaustedParcels++;
        }
        return new FlowGameResources(snapshot.SessionId, _saveId, _runtime.NetworkId.Value, snapshot.Revision,
            _runtime.Now, snapshot.State, runtime, new(_payloads.Count, MaxCargo),
            new(characters, (long)MaxCargo * FlowItemCodec.MaxPayloadLength), FlowItemCodec.MaxPayloadLength,
            CheckpointCodec.MaxImageBytes, maximumAttempts, exhaustedParcels, Array.AsReadOnly(ports),
            new(_admissionRejections[0], _admissionRejections[1], _admissionRejections[2]), TickCounters);
    }

    private FlowResourceLimitException RejectResource(FlowAdmissionResource resource, int used, int limit)
    {
        int index = (int)resource;
        if (_admissionRejections[index] < long.MaxValue) _admissionRejections[index]++;
        return new FlowResourceLimitException(resource, used, limit);
    }
}
