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

        bool overlayChanged = !ReferenceEquals(currentOverlay, originalOverlay);
        bool menuChanged = !ReferenceEquals(currentMenu, originalMenu);
        if (overlayChanged && menuChanged && ReferenceEquals(currentOverlayMenu, currentMenu))
            return ChestsAnywhereAutomatedHandoffState.Synchronized;

        if (ReferenceEquals(currentOverlay, originalOverlay)
            && ReferenceEquals(currentOverlayMenu, originalMenu)
            && menuChanged)
        {
            return ChestsAnywhereAutomatedHandoffState.AwaitingOverlaySynchronization;
        }

        return ChestsAnywhereAutomatedHandoffState.Invalid;
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
        if (currentMenu == null)
        {
            return ReferenceEquals(currentOverlay, originalOverlay)
                && ReferenceEquals(currentOverlayMenu, originalMenu);
        }
        if (ReferenceEquals(currentOverlay, originalOverlay)
            && ReferenceEquals(currentMenu, originalMenu)
            && ReferenceEquals(currentOverlayMenu, originalMenu))
        {
            return true;
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
