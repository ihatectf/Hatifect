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
public enum FlowParcelActions { None = 0, Reserve = 1, Cancel = 2, RetryDelivery = 4, ReconcileTransfer = 8, ReturnToSource = 16 }

/// <summary>Expected revision and session prevent a retained UI action from mutating newer or foreign state.</summary>
public sealed record FlowParcelCommand(Guid SessionId, long ExpectedRevision, Guid ParcelId, FlowParcelAction Action);
public sealed record FlowCommandResult(FlowCommandStatus Status, long Revision);
public sealed record FlowStationSnapshot(Guid Id);
public sealed record FlowLinkSnapshot(Guid Id, Guid Origin, Guid Destination, int Capacity, long TransitTicks);
public sealed record FlowParcelSnapshot(Guid Id, Guid ShipmentId, Guid CargoId, string ItemKey, int Quantity,
    Guid Origin, Guid Destination, Guid CurrentStation, ParcelState State, long Version,
    int DeliveryAttempts, FlowParcelActions Actions);

/// <summary>An immutable application projection, never a persistence image or a live domain collection.</summary>
public sealed class FlowSnapshot
{
    public Guid SessionId { get; }
    public Guid NetworkId { get; }
    public long Revision { get; }
    public FlowApplicationState State { get; }
    public IReadOnlyList<FlowStationSnapshot> Stations { get; }
    public IReadOnlyList<FlowLinkSnapshot> Links { get; }
    public IReadOnlyList<FlowParcelSnapshot> Parcels { get; }

    internal FlowSnapshot(Guid sessionId, Guid networkId, long revision, FlowApplicationState state,
        FlowStationSnapshot[] stations, FlowLinkSnapshot[] links, FlowParcelSnapshot[] parcels)
    {
        SessionId = sessionId;
        NetworkId = networkId;
        Revision = revision;
        State = state;
        Stations = Array.AsReadOnly((FlowStationSnapshot[])stations.Clone());
        Links = Array.AsReadOnly((FlowLinkSnapshot[])links.Clone());
        Parcels = Array.AsReadOnly((FlowParcelSnapshot[])parcels.Clone());
    }
}
