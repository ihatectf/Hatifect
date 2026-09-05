using System;
using System.Collections.Generic;
using Hatifect.Flow.Domain.Shipments;

namespace Hatifect.Flow.Application;

/// <summary>A session-scoped view and command boundary. Access stays on the owning thread.</summary>
public interface IFlowApplication
{
    FlowSnapshot ReadSnapshot();
    FlowCommandResult Execute(FlowParcelCommand command);
    event Action<long>? RevisionChanged;
}

public enum FlowApplicationState { Active, Closed, Faulted, Paused, RecoveryRequired }
public enum FlowParcelAction { Reserve, Cancel, RetryDelivery, ReconcileTransfer, ReturnToSource }
public enum FlowCommandStatus { Applied, Rejected, Conflict, InvalidCommand, SessionClosed, Faulted }

[Flags]
public enum FlowParcelActions { None = 0, Reserve = 1, Cancel = 2, RetryDelivery = 4, ReconcileTransfer = 8, ReturnToSource = 16, All = 31 }

/// <summary>Expected revision and session prevent a retained UI action from mutating newer or foreign state.</summary>
public sealed record FlowParcelCommand(Guid SessionId, long ExpectedRevision, Guid ParcelId, FlowParcelAction Action);
public sealed record FlowCommandResult(FlowCommandStatus Status, long Revision)
{
    public FlowRejectionCode Code { get; init; } = FlowReasons.Status(Status);
    public string ReasonKey => FlowReasons.Key(Code);
    internal FlowCommandResult(FlowCommandStatus status, long revision, FlowRejectionCode code) : this(status, revision)
        => Code = code == FlowRejectionCode.None ? FlowReasons.Status(status) : code;
}
public sealed record FlowStationSnapshot(Guid Id);
public sealed record FlowLinkSnapshot(Guid Id, Guid Origin, Guid Destination, int Capacity, long TransitTicks);
public sealed record FlowParcelSnapshot(Guid Id, Guid ShipmentId, Guid CargoId, string ItemKey, int Quantity,
    Guid Origin, Guid Destination, Guid CurrentStation, ParcelState State, long Version,
    int DeliveryAttempts, FlowParcelActions Actions)
{
    public FlowActionAvailabilitySet Availability { get; init; } = FlowActionAvailabilitySet.FromMask(Actions);
    internal FlowParcelSnapshot(Guid id, Guid shipmentId, Guid cargoId, string itemKey, int quantity,
        Guid origin, Guid destination, Guid currentStation, ParcelState state, long version,
        int deliveryAttempts, FlowParcelActions actions, FlowActionAvailabilitySet availability)
        : this(id, shipmentId, cargoId, itemKey, quantity, origin, destination, currentStation, state, version, deliveryAttempts, actions)
        => Availability = availability;
}

/// <summary>An immutable application projection, never a persistence image or a live domain collection.</summary>
public sealed class FlowSnapshot
{
    public Guid SessionId { get; }
    public Guid NetworkId { get; }
    public long Revision { get; }
    public FlowApplicationState State { get; }
    public FlowProviderMode ProviderMode { get; }
    public FlowParcelActions SupportedOperations { get; }
    public FlowRejectionCode Code => FlowReasons.State(State);
    public string ReasonKey => FlowReasons.Key(Code);
    public IReadOnlyList<FlowStationSnapshot> Stations { get; }
    public IReadOnlyList<FlowLinkSnapshot> Links { get; }
    public IReadOnlyList<FlowParcelSnapshot> Parcels { get; }

    internal FlowSnapshot(Guid sessionId, Guid networkId, long revision, FlowApplicationState state,
        FlowStationSnapshot[] stations, FlowLinkSnapshot[] links, FlowParcelSnapshot[] parcels,
        FlowProviderMode providerMode = FlowProviderMode.DiagnosticFake, FlowParcelActions supportedOperations = FlowParcelActions.All)
    {
        SessionId = sessionId;
        NetworkId = networkId;
        Revision = revision;
        State = state;
        ProviderMode = providerMode;
        SupportedOperations = supportedOperations;
        Stations = Array.AsReadOnly((FlowStationSnapshot[])stations.Clone());
        Links = Array.AsReadOnly((FlowLinkSnapshot[])links.Clone());
        Parcels = Array.AsReadOnly((FlowParcelSnapshot[])parcels.Clone());
    }
}
