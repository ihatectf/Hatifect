using System;

namespace Hatifect.ChestsAnywhereOverlay.Integration;

internal interface IChestsAnywhereNativeSelectorAccess
{
    object Overlay { get; }
    object? ChestSelector { get; set; }
    object? CategorySelector { get; set; }
    int CategoryCount { get; }
    object? EditButton { get; }
    object EditBounds { get; set; }
    object HiddenEditBounds { get; }
}

/// <summary>
/// Host-free exact-value lease for Chests Anywhere's native selector state. Reflection and XNA
/// geometry remain behind <see cref="IChestsAnywhereNativeSelectorAccess"/>.
/// </summary>
internal sealed class ChestsAnywhereNativeSelectorLease
{
    private IChestsAnywhereNativeSelectorAccess? _access;
    private object? _originalChestSelector;
    private object? _originalCategorySelector;
    private object? _originalEditButton;
    private object? _originalEditBounds;

    internal bool IsActive => _access != null;

    internal bool Suppress(IChestsAnywhereNativeSelectorAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (_access != null
            && ReferenceEquals(_access.Overlay, access.Overlay)
            && ReferenceEquals(access.EditButton, _originalEditButton)
            && access.ChestSelector == null
            && access.CategorySelector == null
            && Equals(access.EditBounds, access.HiddenEditBounds))
        {
            return true;
        }

        if (_access != null)
        {
            if (!TryRestore(out Exception? restorationFailure))
            {
                throw new InvalidOperationException(
                    "The previous Chests Anywhere selector lease could not be restored before reacquisition.",
                    restorationFailure);
            }
        }

        object chestSelector = access.ChestSelector
            ?? throw new InvalidOperationException(
                "Chests Anywhere's chest selector was already absent before Hatifect acquired it.");
        int categoryCount = access.CategoryCount;
        if (categoryCount < 1)
            throw new InvalidOperationException("Chests Anywhere's active overlay has no categories.");
        object? categorySelector = access.CategorySelector;
        if ((categoryCount > 1) != (categorySelector != null))
        {
            throw new InvalidOperationException(
                $"Chests Anywhere exposed {categoryCount} categories with an inconsistent category-selector state.");
        }
        object editButton = access.EditButton
            ?? throw new InvalidOperationException(
                "Chests Anywhere's edit button was absent before Hatifect acquired it.");
        object editBounds = access.EditBounds
            ?? throw new InvalidOperationException(
                "Chests Anywhere's edit-button bounds could not be leased exactly.");
        object hiddenBounds = access.HiddenEditBounds
            ?? throw new InvalidOperationException(
                "Chests Anywhere's hidden edit-button bounds are unavailable.");

        // Publish the complete lease before the first mutation so a partial write failure can
        // restore every value that Hatifect actually acquired.
        _access = access;
        _originalChestSelector = chestSelector;
        _originalCategorySelector = categorySelector;
        _originalEditButton = editButton;
        _originalEditBounds = editBounds;
        access.ChestSelector = null;
        access.CategorySelector = null;
        access.EditBounds = hiddenBounds;
        if (access.ChestSelector == null
            && access.CategorySelector == null
            && Equals(access.EditBounds, hiddenBounds))
        {
            return true;
        }

        throw new InvalidOperationException("The exact native selector lease did not reach its suppressed state.");
    }

    internal bool TryRestore(out Exception? error)
    {
        error = null;
        IChestsAnywhereNativeSelectorAccess? access = _access;
        if (access == null) return true;
        try
        {
            bool chestRestored = RestoreNullSuppression(
                () => access.ChestSelector,
                value => access.ChestSelector = value,
                _originalChestSelector);
            bool categoryRestored = RestoreNullSuppression(
                () => access.CategorySelector,
                value => access.CategorySelector = value,
                _originalCategorySelector);
            bool editRestored = true;
            if (Equals(access.EditBounds, access.HiddenEditBounds))
            {
                access.EditBounds = _originalEditBounds!;
                editRestored = Equals(access.EditBounds, _originalEditBounds);
            }
            bool restored = chestRestored && categoryRestored && editRestored;
            if (!restored)
                throw new InvalidOperationException("The exact native selector lease did not round-trip.");
            Clear();
            return true;
        }
        catch (Exception failure)
        {
            error = failure;
            return false;
        }
    }

    private static bool RestoreNullSuppression(
        Func<object?> read,
        Action<object?> write,
        object? original)
    {
        if (read() != null)
            return true;
        write(original);
        return ReferenceEquals(read(), original);
    }

    private void Clear()
    {
        _access = null;
        _originalChestSelector = null;
        _originalCategorySelector = null;
        _originalEditButton = null;
        _originalEditBounds = null;
    }
}
