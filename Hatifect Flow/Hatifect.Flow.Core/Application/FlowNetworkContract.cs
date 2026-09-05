using System;
using System.Collections.Generic;

namespace Hatifect.Flow.Application;

/// <summary>Cold-path network authoring. Targets are opaque, host-captured capabilities; no game objects cross this boundary.</summary>
public interface IFlowNetworkApplication : IFlowApplication
{
    FlowNetworkSnapshot ReadNetwork();
    FlowRoutePreview PreviewRoute(Guid source, Guid destination);
    IReadOnlyList<FlowInventorySlot> ReadInventory(Guid station);
    FlowCommandResult Execute(FlowNetworkCommand command);
    FlowCommandResult Execute(FlowSendCommand command);
    IReadOnlyList<FlowRecoveryIssue> ReadRecovery();
    FlowCommandResult Execute(FlowRecoveryCommand command);
}

public enum FlowNetworkAction { RegisterStation, RenameStation, RebindStation, AddLink, RemoveLink }

/// <summary>Station identifies a selected station, Destination an endpoint, Target a captured physical target or link ID.
/// Only parameters belonging to Action are consumed. Every action checks session and revision before mutation.</summary>
public sealed record FlowNetworkCommand(Guid SessionId, long ExpectedRevision, FlowNetworkAction Action,
    Guid Station = default, Guid Destination = default, Guid Target = default, string Name = "",
    int Capacity = 999, long TransitTicks = 180);

public sealed record FlowStationDetails(Guid Id, string Name, string Location, bool Available);
public sealed record FlowRoutePreview(bool Found, int LinkCount, long TransitTicks, int AvailableUnits);
public sealed record FlowInventorySlot(int Index, string ItemKey, int Quantity, string Fingerprint, string Detail);
/// <summary>Fingerprint must equal the current complete saved item representation of the selected physical slot.</summary>
public sealed record FlowSendCommand(Guid SessionId, long ExpectedRevision, Guid Source, Guid Destination, int Slot, string Fingerprint);
public sealed record FlowRecoveryIssue(Guid ParcelId, string ItemKey, int Quantity, string Phase, string Receipt, bool StationAvailable, bool CanReconcile);
/// <summary>Reconciles only a retained settled receipt. Missing receipts never authorize physical replay.</summary>
public sealed record FlowRecoveryCommand(Guid SessionId, long ExpectedRevision, Guid ParcelId);

/// <summary>Detached station metadata and one captured target, paired with an immutable transport revision.</summary>
public sealed class FlowNetworkSnapshot
{
    public FlowSnapshot Transport { get; }
    public IReadOnlyList<FlowStationDetails> Stations { get; }
    public Guid Target { get; }
    public string TargetDescription { get; }
    internal FlowNetworkSnapshot(FlowSnapshot transport, FlowStationDetails[] stations, Guid target, string targetDescription)
    {
        Transport = transport;
        Stations = Array.AsReadOnly((FlowStationDetails[])stations.Clone());
        Target = target;
        TargetDescription = targetDescription;
    }
}
