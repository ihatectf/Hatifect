namespace Hatifect.ChestsAnywhereOverlay.Integration;

internal enum ChestsAnywhereAutomatedHandoffState
{
    Invalid,
    AwaitingOverlaySynchronization,
    Synchronized
}

/// <summary>
/// Classifies the two synchronous states produced by the pinned Chests Anywhere 1.30.1 handoff.
/// SelectChest replaces the active menu first, while the old overlay remains current until
/// ChangeOverlayIfNeeded runs on the next native update boundary.
/// </summary>
internal static class ChestsAnywhereAutomatedHandoffPolicy
{
    internal static ChestsAnywhereAutomatedHandoffState Classify(
        object originalOverlay,
        object originalMenu,
        object? currentOverlay,
        object? currentMenu,
        object? currentOverlayMenu)
    {
        ArgumentNullException.ThrowIfNull(originalOverlay);
        ArgumentNullException.ThrowIfNull(originalMenu);
        if (currentOverlay == null || currentMenu == null || currentOverlayMenu == null)
            return ChestsAnywhereAutomatedHandoffState.Invalid;

        if (ReferenceEquals(currentMenu, originalMenu))
            return ChestsAnywhereAutomatedHandoffState.Invalid;

        if (ReferenceEquals(currentOverlay, originalOverlay))
        {
            return ReferenceEquals(currentOverlayMenu, originalMenu)
                ? ChestsAnywhereAutomatedHandoffState.AwaitingOverlaySynchronization
                : ChestsAnywhereAutomatedHandoffState.Invalid;
        }

        return ReferenceEquals(currentOverlayMenu, currentMenu)
            ? ChestsAnywhereAutomatedHandoffState.Synchronized
            : ChestsAnywhereAutomatedHandoffState.Invalid;
    }

    internal static bool CanRecoverForRollback(
        object originalOverlay,
        object originalMenu,
        object? currentOverlay,
        object? currentMenu,
        object? currentOverlayMenu)
    {
        ArgumentNullException.ThrowIfNull(originalOverlay);
        ArgumentNullException.ThrowIfNull(originalMenu);

        if (currentOverlay == null)
            return true;

        // A closed or unchanged menu is recoverable only while the original overlay still owns the original menu.
        if (currentMenu == null || ReferenceEquals(currentMenu, originalMenu))
        {
            return ReferenceEquals(currentOverlay, originalOverlay)
                && ReferenceEquals(currentOverlayMenu, originalMenu);
        }

        return Classify(
                originalOverlay,
                originalMenu,
                currentOverlay,
                currentMenu,
                currentOverlayMenu)
            != ChestsAnywhereAutomatedHandoffState.Invalid;
    }
}
