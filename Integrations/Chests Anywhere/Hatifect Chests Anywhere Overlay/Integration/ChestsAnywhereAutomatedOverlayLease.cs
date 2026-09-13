using System;

namespace Hatifect.ChestsAnywhereOverlay.Integration;

/// <summary>
/// TestHarness-only reference-identity lease for the exact native menu and overlay opened by the
/// adapter. It contains no game or reflection policy and therefore remains valid if the integration
/// later disables new work.
/// </summary>
internal sealed class ChestsAnywhereAutomatedOverlayLease
{
    private object? _overlay;
    private object? _menu;

    internal bool IsActive => _overlay != null || _menu != null;
    internal bool IsCommitted => _overlay != null && _menu != null;

    internal void Acquire(object overlay, object menu)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(menu);
        if (IsActive)
            throw new InvalidOperationException("An automated Chests Anywhere ownership lease is already active.");
        _overlay = overlay;
        _menu = menu;
    }

    internal void AcquireProvisionalMenu(object menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        if (IsActive)
            throw new InvalidOperationException("An automated Chests Anywhere ownership lease is already active.");
        _menu = menu;
    }

    internal bool TryAcquireProvisionalMenuAfterOpenAttempt(bool nativeOpenAttempted, object? currentMenu)
    {
        if (!nativeOpenAttempted || IsActive || currentMenu == null)
            return false;
        AcquireProvisionalMenu(currentMenu);
        return true;
    }

    internal bool ShouldRollbackFailedOpen(bool nativeOpenAttempted)
        => nativeOpenAttempted && IsActive;

    internal bool TryAttachProvisionalOverlay(object overlay, object menu)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(menu);
        if (_overlay != null || _menu == null || !ReferenceEquals(_menu, menu))
            return false;
        _overlay = overlay;
        return true;
    }

    internal bool Matches(object? overlay, object? menu)
        => IsCommitted && ReferenceEquals(_overlay, overlay) && ReferenceEquals(_menu, menu);

    internal bool TryAdvanceAfterMutation(
        object expectedOverlay,
        object expectedMenu,
        bool mutationAttempted,
        object? nextOverlay,
        object? nextMenu)
    {
        ArgumentNullException.ThrowIfNull(expectedOverlay);
        ArgumentNullException.ThrowIfNull(expectedMenu);
        if (!mutationAttempted || !Matches(expectedOverlay, expectedMenu))
            return false;
        if (nextOverlay == null && nextMenu == null)
        {
            Clear();
            return true;
        }
        if (nextOverlay != null)
            _overlay = nextOverlay;
        if (nextMenu != null)
            _menu = nextMenu;
        return true;
    }

    internal bool TryRecoverMenuAfterMutation(
        object expectedOverlay,
        object expectedMenu,
        bool mutationAttempted,
        object? nextMenu)
    {
        ArgumentNullException.ThrowIfNull(expectedOverlay);
        ArgumentNullException.ThrowIfNull(expectedMenu);
        if (!mutationAttempted || nextMenu == null || !Matches(expectedOverlay, expectedMenu))
            return false;

        // The active menu is read directly from Game1 before the private overlay boundary. If its
        // getter or shape read fails, retain only the causally-created menu identity. Provisional
        // cleanup closes that exact menu before it reads or synchronizes the unknown overlay.
        _overlay = null;
        _menu = nextMenu;
        return true;
    }

    internal bool TryClose(
        Func<object?> readOverlay,
        Func<object?> readMenu,
        Action<object> closeMenu,
        Action synchronizeOverlay,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(readOverlay);
        ArgumentNullException.ThrowIfNull(readMenu);
        ArgumentNullException.ThrowIfNull(closeMenu);
        ArgumentNullException.ThrowIfNull(synchronizeOverlay);
        diagnostic = string.Empty;

        object? currentMenu = readMenu();
        if (!IsActive)
        {
            object? unownedOverlay = readOverlay();
            if (unownedOverlay == null && currentMenu == null) return true;
            diagnostic = "No Hatifect automation lease owns the current native menu and overlay.";
            return false;
        }

        // A provisional lease is acquired synchronously from an empty menu/overlay baseline after
        // OpenMenu creates the exact menu. Close that menu by reference identity before attempting
        // to inspect a partially-created overlay: private overlay reflection is allowed to fail,
        // but it must never make the exact owned menu impossible to retire.
        if (!IsCommitted)
        {
            if (currentMenu != null && !ReferenceEquals(_menu, currentMenu))
            {
                diagnostic = "The current native menu no longer matches Hatifect's exact provisional automation lease.";
                return false;
            }
            if (currentMenu != null)
                closeMenu(_menu!);
            else if (readOverlay() == null)
            {
                // A prior synchronization may have completed both native mutations and thrown
                // afterwards. Once the exact menu is already retired, observe that completed
                // postcondition before retrying a callback that may remain faulting.
                Clear();
                return true;
            }
            synchronizeOverlay();

            object? provisionalOverlayAfterClose = readOverlay();
            object? provisionalMenuAfterClose = readMenu();
            if (provisionalOverlayAfterClose == null && provisionalMenuAfterClose == null)
            {
                Clear();
                return true;
            }

            diagnostic = "Chests Anywhere did not retire both members of the exact provisional automation lease.";
            return false;
        }

        object? currentOverlay = readOverlay();
        if (currentOverlay == null && currentMenu == null)
        {
            Clear();
            return true;
        }
        bool overlayOwnedOrRetired = currentOverlay == null
            || (_overlay != null && ReferenceEquals(_overlay, currentOverlay));
        bool menuOwnedOrRetired = currentMenu == null
            || (_menu != null && ReferenceEquals(_menu, currentMenu));
        if (!overlayOwnedOrRetired || !menuOwnedOrRetired)
        {
            diagnostic = "The current native menu or overlay no longer matches Hatifect's exact automation lease.";
            return false;
        }

        if (currentMenu != null)
            closeMenu(_menu!);
        synchronizeOverlay();
        currentOverlay = readOverlay();
        currentMenu = readMenu();
        if (currentOverlay != null || currentMenu != null)
        {
            diagnostic = "Chests Anywhere did not retire both members of the exact automation lease.";
            return false;
        }

        Clear();
        return true;
    }

    /// <summary>
    /// Forget TestHarness bookkeeping only after Stardew has retired both exact native identities.
    /// This never closes or mutates a menu and therefore cannot affect a new title/menu owner.
    /// </summary>
    internal bool TryReleaseAfterNativeRetirement(
        Func<object?> readOverlay,
        Func<object?> readMenu,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(readOverlay);
        ArgumentNullException.ThrowIfNull(readMenu);
        diagnostic = string.Empty;
        if (!IsActive) return true;

        object? currentOverlay = readOverlay();
        object? currentMenu = readMenu();
        if ((_overlay != null && ReferenceEquals(_overlay, currentOverlay))
            || (_menu != null && ReferenceEquals(_menu, currentMenu)))
        {
            diagnostic = "The exact automated Chests Anywhere overlay or menu is still active after ReturnedToTitle.";
            return false;
        }

        Clear();
        return true;
    }

    private void Clear()
    {
        _overlay = null;
        _menu = null;
    }
}
