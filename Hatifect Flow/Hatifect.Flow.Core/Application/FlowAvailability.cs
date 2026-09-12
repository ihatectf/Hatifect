using System;

namespace Hatifect.Flow.Application;

public enum FlowProviderMode { DiagnosticFake, GameInventory }

/// <summary>Stable domain reasons. Localization belongs to the consumer; values are append-only.</summary>
public enum FlowRejectionCode
{
    None, ActionUnavailable, UnsupportedAction, Paused, RecoveryRequired, SessionClosed, Faulted,
    StaleSession, StaleRevision, StateChanged, InvalidCommand, ParcelNotFound, InvalidState,
    WorkLimit, RouteUnavailable, RouteSearchLimit, CapacityUnavailable, RetryLimit, OperationPending,
    ProviderUnavailable, UnknownOutcome, StationLimit, LifetimeLinkLimit, RetainedCargoLimit
}

public sealed record FlowActionAvailability(FlowParcelAction Action, bool Available, FlowRejectionCode Code, string ReasonKey)
{
    internal static FlowActionAvailability From(FlowParcelAction action, FlowRejectionCode code)
        => new(action, code == FlowRejectionCode.None, code, FlowReasons.Key(code));
}

/// <summary>Fixed immutable value set; equality includes reasons without comparing list identities.</summary>
public sealed record FlowActionAvailabilitySet(FlowActionAvailability Reserve, FlowActionAvailability Cancel,
    FlowActionAvailability RetryDelivery, FlowActionAvailability ReconcileTransfer, FlowActionAvailability ReturnToSource)
{
    private static readonly FlowActionAvailabilitySet[] LegacyMasks = CreateLegacyMasks();
    public FlowActionAvailability this[FlowParcelAction action] => action switch
    {
        FlowParcelAction.Reserve => Reserve,
        FlowParcelAction.Cancel => Cancel,
        FlowParcelAction.RetryDelivery => RetryDelivery,
        FlowParcelAction.ReconcileTransfer => ReconcileTransfer,
        FlowParcelAction.ReturnToSource => ReturnToSource,
        _ => FlowActionAvailability.From(action, FlowRejectionCode.UnsupportedAction)
    };

    internal FlowParcelActions Actions => (Reserve.Available ? FlowParcelActions.Reserve : 0)
        | (Cancel.Available ? FlowParcelActions.Cancel : 0) | (RetryDelivery.Available ? FlowParcelActions.RetryDelivery : 0)
        | (ReconcileTransfer.Available ? FlowParcelActions.ReconcileTransfer : 0) | (ReturnToSource.Available ? FlowParcelActions.ReturnToSource : 0);

    internal static FlowActionAvailabilitySet FromCodes(FlowRejectionCode reserve, FlowRejectionCode cancel,
        FlowRejectionCode retry, FlowRejectionCode reconcile, FlowRejectionCode returning)
        => new(FlowActionAvailability.From(FlowParcelAction.Reserve, reserve), FlowActionAvailability.From(FlowParcelAction.Cancel, cancel),
            FlowActionAvailability.From(FlowParcelAction.RetryDelivery, retry), FlowActionAvailability.From(FlowParcelAction.ReconcileTransfer, reconcile),
            FlowActionAvailability.From(FlowParcelAction.ReturnToSource, returning));

    internal static FlowActionAvailabilitySet Disabled(FlowRejectionCode code) => FromCodes(code, code, code, code, code);

    internal static FlowActionAvailabilitySet FromMask(FlowParcelActions actions)
    {
        if ((actions & ~FlowParcelActions.All) != 0) throw new ArgumentOutOfRangeException(nameof(actions));
        return LegacyMasks[(int)actions];
    }

    private static FlowActionAvailabilitySet[] CreateLegacyMasks()
    {
        var masks = new FlowActionAvailabilitySet[(int)FlowParcelActions.All + 1];
        for (int index = 0; index < masks.Length; index++) masks[index] = Mask((FlowParcelActions)index);
        return masks;
    }

    private static FlowActionAvailabilitySet Mask(FlowParcelActions actions)
    {
        FlowRejectionCode Code(FlowParcelActions flag) => (actions & flag) != 0 ? FlowRejectionCode.None : FlowRejectionCode.ActionUnavailable;
        return FromCodes(Code(FlowParcelActions.Reserve), Code(FlowParcelActions.Cancel), Code(FlowParcelActions.RetryDelivery),
            Code(FlowParcelActions.ReconcileTransfer), Code(FlowParcelActions.ReturnToSource));
    }

    internal FlowActionAvailabilitySet Restrict(FlowParcelActions supported)
    {
        FlowRejectionCode Code(FlowParcelAction action) => (supported & (FlowParcelActions)(1 << (int)action)) != 0
            ? this[action].Code : FlowRejectionCode.UnsupportedAction;
        return FromCodes(Code(FlowParcelAction.Reserve), Code(FlowParcelAction.Cancel), Code(FlowParcelAction.RetryDelivery),
            Code(FlowParcelAction.ReconcileTransfer), Code(FlowParcelAction.ReturnToSource));
    }
}

internal static class FlowReasons
{
    internal static string Key(FlowRejectionCode code) => code == FlowRejectionCode.None ? string.Empty : "flow.reason." + code;
    internal static FlowRejectionCode State(FlowApplicationState state) => state switch
    {
        FlowApplicationState.Active => FlowRejectionCode.None,
        FlowApplicationState.Paused => FlowRejectionCode.Paused,
        FlowApplicationState.RecoveryRequired => FlowRejectionCode.RecoveryRequired,
        FlowApplicationState.Closed => FlowRejectionCode.SessionClosed,
        _ => FlowRejectionCode.Faulted
    };
    internal static FlowRejectionCode Status(FlowCommandStatus status) => status switch
    {
        FlowCommandStatus.Applied => FlowRejectionCode.None,
        FlowCommandStatus.Rejected => FlowRejectionCode.ActionUnavailable,
        FlowCommandStatus.Conflict => FlowRejectionCode.StateChanged,
        FlowCommandStatus.InvalidCommand => FlowRejectionCode.InvalidCommand,
        FlowCommandStatus.SessionClosed => FlowRejectionCode.SessionClosed,
        _ => FlowRejectionCode.Faulted
    };
}
