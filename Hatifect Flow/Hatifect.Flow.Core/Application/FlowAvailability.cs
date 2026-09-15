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
        | (Cancel.Available ? FlowParcelActions.Cancel : 0)
        | (RetryDelivery.Available ? FlowParcelActions.RetryDelivery : 0)
        | (ReconcileTransfer.Available ? FlowParcelActions.ReconcileTransfer : 0)
        | (ReturnToSource.Available ? FlowParcelActions.ReturnToSource : 0);

    internal static FlowActionAvailabilitySet FromCodes(FlowRejectionCode reserve, FlowRejectionCode cancel,
        FlowRejectionCode retry, FlowRejectionCode reconcile, FlowRejectionCode returning)
        => new(
            FlowActionAvailability.From(FlowParcelAction.Reserve, reserve),
            FlowActionAvailability.From(FlowParcelAction.Cancel, cancel),
            FlowActionAvailability.From(FlowParcelAction.RetryDelivery, retry),
            FlowActionAvailability.From(FlowParcelAction.ReconcileTransfer, reconcile),
            FlowActionAvailability.From(FlowParcelAction.ReturnToSource, returning));

    internal static FlowActionAvailabilitySet Disabled(FlowRejectionCode code) => FromCodes(code, code, code, code, code);

    internal static FlowActionAvailabilitySet FromMask(FlowParcelActions actions)
    {
        if ((actions & ~FlowParcelActions.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(actions));

        return LegacyMasks[(int)actions];
    }

    private static FlowActionAvailabilitySet[] CreateLegacyMasks()
    {
        var masks = new FlowActionAvailabilitySet[(int)FlowParcelActions.All + 1];
        for (int index = 0; index < masks.Length; index++)
            masks[index] = CreateLegacyAvailability((FlowParcelActions)index);

        return masks;
    }

    private static FlowActionAvailabilitySet CreateLegacyAvailability(FlowParcelActions actions)
    {
        // The legacy mask records availability, but not the reason an action was refused.
        FlowRejectionCode GetRejectionCode(FlowParcelActions flag)
        {
            return (actions & flag) != 0
                ? FlowRejectionCode.None
                : FlowRejectionCode.ActionUnavailable;
        }

        return FromCodes(
            GetRejectionCode(FlowParcelActions.Reserve),
            GetRejectionCode(FlowParcelActions.Cancel),
            GetRejectionCode(FlowParcelActions.RetryDelivery),
            GetRejectionCode(FlowParcelActions.ReconcileTransfer),
            GetRejectionCode(FlowParcelActions.ReturnToSource));
    }

    internal FlowActionAvailabilitySet Restrict(FlowParcelActions supported)
    {
        FlowRejectionCode GetRejectionCode(FlowActionAvailability availability, FlowParcelActions flag)
        {
            if ((supported & flag) == 0)
                return FlowRejectionCode.UnsupportedAction;

            return availability.Code;
        }

        return FromCodes(
            GetRejectionCode(Reserve, FlowParcelActions.Reserve),
            GetRejectionCode(Cancel, FlowParcelActions.Cancel),
            GetRejectionCode(RetryDelivery, FlowParcelActions.RetryDelivery),
            GetRejectionCode(ReconcileTransfer, FlowParcelActions.ReconcileTransfer),
            GetRejectionCode(ReturnToSource, FlowParcelActions.ReturnToSource));
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
